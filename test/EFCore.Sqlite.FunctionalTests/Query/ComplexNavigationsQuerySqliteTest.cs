// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Threading.Tasks;
using Xunit;

namespace Microsoft.EntityFrameworkCore.Query
{
    public class ComplexNavigationsQuerySqliteTest : ComplexNavigationsQueryRelationalTestBase<ComplexNavigationsQuerySqliteFixture>
    {
        public ComplexNavigationsQuerySqliteTest(ComplexNavigationsQuerySqliteFixture fixture)
            : base(fixture)
        {
        }

        [ConditionalTheory(Skip = "Issue #17230")]
        public override Task Include_inside_subquery(bool async)
        {
            return base.Include_inside_subquery(async);
        }

        // Sqlite does not support cross/outer apply
        public override Task Filtered_include_after_different_filtered_include_different_level(bool async)
            => null;

        public override void Filtered_include_outer_parameter_used_inside_filter() { }

        public override Task Filtered_include_and_non_filtered_include_followed_by_then_include_on_same_navigation(bool async)
            => null;

        public override Task Filtered_include_multiple_multi_level_includes_with_first_level_using_filter_include_on_one_of_the_chains_only(
            bool async)
            => null;

        public override Task Filtered_include_same_filter_set_on_same_navigation_twice_followed_by_ThenIncludes(bool async)
            => null;

        public override Task Filtered_include_complex_three_level_with_middle_having_filter1(bool async)
            => null;

        public override Task Filtered_include_complex_three_level_with_middle_having_filter2(bool async)
            => null;

        public override Task Filtered_include_and_non_filtered_include_followed_by_then_include_on_same_navigation_split(bool async)
            => null;

        public override Task
            Filtered_include_multiple_multi_level_includes_with_first_level_using_filter_include_on_one_of_the_chains_only_split(bool async)
            => null;

        public override Task Filtered_include_same_filter_set_on_same_navigation_twice_followed_by_ThenIncludes_split(bool async)
            => null;
    }
}
