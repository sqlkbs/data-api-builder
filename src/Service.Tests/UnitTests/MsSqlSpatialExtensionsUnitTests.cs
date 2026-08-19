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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Azure.DataApiBuilder.Service.Tests.UnitTests
{
    /// <summary>
    /// Unit tests for the custom MS SQL Server spatial (geometry/geography) support
    /// implemented in Azure.DataApiBuilder.Core.Custom.MsSqlSpatialExtensions.
    /// Verifies that geometry/geography columns map to string, that SELECT projections
    /// use Well-Known Text (STAsText), and that INSERT/UPDATE parameters are wrapped
    /// with STGeomFromText.
    /// </summary>
    [TestClass, TestCategory(TestCategory.MSSQL)]
    public class MsSqlSpatialExtensionsUnitTests
    {
        private const string ENTITY_NAME = "Place";
        private const string SCHEMA_NAME = "dbo";
        private const string TABLE_NAME = "places";
        private const string SPATIAL_COLUMN_NAME = "shape";

        #region System type mapping

        /// <summary>
        /// Verifies the SQL Server type mapping extension resolves geometry/geography to typeof(string).
        /// </summary>
        [DataTestMethod]
        [DataRow("geometry", DisplayName = "geometry maps to string")]
        [DataRow("geography", DisplayName = "geography maps to string")]
        [DataRow("GEOMETRY", DisplayName = "mapping is case-insensitive")]
        public void ToSpatialSystemTypeOrDefault_SpatialType_MapsToString(string sqlTypeName)
        {
            Assert.AreEqual(typeof(string), sqlTypeName.ToSpatialSystemTypeOrDefault());
        }

        /// <summary>
        /// Verifies that non-spatial type names keep delegating to the core TypeHelper mapping.
        /// </summary>
        [DataTestMethod]
        [DataRow("int", typeof(int), DisplayName = "int keeps core mapping")]
        [DataRow("varchar", typeof(string), DisplayName = "varchar keeps core mapping")]
        [DataRow("datetime2", typeof(DateTime), DisplayName = "datetime2 keeps core mapping")]
        public void ToSpatialSystemTypeOrDefault_NonSpatialType_DelegatesToCoreMapping(string sqlTypeName, Type expectedSystemType)
        {
            Assert.AreEqual(expectedSystemType, sqlTypeName.ToSpatialSystemTypeOrDefault());
        }

        /// <summary>
        /// Verifies a geometry column definition (initially reported as byte[] by the DataAdapter)
        /// is remapped to string with a string DbType and retains its raw type name.
        /// </summary>
        [TestMethod]
        public void ApplySpatialTypeMapping_GeometryColumn_RemapsToWktString()
        {
            ColumnDefinition column = new(typeof(byte[])) { DbType = DbType.Binary };

            column.ApplySpatialTypeMapping("geometry");

            Assert.AreEqual("geometry", column.SqlTypeName);
            Assert.AreEqual(typeof(string), column.SystemType);
            Assert.AreEqual(DbType.String, column.DbType);
        }

        /// <summary>
        /// Verifies a geography column definition is remapped to string with a string DbType.
        /// </summary>
        [TestMethod]
        public void ApplySpatialTypeMapping_GeographyColumn_RemapsToWktString()
        {
            ColumnDefinition column = new(typeof(byte[])) { DbType = DbType.Binary };

            column.ApplySpatialTypeMapping("geography");

            Assert.AreEqual("geography", column.SqlTypeName);
            Assert.AreEqual(typeof(string), column.SystemType);
            Assert.AreEqual(DbType.String, column.DbType);
        }

        /// <summary>
        /// Verifies non-spatial columns only have their raw type name recorded and are otherwise untouched.
        /// </summary>
        [TestMethod]
        public void ApplySpatialTypeMapping_NonSpatialColumn_RecordsTypeNameOnly()
        {
            ColumnDefinition column = new(typeof(string)) { DbType = DbType.String };

            column.ApplySpatialTypeMapping("nvarchar");

            Assert.AreEqual("nvarchar", column.SqlTypeName);
            Assert.AreEqual(typeof(string), column.SystemType);
            Assert.AreEqual(DbType.String, column.DbType);
        }

        #endregion

        #region SELECT projections

        /// <summary>
        /// Verifies the SELECT projection format [Column].STAsText() AS [Label] for both spatial types.
        /// </summary>
        [DataTestMethod]
        [DataRow("geometry", DisplayName = "geometry projection uses STAsText")]
        [DataRow("geography", DisplayName = "geography projection uses STAsText")]
        public void ToSpatialProjection_GeneratesWktProjection(string sqlTypeName)
        {
            string projection = sqlTypeName.ToSpatialProjection("[dbo].[places].[shape]", "shape");

            Assert.AreEqual("[dbo].[places].[shape].STAsText() AS [shape]", projection);
        }

        /// <summary>
        /// Verifies the projection omits the alias when no label is provided.
        /// </summary>
        [TestMethod]
        public void ToSpatialProjection_WithoutLabel_OmitsAlias()
        {
            string projection = "geometry".ToSpatialProjection("[dbo].[places].[shape]");

            Assert.AreEqual("[dbo].[places].[shape].STAsText()", projection);
        }

        /// <summary>
        /// Verifies a full SELECT query built by MsSqlQueryBuilder projects geometry/geography columns as WKT.
        /// </summary>
        [DataTestMethod]
        [DataRow("geometry", DisplayName = "SELECT projects geometry as WKT")]
        [DataRow("geography", DisplayName = "SELECT projects geography as WKT")]
        public void SelectQuery_WithSpatialColumn_ProjectsWkt(string spatialTypeName)
        {
            Mock<ISqlMetadataProvider> metadataProvider = CreateMetadataProvider(spatialTypeName);
            RuntimeConfigProvider runtimeConfigProvider = TestHelper.GenerateInMemoryRuntimeConfigProvider(CreateRuntimeConfig());

            FindRequestContext context = new(
                entityName: ENTITY_NAME,
                dbo: new DatabaseTable(schemaName: SCHEMA_NAME, tableName: TABLE_NAME),
                isList: true)
            {
                FieldsToBeReturned = new() { "id", "name", SPATIAL_COLUMN_NAME }
            };

            SqlQueryStructure queryStructure = new(
                context,
                metadataProvider.Object,
                new Mock<IAuthorizationResolver>().Object,
                runtimeConfigProvider,
                gQLFilterParser: null!,
                httpContext: null);

            string query = new MsSqlQueryBuilder().Build(queryStructure);

            Assert.IsTrue(query.Contains("[dbo_places].[shape].STAsText() AS [shape]", StringComparison.Ordinal));
            Assert.IsFalse(query.Contains("[dbo_places].[shape] AS [shape]", StringComparison.Ordinal));
        }

        #endregion

        #region Parameter formatting

        /// <summary>
        /// Verifies INSERT/UPDATE parameters are wrapped with the type-specific STGeomFromText expression.
        /// </summary>
        [DataTestMethod]
        [DataRow("geometry", "geometry::STGeomFromText(@param0, 4326)", DisplayName = "geometry parameter uses geometry::STGeomFromText")]
        [DataRow("geography", "geography::STGeomFromText(@param0, 4326)", DisplayName = "geography parameter uses geography::STGeomFromText")]
        public void ToSpatialParameter_GeneratesStGeomFromText(string sqlTypeName, string expectedParameter)
        {
            Assert.AreEqual(expectedParameter, sqlTypeName.ToSpatialParameter("@param0"));
        }

        /// <summary>
        /// Verifies that formatting a parameter for a non-spatial type throws.
        /// </summary>
        [TestMethod]
        public void ToSpatialParameter_NonSpatialType_Throws()
        {
            Assert.ThrowsException<ArgumentException>(() => "nvarchar".ToSpatialParameter("@param0"));
        }

        /// <summary>
        /// Verifies a full INSERT query built by MsSqlQueryBuilder wraps spatial values with STGeomFromText
        /// while leaving non-spatial parameters unchanged.
        /// </summary>
        [DataTestMethod]
        [DataRow("geometry", DisplayName = "INSERT wraps geometry value with STGeomFromText")]
        [DataRow("geography", DisplayName = "INSERT wraps geography value with STGeomFromText")]
        public void InsertQuery_WithSpatialColumn_WrapsValueWithStGeomFromText(string spatialTypeName)
        {
            Mock<ISqlMetadataProvider> metadataProvider = CreateMetadataProvider(spatialTypeName);

            SqlInsertStructure insertStructure = new(
                entityName: ENTITY_NAME,
                sqlMetadataProvider: metadataProvider.Object,
                authorizationResolver: new Mock<IAuthorizationResolver>().Object,
                gQLFilterParser: null!,
                mutationParams: new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Seattle", [SPATIAL_COLUMN_NAME] = "POINT (30 10)" },
                httpContext: null);

            string query = new MsSqlQueryBuilder().Build(insertStructure);

            Assert.IsTrue(
                query.Contains($"VALUES (@param0, @param1, {spatialTypeName}::STGeomFromText(@param2, 4326))", StringComparison.Ordinal),
                $"INSERT SQL did not wrap the spatial value as expected: {query}");
        }

        /// <summary>
        /// Verifies a full UPDATE query built by MsSqlQueryBuilder wraps spatial SET clause values with STGeomFromText.
        /// </summary>
        [DataTestMethod]
        [DataRow("geometry", DisplayName = "UPDATE wraps geometry value with STGeomFromText")]
        [DataRow("geography", DisplayName = "UPDATE wraps geography value with STGeomFromText")]
        public void UpdateQuery_WithSpatialColumn_WrapsSetClauseWithStGeomFromText(string spatialTypeName)
        {
            Mock<ISqlMetadataProvider> metadataProvider = CreateMetadataProvider(spatialTypeName);

            SqlUpdateStructure updateStructure = new(
                entityName: ENTITY_NAME,
                sqlMetadataProvider: metadataProvider.Object,
                authorizationResolver: new Mock<IAuthorizationResolver>().Object,
                gQLFilterParser: null!,
                mutationParams: new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Seattle", [SPATIAL_COLUMN_NAME] = "POINT (30 10)" },
                httpContext: null,
                isIncrementalUpdate: true);

            string query = new MsSqlQueryBuilder().Build(updateStructure);

            Assert.IsTrue(
                query.Contains($"[dbo].[places].[shape] = {spatialTypeName}::STGeomFromText(@param2, 4326)", StringComparison.Ordinal),
                $"UPDATE SQL did not wrap the spatial value as expected: {query}");
        }

        /// <summary>
        /// Verifies that parameters for non-spatial columns are returned unchanged.
        /// </summary>
        [TestMethod]
        public void ToSpatialParameterOrDefault_NonSpatialColumn_ReturnsParamUnchanged()
        {
            SourceDefinition sourceDefinition = new();
            sourceDefinition.Columns.Add("name", new ColumnDefinition(typeof(string)) { DbType = DbType.String, SqlTypeName = "nvarchar" });

            Assert.AreEqual("@param0", "@param0".ToSpatialParameterOrDefault(sourceDefinition, "name"));
        }

        #endregion

        #region Mutation OUTPUT projections

        /// <summary>
        /// Verifies a POST (INSERT) mutation OUTPUT clause projects spatial columns as WKT
        /// instead of the raw CLR spatial value.
        /// </summary>
        [DataTestMethod]
        [DataRow("geometry", DisplayName = "INSERT OUTPUT projects geometry as WKT")]
        [DataRow("geography", DisplayName = "INSERT OUTPUT projects geography as WKT")]
        public void InsertQuery_WithSpatialColumn_OutputClauseProjectsWkt(string spatialTypeName)
        {
            Mock<ISqlMetadataProvider> metadataProvider = CreateMetadataProvider(spatialTypeName);

            SqlInsertStructure insertStructure = new(
                entityName: ENTITY_NAME,
                sqlMetadataProvider: metadataProvider.Object,
                authorizationResolver: new Mock<IAuthorizationResolver>().Object,
                gQLFilterParser: null!,
                mutationParams: new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Seattle", [SPATIAL_COLUMN_NAME] = "POINT (30 10)" },
                httpContext: null);

            string query = new MsSqlQueryBuilder().Build(insertStructure);

            Assert.IsTrue(
                query.Contains("Inserted.[shape].STAsText() AS [shape]", StringComparison.Ordinal),
                $"INSERT OUTPUT clause did not project the spatial column as WKT: {query}");
            Assert.IsFalse(
                query.Contains("Inserted.[shape] AS [shape]", StringComparison.Ordinal),
                $"INSERT OUTPUT clause still returns the raw spatial column: {query}");
        }

        /// <summary>
        /// Verifies a PATCH (UPDATE) mutation OUTPUT clause projects spatial columns as WKT
        /// instead of the raw CLR spatial value.
        /// </summary>
        [DataTestMethod]
        [DataRow("geometry", DisplayName = "UPDATE OUTPUT projects geometry as WKT")]
        [DataRow("geography", DisplayName = "UPDATE OUTPUT projects geography as WKT")]
        public void UpdateQuery_WithSpatialColumn_OutputClauseProjectsWkt(string spatialTypeName)
        {
            Mock<ISqlMetadataProvider> metadataProvider = CreateMetadataProvider(spatialTypeName);

            SqlUpdateStructure updateStructure = new(
                entityName: ENTITY_NAME,
                sqlMetadataProvider: metadataProvider.Object,
                authorizationResolver: new Mock<IAuthorizationResolver>().Object,
                gQLFilterParser: null!,
                mutationParams: new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Seattle", [SPATIAL_COLUMN_NAME] = "POINT (30 10)" },
                httpContext: null,
                isIncrementalUpdate: true);

            string query = new MsSqlQueryBuilder().Build(updateStructure);

            Assert.IsTrue(
                query.Contains("Inserted.[shape].STAsText() AS [shape]", StringComparison.Ordinal),
                $"UPDATE OUTPUT clause did not project the spatial column as WKT: {query}");
            Assert.IsFalse(
                query.Contains("Inserted.[shape] AS [shape]", StringComparison.Ordinal),
                $"UPDATE OUTPUT clause still returns the raw spatial column: {query}");
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Creates a metadata provider mock whose source definition contains a spatial column of the given type.
        /// </summary>
        private static Mock<ISqlMetadataProvider> CreateMetadataProvider(string spatialTypeName)
        {
            SourceDefinition sourceDefinition = new();
            sourceDefinition.Columns.Add("id", new ColumnDefinition(typeof(int)) { DbType = DbType.Int32 });
            sourceDefinition.Columns.Add("name", new ColumnDefinition(typeof(string)) { DbType = DbType.String, SqlTypeName = "nvarchar" });
            sourceDefinition.Columns.Add(SPATIAL_COLUMN_NAME, new ColumnDefinition(typeof(string)) { DbType = DbType.String, SqlTypeName = spatialTypeName });
            sourceDefinition.PrimaryKey.Add("id");

            DatabaseObject databaseObject = new DatabaseTable(schemaName: SCHEMA_NAME, tableName: TABLE_NAME);
            Dictionary<string, DatabaseObject> entities = new()
            {
                [ENTITY_NAME] = databaseObject
            };

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

        /// <summary>
        /// Creates the minimal runtime config used to exercise the SqlQueryStructure Find path.
        /// </summary>
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
