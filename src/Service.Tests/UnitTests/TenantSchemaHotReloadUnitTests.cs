// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using System.Linq;
using Azure.DataApiBuilder.Config;
using Azure.DataApiBuilder.Config.ObjectModel;
using Azure.DataApiBuilder.Core.Configurations;
using Azure.DataApiBuilder.Core.Custom;
using Azure.DataApiBuilder.Core.Custom.HotReload;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Azure.DataApiBuilder.Service.Tests.UnitTests
{
    /// <summary>
    /// Unit tests for the integration between the tenant dynamic schema and the existing dual-trigger
    /// hot reload architecture.
    ///
    /// Covers the three gaps the tenant feature had to close: a registry-only change must still widen
    /// the change set so entity metadata is rebuilt, the registry must be published before schema
    /// inference runs, and the admin endpoint must not exist at all without an API key.
    ///
    /// End-to-end verification that an MS SQL metadata provider is rebuilt requires a live SQL Server
    /// (metadata inference issues database roundtrips), so the change-set widening is asserted directly.
    /// </summary>
    [TestClass, TestCategory(TestCategory.MSSQL)]
    public class TenantSchemaHotReloadUnitTests
    {
        private const string CONFIG_PATH = @"C:\dab\config\dab-config.json";
        private const string TENANT_A = "TENANT-A";
        private const string TENANT_B = "TENANT-B";

        #region Change-set widening for registry-only changes

        /// <summary>
        /// When only dbo.sys_TenantSchemaFields changed, RuntimeConfigDiffer yields an empty diff and
        /// the scoped rebuild would touch nothing. Widening the change set to the tenant data sources
        /// is what makes newly registered attributes appear without a restart.
        /// </summary>
        [TestMethod]
        public void WidenDiffForTenantSchema_EmptyDiff_CoversEveryTenantDataSource()
        {
            RuntimeConfig config = LoadMsSqlConfig(addSecondTenant: true);

            ConfigDiff widened = HotReloadEngine.WidenDiffForTenantSchema(ConfigDiff.Empty, config);

            Assert.IsTrue(widened.HasDataSourceChanges, "A dynamic schema change must produce a non-empty change set.");
            Assert.AreEqual(2, widened.ChangedDataSourceNames.Count, "Both tenant data sources must be rebuilt.");
            Assert.IsTrue(widened.ChangedDataSourceNames.Contains("tenant-b"));
            Assert.IsTrue(widened.ChangedDatabaseTypes.Contains(DatabaseType.MSSQL));
        }

        /// <summary>Widening preserves the data sources the configuration diff already reported.</summary>
        [TestMethod]
        public void WidenDiffForTenantSchema_PreservesExistingChangedDataSources()
        {
            RuntimeConfig config = LoadMsSqlConfig(addSecondTenant: false);

            ConfigDiff widened = HotReloadEngine.WidenDiffForTenantSchema(
                new ConfigDiff(new[] { "some-other-source" }, new[] { DatabaseType.PostgreSQL }),
                config);

            Assert.IsTrue(widened.ChangedDataSourceNames.Contains("some-other-source"), "Existing changes must not be lost.");
            Assert.IsTrue(widened.ChangedDatabaseTypes.Contains(DatabaseType.PostgreSQL));
            Assert.IsTrue(widened.ChangedDatabaseTypes.Contains(DatabaseType.MSSQL));
        }

        /// <summary>
        /// A data source that declares no tenant participates in no dynamic schema, so it is not
        /// dragged into a rebuild.
        /// </summary>
        [TestMethod]
        public void WidenDiffForTenantSchema_NoTenantDataSources_LeavesDiffEmpty()
        {
            RuntimeConfig config = LoadMsSqlConfig(addSecondTenant: false, declareTenantId: false);

            ConfigDiff widened = HotReloadEngine.WidenDiffForTenantSchema(ConfigDiff.Empty, config);

            Assert.IsFalse(widened.HasDataSourceChanges, "Without a declared tenant nothing needs rebuilding.");
        }

        #endregion

        #region Tenant resolution from the data source

        /// <summary>
        /// The tenant is read from the data source options, which is what keeps tenant resolution off
        /// the request path entirely. The option survives dab-config.json deserialization.
        /// </summary>
        [TestMethod]
        public void TryGetTenantId_DeclaredOption_IsReadFromDeserializedConfig()
        {
            RuntimeConfig config = LoadMsSqlConfig(addSecondTenant: false);

            Assert.IsTrue(config.DataSource.TryGetTenantId(out string? tenantId), "The tenant-id option must be readable.");
            Assert.AreEqual(TENANT_A, tenantId);
        }

        /// <summary>A data source without the option yields no tenant rather than an empty string.</summary>
        [TestMethod]
        public void TryGetTenantId_MissingOrBlankOption_ReturnsFalse()
        {
            RuntimeConfig config = LoadMsSqlConfig(addSecondTenant: false, declareTenantId: false);
            Assert.IsFalse(config.DataSource.TryGetTenantId(out string? missing));
            Assert.IsNull(missing);

            DataSource blank = new(
                DatabaseType.MSSQL,
                "Server=x;",
                new Dictionary<string, object?> { { TenantSchemaDataSourceExtensions.TENANT_ID_OPTION, "   " } });
            Assert.IsFalse(blank.TryGetTenantId(out _), "A blank tenant must not be treated as declared.");
        }

        /// <summary>Only MS SQL family data sources take part in the tenant dynamic schema.</summary>
        [TestMethod]
        public void GetTenantDataSources_SkipsNonMsSqlDataSources()
        {
            RuntimeConfig config = CreateMultiDataSourceConfig(
                ("tenant-a", TENANT_A, DatabaseType.MSSQL),
                ("cosmos-tenant", TENANT_B, DatabaseType.CosmosDB_NoSQL));

            List<string> tenantIds = config.GetTenantDataSources().Select(source => source.TenantId).ToList();

            CollectionAssert.AreEquivalent(new[] { TENANT_A }, tenantIds, "A non-MS SQL data source must be ignored.");
        }

        /// <summary>A data source that declares no tenant is skipped even when it is MS SQL.</summary>
        [TestMethod]
        public void GetTenantDataSources_SkipsDataSourcesWithoutTenantId()
        {
            RuntimeConfig config = CreateMultiDataSourceConfig(
                ("tenant-a", TENANT_A, DatabaseType.MSSQL),
                ("shared", null, DatabaseType.MSSQL));

            List<string> tenantIds = config.GetTenantDataSources().Select(source => source.TenantId).ToList();

            CollectionAssert.AreEquivalent(new[] { TENANT_A }, tenantIds);
        }

        #endregion

        #region Registry registration and publication

        /// <summary>
        /// The registry is registered as a singleton behind its interface so the hot reload engine can
        /// discover it, and both resolutions must yield the same instance holding the same cache.
        /// </summary>
        [TestMethod]
        public void AddTenantSchemaRegistry_RegistersSingletonBehindInterface()
        {
            using ServiceProvider serviceProvider = BuildServiceProviderWithRegistry();

            ITenantSchemaRegistryService fromInterface = serviceProvider.GetRequiredService<ITenantSchemaRegistryService>();
            TenantSchemaRegistryService fromConcreteType = serviceProvider.GetRequiredService<TenantSchemaRegistryService>();

            Assert.AreSame(fromInterface, fromConcreteType, "A single cache instance must back both registrations.");
            Assert.AreSame(fromInterface, serviceProvider.GetRequiredService<ITenantSchemaRegistryService>(), "The registry must be a singleton.");
        }

        /// <summary>
        /// Publication must happen before schema inference, because the metadata provider is created
        /// with 'new' by MetadataProviderFactory and reads the registry through the ambient accessor.
        /// </summary>
        [TestMethod]
        public void UseTenantSchemaRegistry_PublishesRegistryToAmbientAccessor()
        {
            ITenantSchemaRegistryService? previous = TenantSchemaRegistryAccessor.Current;
            try
            {
                TenantSchemaRegistryAccessor.Current = null;

                using ServiceProvider serviceProvider = BuildServiceProviderWithRegistry();
                serviceProvider.UseTenantSchemaRegistry();

                Assert.AreSame(
                    serviceProvider.GetRequiredService<ITenantSchemaRegistryService>(),
                    TenantSchemaRegistryAccessor.Current,
                    "Schema inference resolves the registry through this accessor.");
            }
            finally
            {
                TenantSchemaRegistryAccessor.Current = previous;
            }
        }

        /// <summary>
        /// When the feature is not registered the accessor stays null and every consumer degrades to a
        /// no-op instead of throwing.
        /// </summary>
        [TestMethod]
        public void UseTenantSchemaRegistry_WithoutRegistration_LeavesAccessorNull()
        {
            ITenantSchemaRegistryService? previous = TenantSchemaRegistryAccessor.Current;
            try
            {
                ServiceCollection services = new();
                services.AddLogging();
                using ServiceProvider serviceProvider = services.BuildServiceProvider();

                serviceProvider.UseTenantSchemaRegistry();

                Assert.IsNull(TenantSchemaRegistryAccessor.Current);
            }
            finally
            {
                TenantSchemaRegistryAccessor.Current = previous;
            }
        }

        #endregion

        #region Admin endpoint exposure

        /// <summary>
        /// Without DAB_HOT_RELOAD_API_KEY the route is not mapped at all, so the endpoint answers 404
        /// and does not disclose that an administrative trigger exists.
        /// </summary>
        [TestMethod]
        public void MapAdminHotReloadEndpoint_WithoutApiKey_DoesNotMapRoute()
        {
            using ServiceProvider serviceProvider = BuildEndpointServiceProvider(apiKey: null);
            FakeEndpointRouteBuilder endpoints = new(serviceProvider);

            endpoints.MapAdminHotReloadEndpoint();

            Assert.AreEqual(0, endpoints.DataSources.Count, "An unconfigured deployment must leave the route unmapped (404).");
        }

        /// <summary>With a configured key the route is mapped as before.</summary>
        [TestMethod]
        public void MapAdminHotReloadEndpoint_WithApiKey_MapsRoute()
        {
            using ServiceProvider serviceProvider = BuildEndpointServiceProvider(apiKey: "configured-key");
            FakeEndpointRouteBuilder endpoints = new(serviceProvider);

            endpoints.MapAdminHotReloadEndpoint();

            Assert.AreEqual(1, endpoints.DataSources.Count, "A configured key must expose the trigger.");
            Assert.IsTrue(
                endpoints.DataSources
                    .SelectMany(dataSource => dataSource.Endpoints)
                    .OfType<RouteEndpoint>()
                    .Any(endpoint => endpoint.RoutePattern.RawText == AdminEndpointHotReloadSignal.ENDPOINT_PATH),
                "The mapped route must be POST /admin/hot-reload.");
        }

        #endregion

        #region Helpers

        private static ServiceProvider BuildServiceProviderWithRegistry()
        {
            MockFileSystem fileSystem = new();
            fileSystem.AddFile(CONFIG_PATH, new MockFileData(BuildMsSqlConfigJson(declareTenantId: true)));
            FileSystemRuntimeConfigLoader loader = new(fileSystem);
            loader.UpdateConfigFilePath(CONFIG_PATH);

            ServiceCollection services = new();
            services.AddLogging();
            services.AddSingleton(new RuntimeConfigProvider(loader));
            services.AddSingleton(new Mock<Core.Resolvers.Factories.IAbstractQueryManagerFactory>().Object);
            services.AddTenantSchemaRegistry();
            return services.BuildServiceProvider();
        }

        private static ServiceProvider BuildEndpointServiceProvider(string? apiKey)
        {
            ServiceCollection services = new();
            services.AddLogging();
            services.AddRouting();
            services.AddSingleton(new AdminEndpointHotReloadSignal(apiKeyProvider: () => apiKey));
            return services.BuildServiceProvider();
        }

        private static RuntimeConfig LoadMsSqlConfig(bool addSecondTenant, bool declareTenantId = true)
        {
            if (addSecondTenant)
            {
                // RuntimeConfig.UpdateDataSourceNameToDataSource only replaces an existing entry, so a
                // genuine multi-data-source configuration is constructed directly.
                return CreateMultiDataSourceConfig(
                    ("tenant-a", TENANT_A, DatabaseType.MSSQL),
                    ("tenant-b", TENANT_B, DatabaseType.MSSQL));
            }

            MockFileSystem fileSystem = new();
            fileSystem.AddFile(CONFIG_PATH, new MockFileData(BuildMsSqlConfigJson(declareTenantId)));

            FileSystemRuntimeConfigLoader loader = new(fileSystem);
            loader.UpdateConfigFilePath(CONFIG_PATH);
            RuntimeConfigProvider provider = new(loader);
            Assert.IsTrue(provider.TryGetConfig(out RuntimeConfig? config), "The test configuration must load.");

            return config!;
        }

        /// <summary>
        /// Builds a configuration with one data source per tenant, all pointing at the same physical
        /// database — the deployment shape this feature targets. A null tenant id means the data source
        /// declares no tenant.
        /// </summary>
        private static RuntimeConfig CreateMultiDataSourceConfig(
            params (string DataSourceName, string? TenantId, DatabaseType DatabaseType)[] dataSourceDefinitions)
        {
            const string connectionString = "Server=tcp:127.0.0.1,1433;Database=tenants;User ID=sa;Password=Placeholder1!;TrustServerCertificate=true;";

            Dictionary<string, DataSource> dataSources = new();
            foreach ((string dataSourceName, string? tenantId, DatabaseType databaseType) in dataSourceDefinitions)
            {
                dataSources[dataSourceName] = new DataSource(
                    databaseType,
                    connectionString,
                    tenantId is null
                        ? null
                        : new Dictionary<string, object?> { { TenantSchemaDataSourceExtensions.TENANT_ID_OPTION, tenantId } });
            }

            return new RuntimeConfig(
                Schema: "test-schema",
                DataSource: dataSources[dataSourceDefinitions[0].DataSourceName],
                Runtime: new RuntimeOptions(
                    Rest: new(),
                    GraphQL: new(),
                    Mcp: null,
                    Host: new(Cors: null, Authentication: null, Mode: HostMode.Production)),
                Entities: new RuntimeEntities(new Dictionary<string, Entity>()),
                DefaultDataSourceName: dataSourceDefinitions[0].DataSourceName,
                DataSourceNameToDataSource: dataSources,
                EntityNameToDataSourceName: new Dictionary<string, string>());
        }

        private static string BuildMsSqlConfigJson(bool declareTenantId)
        {
            string options = declareTenantId
                ? $@",
    ""options"": {{ ""{TenantSchemaDataSourceExtensions.TENANT_ID_OPTION}"": ""{TENANT_A}"" }}"
                : string.Empty;

            return $@"{{
  ""$schema"": ""https://github.com/Azure/data-api-builder/releases/download/vmajor.minor.patch/dab.draft.schema.json"",
  ""data-source"": {{
    ""database-type"": ""mssql"",
    ""connection-string"": ""Server=tcp:127.0.0.1,1433;Database=tenants;User ID=sa;Password=Placeholder1!;TrustServerCertificate=true;""{options}
  }},
  ""runtime"": {{
    ""rest"": {{ ""enabled"": true, ""path"": ""/api"" }},
    ""graphql"": {{ ""enabled"": true, ""path"": ""/graphql"" }},
    ""host"": {{ ""mode"": ""production"", ""authentication"": {{ ""provider"": ""AppService"" }} }}
  }},
  ""entities"": {{
    ""Assay"": {{
      ""source"": {{ ""object"": ""dbo.Assay"", ""type"": ""table"" }},
      ""permissions"": [ {{ ""role"": ""anonymous"", ""actions"": [ {{ ""action"": ""*"" }} ] }} ]
    }}
  }}
}}";
        }

        /// <summary>
        /// Minimal endpoint route builder, so route mapping can be asserted without standing up a host.
        /// </summary>
        private sealed class FakeEndpointRouteBuilder : IEndpointRouteBuilder
        {
            public FakeEndpointRouteBuilder(IServiceProvider serviceProvider) => ServiceProvider = serviceProvider;

            public IServiceProvider ServiceProvider { get; }

            public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();

            public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
        }

        #endregion
    }
}
