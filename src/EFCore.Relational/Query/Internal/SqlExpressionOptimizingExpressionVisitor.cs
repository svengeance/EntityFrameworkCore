// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace Microsoft.EntityFrameworkCore.Query.Internal
{
    public class SqlExpressionOptimizingExpressionVisitor : ExpressionVisitor
    {
        private readonly bool _useRelationalNulls;
        private readonly IReadOnlyDictionary<string, object> _parametersValues;

        private static bool TryNegate(ExpressionType expressionType, out ExpressionType result)
        {
            var negated = expressionType switch
            {
                ExpressionType.AndAlso => ExpressionType.OrElse,
                ExpressionType.OrElse => ExpressionType.AndAlso,
                ExpressionType.Equal => ExpressionType.NotEqual,
                ExpressionType.NotEqual => ExpressionType.Equal,
                ExpressionType.GreaterThan => ExpressionType.LessThanOrEqual,
                ExpressionType.GreaterThanOrEqual => ExpressionType.LessThan,
                ExpressionType.LessThan => ExpressionType.GreaterThanOrEqual,
                ExpressionType.LessThanOrEqual => ExpressionType.GreaterThan,
                _ => (ExpressionType?)null
            };

            result = negated ?? default;

            return negated.HasValue;
        }

        public SqlExpressionOptimizingExpressionVisitor(
            ISqlExpressionFactory sqlExpressionFactory,
            bool useRelationalNulls,
            IReadOnlyDictionary<string, object> parametersValues)
        {
            SqlExpressionFactory = sqlExpressionFactory;
            _useRelationalNulls = useRelationalNulls;
            _parametersValues = parametersValues;
        }

        protected virtual ISqlExpressionFactory SqlExpressionFactory { get; }

        protected override Expression VisitExtension(Expression extensionExpression)
            => extensionExpression switch
            {
                SqlUnaryExpression sqlUnaryExpression => VisitSqlUnaryExpression(sqlUnaryExpression),
                SqlBinaryExpression sqlBinaryExpression => VisitSqlBinaryExpression(sqlBinaryExpression),
                SelectExpression selectExpression => VisitSelectExpression(selectExpression),
                _ => base.VisitExtension(extensionExpression),
            };

        private Expression VisitSelectExpression(SelectExpression selectExpression)
        {
            var newExpression = base.VisitExtension(selectExpression);

            // if predicate is optimized to true, we can simply remove it
            if (newExpression is SelectExpression newSelectExpression)
            {
                var changed = false;
                var newPredicate = newSelectExpression.Predicate;
                var newHaving = newSelectExpression.Having;
                if (newSelectExpression.Predicate is SqlConstantExpression predicateConstantExpression
                    && predicateConstantExpression.Value is bool predicateBoolValue
                    && predicateBoolValue)
                {
                    newPredicate = null;
                    changed = true;
                }

                if (newSelectExpression.Having is SqlConstantExpression havingConstantExpression
                    && havingConstantExpression.Value is bool havingBoolValue
                    && havingBoolValue)
                {
                    newHaving = null;
                    changed = true;
                }

                return changed
                    ? newSelectExpression.Update(
                        newSelectExpression.Projection.ToList(),
                        newSelectExpression.Tables.ToList(),
                        newPredicate,
                        newSelectExpression.GroupBy.ToList(),
                        newHaving,
                        newSelectExpression.Orderings.ToList(),
                        newSelectExpression.Limit,
                        newSelectExpression.Offset,
                        newSelectExpression.IsDistinct,
                        newSelectExpression.Alias)
                    : newSelectExpression;
            }

            return newExpression;
        }

        protected virtual Expression VisitSqlUnaryExpression(SqlUnaryExpression sqlUnaryExpression)
        {
            var newOperand = (SqlExpression)Visit(sqlUnaryExpression.Operand);

            return SimplifyUnaryExpression(
                sqlUnaryExpression.OperatorType,
                newOperand,
                sqlUnaryExpression.Type,
                sqlUnaryExpression.TypeMapping);
        }

        private SqlExpression SimplifyUnaryExpression(
            ExpressionType operatorType,
            SqlExpression operand,
            Type type,
            RelationalTypeMapping typeMapping)
        {
            switch (operatorType)
            {
                case ExpressionType.Not
                    when type == typeof(bool)
                    || type == typeof(bool?):
                {
                    switch (operand)
                    {
                        // !(true) -> false
                        // !(false) -> true
                        case SqlConstantExpression constantOperand
                            when constantOperand.Value is bool value:
                        {
                            return SqlExpressionFactory.Constant(!value, typeMapping);
                        }

                        case InExpression inOperand:
                            return inOperand.Negate();

                        case SqlUnaryExpression unaryOperand:
                            switch (unaryOperand.OperatorType)
                            {
                                // !(!a) -> a
                                case ExpressionType.Not:
                                    return unaryOperand.Operand;

                                //!(a IS NULL) -> a IS NOT NULL
                                case ExpressionType.Equal:
                                    return SqlExpressionFactory.IsNotNull(unaryOperand.Operand);

                                //!(a IS NOT NULL) -> a IS NULL
                                case ExpressionType.NotEqual:
                                    return SqlExpressionFactory.IsNull(unaryOperand.Operand);
                            }

                            break;

                        // these optimizations are only valid in 2-value logic
                        // NullSemantics removes all nulls from expressions wrapped around Not
                        // so the optimizations are safe to do as long as UseRelationalNulls = false
                        case SqlBinaryExpression binaryOperand
                            when !_useRelationalNulls:
                        {
                            // De Morgan's
                            if (binaryOperand.OperatorType == ExpressionType.AndAlso
                                || binaryOperand.OperatorType == ExpressionType.OrElse)
                            {
                                var newLeft = SimplifyUnaryExpression(ExpressionType.Not, binaryOperand.Left, type, typeMapping);
                                var newRight = SimplifyUnaryExpression(ExpressionType.Not, binaryOperand.Right, type, typeMapping);

                                return SimplifyLogicalSqlBinaryExpression(
                                    binaryOperand.OperatorType == ExpressionType.AndAlso
                                        ? ExpressionType.OrElse
                                        : ExpressionType.AndAlso,
                                    newLeft,
                                    newRight,
                                    binaryOperand.TypeMapping);
                            }

                            if (TryNegate(binaryOperand.OperatorType, out var negated))
                            {
                                return SimplifyBinaryExpression(
                                    negated,
                                    binaryOperand.Left,
                                    binaryOperand.Right,
                                    binaryOperand.TypeMapping);
                            }
                        }
                        break;
                    }
                    break;
                }
            }

            return SqlExpressionFactory.MakeUnary(operatorType, operand, type, typeMapping);
        }

        protected virtual Expression VisitSqlBinaryExpression(SqlBinaryExpression sqlBinaryExpression)
        {
            var newLeft = (SqlExpression)Visit(sqlBinaryExpression.Left);
            var newRight = (SqlExpression)Visit(sqlBinaryExpression.Right);

            return SimplifyBinaryExpression(
                sqlBinaryExpression.OperatorType,
                newLeft,
                newRight,
                sqlBinaryExpression.TypeMapping);
        }

        private SqlExpression SimplifyBinaryExpression(
            ExpressionType operatorType,
            SqlExpression left,
            SqlExpression right,
            RelationalTypeMapping typeMapping)
        {
            switch (operatorType)
            {
                case ExpressionType.AndAlso:
                case ExpressionType.OrElse:
                    var leftUnary = left as SqlUnaryExpression;
                    var rightUnary = right as SqlUnaryExpression;
                    if (leftUnary != null
                        && rightUnary != null
                        && (leftUnary.OperatorType == ExpressionType.Equal || leftUnary.OperatorType == ExpressionType.NotEqual)
                        && (rightUnary.OperatorType == ExpressionType.Equal || rightUnary.OperatorType == ExpressionType.NotEqual)
                        && leftUnary.Operand.Equals(rightUnary.Operand))
                    {
                        // a is null || a is null -> a is null
                        // a is not null || a is not null -> a is not null
                        // a is null && a is null -> a is null
                        // a is not null && a is not null -> a is not null
                        // a is null || a is not null -> true
                        // a is null && a is not null -> false
                        return leftUnary.OperatorType == rightUnary.OperatorType
                            ? (SqlExpression)leftUnary
                            : SqlExpressionFactory.Constant(operatorType == ExpressionType.OrElse, typeMapping);
                    }

                    return SimplifyLogicalSqlBinaryExpression(
                        operatorType,
                        left,
                        right,
                        typeMapping);
            }

            return SqlExpressionFactory.MakeBinary(operatorType, left, right, typeMapping);
        }

        private SqlExpression SimplifyLogicalSqlBinaryExpression(
            ExpressionType operatorType,
            SqlExpression left,
            SqlExpression right,
            RelationalTypeMapping typeMapping)
        {
            // true && a -> a
            // true || a -> true
            // false && a -> false
            // false || a -> a
            if (left is SqlConstantExpression newLeftConstant)
            {
                return operatorType == ExpressionType.AndAlso
                    ? (bool)newLeftConstant.Value
                        ? right
                        : newLeftConstant
                    : (bool)newLeftConstant.Value
                        ? newLeftConstant
                        : right;
            }
            else if (right is SqlConstantExpression newRightConstant)
            {
                // a && true -> a
                // a || true -> true
                // a && false -> false
                // a || false -> a
                return operatorType == ExpressionType.AndAlso
                    ? (bool)newRightConstant.Value
                        ? left
                        : newRightConstant
                    : (bool)newRightConstant.Value
                        ? newRightConstant
                        : left;
            }

            return SqlExpressionFactory.MakeBinary(operatorType, left, right, typeMapping);
        }
    }
}
