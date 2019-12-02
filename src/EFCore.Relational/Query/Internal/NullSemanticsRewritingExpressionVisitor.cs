// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace Microsoft.EntityFrameworkCore.Query.Internal
{
    public class NullSemanticsRewritingExpressionVisitor : SqlExpressionVisitor
    {
        private readonly bool _useRelationalNulls;
        private readonly ISqlExpressionFactory _sqlExpressionFactory;
        private readonly List<ColumnExpression> _nonNullableColumns = new List<ColumnExpression>();
        private readonly IReadOnlyDictionary<string, object> _parameterValues;

        private bool _isNullable;
        private bool _canOptimize;

        public virtual bool CanCache { get; set; }

        public NullSemanticsRewritingExpressionVisitor(
            bool useRelationalNulls,
            ISqlExpressionFactory sqlExpressionFactory,
            IReadOnlyDictionary<string, object> parameterValues)
        {
            _useRelationalNulls = useRelationalNulls;
            _sqlExpressionFactory = sqlExpressionFactory;
            _parameterValues = parameterValues;
            _canOptimize = true;
            CanCache = true;
        }

        protected override Expression VisitCase(CaseExpression caseExpression)
        {
            _isNullable = false;
            // if there is no 'else' there is a possibility of null, when none of the conditions are met
            // otherwise the result is nullable if any of the WhenClause results OR ElseResult is nullable
            var isNullable = caseExpression.ElseResult == null;

            var canOptimize = _canOptimize;
            var testIsCondition = caseExpression.Operand == null;
            _canOptimize = false;
            var newOperand = (SqlExpression)Visit(caseExpression.Operand);
            var newWhenClauses = new List<CaseWhenClause>();
            foreach (var whenClause in caseExpression.WhenClauses)
            {
                _canOptimize = testIsCondition;
                var newTest = (SqlExpression)Visit(whenClause.Test);
                _canOptimize = false;
                _isNullable = false;
                var newResult = (SqlExpression)Visit(whenClause.Result);
                isNullable |= _isNullable;
                newWhenClauses.Add(new CaseWhenClause(newTest, newResult));
            }

            _canOptimize = false;
            var newElseResult = (SqlExpression)Visit(caseExpression.ElseResult);
            _isNullable |= isNullable;
            _canOptimize = canOptimize;

            return caseExpression.Update(newOperand, newWhenClauses, newElseResult);
        }

        protected override Expression VisitColumn(ColumnExpression columnExpression)
        {
            _isNullable = !_nonNullableColumns.Contains(columnExpression) && columnExpression.IsNullable;

            return columnExpression;
        }

        protected override Expression VisitCrossApply(CrossApplyExpression crossApplyExpression)
        {
            var canOptimize = _canOptimize;
            _canOptimize = false;
            var table = (TableExpressionBase)Visit(crossApplyExpression.Table);
            _canOptimize = canOptimize;

            return crossApplyExpression.Update(table);
        }

        protected override Expression VisitCrossJoin(CrossJoinExpression crossJoinExpression)
        {
            var canOptimize = _canOptimize;
            _canOptimize = false;
            var table = (TableExpressionBase)Visit(crossJoinExpression.Table);
            _canOptimize = canOptimize;

            return crossJoinExpression.Update(table);
        }

        protected override Expression VisitExcept(ExceptExpression exceptExpression)
        {
            var canOptimize = _canOptimize;
            _canOptimize = false;
            var source1 = (SelectExpression)Visit(exceptExpression.Source1);
            var source2 = (SelectExpression)Visit(exceptExpression.Source2);
            _canOptimize = canOptimize;

            return exceptExpression.Update(source1, source2);
        }

        protected override Expression VisitExists(ExistsExpression existsExpression)
        {
            var canOptimize = _canOptimize;
            _canOptimize = false;
            var newSubquery = (SelectExpression)Visit(existsExpression.Subquery);
            _canOptimize = canOptimize;

            return existsExpression.Update(newSubquery);
        }

        protected override Expression VisitFromSql(FromSqlExpression fromSqlExpression)
            => fromSqlExpression;

        protected override Expression VisitIn(InExpression inExpression)
        {
            var canOptimize = _canOptimize;
            _canOptimize = false;
            _isNullable = false;
            var item = (SqlExpression)Visit(inExpression.Item);
            var itemNullable = _isNullable;
            _isNullable = false;
            var subquery = (SelectExpression)Visit(inExpression.Subquery);
            var subqueryNullable = _isNullable;

            if (inExpression.Values == null)
            {
                _isNullable |= itemNullable;
                _canOptimize = canOptimize;

                return inExpression.Update(item, values: null, subquery);
            }

            _isNullable = false;

            // for relational null semantics just leave as is
            if (_useRelationalNulls)
            {
                var values = (SqlExpression)Visit(inExpression.Values);
                _isNullable = _isNullable || itemNullable || subqueryNullable;
                _canOptimize = canOptimize;

                return inExpression.Update(item, values, subquery);
            }

            // for c# null semantics we need to remove nulls from Values and add IsNull/IsNotNull when necessary
            (var inValues, var hasNullValue) = ProcessInExpressionValues(inExpression.Values);

            _canOptimize = canOptimize;

            // either values array is empty or only contains null
            if (inValues == null)
            {
                _isNullable = false;

                // a IN () -> false
                // non_nullable IN (NULL) -> false
                // a NOT IN () -> true
                // non_nullable NOT IN (NULL) -> true
                // nullable IN (NULL) -> nullable IS NULL
                // nullable NOT IN (NULL) -> nullable IS NOT NULL
                return !hasNullValue || !itemNullable
                    ? (SqlExpression)_sqlExpressionFactory.Constant(
                        inExpression.IsNegated,
                        inExpression.TypeMapping)
                    : inExpression.IsNegated
                        ? _sqlExpressionFactory.IsNotNull(item)
                        : _sqlExpressionFactory.IsNull(item);
            }

            _isNullable = _isNullable || itemNullable || subqueryNullable;

            if (!itemNullable
                || (_canOptimize && !inExpression.IsNegated && !hasNullValue))
            {
                // non_nullable IN (1, 2) -> non_nullable IN (1, 2)
                // non_nullable IN (1, 2, NULL) -> non_nullable IN (1, 2)
                // nullable IN (1, 2) -> nullable IN (1, 2) (optimized)
                return inExpression.Update(item, inValues, subquery);
            }

            // adding null comparison term to remove nulls completely from the resulting expression
            _isNullable = false;

            // nullable IN (1, 2) -> nullable IN (1, 2) AND nullable IS NOT NULL (full)
            // nullable IN (1, 2, NULL) -> nullable IN (1, 2) OR nullable IS NULL (full)
            // nullable NOT IN (1, 2) -> nullable NOT IN (1, 2) OR nullable IS NULL (full)
            // nullable NOT IN (1, 2, NULL) -> nullable NOT IN (1, 2) AND nullable IS NOT NULL (full)
            return inExpression.IsNegated == hasNullValue
                ? _sqlExpressionFactory.AndAlso(
                    inExpression.Update(item, inValues, subquery),
                    _sqlExpressionFactory.IsNotNull(item))
                : _sqlExpressionFactory.OrElse(
                    inExpression.Update(item, inValues, subquery),
                    _sqlExpressionFactory.IsNull(item));
        }

        private (SqlExpression processedValues, bool hasNullValue) ProcessInExpressionValues(SqlExpression valuesExpression)
        {
            if (valuesExpression == null)
            {
                return (processedValues: null, hasNullValue: false);
            }

            var inValues = new List<object>();
            var hasNullValue = false;
            RelationalTypeMapping typeMapping = null;

            switch (valuesExpression)
            {
                case SqlConstantExpression sqlConstant:
                {
                    CanCache = false;
                    typeMapping = sqlConstant.TypeMapping;
                    var values = (IEnumerable)sqlConstant.Value;
                    foreach (var value in values)
                    {
                        if (value == null)
                        {
                            hasNullValue = true;
                            continue;
                        }

                        inValues.Add(value);
                    }

                    break;
                }

                case SqlParameterExpression sqlParameter:
                {
                    CanCache = false;
                    typeMapping = sqlParameter.TypeMapping;
                    var values = (IEnumerable)_parameterValues[sqlParameter.Name];
                    foreach (var value in values)
                    {
                        if (value == null)
                        {
                            hasNullValue = true;
                            continue;
                        }

                        inValues.Add(value);
                    }

                    break;
                }
            }

            var processedValues = inValues.Count > 0
                ? (SqlExpression)Visit(_sqlExpressionFactory.Constant(inValues, typeMapping))
                : null;

            return (processedValues, hasNullValue);
        }

        protected override Expression VisitInnerJoin(InnerJoinExpression innerJoinExpression)
        {
            var canOptimize = _canOptimize;
            _canOptimize = false;
            var newTable = (TableExpressionBase)Visit(innerJoinExpression.Table);
            var newJoinPredicate = VisitJoinPredicate((SqlBinaryExpression)innerJoinExpression.JoinPredicate);
            _canOptimize = canOptimize;

            return innerJoinExpression.Update(newTable, newJoinPredicate);
        }

        protected override Expression VisitIntersect(IntersectExpression intersectExpression)
        {
            var canOptimize = _canOptimize;
            _canOptimize = false;
            var source1 = (SelectExpression)Visit(intersectExpression.Source1);
            var source2 = (SelectExpression)Visit(intersectExpression.Source2);
            _canOptimize = canOptimize;

            return intersectExpression.Update(source1, source2);
        }

        protected override Expression VisitLeftJoin(LeftJoinExpression leftJoinExpression)
        {
            var canOptimize = _canOptimize;
            _canOptimize = false;
            var newTable = (TableExpressionBase)Visit(leftJoinExpression.Table);
            var newJoinPredicate = VisitJoinPredicate((SqlBinaryExpression)leftJoinExpression.JoinPredicate);
            _canOptimize = canOptimize;

            return leftJoinExpression.Update(newTable, newJoinPredicate);
        }

        private SqlExpression VisitJoinPredicate(SqlBinaryExpression predicate)
        {
            var canOptimize = _canOptimize;
            _canOptimize = true;

            if (predicate.OperatorType == ExpressionType.Equal)
            {
                _isNullable = false;
                var left = (SqlExpression)Visit(predicate.Left);
                var leftNullable = _isNullable;
                _isNullable = false;
                var right = (SqlExpression)Visit(predicate.Right);
                var rightNullable = _isNullable;

                var result = OptimizeComparison(
                    predicate.Update(left, right),
                    left,
                    right,
                    leftNullable,
                    rightNullable,
                    _canOptimize);

                _canOptimize = canOptimize;

                return result;
            }

            if (predicate.OperatorType == ExpressionType.AndAlso)
            {
                var newPredicate = (SqlExpression)VisitSqlBinary(predicate);
                _canOptimize = canOptimize;

                return newPredicate;
            }

            throw new InvalidOperationException("Unexpected join predicate shape: " + predicate);
        }

        protected override Expression VisitLike(LikeExpression likeExpression)
        {
            var canOptimize = _canOptimize;
            _canOptimize = false;
            _isNullable = false;
            var newMatch = (SqlExpression)Visit(likeExpression.Match);
            var isNullable = _isNullable;
            _isNullable = false;
            var newPattern = (SqlExpression)Visit(likeExpression.Pattern);
            isNullable |= _isNullable;
            _isNullable = false;
            var newEscapeChar = (SqlExpression)Visit(likeExpression.EscapeChar);
            _isNullable |= isNullable;
            _canOptimize = canOptimize;

            return likeExpression.Update(newMatch, newPattern, newEscapeChar);
        }

        protected override Expression VisitOrdering(OrderingExpression orderingExpression)
        {
            var expression = (SqlExpression)Visit(orderingExpression.Expression);

            return orderingExpression.Update(expression);
        }

        protected override Expression VisitOuterApply(OuterApplyExpression outerApplyExpression)
        {
            var canOptimize = _canOptimize;
            _canOptimize = false;
            var table = (TableExpressionBase)Visit(outerApplyExpression.Table);
            _canOptimize = canOptimize;

            return outerApplyExpression.Update(table);
        }

        protected override Expression VisitProjection(ProjectionExpression projectionExpression)
        {
            var expression = (SqlExpression)Visit(projectionExpression.Expression);

            return projectionExpression.Update(expression);
        }

        protected override Expression VisitRowNumber(RowNumberExpression rowNumberExpression)
        {
            var canOptimize = _canOptimize;
            _canOptimize = false;
            var changed = false;
            var partitions = new List<SqlExpression>();
            foreach (var partition in rowNumberExpression.Partitions)
            {
                var newPartition = (SqlExpression)Visit(partition);
                changed |= newPartition != partition;
                partitions.Add(newPartition);
            }

            var orderings = new List<OrderingExpression>();
            foreach (var ordering in rowNumberExpression.Orderings)
            {
                var newOrdering = (OrderingExpression)Visit(ordering);
                changed |= newOrdering != ordering;
                orderings.Add(newOrdering);
            }

            _canOptimize = canOptimize;

            return rowNumberExpression.Update(partitions, orderings);
        }

        protected override Expression VisitScalarSubquery(ScalarSubqueryExpression scalarSubqueryExpression)
        {
            var canOptimize = _canOptimize;
            _canOptimize = false;
            var subquery = (SelectExpression)Visit(scalarSubqueryExpression.Subquery);
            _canOptimize = canOptimize;

            return scalarSubqueryExpression.Update(subquery);
        }

        protected override Expression VisitSelect(SelectExpression selectExpression)
        {
            var changed = false;
            var canOptimize = _canOptimize;
            var projections = new List<ProjectionExpression>();
            _canOptimize = false;
            foreach (var item in selectExpression.Projection)
            {
                var updatedProjection = (ProjectionExpression)Visit(item);
                projections.Add(updatedProjection);
                changed |= updatedProjection != item;
            }

            var tables = new List<TableExpressionBase>();
            foreach (var table in selectExpression.Tables)
            {
                var newTable = (TableExpressionBase)Visit(table);
                changed |= newTable != table;
                tables.Add(newTable);
            }

            _canOptimize = true;
            var predicate = (SqlExpression)Visit(selectExpression.Predicate);
            changed |= predicate != selectExpression.Predicate;

            var groupBy = new List<SqlExpression>();
            _canOptimize = false;
            foreach (var groupingKey in selectExpression.GroupBy)
            {
                var newGroupingKey = (SqlExpression)Visit(groupingKey);
                changed |= newGroupingKey != groupingKey;
                groupBy.Add(newGroupingKey);
            }

            _canOptimize = true;
            var havingExpression = (SqlExpression)Visit(selectExpression.Having);
            changed |= havingExpression != selectExpression.Having;

            var orderings = new List<OrderingExpression>();
            _canOptimize = false;
            foreach (var ordering in selectExpression.Orderings)
            {
                var orderingExpression = (SqlExpression)Visit(ordering.Expression);
                changed |= orderingExpression != ordering.Expression;
                orderings.Add(ordering.Update(orderingExpression));
            }

            var offset = (SqlExpression)Visit(selectExpression.Offset);
            changed |= offset != selectExpression.Offset;

            var limit = (SqlExpression)Visit(selectExpression.Limit);
            changed |= limit != selectExpression.Limit;

            _canOptimize = canOptimize;

            // we assume SelectExpression can always be null
            // (e.g. projecting non-nullable column but with predicate that filters out all rows)
            _isNullable = true;

            return changed
                ? selectExpression.Update(
                    projections, tables, predicate, groupBy, havingExpression, orderings, limit, offset, selectExpression.IsDistinct,
                    selectExpression.Alias)
                : selectExpression;
        }

        protected override Expression VisitSqlBinary(SqlBinaryExpression sqlBinaryExpression)
        {
            _isNullable = false;
            var canOptimize = _canOptimize;

            _canOptimize = _canOptimize && (sqlBinaryExpression.OperatorType == ExpressionType.AndAlso
                || sqlBinaryExpression.OperatorType == ExpressionType.OrElse);

            var nonNullableColumns = new List<ColumnExpression>();
            if (sqlBinaryExpression.OperatorType == ExpressionType.AndAlso)
            {
                nonNullableColumns = FindNonNullableColumns(sqlBinaryExpression.Left);
            }

            var newLeft = (SqlExpression)Visit(sqlBinaryExpression.Left);
            var leftNullable = _isNullable;

            _isNullable = false;
            if (nonNullableColumns.Count > 0)
            {
                _nonNullableColumns.AddRange(nonNullableColumns);
            }

            var newRight = (SqlExpression)Visit(sqlBinaryExpression.Right);
            var rightNullable = _isNullable;

            foreach (var nonNullableColumn in nonNullableColumns)
            {
                _nonNullableColumns.Remove(nonNullableColumn);
            }

            if (sqlBinaryExpression.OperatorType == ExpressionType.Coalesce)
            {
                _isNullable = leftNullable && rightNullable;
                _canOptimize = canOptimize;

                return sqlBinaryExpression.Update(newLeft, newRight);
            }

            if (sqlBinaryExpression.OperatorType == ExpressionType.Equal
                || sqlBinaryExpression.OperatorType == ExpressionType.NotEqual)
            {
                var updated = sqlBinaryExpression.Update(newLeft, newRight);

                var optimized = OptimizeComparison(
                    updated,
                    newLeft,
                    newRight,
                    leftNullable,
                    rightNullable,
                    canOptimize);

                // we assume that NullSemantics rewrite is only needed (on the current level)
                // if the optimization didn't make any changes.
                // Reason is that optimization can/will change the nullability of the resulting expression
                // and that inforation is not tracked/stored anywhere
                // so we can no longer rely on nullabilities that we computed earlier (leftNullable, rightNullable)
                // when performing null semantics rewrite.
                // It should be fine because current optimizations *radically* change the expression
                // (e.g. binary -> unary, or binary -> constant)
                // but we need to pay attention in the future if we introduce more subtle transformations here
                if (optimized == updated
                    && (leftNullable || rightNullable)
                    && !_useRelationalNulls)
                {
                    var result = RewriteNullSemantics(
                        updated,
                        updated.Left,
                        updated.Right,
                        leftNullable,
                        rightNullable,
                        canOptimize);

                    _canOptimize = canOptimize;

                    return result;
                }

                _canOptimize = canOptimize;

                return optimized;
            }

            _isNullable = leftNullable || rightNullable;
            _canOptimize = canOptimize;

            return sqlBinaryExpression.Update(newLeft, newRight);
        }

        private SqlExpression OptimizeComparison(
            SqlBinaryExpression sqlBinaryExpression,
            SqlExpression left,
            SqlExpression right,
            bool leftNullable,
            bool rightNullable,
            bool canOptimize)
        {
            var leftNullValue = leftNullable && (left is SqlConstantExpression || left is SqlParameterExpression);
            var rightNullValue = rightNullable && (right is SqlConstantExpression || right is SqlParameterExpression);

            // a == null -> a IS NULL
            // a != null -> a IS NOT NULL
            if (rightNullValue)
            {
                var result = sqlBinaryExpression.OperatorType == ExpressionType.Equal
                    ? ProcessNullNotNull(_sqlExpressionFactory.IsNull(left), left, leftNullable)
                    : ProcessNullNotNull(_sqlExpressionFactory.IsNotNull(left), left, leftNullable);

                _isNullable = false;
                _canOptimize = canOptimize;

                return result;
            }

            // null == a -> a IS NULL
            // null != a -> a IS NOT NULL
            if (leftNullValue)
            {
                var result = sqlBinaryExpression.OperatorType == ExpressionType.Equal
                    ? ProcessNullNotNull(_sqlExpressionFactory.IsNull(right), right, rightNullable)
                    : ProcessNullNotNull(_sqlExpressionFactory.IsNotNull(right), right, rightNullable);

                _isNullable = false;
                _canOptimize = canOptimize;

                return result;
            }

            if (IsTrueOrFalse(right) is bool rightTrueFalseValue
                && !leftNullable)
            {
                _isNullable = leftNullable;
                _canOptimize = canOptimize;

                // only correct in 2-value logic
                // a == true -> a
                // a == false -> !a
                // a != true -> !a
                // a != false -> a
                return sqlBinaryExpression.OperatorType == ExpressionType.Equal
                    ? rightTrueFalseValue
                        ? left
                        : _sqlExpressionFactory.Not(left)
                    : rightTrueFalseValue
                        ? _sqlExpressionFactory.Not(left)
                        : left;
            }

            if (IsTrueOrFalse(left) is bool leftTrueFalseValue
                && !rightNullable)
            {
                _isNullable = rightNullable;
                _canOptimize = canOptimize;

                // only correct in 2-value logic
                // true == a -> a
                // false == a -> !a
                // true != a -> !a
                // false != a -> a
                return sqlBinaryExpression.OperatorType == ExpressionType.Equal
                    ? leftTrueFalseValue
                        ? right
                        : _sqlExpressionFactory.Not(right)
                    : leftTrueFalseValue
                        ? _sqlExpressionFactory.Not(right)
                        : right;
            }

            // only correct in 2-value logic
            // a == a -> true
            // a != a -> false
            if (!leftNullable
                && left.Equals(right))
            {
                _isNullable = false;
                _canOptimize = canOptimize;

                return _sqlExpressionFactory.Constant(
                    sqlBinaryExpression.OperatorType == ExpressionType.Equal,
                    sqlBinaryExpression.TypeMapping);
            }

            if (!leftNullable
                && !rightNullable
                && (sqlBinaryExpression.OperatorType == ExpressionType.Equal || sqlBinaryExpression.OperatorType == ExpressionType.NotEqual))
            {
                var leftUnary = left as SqlUnaryExpression;
                var rightUnary = right as SqlUnaryExpression;

                var leftNegated = leftUnary?.IsLogicalNot() == true;
                var rightNegated = rightUnary?.IsLogicalNot() == true;

                if (leftNegated)
                {
                    left = leftUnary.Operand;
                }

                if (rightNegated)
                {
                    right = rightUnary.Operand;
                }

                // a == b <=> !a == !b -> a == b
                // !a == b <=> a == !b -> a != b
                // a != b <=> !a != !b -> a != b
                // !a != b <=> a != !b -> a == b
                return sqlBinaryExpression.OperatorType == ExpressionType.Equal
                    ? leftNegated == rightNegated
                        ? _sqlExpressionFactory.Equal(left, right)
                        : _sqlExpressionFactory.NotEqual(left, right)
                    : leftNegated == rightNegated
                        ? _sqlExpressionFactory.NotEqual(left, right)
                        : _sqlExpressionFactory.Equal(left, right);
            }

            return sqlBinaryExpression.Update(left, right);

            bool? IsTrueOrFalse(SqlExpression sqlExpression)
            {
                if (sqlExpression is SqlConstantExpression sqlConstantExpression && sqlConstantExpression.Value is bool boolConstant)
                {
                    return boolConstant;
                }

                // TODO: should we do this for parameters also?
                //if (sqlExpression is SqlParameterExpression sqlParameterExpression && _parameterValues[sqlParameterExpression.Name] is bool boolParameter)
                //{
                //    return boolParameter;
                //}

                return null;
            }
        }

        private SqlExpression RewriteNullSemantics(
            SqlBinaryExpression sqlBinaryExpression,
            SqlExpression left,
            SqlExpression right,
            bool leftNullable,
            bool rightNullable,
            bool canOptimize)
        {
            var leftUnary = left as SqlUnaryExpression;
            var rightUnary = right as SqlUnaryExpression;

            var leftNegated = leftUnary?.IsLogicalNot() == true;
            var rightNegated = rightUnary?.IsLogicalNot() == true;

            if (leftNegated)
            {
                left = leftUnary.Operand;
            }

            if (rightNegated)
            {
                right = rightUnary.Operand;
            }

            var leftIsNull = ProcessNullNotNull(_sqlExpressionFactory.IsNull(left), left, leftNullable);
            var rightIsNull = ProcessNullNotNull(_sqlExpressionFactory.IsNull(right), right, rightNullable);

            // optimized expansion which doesn't distinguish between null and false
            if (canOptimize
                && sqlBinaryExpression.OperatorType == ExpressionType.Equal
                && !leftNegated
                && !rightNegated)
            {
                // when we use optimized form, the result can still be nullable
                if (leftNullable && rightNullable)
                {
                    _isNullable = true;
                    _canOptimize = canOptimize;

                    return _sqlExpressionFactory.OrElse(
                        _sqlExpressionFactory.Equal(left, right),
                        _sqlExpressionFactory.AndAlso(leftIsNull, rightIsNull));
                }

                if ((leftNullable && !rightNullable)
                    || (!leftNullable && rightNullable))
                {
                    _isNullable = true;
                    _canOptimize = canOptimize;

                    return _sqlExpressionFactory.Equal(left, right);
                }
            }

            // doing a full null semantics rewrite - removing all nulls from truth table
            _isNullable = false;
            _canOptimize = canOptimize;

            if (sqlBinaryExpression.OperatorType == ExpressionType.Equal)
            {
                if (leftNullable && rightNullable)
                {
                    // ?a == ?b <=> !(?a) == !(?b) -> [(a == b) && (a != null && b != null)] || (a == null && b == null))
                    // !(?a) == ?b <=> ?a == !(?b) -> [(a != b) && (a != null && b != null)] || (a == null && b == null)
                    return leftNegated == rightNegated
                        ? ExpandNullableEqualNullable(left, right, leftIsNull, rightIsNull)
                        : ExpandNegatedNullableEqualNullable(left, right, leftIsNull, rightIsNull);
                }

                if (leftNullable && !rightNullable)
                {
                    // ?a == b <=> !(?a) == !b -> (a == b) && (a != null)
                    // !(?a) == b <=> ?a == !b -> (a != b) && (a != null)
                    return leftNegated == rightNegated
                        ? ExpandNullableEqualNonNullable(left, right, leftIsNull)
                        : ExpandNegatedNullableEqualNonNullable(left, right, leftIsNull);
                }

                if (rightNullable && !leftNullable)
                {
                    // a == ?b <=> !a == !(?b) -> (a == b) && (b != null)
                    // !a == ?b <=> a == !(?b) -> (a != b) && (b != null)
                    return leftNegated == rightNegated
                        ? ExpandNullableEqualNonNullable(left, right, rightIsNull)
                        : ExpandNegatedNullableEqualNonNullable(left, right, rightIsNull);
                }
            }

            if (sqlBinaryExpression.OperatorType == ExpressionType.NotEqual)
            {
                if (leftNullable && rightNullable)
                {
                    // ?a != ?b <=> !(?a) != !(?b) -> [(a != b) || (a == null || b == null)] && (a != null || b != null)
                    // !(?a) != ?b <=> ?a != !(?b) -> [(a == b) || (a == null || b == null)] && (a != null || b != null)
                    return leftNegated == rightNegated
                        ? ExpandNullableNotEqualNullable(left, right, leftIsNull, rightIsNull)
                        : ExpandNegatedNullableNotEqualNullable(left, right, leftIsNull, rightIsNull);
                }

                if (leftNullable)
                {
                    // ?a != b <=> !(?a) != !b -> (a != b) || (a == null)
                    // !(?a) != b <=> ?a != !b -> (a == b) || (a == null)
                    return leftNegated == rightNegated
                        ? ExpandNullableNotEqualNonNullable(left, right, leftIsNull)
                        : ExpandNegatedNullableNotEqualNonNullable(left, right, leftIsNull);
                }

                if (rightNullable)
                {
                    // a != ?b <=> !a != !(?b) -> (a != b) || (b == null)
                    // !a != ?b <=> a != !(?b) -> (a == b) || (b == null)
                    return leftNegated == rightNegated
                        ? ExpandNullableNotEqualNonNullable(left, right, rightIsNull)
                        : ExpandNegatedNullableNotEqualNonNullable(left, right, rightIsNull);
                }
            }

            return sqlBinaryExpression.Update(left, right);
        }

        protected override Expression VisitSqlConstant(SqlConstantExpression sqlConstantExpression)
        {
            _isNullable = sqlConstantExpression.Value == null;

            return sqlConstantExpression;
        }

        protected override Expression VisitSqlFragment(SqlFragmentExpression sqlFragmentExpression)
            => sqlFragmentExpression;

        protected override Expression VisitSqlFunction(SqlFunctionExpression sqlFunctionExpression)
        {
            var canOptimize = _canOptimize;
            _canOptimize = false;

            var newInstance = (SqlExpression)Visit(sqlFunctionExpression.Instance);
            var newArguments = new SqlExpression[sqlFunctionExpression.Arguments.Count];
            for (var i = 0; i < newArguments.Length; i++)
            {
                newArguments[i] = (SqlExpression)Visit(sqlFunctionExpression.Arguments[i]);
            }

            _canOptimize = canOptimize;

            // TODO: #18555
            _isNullable = true;

            return sqlFunctionExpression.Update(newInstance, newArguments);
        }

        protected override Expression VisitSqlParameter(SqlParameterExpression sqlParameterExpression)
        {
            _isNullable = _parameterValues[sqlParameterExpression.Name] == null;

            return sqlParameterExpression;
        }

        protected override Expression VisitSqlUnary(SqlUnaryExpression sqlUnaryExpression)
        {
            _isNullable = false;
            var canOptimize = _canOptimize;
            _canOptimize = false;

            var operand = (SqlExpression)Visit(sqlUnaryExpression.Operand);

            _canOptimize = canOptimize;

            if (sqlUnaryExpression.OperatorType == ExpressionType.Equal
                || sqlUnaryExpression.OperatorType == ExpressionType.NotEqual)
            {
                // result of IsNull/IsNotNull can never be null
                var isNullable = _isNullable;
                _isNullable = false;

                return ProcessNullNotNull(sqlUnaryExpression, operand, isNullable);
            }

            if (operand is SqlBinaryExpression sqlBinaryOperand)
            {
                // only correct in 2-value logic
                // !(a == b) -> a != b
                // !(a != b) -> a == b
                // !(a > b) -> a <= b
                // !(a >= b) -> a < b
                // !(a < b) -> a >= b
                // !(a <= b) -> a > b
                if (!_isNullable
                    && TryNegate(sqlBinaryOperand.OperatorType, out var negated))
                {
                    return _sqlExpressionFactory.MakeBinary(
                        negated,
                        sqlBinaryOperand.Left,
                        sqlBinaryOperand.Right,
                        sqlBinaryOperand.TypeMapping);
                }
            }

            return sqlUnaryExpression.Update(operand);

            static bool TryNegate(ExpressionType expressionType, out ExpressionType result)
            {
                var negated = expressionType switch
                {
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
        }

        private SqlExpression ProcessNullNotNull(
            SqlUnaryExpression sqlUnaryExpression,
            SqlExpression operand,
            bool? operandNullable)
        {
            if (operandNullable == false)
            {
                // when we know that operand is non-nullable:
                // not_null_operand is null-> false
                // not_null_operand is not null -> true
                return _sqlExpressionFactory.Constant(
                    sqlUnaryExpression.OperatorType == ExpressionType.NotEqual,
                    sqlUnaryExpression.TypeMapping);
            }

            switch (operand)
            {
                case SqlConstantExpression sqlConstantOperand:
                    // null_value_constant is null -> true
                    // null_value_constant is not null -> false
                    // not_null_value_constant is null -> false
                    // not_null_value_constant is not null -> true
                    return sqlConstantOperand.Value == null
                        ? _sqlExpressionFactory.Constant(
                            sqlUnaryExpression.OperatorType == ExpressionType.Equal,
                            sqlUnaryExpression.TypeMapping)
                        : _sqlExpressionFactory.Constant(
                            sqlUnaryExpression.OperatorType == ExpressionType.NotEqual,
                            sqlUnaryExpression.TypeMapping);

                case SqlParameterExpression sqlParameterOperand:
                    // null_value_parameter is null -> true
                    // null_value_parameter is not null -> false
                    // not_null_value_parameter is null -> false
                    // not_null_value_parameter is not null -> true
                    return _parameterValues[sqlParameterOperand.Name] == null
                        ? _sqlExpressionFactory.Constant(
                            sqlUnaryExpression.OperatorType == ExpressionType.Equal,
                            sqlUnaryExpression.TypeMapping)
                        : _sqlExpressionFactory.Constant(
                            sqlUnaryExpression.OperatorType == ExpressionType.NotEqual,
                            sqlUnaryExpression.TypeMapping);

                case ColumnExpression columnOperand
                    when !columnOperand.IsNullable || _nonNullableColumns.Contains(columnOperand):
                {
                    // IsNull(non_nullable_column) -> false
                    // IsNotNull(non_nullable_column) -> true
                    return _sqlExpressionFactory.Constant(
                        sqlUnaryExpression.OperatorType == ExpressionType.NotEqual,
                        sqlUnaryExpression.TypeMapping);
                }

                case SqlUnaryExpression sqlUnaryOperand:
                    switch (sqlUnaryOperand.OperatorType)
                    {
                        case ExpressionType.Convert:
                        case ExpressionType.Not:
                        case ExpressionType.Negate:
                            // op(a) is null -> a is null
                            // op(a) is not null -> a is not null
                            return ProcessNullNotNull(
                                _sqlExpressionFactory.MakeUnary(
                                    sqlUnaryExpression.OperatorType,
                                    sqlUnaryOperand.Operand,
                                    sqlUnaryExpression.Type,
                                    sqlUnaryExpression.TypeMapping),
                                sqlUnaryOperand.Operand,
                                operandNullable);

                        case ExpressionType.Equal:
                        case ExpressionType.NotEqual:
                            // (a is null) is null -> false
                            // (a is not null) is null -> false
                            // (a is null) is not null -> true
                            // (a is not null) is not null -> true
                            return _sqlExpressionFactory.Constant(
                                sqlUnaryOperand.OperatorType == ExpressionType.NotEqual,
                                sqlUnaryOperand.TypeMapping);
                    }
                    break;

                case SqlBinaryExpression sqlBinaryOperand
                    when sqlBinaryOperand.OperatorType != ExpressionType.AndAlso
                        && sqlBinaryOperand.OperatorType != ExpressionType.OrElse:
                {
                    // in general:
                    // binaryOp(a, b) == null -> a == null || b == null
                    // binaryOp(a, b) != null -> a != null && b != null
                    // for coalesce:
                    // (a ?? b) == null -> a == null && b == null
                    // (a ?? b) != null -> a != null || b != null
                    // for AndAlso, OrElse we can't do this optimization
                    // we could do something like this, but it seems too complicated:
                    // (a && b) == null -> a == null && b != 0 || a != 0 && b == null
                    // NOTE: we don't preserve nullabilities of left/right individually so we are using nullability binary expression as a whole
                    // this may lead to missing some optimizations, where one of the operands (left or right) is not nullable and the other one is
                    var left = ProcessNullNotNull(
                        _sqlExpressionFactory.MakeUnary(
                            sqlUnaryExpression.OperatorType,
                            sqlBinaryOperand.Left,
                            typeof(bool),
                            sqlUnaryExpression.TypeMapping),
                        sqlBinaryOperand.Left,
                        operandNullable: null);

                    var right = ProcessNullNotNull(
                        _sqlExpressionFactory.MakeUnary(
                            sqlUnaryExpression.OperatorType,
                            sqlBinaryOperand.Right,
                            typeof(bool),
                            sqlUnaryExpression.TypeMapping),
                        sqlBinaryOperand.Right,
                        operandNullable: null);

                    return sqlBinaryOperand.OperatorType == ExpressionType.Coalesce
                        ? _sqlExpressionFactory.MakeBinary(
                            sqlUnaryExpression.OperatorType == ExpressionType.Equal
                                ? ExpressionType.AndAlso
                                : ExpressionType.OrElse,
                            left,
                            right,
                            sqlUnaryExpression.TypeMapping)
                        : _sqlExpressionFactory.MakeBinary(
                            sqlUnaryExpression.OperatorType == ExpressionType.Equal
                                ? ExpressionType.OrElse
                                : ExpressionType.AndAlso,
                            left,
                            right,
                            sqlUnaryExpression.TypeMapping);
                }
            }

            return sqlUnaryExpression.Update(operand);
        }

        protected override Expression VisitTable(TableExpression tableExpression)
            => tableExpression;

        protected override Expression VisitUnion(UnionExpression unionExpression)
        {
            var canOptimize = _canOptimize;
            _canOptimize = false;
            var source1 = (SelectExpression)Visit(unionExpression.Source1);
            var source2 = (SelectExpression)Visit(unionExpression.Source2);
            _canOptimize = canOptimize;

            return unionExpression.Update(source1, source2);
        }

        private List<ColumnExpression> FindNonNullableColumns(SqlExpression sqlExpression)
        {
            var result = new List<ColumnExpression>();
            if (sqlExpression is SqlBinaryExpression sqlBinaryExpression)
            {
                if (sqlBinaryExpression.OperatorType == ExpressionType.NotEqual)
                {
                    if (sqlBinaryExpression.Left is ColumnExpression leftColumn
                        && leftColumn.IsNullable
                        && sqlBinaryExpression.Right is SqlConstantExpression rightConstant
                        && rightConstant.Value == null)
                    {
                        result.Add(leftColumn);
                    }

                    if (sqlBinaryExpression.Right is ColumnExpression rightColumn
                        && rightColumn.IsNullable
                        && sqlBinaryExpression.Left is SqlConstantExpression leftConstant
                        && leftConstant.Value == null)
                    {
                        result.Add(rightColumn);
                    }
                }

                if (sqlBinaryExpression.OperatorType == ExpressionType.AndAlso)
                {
                    result.AddRange(FindNonNullableColumns(sqlBinaryExpression.Left));
                    result.AddRange(FindNonNullableColumns(sqlBinaryExpression.Right));
                }
            }

            return result;
        }

        // ?a == ?b -> [(a == b) && (a != null && b != null)] || (a == null && b == null))
        //
        // a | b | F1 = a == b | F2 = (a != null && b != null) | F3 = F1 && F2 |
        //   |   |             |                               |               |
        // 0 | 0 | 1           | 1                             | 1             |
        // 0 | 1 | 0           | 1                             | 0             |
        // 0 | N | N           | 0                             | 0             |
        // 1 | 0 | 0           | 1                             | 0             |
        // 1 | 1 | 1           | 1                             | 1             |
        // 1 | N | N           | 0                             | 0             |
        // N | 0 | N           | 0                             | 0             |
        // N | 1 | N           | 0                             | 0             |
        // N | N | N           | 0                             | 0             |
        //
        // a | b | F4 = (a == null && b == null) | Final = F3 OR F4 |
        //   |   |                               |                  |
        // 0 | 0 | 0                             | 1 OR 0 = 1       |
        // 0 | 1 | 0                             | 0 OR 0 = 0       |
        // 0 | N | 0                             | 0 OR 0 = 0       |
        // 1 | 0 | 0                             | 0 OR 0 = 0       |
        // 1 | 1 | 0                             | 1 OR 0 = 1       |
        // 1 | N | 0                             | 0 OR 0 = 0       |
        // N | 0 | 0                             | 0 OR 0 = 0       |
        // N | 1 | 0                             | 0 OR 0 = 0       |
        // N | N | 1                             | 0 OR 1 = 1       |
        private SqlBinaryExpression ExpandNullableEqualNullable(
            SqlExpression left, SqlExpression right, SqlExpression leftIsNull, SqlExpression rightIsNull)
            => _sqlExpressionFactory.OrElse(
                _sqlExpressionFactory.AndAlso(
                    _sqlExpressionFactory.Equal(left, right),
                    _sqlExpressionFactory.AndAlso(
                        _sqlExpressionFactory.Not(leftIsNull),
                        _sqlExpressionFactory.Not(rightIsNull))),
                _sqlExpressionFactory.AndAlso(
                    leftIsNull,
                    rightIsNull));

        // !(?a) == ?b -> [(a != b) && (a != null && b != null)] || (a == null && b == null)
        //
        // a | b | F1 = a != b | F2 = (a != null && b != null) | F3 = F1 && F2 |
        //   |   |             |                               |               |
        // 0 | 0 | 0           | 1                             | 0             |
        // 0 | 1 | 1           | 1                             | 1             |
        // 0 | N | N           | 0                             | 0             |
        // 1 | 0 | 1           | 1                             | 1             |
        // 1 | 1 | 0           | 1                             | 0             |
        // 1 | N | N           | 0                             | 0             |
        // N | 0 | N           | 0                             | 0             |
        // N | 1 | N           | 0                             | 0             |
        // N | N | N           | 0                             | 0             |
        //
        // a | b | F4 = (a == null && b == null) | Final = F3 OR F4 |
        //   |   |                               |                  |
        // 0 | 0 | 0                             | 0 OR 0 = 0       |
        // 0 | 1 | 0                             | 1 OR 0 = 1       |
        // 0 | N | 0                             | 0 OR 0 = 0       |
        // 1 | 0 | 0                             | 1 OR 0 = 1       |
        // 1 | 1 | 0                             | 0 OR 0 = 0       |
        // 1 | N | 0                             | 0 OR 0 = 0       |
        // N | 0 | 0                             | 0 OR 0 = 0       |
        // N | 1 | 0                             | 0 OR 0 = 0       |
        // N | N | 1                             | 0 OR 1 = 1       |
        private SqlBinaryExpression ExpandNegatedNullableEqualNullable(
            SqlExpression left, SqlExpression right, SqlExpression leftIsNull, SqlExpression rightIsNull)
            => _sqlExpressionFactory.OrElse(
                _sqlExpressionFactory.AndAlso(
                    _sqlExpressionFactory.NotEqual(left, right),
                    _sqlExpressionFactory.AndAlso(
                        _sqlExpressionFactory.Not(leftIsNull),
                        _sqlExpressionFactory.Not(rightIsNull))),
                _sqlExpressionFactory.AndAlso(
                    leftIsNull,
                    rightIsNull));

        // ?a == b -> (a == b) && (a != null)
        //
        // a | b | F1 = a == b | F2 = (a != null) | Final = F1 && F2 |
        //   |   |             |                  |                  |
        // 0 | 0 | 1           | 1                | 1                |
        // 0 | 1 | 0           | 1                | 0                |
        // 1 | 0 | 0           | 1                | 0                |
        // 1 | 1 | 1           | 1                | 1                |
        // N | 0 | N           | 0                | 0                |
        // N | 1 | N           | 0                | 0                |
        private SqlBinaryExpression ExpandNullableEqualNonNullable(
            SqlExpression left, SqlExpression right, SqlExpression leftIsNull)
            => _sqlExpressionFactory.AndAlso(
                _sqlExpressionFactory.Equal(left, right),
                _sqlExpressionFactory.Not(leftIsNull));

        // !(?a) == b -> (a != b) && (a != null)
        //
        // a | b | F1 = a != b | F2 = (a != null) | Final = F1 && F2 |
        //   |   |             |                  |                  |
        // 0 | 0 | 0           | 1                | 0                |
        // 0 | 1 | 1           | 1                | 1                |
        // 1 | 0 | 1           | 1                | 1                |
        // 1 | 1 | 0           | 1                | 0                |
        // N | 0 | N           | 0                | 0                |
        // N | 1 | N           | 0                | 0                |
        private SqlBinaryExpression ExpandNegatedNullableEqualNonNullable(
            SqlExpression left, SqlExpression right, SqlExpression leftIsNull)
            => _sqlExpressionFactory.AndAlso(
                _sqlExpressionFactory.NotEqual(left, right),
                _sqlExpressionFactory.Not(leftIsNull));

        // ?a != ?b -> [(a != b) || (a == null || b == null)] && (a != null || b != null)
        //
        // a | b | F1 = a != b | F2 = (a == null || b == null) | F3 = F1 || F2 |
        //   |   |             |                               |               |
        // 0 | 0 | 0           | 0                             | 0             |
        // 0 | 1 | 1           | 0                             | 1             |
        // 0 | N | N           | 1                             | 1             |
        // 1 | 0 | 1           | 0                             | 1             |
        // 1 | 1 | 0           | 0                             | 0             |
        // 1 | N | N           | 1                             | 1             |
        // N | 0 | N           | 1                             | 1             |
        // N | 1 | N           | 1                             | 1             |
        // N | N | N           | 1                             | 1             |
        //
        // a | b | F4 = (a != null || b != null) | Final = F3 && F4 |
        //   |   |                               |                  |
        // 0 | 0 | 1                             | 0 && 1 = 0       |
        // 0 | 1 | 1                             | 1 && 1 = 1       |
        // 0 | N | 1                             | 1 && 1 = 1       |
        // 1 | 0 | 1                             | 1 && 1 = 1       |
        // 1 | 1 | 1                             | 0 && 1 = 0       |
        // 1 | N | 1                             | 1 && 1 = 1       |
        // N | 0 | 1                             | 1 && 1 = 1       |
        // N | 1 | 1                             | 1 && 1 = 1       |
        // N | N | 0                             | 1 && 0 = 0       |
        private SqlBinaryExpression ExpandNullableNotEqualNullable(
            SqlExpression left, SqlExpression right, SqlExpression leftIsNull, SqlExpression rightIsNull)
            => _sqlExpressionFactory.AndAlso(
                _sqlExpressionFactory.OrElse(
                    _sqlExpressionFactory.NotEqual(left, right),
                    _sqlExpressionFactory.OrElse(
                        leftIsNull,
                        rightIsNull)),
                _sqlExpressionFactory.OrElse(
                    _sqlExpressionFactory.Not(leftIsNull),
                    _sqlExpressionFactory.Not(rightIsNull)));

        // !(?a) != ?b -> [(a == b) || (a == null || b == null)] && (a != null || b != null)
        //
        // a | b | F1 = a == b | F2 = (a == null || b == null) | F3 = F1 || F2 |
        //   |   |             |                               |               |
        // 0 | 0 | 1           | 0                             | 1             |
        // 0 | 1 | 0           | 0                             | 0             |
        // 0 | N | N           | 1                             | 1             |
        // 1 | 0 | 0           | 0                             | 0             |
        // 1 | 1 | 1           | 0                             | 1             |
        // 1 | N | N           | 1                             | 1             |
        // N | 0 | N           | 1                             | 1             |
        // N | 1 | N           | 1                             | 1             |
        // N | N | N           | 1                             | 1             |
        //
        // a | b | F4 = (a != null || b != null) | Final = F3 && F4 |
        //   |   |                               |                  |
        // 0 | 0 | 1                             | 1 && 1 = 1       |
        // 0 | 1 | 1                             | 0 && 1 = 0       |
        // 0 | N | 1                             | 1 && 1 = 1       |
        // 1 | 0 | 1                             | 0 && 1 = 0       |
        // 1 | 1 | 1                             | 1 && 1 = 1       |
        // 1 | N | 1                             | 1 && 1 = 1       |
        // N | 0 | 1                             | 1 && 1 = 1       |
        // N | 1 | 1                             | 1 && 1 = 1       |
        // N | N | 0                             | 1 && 0 = 0       |
        private SqlBinaryExpression ExpandNegatedNullableNotEqualNullable(
            SqlExpression left, SqlExpression right, SqlExpression leftIsNull, SqlExpression rightIsNull)
            => _sqlExpressionFactory.AndAlso(
                _sqlExpressionFactory.OrElse(
                    _sqlExpressionFactory.Equal(left, right),
                    _sqlExpressionFactory.OrElse(
                        leftIsNull,
                        rightIsNull)),
                _sqlExpressionFactory.OrElse(
                    _sqlExpressionFactory.Not(leftIsNull),
                    _sqlExpressionFactory.Not(rightIsNull)));

        // ?a != b -> (a != b) || (a == null)
        //
        // a | b | F1 = a != b | F2 = (a == null) | Final = F1 OR F2 |
        //   |   |             |                  |                  |
        // 0 | 0 | 0           | 0                | 0                |
        // 0 | 1 | 1           | 0                | 1                |
        // 1 | 0 | 1           | 0                | 1                |
        // 1 | 1 | 0           | 0                | 0                |
        // N | 0 | N           | 1                | 1                |
        // N | 1 | N           | 1                | 1                |
        private SqlBinaryExpression ExpandNullableNotEqualNonNullable(
            SqlExpression left, SqlExpression right, SqlExpression leftIsNull)
            => _sqlExpressionFactory.OrElse(
                _sqlExpressionFactory.NotEqual(left, right),
                leftIsNull);

        // !(?a) != b -> (a == b) || (a == null)
        //
        // a | b | F1 = a == b | F2 = (a == null) | F3 = F1 OR F2 |
        //   |   |             |                  |               |
        // 0 | 0 | 1           | 0                | 1             |
        // 0 | 1 | 0           | 0                | 0             |
        // 1 | 0 | 0           | 0                | 0             |
        // 1 | 1 | 1           | 0                | 1             |
        // N | 0 | N           | 1                | 1             |
        // N | 1 | N           | 1                | 1             |
        private SqlBinaryExpression ExpandNegatedNullableNotEqualNonNullable(
            SqlExpression left, SqlExpression right, SqlExpression leftIsNull)
            => _sqlExpressionFactory.OrElse(
                _sqlExpressionFactory.Equal(left, right),
                leftIsNull);
    }
}
