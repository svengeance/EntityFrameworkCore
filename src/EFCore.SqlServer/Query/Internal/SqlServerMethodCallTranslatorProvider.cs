// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;

namespace Microsoft.EntityFrameworkCore.SqlServer.Query.Internal
{
    public class SqlServerMethodCallTranslatorProvider : RelationalMethodCallTranslatorProvider
    {
        public SqlServerMethodCallTranslatorProvider([NotNull] RelationalMethodCallTranslatorProviderDependencies dependencies)
            : base(dependencies)
        {
            var sqlExpressionFactory = dependencies.SqlExpressionFactory;
            var typeMappingSource = dependencies.RelationalTypeMappingSource;
            AddTranslators(
                new IMethodCallTranslator[]
                {
                    new SqlServerByteArrayMethodTranslator(sqlExpressionFactory),
                    new SqlServerConvertTranslator(sqlExpressionFactory),
                    new SqlServerDateDiffFunctionsTranslator(sqlExpressionFactory),
                    new SqlServerDateTimeFromPartsFunctionTranslator(sqlExpressionFactory, typeMappingSource),
                    new SqlServerDateTimeMethodTranslator(sqlExpressionFactory),
                    new SqlServerFullTextSearchFunctionsTranslator(sqlExpressionFactory),
                    new SqlServerIsDateFunctionTranslator(sqlExpressionFactory),
                    new SqlServerMathTranslator(sqlExpressionFactory),
                    new SqlServerNewGuidTranslator(sqlExpressionFactory),
                    new SqlServerObjectToStringTranslator(sqlExpressionFactory),
                    new SqlServerStringMethodTranslator(sqlExpressionFactory)
                });
        }
    }
}
