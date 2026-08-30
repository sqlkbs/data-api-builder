// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Data;
using Azure.DataApiBuilder.Auth;
using Azure.DataApiBuilder.Config.DatabasePrimitives;
using Azure.DataApiBuilder.Config.ObjectModel;
using Azure.DataApiBuilder.Core.Configurations;
using Azure.DataApiBuilder.Core.Custom;
using Azure.DataApiBuilder.Core.Models;
using Azure.DataApiBuilder.Core.Resolvers;
using Azure.DataApiBuilder.Core.Services;
using Azure.DataApiBuilder.Service.Exceptions;
using Azure.DataApiBuilder.Service.GraphQLBuilder.GraphQLTypes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Azure.DataApiBuilder.Service.Tests.UnitTests
{
    /// <summary>
    /// Unit tests for the SQL generated for tenant JSON attributes
    /// (Azure.DataApiBuilder.Core.Custom.MsSqlTenantSchemaExtensions).
    ///
    /// Verifies that FieldSourceType = 2 attributes project through CAST(JSON_VALUE(...)), that reads
    /// and writes coexist with the existing spatial support, that writes collapse into a single
    /// JSON_MODIFY chain per container column, and that omitted attributes are never dropped.
    /// </summary>
    [TestClass, TestCategory(TestCategory.MSSQL)]
    public class MsSqlTenantSchemaExtensionsUnitTests
    {
        private const string ENTITY_NAME = "Assay";
        private const string SCHEMA_NAME = "dbo";
        private const string TABLE_NAME = "assay";
        private const string SOURCE_ALIAS = "dbo_assay";
        private const string CONTAINER_COLUMN = "CustomAttributesJson";
        private const string SPATIAL_COLUMN_NAME = "shape";
        private const string DECIMAL_CAST = "DECIMAL(18,6)";

        #region Declared data type mapping

        /// <summary>
        /// Verifies the declared DataType drives the CLR type, the read cast and the write expression,
        /// which together preserve numeric and temporal types through FOR JSON PATH serialization.
        /// </summary>
        [DataTestMethod]
        [DataRow("nvarchar", typeof(string), null, "@p", DisplayName = "text needs no cast")]
        [DataRow("int", typeof(int), "INT", "CAST(@p AS INT)", DisplayName = "int")]
        [DataRow("bigint", typeof(long), "BIGINT", "CAST(@p AS BIGINT)", DisplayName = "bigint")]
        [DataRow("bit", typeof(bool), "BIT", "CAST(@p AS BIT)", DisplayName = "bit")]
        [DataRow("float", typeof(double), "FLOAT", "CAST(@p AS FLOAT)", DisplayName = "float")]
        [DataRow("datetime2", typeof(DateTime), "DATETIME2", "CONVERT(NVARCHAR(33), @p, 127)", DisplayName = "datetime2 writes ISO 8601")]
        [DataRow("uniqueidentifier", typeof(Guid), "UNIQUEIDENTIFIER", "CAST(@p AS NVARCHAR(36))", DisplayName = "guid writes as JSON string")]
        public void TryMapTenantDataType_SupportedTypes_ResolveReadAndWriteForms(
            string dataType,
            Type expectedSystemType,
            string? expectedReadCast,
            string expectedWriteExpression)
        {
            Assert.IsTrue(MsSqlTenantSchemaExtensions.TryMapTenantDataType(dataType, out TenantDataTypeMapping? mapping));
            Assert.AreEqual(expectedSystemType, mapping!.SystemType);
            Assert.AreEqual(expectedReadCast, mapping.SqlReadCastType);
            Assert.AreEqual(
                expectedWriteExpression,
                string.Format(System.Globalization.CultureInfo.InvariantCulture, mapping.SqlWriteValueTemplate, "@p"));
        }

        /// <summary>An explicit precision on the declared type is preserved rather than widened.</summary>
        [TestMethod]
        public void TryMapTenantDataType_DecimalWithPrecision_PreservesPrecision()
        {
            Assert.IsTrue(MsSqlTenantSchemaExtensions.TryMapTenantDataType("decimal(18,6)", out TenantDataTypeMapping? mapping));
            Assert.AreEqual("DECIMAL(18,6)", mapping!.SqlReadCastType);

            Assert.IsTrue(MsSqlTenantSchemaExtensions.TryMapTenantDataType("decimal", out TenantDataTypeMapping? defaulted));
            Assert.AreEqual("DECIMAL(38,10)", defaulted!.SqlReadCastType, "A precision-less decimal must fall back to a wide default.");
        }

        /// <summary>
        /// Spatial types cannot be represented as a JSON scalar, so they are rejected for JSON
        /// attributes; physical geometry/geography columns remain handled by the spatial support.
        /// </summary>
        [DataTestMethod]
        [DataRow("geometry", DisplayName = "geometry is rejected")]
        [DataRow("geography", DisplayName = "geography is rejected")]
        [DataRow("varbinary", DisplayName = "varbinary is rejected")]
        [DataRow("", DisplayName = "empty is rejected")]
        [DataRow(null, DisplayName = "null is rejected")]
        public void TryMapTenantDataType_UnsupportedType_IsRejected(string? dataType)
        {
            Assert.IsFalse(MsSqlTenantSchemaExtensions.TryMapTenantDataType(dataType, out TenantDataTypeMapping? mapping));
            Assert.IsNull(mapping);
        }

        /// <summary>Verifies the read expression shape, including JSON path literal escaping.</summary>
        [TestMethod]
        public void ToJsonValueExpression_BuildsCastJsonValue()
        {
            VirtualColumnDefinition numeric = CreateVirtualColumn("recoveryRate", "$.RecoveryRate", "decimal(18,6)");
            Assert.AreEqual(
                $"CAST(JSON_VALUE([{SOURCE_ALIAS}].[{CONTAINER_COLUMN}], '$.RecoveryRate') AS {DECIMAL_CAST})",
                numeric.ToJsonValueExpression($"[{SOURCE_ALIAS}].[{CONTAINER_COLUMN}]"));

            VirtualColumnDefinition text = CreateVirtualColumn("notes", "$.Notes", "nvarchar");
            Assert.AreEqual(
                $"JSON_VALUE([{SOURCE_ALIAS}].[{CONTAINER_COLUMN}], '$.Notes')",
                text.ToJsonValueExpression($"[{SOURCE_ALIAS}].[{CONTAINER_COLUMN}]"),
                "A text attribute needs no cast because JSON_VALUE already returns nvarchar.");

            VirtualColumnDefinition quoted = CreateVirtualColumn("odd", "$.O'Brien", "nvarchar");
            Assert.IsTrue(
                quoted.ToJsonValueExpression("[c]").Contains("'$.O''Brien'", StringComparison.Ordinal),
                "A quote inside the JSON path must be escaped for the SQL literal.");
        }

        #endregion

        #region SELECT projection

        /// <summary>
        /// Verifies a full SELECT built by MsSqlQueryBuilder projects a JSON attribute through
        /// CAST(JSON_VALUE(...)) aliased to its ApiAlias, and never emits the virtual column as a
        /// physical column reference.
        /// </summary>
        [TestMethod]
        public void SelectQuery_WithJsonAttribute_ProjectsCastJsonValue()
        {
            string query = BuildSelectQuery(new List<string> { "id", "name", "recoveryRate" });

            Assert.IsTrue(
                query.Contains(
                    $"CAST(JSON_VALUE([{SOURCE_ALIAS}].[{CONTAINER_COLUMN}], '$.RecoveryRate') AS {DECIMAL_CAST}) AS [recoveryRate]",
                    StringComparison.Ordinal),
                $"SELECT did not project the JSON attribute as expected: {query}");

            Assert.IsFalse(
                query.Contains($"[{SOURCE_ALIAS}].[recoveryRate]", StringComparison.Ordinal),
                $"The virtual column must never be referenced as a physical column: {query}");
        }

        /// <summary>A text attribute is projected without a cast.</summary>
        [TestMethod]
        public void SelectQuery_WithTextJsonAttribute_OmitsCast()
        {
            string query = BuildSelectQuery(new List<string> { "id", "notes" });

            Assert.IsTrue(
                query.Contains($"JSON_VALUE([{SOURCE_ALIAS}].[{CONTAINER_COLUMN}], '$.Notes') AS [notes]", StringComparison.Ordinal),
                $"SELECT did not project the text attribute as expected: {query}");
            Assert.IsFalse(
                query.Contains("CAST(JSON_VALUE", StringComparison.Ordinal),
                $"A text attribute must not be cast: {query}");
        }

        /// <summary>
        /// Verifies the tenant dynamic schema and the existing spatial support coexist in a single
        /// query: JSON attributes project via JSON_VALUE while geometry still projects via STAsText().
        /// </summary>
        [TestMethod]
        public void SelectQuery_WithJsonAttributeAndSpatialColumn_ProjectsBoth()
        {
            string query = BuildSelectQuery(
                new List<string> { "id", SPATIAL_COLUMN_NAME, "recoveryRate" },
                includeSpatialColumn: true);

            Assert.IsTrue(
                query.Contains($"[{SOURCE_ALIAS}].[{SPATIAL_COLUMN_NAME}].STAsText() AS [{SPATIAL_COLUMN_NAME}]", StringComparison.Ordinal),
                $"Spatial projection regressed: {query}");
            Assert.IsTrue(
                query.Contains($"CAST(JSON_VALUE([{SOURCE_ALIAS}].[{CONTAINER_COLUMN}], '$.RecoveryRate') AS {DECIMAL_CAST}) AS [recoveryRate]", StringComparison.Ordinal),
                $"JSON attribute projection missing: {query}");
        }

        /// <summary>
        /// A predicate over a JSON attribute is rewritten too, because the Build(Column) override is
        /// the single chokepoint through which filters render column references.
        /// </summary>
        [TestMethod]
        public void SelectQuery_FilterOnJsonAttribute_RewritesPredicateColumn()
        {
            SqlQueryStructure structure = CreateQueryStructure(new List<string> { "id", "recoveryRate" });

            structure.Predicates.Add(new Predicate(
                new PredicateOperand(new Column(SCHEMA_NAME, TABLE_NAME, "recoveryRate", SOURCE_ALIAS)),
                PredicateOperation.GreaterThan,
                new PredicateOperand("@filterParam")));

            string query = new MsSqlQueryBuilder().Build(structure);

            Assert.IsTrue(
                query.Contains(
                    $"CAST(JSON_VALUE([{SOURCE_ALIAS}].[{CONTAINER_COLUMN}], '$.RecoveryRate') AS {DECIMAL_CAST}) > @filterParam",
                    StringComparison.Ordinal),
                $"Filter predicate was not rewritten to JSON_VALUE: {query}");
        }

        /// <summary>ORDER BY over a JSON attribute is rewritten through the same chokepoint.</summary>
        [TestMethod]
        public void SelectQuery_OrderByJsonAttribute_RewritesOrderByColumn()
        {
            SqlQueryStructure structure = CreateQueryStructure(new List<string> { "id", "recoveryRate" });

            structure.OrderByColumns.Insert(0, new OrderByColumn(
                tableSchema: SCHEMA_NAME,
                tableName: TABLE_NAME,
                columnName: "recoveryRate",
                tableAlias: SOURCE_ALIAS,
                direction: OrderBy.DESC));

            string query = new MsSqlQueryBuilder().Build(structure);

            Assert.IsTrue(
                query.Contains(
                    $"ORDER BY CAST(JSON_VALUE([{SOURCE_ALIAS}].[{CONTAINER_COLUMN}], '$.RecoveryRate') AS {DECIMAL_CAST}) DESC",
                    StringComparison.Ordinal),
                $"ORDER BY was not rewritten to JSON_VALUE: {query}");
        }

        /// <summary>
        /// A physical column of the same entity must be untouched, proving the rewrite is targeted
        /// rather than applied to every column.
        /// </summary>
        [TestMethod]
        public void SelectQuery_PhysicalColumns_AreNotRewritten()
        {
            string query = BuildSelectQuery(new List<string> { "id", "name", "recoveryRate" });

            Assert.IsTrue(query.Contains($"[{SOURCE_ALIAS}].[name] AS [name]", StringComparison.Ordinal), query);
            Assert.IsTrue(query.Contains($"[{SOURCE_ALIAS}].[id] AS [id]", StringComparison.Ordinal), query);
        }

        #endregion

        #region INSERT

        /// <summary>
        /// Verifies several JSON attributes collapse into one JSON_MODIFY chain assigned to their
        /// container column, so N API fields become a single SQL column.
        /// </summary>
        [TestMethod]
        public void InsertQuery_WithJsonAttributes_CollapsesIntoSingleJsonModifyChain()
        {
            SqlInsertStructure insertStructure = new(
                entityName: ENTITY_NAME,
                sqlMetadataProvider: CreateMetadataProvider().Object,
                authorizationResolver: new Mock<IAuthorizationResolver>().Object,
                gQLFilterParser: null!,
                mutationParams: new Dictionary<string, object?>
                {
                    ["id"] = 1,
                    ["name"] = "Sample-1",
                    ["recoveryRate"] = 91.5m,
                    ["goldGrade"] = 2.4m
                },
                httpContext: null);

            string query = new MsSqlQueryBuilder().Build(insertStructure);

            Assert.IsTrue(
                query.Contains($"INSERT INTO [{SCHEMA_NAME}].[{TABLE_NAME}] ([id], [name], [{CONTAINER_COLUMN}])", StringComparison.Ordinal),
                $"The insert column list must contain the container column once, not the attributes: {query}");

            Assert.IsTrue(
                query.Contains(
                    $"NULLIF(JSON_MODIFY(JSON_MODIFY('{{}}', '$.RecoveryRate', CAST(@param2 AS {DECIMAL_CAST})), '$.GoldGrade', CAST(@param3 AS {DECIMAL_CAST})), N'{{}}')",
                    StringComparison.Ordinal),
                $"The insert values must nest one JSON_MODIFY per attribute and collapse to NULL when empty: {query}");

            Assert.IsFalse(
                ExtractInsertColumnList(query).Contains("[recoveryRate]", StringComparison.Ordinal),
                $"Attributes must not appear in the insert column list: {query}");
            Assert.IsFalse(
                ExtractInsertColumnList(query).Contains("[goldGrade]", StringComparison.Ordinal),
                $"Attributes must not appear in the insert column list: {query}");
        }

        /// <summary>
        /// The mutation OUTPUT clause returns JSON attributes through JSON_VALUE against the inserted
        /// row, so a create response includes the custom attributes.
        /// </summary>
        [TestMethod]
        public void InsertQuery_OutputClause_ProjectsJsonAttributes()
        {
            SqlInsertStructure insertStructure = new(
                entityName: ENTITY_NAME,
                sqlMetadataProvider: CreateMetadataProvider().Object,
                authorizationResolver: new Mock<IAuthorizationResolver>().Object,
                gQLFilterParser: null!,
                mutationParams: new Dictionary<string, object?> { ["id"] = 1, ["recoveryRate"] = 91.5m },
                httpContext: null);

            string query = new MsSqlQueryBuilder().Build(insertStructure);

            Assert.IsTrue(
                query.Contains(
                    $"CAST(JSON_VALUE(Inserted.[{CONTAINER_COLUMN}], '$.RecoveryRate') AS {DECIMAL_CAST}) AS [recoveryRate]",
                    StringComparison.Ordinal),
                $"The OUTPUT clause did not project the JSON attribute: {query}");
        }

        /// <summary>
        /// Writing the container column directly alongside its attributes would emit two assignments
        /// for one column, so it is rejected with a client error instead of invalid SQL.
        /// </summary>
        [TestMethod]
        public void InsertQuery_WritingContainerColumnAndAttributes_Throws()
        {
            SqlInsertStructure insertStructure = new(
                entityName: ENTITY_NAME,
                sqlMetadataProvider: CreateMetadataProvider().Object,
                authorizationResolver: new Mock<IAuthorizationResolver>().Object,
                gQLFilterParser: null!,
                mutationParams: new Dictionary<string, object?>
                {
                    ["id"] = 1,
                    [CONTAINER_COLUMN] = "{}",
                    ["recoveryRate"] = 91.5m
                },
                httpContext: null);

            DataApiBuilderException exception = Assert.ThrowsException<DataApiBuilderException>(
                () => new MsSqlQueryBuilder().Build(insertStructure));

            Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, exception.StatusCode);
            Assert.IsTrue(exception.Message.Contains(CONTAINER_COLUMN, StringComparison.Ordinal), exception.Message);
        }

        /// <summary>An insert touching no JSON attribute must produce exactly the original SQL shape.</summary>
        [TestMethod]
        public void InsertQuery_WithoutJsonAttributes_IsUnchanged()
        {
            SqlInsertStructure insertStructure = new(
                entityName: ENTITY_NAME,
                sqlMetadataProvider: CreateMetadataProvider().Object,
                authorizationResolver: new Mock<IAuthorizationResolver>().Object,
                gQLFilterParser: null!,
                mutationParams: new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Sample-1" },
                httpContext: null);

            string query = new MsSqlQueryBuilder().Build(insertStructure);

            Assert.IsTrue(query.Contains($"INSERT INTO [{SCHEMA_NAME}].[{TABLE_NAME}] ([id], [name])", StringComparison.Ordinal), query);
            Assert.IsTrue(query.Contains("VALUES (@param0, @param1)", StringComparison.Ordinal), query);
            Assert.IsFalse(query.Contains("JSON_MODIFY", StringComparison.Ordinal), query);
        }

        #endregion

        #region UPDATE

        /// <summary>
        /// Verifies the SET clause collapses JSON attributes into one assignment on the container
        /// column, based on ISNULL so a NULL document is initialized rather than left NULL.
        /// </summary>
        [TestMethod]
        public void UpdateQuery_WithJsonAttributes_CollapsesIntoContainerAssignment()
        {
            string query = BuildUpdateQuery(new Dictionary<string, object?>
            {
                ["id"] = 1,
                ["recoveryRate"] = 91.5m,
                ["goldGrade"] = 2.4m
            });

            Assert.IsTrue(
                query.Contains(
                    $"SET [{CONTAINER_COLUMN}] = NULLIF(JSON_MODIFY(JSON_MODIFY(ISNULL([{CONTAINER_COLUMN}], '{{}}'), '$.RecoveryRate', CAST(@param1 AS {DECIMAL_CAST})), '$.GoldGrade', CAST(@param2 AS {DECIMAL_CAST})), N'{{}}')",
                    StringComparison.Ordinal),
                $"The SET clause did not collapse the JSON attributes as expected: {query}");

            Assert.IsFalse(
                query.Contains("[recoveryRate] =", StringComparison.Ordinal),
                $"A virtual column must never be an assignment target: {query}");
        }

        /// <summary>Physical and JSON assignments are combined, with physical ones left untouched.</summary>
        [TestMethod]
        public void UpdateQuery_MixedPhysicalAndJsonAttributes_KeepsPhysicalAssignments()
        {
            string query = BuildUpdateQuery(new Dictionary<string, object?>
            {
                ["id"] = 1,
                ["name"] = "Renamed",
                ["recoveryRate"] = 91.5m
            });

            Assert.IsTrue(
                query.Contains($"[{SCHEMA_NAME}].[{TABLE_NAME}].[name] = @param1", StringComparison.Ordinal),
                $"The physical assignment must be preserved verbatim: {query}");
            Assert.IsTrue(
                query.Contains($"[{CONTAINER_COLUMN}] = NULLIF(JSON_MODIFY(ISNULL([{CONTAINER_COLUMN}], '{{}}'), '$.RecoveryRate', CAST(@param2 AS {DECIMAL_CAST})), N'{{}}')", StringComparison.Ordinal),
                $"The JSON assignment must be present: {query}");
        }

        /// <summary>
        /// An attribute the request did not mention must not appear in the JSON_MODIFY chain, which is
        /// what guarantees a write can never drop a tenant's existing custom data.
        /// </summary>
        [TestMethod]
        public void UpdateQuery_OmittedAttributes_AreNotTouched()
        {
            string query = BuildUpdateQuery(new Dictionary<string, object?>
            {
                ["id"] = 1,
                ["recoveryRate"] = 91.5m
            });

            // Only the SET clause is examined: the OUTPUT clause legitimately reads every attribute
            // back out, including ones this request did not write.
            string setClause = ExtractSetClause(query);
            Assert.IsTrue(setClause.Contains("'$.RecoveryRate'", StringComparison.Ordinal), setClause);
            Assert.IsFalse(setClause.Contains("'$.GoldGrade'", StringComparison.Ordinal), $"An omitted attribute must not be written: {setClause}");
            Assert.IsFalse(setClause.Contains("'$.Notes'", StringComparison.Ordinal), $"An omitted attribute must not be written: {setClause}");
        }

        /// <summary>
        /// An explicitly null attribute removes its key through lax-mode JSON_MODIFY. Because that
        /// single attribute was the only one written, the chain leaves an empty document and the
        /// container column is therefore stored as SQL NULL.
        /// </summary>
        [TestMethod]
        public void UpdateQuery_ExplicitNullAttribute_DeletesKeyAndNullsEmptyContainer()
        {
            string query = BuildUpdateQuery(new Dictionary<string, object?>
            {
                ["id"] = 1,
                ["recoveryRate"] = null
            });

            string setClause = ExtractSetClause(query);
            Assert.IsTrue(
                setClause.Contains(
                    $"NULLIF(JSON_MODIFY(ISNULL([{CONTAINER_COLUMN}], '{{}}'), '$.RecoveryRate', NULL), N'{{}}')",
                    StringComparison.Ordinal),
                $"An explicit null must delete the key, and an empty document must collapse to NULL: {setClause}");
            Assert.IsFalse(
                setClause.Contains("strict", StringComparison.Ordinal),
                $"The two-step key-preserving form must no longer be emitted: {setClause}");
        }

        /// <summary>
        /// When an update nulls the only attribute holding a value, every key is removed, so the
        /// container column is stored as NULL rather than as an empty object.
        /// </summary>
        [TestMethod]
        public void UpdateQuery_AllAttributesNull_NullsContainerColumn()
        {
            string query = BuildUpdateQuery(new Dictionary<string, object?>
            {
                ["id"] = 1,
                ["recoveryRate"] = null,
                ["goldGrade"] = null
            });

            string setClause = ExtractSetClause(query);
            Assert.IsTrue(
                setClause.Contains(
                    $"NULLIF(JSON_MODIFY(JSON_MODIFY(ISNULL([{CONTAINER_COLUMN}], '{{}}'), '$.RecoveryRate', NULL), '$.GoldGrade', NULL), N'{{}}')",
                    StringComparison.Ordinal),
                $"Nulling every attribute must null the container: {setClause}");
        }

        /// <summary>
        /// An insert whose attributes are all null stores SQL NULL in the container column: no empty
        /// object shell is persisted, which keeps "NULL means no custom attributes" uniform.
        /// </summary>
        [TestMethod]
        public void InsertQuery_AllAttributesNull_InsertsNullContainer()
        {
            SqlInsertStructure insertStructure = new(
                entityName: ENTITY_NAME,
                sqlMetadataProvider: CreateMetadataProvider().Object,
                authorizationResolver: new Mock<IAuthorizationResolver>().Object,
                gQLFilterParser: null!,
                mutationParams: new Dictionary<string, object?>
                {
                    ["id"] = 1,
                    ["recoveryRate"] = null
                },
                httpContext: null);

            string query = new MsSqlQueryBuilder().Build(insertStructure);

            Assert.IsTrue(
                query.Contains(
                    $"VALUES (@param0, NULLIF(JSON_MODIFY('{{}}', '$.RecoveryRate', NULL), N'{{}}'))",
                    StringComparison.Ordinal),
                $"An insert with only null attributes must store NULL in the container: {query}");
        }

        /// <summary>An update touching no JSON attribute must produce exactly the original SQL shape.</summary>
        [TestMethod]
        public void UpdateQuery_WithoutJsonAttributes_IsUnchanged()
        {
            string query = BuildUpdateQuery(new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Renamed" });

            Assert.IsTrue(query.Contains($"SET [{SCHEMA_NAME}].[{TABLE_NAME}].[name] = @param1", StringComparison.Ordinal), query);
            Assert.IsFalse(query.Contains("JSON_MODIFY", StringComparison.Ordinal), query);
        }

        #endregion

        #region Helpers

        private static string BuildSelectQuery(List<string> fieldsToBeReturned, bool includeSpatialColumn = false)
            => new MsSqlQueryBuilder().Build(CreateQueryStructure(fieldsToBeReturned, includeSpatialColumn));

        /// <summary>
        /// Returns just the parenthesized INSERT column list, so an assertion about which columns are
        /// written is not confused by the OUTPUT clause that reads attributes back out.
        /// </summary>
        private static string ExtractInsertColumnList(string query)
        {
            int start = query.IndexOf('(', StringComparison.Ordinal);
            int end = query.IndexOf(") OUTPUT", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0 && end > start, $"Could not locate the insert column list in: {query}");
            return query[start..end];
        }

        /// <summary>
        /// Returns just the UPDATE SET clause, so an assertion about which attributes are written is not
        /// confused by the OUTPUT clause that reads attributes back out.
        /// </summary>
        private static string ExtractSetClause(string query)
        {
            const string setToken = " SET ";
            int start = query.IndexOf(setToken, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"Could not locate the SET clause in: {query}");
            start += setToken.Length;

            int end = query.IndexOf(" OUTPUT ", start, StringComparison.Ordinal);
            if (end < 0)
            {
                end = query.IndexOf(" WHERE ", start, StringComparison.Ordinal);
            }

            Assert.IsTrue(end > start, $"Could not locate the end of the SET clause in: {query}");
            return query[start..end];
        }

        private static SqlQueryStructure CreateQueryStructure(List<string> fieldsToBeReturned, bool includeSpatialColumn = false)
        {
            FindRequestContext context = new(
                entityName: ENTITY_NAME,
                dbo: new DatabaseTable(schemaName: SCHEMA_NAME, tableName: TABLE_NAME),
                isList: true)
            {
                FieldsToBeReturned = fieldsToBeReturned
            };

            return new SqlQueryStructure(
                context,
                CreateMetadataProvider(includeSpatialColumn).Object,
                new Mock<IAuthorizationResolver>().Object,
                TestHelper.GenerateInMemoryRuntimeConfigProvider(CreateRuntimeConfig()),
                gQLFilterParser: null!,
                httpContext: null);
        }

        private static string BuildUpdateQuery(Dictionary<string, object?> mutationParams)
        {
            SqlUpdateStructure updateStructure = new(
                entityName: ENTITY_NAME,
                sqlMetadataProvider: CreateMetadataProvider().Object,
                authorizationResolver: new Mock<IAuthorizationResolver>().Object,
                gQLFilterParser: null!,
                mutationParams: mutationParams,
                httpContext: null,
                isIncrementalUpdate: true);

            return new MsSqlQueryBuilder().Build(updateStructure);
        }

        private static VirtualColumnDefinition CreateVirtualColumn(string apiAlias, string jsonPath, string dataType)
        {
            Assert.IsTrue(
                MsSqlTenantSchemaExtensions.TryMapTenantDataType(dataType, out TenantDataTypeMapping? mapping),
                $"The test data type '{dataType}' must be supported.");

            return new VirtualColumnDefinition
            {
                ApiAlias = apiAlias,
                ContainerColumnName = CONTAINER_COLUMN,
                JsonPath = jsonPath,
                SqlReadCastType = mapping!.SqlReadCastType,
                SqlWriteValueTemplate = mapping.SqlWriteValueTemplate,
                SystemType = mapping.SystemType,
                DbType = mapping.DbType,
                SqlDbType = mapping.SqlDbType,
                SqlTypeName = dataType,
                IsNullable = true
            };
        }

        /// <summary>
        /// Builds a metadata provider whose source definition carries the physical columns plus three
        /// injected tenant JSON attributes, mirroring what MsSqlMetadataProvider produces at runtime.
        /// </summary>
        private static Mock<ISqlMetadataProvider> CreateMetadataProvider(bool includeSpatialColumn = false)
        {
            SourceDefinition sourceDefinition = new();
            sourceDefinition.Columns.Add("id", new ColumnDefinition(typeof(int)) { DbType = DbType.Int32 });
            sourceDefinition.Columns.Add("name", new ColumnDefinition(typeof(string)) { DbType = DbType.String, SqlTypeName = "nvarchar" });
            sourceDefinition.Columns.Add(CONTAINER_COLUMN, new ColumnDefinition(typeof(string)) { DbType = DbType.String, SqlTypeName = "nvarchar", IsNullable = true });

            if (includeSpatialColumn)
            {
                sourceDefinition.Columns.Add(SPATIAL_COLUMN_NAME, new ColumnDefinition(typeof(string)) { DbType = DbType.String, SqlTypeName = "geometry" });
            }

            sourceDefinition.Columns.Add("recoveryRate", CreateVirtualColumn("recoveryRate", "$.RecoveryRate", "decimal(18,6)"));
            sourceDefinition.Columns.Add("goldGrade", CreateVirtualColumn("goldGrade", "$.GoldGrade", "decimal(18,6)"));
            sourceDefinition.Columns.Add("notes", CreateVirtualColumn("notes", "$.Notes", "nvarchar"));

            sourceDefinition.PrimaryKey.Add("id");

            DatabaseObject databaseObject = new DatabaseTable(schemaName: SCHEMA_NAME, tableName: TABLE_NAME);
            Dictionary<string, DatabaseObject> entities = new() { [ENTITY_NAME] = databaseObject };

            Mock<ISqlMetadataProvider> metadataProvider = new();
            metadataProvider.SetupGet(provider => provider.EntityToDatabaseObject).Returns(entities);
            metadataProvider.Setup(provider => provider.GetSourceDefinition(ENTITY_NAME)).Returns(sourceDefinition);
            metadataProvider.Setup(provider => provider.GetDatabaseType()).Returns(DatabaseType.MSSQL);

            string? exposedName;
            metadataProvider
                .Setup(provider => provider.TryGetExposedColumnName(It.IsAny<string>(), It.IsAny<string>(), out exposedName))
                .Callback(new ColumnNameCallback((string _, string column, out string? name) => name = column))
                .Returns(true);

            string? backingName;
            metadataProvider
                .Setup(provider => provider.TryGetBackingColumn(It.IsAny<string>(), It.IsAny<string>(), out backingName))
                .Callback(new ColumnNameCallback((string _, string column, out string? name) => name = column))
                .Returns(true);

            return metadataProvider;
        }

        private static RuntimeConfig CreateRuntimeConfig()
        {
            return new RuntimeConfig(
                Schema: "test-schema",
                DataSource: new DataSource(DatabaseType: DatabaseType.MSSQL, ConnectionString: "", Options: null),
                Entities: new RuntimeEntities(new Dictionary<string, Entity>()),
                Runtime: new RuntimeOptions(
                    Rest: new(),
                    GraphQL: new(),
                    Mcp: null,
                    Host: new(Cors: null, Authentication: null, Mode: HostMode.Development)));
        }

        private delegate void ColumnNameCallback(string entity, string column, out string? name);

        #endregion
    }
}
