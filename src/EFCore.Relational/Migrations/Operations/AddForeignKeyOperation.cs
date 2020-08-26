// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Diagnostics;
using System.Linq;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Utilities;

namespace Microsoft.EntityFrameworkCore.Migrations.Operations
{
    /// <summary>
    ///     A <see cref="MigrationOperation" /> to add a new foreign key.
    /// </summary>
    [DebuggerDisplay("ALTER TABLE {Table} ADD CONSTRAINT {Name} FOREIGN KEY")]
    public class AddForeignKeyOperation : MigrationOperation, ITableMigrationOperation
    {
        /// <summary>
        ///     The name of the foreign key constraint.
        /// </summary>
        public virtual string Name { get; [param: NotNull] set; }

        /// <summary>
        ///     The schema that contains the table, or <see langword="null" /> if the default schema should be used.
        /// </summary>
        public virtual string Schema { get; [param: CanBeNull] set; }

        /// <summary>
        ///     The table to which the foreign key should be added.
        /// </summary>
        public virtual string Table { get; [param: NotNull] set; }

        /// <summary>
        ///     The ordered-list of column names for the columns that make up the foreign key.
        /// </summary>
        public virtual string[] Columns { get; [param: NotNull] set; }

        /// <summary>
        ///     The schema that contains the table to which this foreign key is constrained,
        ///     or <see langword="null" /> if the default schema should be used.
        /// </summary>
        public virtual string PrincipalSchema { get; [param: CanBeNull] set; }

        /// <summary>
        ///     The table to which the foreign key is constrained.
        /// </summary>
        public virtual string PrincipalTable { get; [param: NotNull] set; }

        /// <summary>
        ///     The ordered-list of column names for the columns to which the columns that make up this foreign key are constrained.
        /// </summary>
        public virtual string[] PrincipalColumns { get; [param: NotNull] set; }

        /// <summary>
        ///     The <see cref="ReferentialAction" /> to use for updates.
        /// </summary>
        public virtual ReferentialAction OnUpdate { get; set; }

        /// <summary>
        ///     The <see cref="ReferentialAction" /> to use for deletes.
        /// </summary>
        public virtual ReferentialAction OnDelete { get; set; }

        /// <summary>
        ///     Creates a new <see cref="AddForeignKeyOperation" /> from the specified foreign key.
        /// </summary>
        /// <param name="foreignKey"> The foreign key. </param>
        /// <returns> The operation. </returns>
        public static AddForeignKeyOperation CreateFrom([NotNull] IForeignKeyConstraint foreignKey)
        {
            Check.NotNull(foreignKey, nameof(foreignKey));

            var operation = new AddForeignKeyOperation
            {
                Schema = foreignKey.Table.Schema,
                Table = foreignKey.Table.Name,
                Name = foreignKey.Name,
                Columns = foreignKey.Columns.Select(c => c.Name).ToArray(),
                PrincipalSchema = foreignKey.PrincipalTable.Schema,
                PrincipalTable = foreignKey.PrincipalTable.Name,
                PrincipalColumns = foreignKey.PrincipalColumns.Select(c => c.Name).ToArray(),
                OnDelete = foreignKey.OnDeleteAction
            };
            operation.AddAnnotations(foreignKey.GetAnnotations());

            return operation;
        }
    }
}
