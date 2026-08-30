// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using System.Linq;
using System.Threading.Tasks;
using Azure.DataApiBuilder.Config;
using Azure.DataApiBuilder.Config.ObjectModel;
using Azure.DataApiBuilder.Core.Configurations;
using Azure.DataApiBuilder.Core.Custom;
using Azure.DataApiBuilder.Core.Resolvers.Factories;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Azure.DataApiBuilder.Service.Tests.UnitTests
{
    /// <summary>
    /// Unit tests for the custom tenant dynamic schema registry
    /// (Azure.DataApiBuilder.Core.Custom.TenantSchemaRegistryService).
    ///
    /// Verifies that dbo.sys_TenantSchemaFields rows map onto the TenantSchemaField model, that the
    /// cache is keyed by (TenantId, EntityName) and swapped atomically, that the snapshot version
    /// only advances on a real content change, and that a database read error preserves the
    /// last-known-good snapshot instead of throwing.
    ///
    /// No database is contacted: the registry's SQL fetch seam is overridden.
    /// </summary>
    [TestClass, TestCategory(TestCategory.MSSQL)]
    public class TenantSchemaRegistryServiceUnitTests
    {
        private const string CONFIG_PATH = @"C:\dab\config\dab-config.json";
        private const string TENANT_A = "TENANT-A";
        private const string TENANT_B = "TENANT-B";
        private const string ENTITY_NAME = "Assay";
        private const string CONTAINER_COLUMN = "CustomAttributesJson";

        #region Row mapping and cache initialization

        /// <summary>
        /// Verifies every column of dbo.sys_TenantSchemaFields survives the round trip into the model
        /// and that rows are grouped under their (TenantId, EntityName) key.
        /// </summary>
        [TestMethod]
        public async Task ReloadRegistryAsync_MapsEveryColumnAndGroupsByTenantEntity()
        {
            TenantSchemaField expected = new()
            {
                FieldId = 42,
                TenantId = TENANT_A,
                EntityName = ENTITY_NAME,
                FieldSourceType = TenantFieldSourceType.JsonAttribute,
                PhysicalColumnName = CONTAINER_COLUMN,
                JsonPath = "$.RecoveryRate",
                ApiAlias = "recoveryRate",
                DataType = "decimal(18,6)",
                DisplayName = "Recovery Rate (%)",
                IsVisible = true,
                IsReadOnly = false
            };

            TestableTenantSchemaRegistryService registry = BuildRegistry(new[] { expected });

            Assert.IsTrue(await registry.ReloadRegistryAsync(), "The reload must publish a snapshot.");
            Assert.IsTrue(registry.IsHydrated, "A successful reload must mark the registry hydrated.");

            IReadOnlyList<TenantSchemaField> fields = registry.GetFields(TENANT_A, ENTITY_NAME);
            Assert.AreEqual(1, fields.Count, "The tenant entity must expose exactly the registered field.");

            TenantSchemaField actual = fields[0];
            Assert.AreEqual(42, actual.FieldId);
            Assert.AreEqual(TENANT_A, actual.TenantId);
            Assert.AreEqual(ENTITY_NAME, actual.EntityName);
            Assert.AreEqual(TenantFieldSourceType.JsonAttribute, actual.FieldSourceType);
            Assert.AreEqual(CONTAINER_COLUMN, actual.PhysicalColumnName);
            Assert.AreEqual("$.RecoveryRate", actual.JsonPath);
            Assert.AreEqual("recoveryRate", actual.ApiAlias);
            Assert.AreEqual("decimal(18,6)", actual.DataType);
            Assert.AreEqual("Recovery Rate (%)", actual.DisplayName);
            Assert.IsTrue(actual.IsVisible);
            Assert.IsFalse(actual.IsReadOnly);
        }

        /// <summary>
        /// The FieldSourceType TINYINT column maps to a byte-backed enum, so all three values
        /// permitted by CK_FieldSourceType round-trip.
        /// </summary>
        [DataTestMethod]
        [DataRow((byte)1, TenantFieldSourceType.StandardColumn, DisplayName = "1 maps to StandardColumn")]
        [DataRow((byte)2, TenantFieldSourceType.JsonAttribute, DisplayName = "2 maps to JsonAttribute")]
        [DataRow((byte)3, TenantFieldSourceType.ForeignLookup, DisplayName = "3 maps to ForeignLookup")]
        public void FieldSourceType_IsByteBackedAndMatchesCheckConstraint(byte storedValue, TenantFieldSourceType expected)
        {
            Assert.AreEqual(typeof(byte), Enum.GetUnderlyingType(typeof(TenantFieldSourceType)));
            Assert.AreEqual(expected, (TenantFieldSourceType)storedValue);
        }

        /// <summary>
        /// Entity and tenant names are matched case-insensitively, matching DAB's own treatment of
        /// entity and column names.
        /// </summary>
        [TestMethod]
        public async Task GetFields_MatchesTenantAndEntityCaseInsensitively()
        {
            TestableTenantSchemaRegistryService registry = BuildRegistry(new[] { CreateJsonAttribute("recoveryRate", "$.RecoveryRate") });
            await registry.ReloadRegistryAsync();

            Assert.AreEqual(1, registry.GetFields("tenant-a", "assay").Count, "Lookup must ignore casing.");
            Assert.AreEqual(1, registry.GetFields(TENANT_A.ToUpperInvariant(), ENTITY_NAME.ToUpperInvariant()).Count);
        }

        /// <summary>An unregistered tenant entity yields an empty list rather than null.</summary>
        [TestMethod]
        public async Task GetFields_UnknownTenantEntity_ReturnsEmpty()
        {
            TestableTenantSchemaRegistryService registry = BuildRegistry(new[] { CreateJsonAttribute("recoveryRate", "$.RecoveryRate") });
            await registry.ReloadRegistryAsync();

            Assert.AreEqual(0, registry.GetFields(TENANT_A, "NotRegistered").Count);
            Assert.AreEqual(0, registry.GetFields("NoSuchTenant", ENTITY_NAME).Count);
            Assert.AreEqual(0, registry.GetFields(string.Empty, ENTITY_NAME).Count);
        }

        /// <summary>
        /// GetJsonAttributes filters to projectable JSON attributes only: standard columns are served
        /// by native dab-config mappings and reserved foreign lookups are not projected yet.
        /// </summary>
        [TestMethod]
        public async Task GetJsonAttributes_ReturnsOnlyProjectableJsonAttributes()
        {
            TenantSchemaField standardColumn = CreateField("assayId", TenantFieldSourceType.StandardColumn, "AssayId", jsonPath: null);
            TenantSchemaField foreignLookup = CreateField("siteCode", TenantFieldSourceType.ForeignLookup, "SiteId", jsonPath: null);
            TenantSchemaField pathlessJsonAttribute = CreateField("broken", TenantFieldSourceType.JsonAttribute, CONTAINER_COLUMN, jsonPath: null);
            TenantSchemaField jsonAttribute = CreateJsonAttribute("recoveryRate", "$.RecoveryRate");

            TestableTenantSchemaRegistryService registry = BuildRegistry(
                new[] { standardColumn, foreignLookup, pathlessJsonAttribute, jsonAttribute });
            await registry.ReloadRegistryAsync();

            Assert.AreEqual(4, registry.GetFields(TENANT_A, ENTITY_NAME).Count, "All rows must be cached.");

            IReadOnlyList<TenantSchemaField> jsonAttributes = registry.GetJsonAttributes(TENANT_A, ENTITY_NAME);
            Assert.AreEqual(1, jsonAttributes.Count, "Only the projectable JSON attribute must be returned.");
            Assert.AreEqual("recoveryRate", jsonAttributes[0].ApiAlias);
        }

        #endregion

        #region Tenant scoping

        /// <summary>
        /// Only the tenants declared by this process's data sources are read, so a shared
        /// multi-tenant table is not loaded in its entirety.
        /// </summary>
        [TestMethod]
        public async Task ReloadRegistryAsync_QueriesOnlyTenantsDeclaredByDataSources()
        {
            TestableTenantSchemaRegistryService registry = BuildRegistry(
                new[] { CreateJsonAttribute("recoveryRate", "$.RecoveryRate") });

            await registry.ReloadRegistryAsync();

            Assert.AreEqual(1, registry.Queries.Count, "One data source implies one read.");
            CollectionAssert.AreEquivalent(new[] { TENANT_A }, registry.Queries[0].TenantIds);
        }

        /// <summary>
        /// Rows are indexed under the tenant they belong to, so the same ApiAlias can resolve to a
        /// different JSON path for each tenant. This is the core reason tenant resolution is bound to
        /// the data source rather than to a process-wide schema/table/column catalog: tenants sharing
        /// one physical table would otherwise collide.
        /// </summary>
        [TestMethod]
        public async Task ReloadRegistryAsync_RowsForMultipleTenants_AreIndexedIndependently()
        {
            TenantSchemaField tenantAField = CreateJsonAttribute("recoveryRate", "$.RecoveryRate");
            TenantSchemaField tenantBField = CreateJsonAttribute("recoveryRate", "$.RecRate", tenantId: TENANT_B);

            // Rows are grouped by the TenantId they carry, independently of which tenants were queried.
            TestableTenantSchemaRegistryService registry = BuildRegistry(
                _ => new[] { tenantAField, tenantBField });

            await registry.ReloadRegistryAsync();

            Assert.AreEqual("$.RecoveryRate", registry.GetFields(TENANT_A, ENTITY_NAME)[0].JsonPath);
            Assert.AreEqual("$.RecRate", registry.GetFields(TENANT_B, ENTITY_NAME)[0].JsonPath);
            Assert.AreEqual(
                1,
                registry.GetFields(TENANT_A, ENTITY_NAME).Count,
                "One tenant's mappings must not leak into another's.");
        }

        /// <summary>
        /// A configuration where no data source declares a tenant leaves the feature inactive without
        /// issuing any query.
        /// </summary>
        [TestMethod]
        public async Task ReloadRegistryAsync_NoTenantDataSources_PublishesEmptySnapshotWithoutQuerying()
        {
            TestableTenantSchemaRegistryService registry = BuildRegistry(
                Array.Empty<TenantSchemaField>(),
                declareTenantId: false);

            Assert.IsTrue(await registry.ReloadRegistryAsync());
            Assert.AreEqual(0, registry.Queries.Count, "Without a declared tenant no query may be issued.");
            Assert.AreEqual(0, registry.GetFields(TENANT_A, ENTITY_NAME).Count);
        }

        #endregion

        #region Atomic swap and snapshot versioning

        /// <summary>
        /// A reload replaces the published snapshot wholesale. A list already handed to a caller must
        /// therefore keep its original contents, which is what allows an in-flight request to run to
        /// completion against a consistent schema.
        /// </summary>
        [TestMethod]
        public async Task ReloadRegistryAsync_SwapsSnapshotAtomically_PreviouslyReadListUnaffected()
        {
            List<TenantSchemaField> rows = new() { CreateJsonAttribute("recoveryRate", "$.RecoveryRate") };
            TestableTenantSchemaRegistryService registry = BuildRegistry(rows);
            await registry.ReloadRegistryAsync();

            IReadOnlyList<TenantSchemaField> inFlightSnapshot = registry.GetFields(TENANT_A, ENTITY_NAME);
            Assert.AreEqual(1, inFlightSnapshot.Count);

            // Publish a different schema for the same tenant entity.
            rows.Add(CreateJsonAttribute("goldGrade", "$.GoldGrade"));
            await registry.ReloadRegistryAsync();

            Assert.AreEqual(1, inFlightSnapshot.Count, "The previously published snapshot must not be mutated in place.");
            Assert.AreEqual(2, registry.GetFields(TENANT_A, ENTITY_NAME).Count, "New readers must observe the new snapshot.");
        }

        /// <summary>
        /// Reloading an unchanged tenant schema must not advance the snapshot version, otherwise the
        /// hot reload engine would rebuild entity metadata on every signal for no reason.
        /// </summary>
        [TestMethod]
        public async Task ReloadRegistryAsync_UnchangedContent_DoesNotAdvanceSnapshotVersion()
        {
            TestableTenantSchemaRegistryService registry = BuildRegistry(
                new[] { CreateJsonAttribute("recoveryRate", "$.RecoveryRate") });

            await registry.ReloadRegistryAsync();
            long versionAfterFirstLoad = registry.SnapshotVersion;

            await registry.ReloadRegistryAsync();

            Assert.AreEqual(versionAfterFirstLoad, registry.SnapshotVersion, "An unchanged schema must not advance the version.");
        }

        /// <summary>A real content change advances the snapshot version so metadata is rebuilt.</summary>
        [TestMethod]
        public async Task ReloadRegistryAsync_ChangedContent_AdvancesSnapshotVersion()
        {
            List<TenantSchemaField> rows = new() { CreateJsonAttribute("recoveryRate", "$.RecoveryRate") };
            TestableTenantSchemaRegistryService registry = BuildRegistry(rows);

            await registry.ReloadRegistryAsync();
            long versionAfterFirstLoad = registry.SnapshotVersion;

            rows.Add(CreateJsonAttribute("goldGrade", "$.GoldGrade"));
            await registry.ReloadRegistryAsync();

            Assert.IsTrue(registry.SnapshotVersion > versionAfterFirstLoad, "A changed schema must advance the version.");
        }

        #endregion

        #region Last-known-good protection

        /// <summary>
        /// A failure reading dbo.sys_TenantSchemaFields must not throw and must not discard the
        /// previously published snapshot, so the engine keeps serving the last-known-good schema.
        /// </summary>
        [TestMethod]
        public async Task ReloadRegistryAsync_ReadFailure_KeepsLastKnownGoodAndReportsFalse()
        {
            bool shouldFail = false;
            TestableTenantSchemaRegistryService registry = BuildRegistry(
                _ => shouldFail
                    ? throw new InvalidOperationException("Invalid object name 'dbo.sys_TenantSchemaFields'.")
                    : new[] { CreateJsonAttribute("recoveryRate", "$.RecoveryRate") });

            Assert.IsTrue(await registry.ReloadRegistryAsync());
            long goodVersion = registry.SnapshotVersion;

            shouldFail = true;
            Assert.IsFalse(await registry.ReloadRegistryAsync(), "A read failure must be reported, not thrown.");

            Assert.IsTrue(registry.IsHydrated, "The registry stays hydrated on the last-known-good snapshot.");
            Assert.AreEqual(goodVersion, registry.SnapshotVersion, "A failed reload must not advance the version.");
            Assert.AreEqual(1, registry.GetFields(TENANT_A, ENTITY_NAME).Count, "The last-known-good field mappings must remain served.");
        }

        /// <summary>
        /// When the very first load fails — for example because the table has not been provisioned —
        /// startup continues with an empty schema rather than failing.
        /// </summary>
        [TestMethod]
        public async Task ReloadRegistryAsync_FailureOnFirstLoad_LeavesEmptySnapshotWithoutThrowing()
        {
            TestableTenantSchemaRegistryService registry = BuildRegistry(
                _ => throw new InvalidOperationException("Invalid object name 'dbo.sys_TenantSchemaFields'."));

            Assert.IsFalse(await registry.ReloadRegistryAsync());
            Assert.IsFalse(registry.IsHydrated);
            Assert.AreEqual(0, registry.GetFields(TENANT_A, ENTITY_NAME).Count);
        }

        #endregion

        #region Lazy hydration

        /// <summary>
        /// EnsureHydratedAsync loads exactly once, so schema inference can call it per entity without
        /// issuing a roundtrip each time.
        /// </summary>
        [TestMethod]
        public async Task EnsureHydratedAsync_LoadsOnlyOnce()
        {
            TestableTenantSchemaRegistryService registry = BuildRegistry(
                new[] { CreateJsonAttribute("recoveryRate", "$.RecoveryRate") });

            await registry.EnsureHydratedAsync();
            await registry.EnsureHydratedAsync();
            await registry.EnsureHydratedAsync();

            Assert.AreEqual(1, registry.Queries.Count, "Hydration must be performed at most once.");
            Assert.IsTrue(registry.IsHydrated);
        }

        /// <summary>
        /// A failed first hydration is not retried on every subsequent call, so a missing table cannot
        /// cause one failed roundtrip per entity during startup. An explicit reload still retries.
        /// </summary>
        [TestMethod]
        public async Task EnsureHydratedAsync_AfterFailure_DoesNotRetryButExplicitReloadDoes()
        {
            bool shouldFail = true;
            TestableTenantSchemaRegistryService registry = BuildRegistry(
                _ => shouldFail
                    ? throw new InvalidOperationException("Invalid object name 'dbo.sys_TenantSchemaFields'.")
                    : new[] { CreateJsonAttribute("recoveryRate", "$.RecoveryRate") });

            await registry.EnsureHydratedAsync();
            await registry.EnsureHydratedAsync();
            Assert.AreEqual(1, registry.Queries.Count, "A failed hydration must not be retried per call.");

            shouldFail = false;
            Assert.IsTrue(await registry.ReloadRegistryAsync(), "An explicit reload must retry after a failure.");
            Assert.AreEqual(1, registry.GetFields(TENANT_A, ENTITY_NAME).Count);
        }

        #endregion

        #region Test helpers

        private static TenantSchemaField CreateJsonAttribute(string apiAlias, string jsonPath, string tenantId = TENANT_A)
            => new()
            {
                FieldId = apiAlias.GetHashCode(StringComparison.Ordinal) & 0x7fffffff,
                TenantId = tenantId,
                EntityName = ENTITY_NAME,
                FieldSourceType = TenantFieldSourceType.JsonAttribute,
                PhysicalColumnName = CONTAINER_COLUMN,
                JsonPath = jsonPath,
                ApiAlias = apiAlias,
                DataType = "decimal(18,6)",
                DisplayName = apiAlias,
                IsVisible = true,
                IsReadOnly = false
            };

        private static TenantSchemaField CreateField(
            string apiAlias,
            TenantFieldSourceType fieldSourceType,
            string physicalColumnName,
            string? jsonPath)
            => new()
            {
                FieldId = apiAlias.GetHashCode(StringComparison.Ordinal) & 0x7fffffff,
                TenantId = TENANT_A,
                EntityName = ENTITY_NAME,
                FieldSourceType = fieldSourceType,
                PhysicalColumnName = physicalColumnName,
                JsonPath = jsonPath,
                ApiAlias = apiAlias,
                DataType = "nvarchar",
                DisplayName = apiAlias,
                IsVisible = true,
                IsReadOnly = false
            };

        private static TestableTenantSchemaRegistryService BuildRegistry(
            IEnumerable<TenantSchemaField> rows,
            bool declareTenantId = true)
        {
            // Captured lazily so a test can mutate the row list between reloads.
            return BuildRegistry(
                tenantIds => rows.Where(row => tenantIds.Contains(row.TenantId, StringComparer.OrdinalIgnoreCase)).ToList(),
                declareTenantId);
        }

        private static TestableTenantSchemaRegistryService BuildRegistry(
            Func<string[], IReadOnlyList<TenantSchemaField>> fetch,
            bool declareTenantId = true)
        {
            MockFileSystem fileSystem = new();
            fileSystem.AddFile(CONFIG_PATH, new MockFileData(BuildMsSqlConfigJson(declareTenantId)));

            FileSystemRuntimeConfigLoader loader = new(fileSystem);
            loader.UpdateConfigFilePath(CONFIG_PATH);
            RuntimeConfigProvider provider = new(loader);

            // Force the load: the registry deliberately uses TryGetLoadedConfig so it never triggers
            // configuration loading as a side effect.
            Assert.IsTrue(provider.TryGetConfig(out RuntimeConfig? _), "The test configuration must load.");

            return new TestableTenantSchemaRegistryService(provider, fetch);
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
    ""{ENTITY_NAME}"": {{
      ""source"": {{ ""object"": ""dbo.Assay"", ""type"": ""table"" }},
      ""permissions"": [ {{ ""role"": ""anonymous"", ""actions"": [ {{ ""action"": ""*"" }} ] }} ]
    }}
  }}
}}";
        }

        /// <summary>
        /// Registry whose SQL fetch is replaced, so the caching, snapshot-swap and last-known-good
        /// behavior can be exercised without a database.
        /// </summary>
        private sealed class TestableTenantSchemaRegistryService : TenantSchemaRegistryService
        {
            private readonly Func<string[], IReadOnlyList<TenantSchemaField>> _fetch;

            public TestableTenantSchemaRegistryService(
                RuntimeConfigProvider runtimeConfigProvider,
                Func<string[], IReadOnlyList<TenantSchemaField>> fetch)
                : base(runtimeConfigProvider, new Mock<IAbstractQueryManagerFactory>().Object, logger: null)
            {
                _fetch = fetch;
            }

            /// <summary>Every query issued during the test, in order.</summary>
            public List<(string DataSourceName, string[] TenantIds)> Queries { get; } = new();

            protected override Task<IReadOnlyList<TenantSchemaField>> QueryTenantFieldsAsync(
                string dataSourceName,
                string[] tenantIds)
            {
                Queries.Add((dataSourceName, tenantIds));
                return Task.FromResult(_fetch(tenantIds));
            }
        }

        #endregion
    }
}
