// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using System.Threading.Tasks;
using Azure.DataApiBuilder.Config;
using Azure.DataApiBuilder.Config.ObjectModel;
using Azure.DataApiBuilder.Core.Configurations;
using Azure.DataApiBuilder.Core.Custom.HotReload;
using Azure.DataApiBuilder.Core.Resolvers;
using Azure.DataApiBuilder.Core.Resolvers.Factories;
using Azure.DataApiBuilder.Core.Services;
using Azure.DataApiBuilder.Core.Services.MetadataProviders;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Azure.DataApiBuilder.Service.Tests.UnitTests;

/// <summary>
/// Verifies that the factory hot reload handlers rebuild ONLY the changed data sources /
/// database types when the hot reload engine installed a scoped change set
/// (<see cref="HotReloadScope"/>), and fall back to DAB's original rebuild-everything
/// behavior when no scope is present.
///
/// <para>Cosmos data sources are used for the metadata-provider tests because
/// <c>CosmosSqlMetadataProvider.InitializeAsync()</c> performs no database roundtrips, keeping
/// the tests fully offline.</para>
/// </summary>
[TestClass]
public class HotReloadFactoryScopingUnitTests
{
    private const string SCHEMA_PATH = @"C:\dab\config\schema.gql";

    private static DataSource BuildCosmosDataSource(string endpoint) => new(
        DatabaseType: DatabaseType.CosmosDB_NoSQL,
        ConnectionString: $"AccountEndpoint={endpoint};AccountKey=abc==;",
        Options: new Dictionary<string, object?>
        {
            ["database"] = "graphqldb",
            ["schema"] = SCHEMA_PATH
        });

    private static RuntimeConfig BuildRuntimeConfig(
        string defaultDataSourceName,
        Dictionary<string, DataSource> dataSources) => new(
            Schema: "https://test/schema.json",
            DataSource: dataSources[defaultDataSourceName],
            Runtime: new RuntimeOptions(Rest: null, GraphQL: null, Mcp: null, Host: null),
            Entities: new RuntimeEntities(new Dictionary<string, Entity>()),
            DefaultDataSourceName: defaultDataSourceName,
            DataSourceNameToDataSource: dataSources,
            EntityNameToDataSourceName: new Dictionary<string, string>());

    private static RuntimeConfigProvider BuildProvider(RuntimeConfig config)
    {
        // Multi-data-source dictionaries are runtime state (not part of the on-disk JSON shape),
        // so inject the RuntimeConfig directly into the loader.
        FileSystemRuntimeConfigLoader loader = new(new MockFileSystem())
        {
            RuntimeConfig = config
        };
        RuntimeConfigProvider provider = new(loader);
        Assert.IsTrue(provider.TryGetConfig(out _), "The test configuration must load.");
        return provider;
    }

    private static MetadataProviderFactory BuildMetadataProviderFactory(
        RuntimeConfigProvider provider,
        HotReloadEventHandler<HotReloadEventArgs>? handler)
    {
        MockFileSystem fileSystem = new();
        fileSystem.AddFile(SCHEMA_PATH, new MockFileData("type Query { f: String }"));
        RuntimeConfigValidator validator = new(provider, fileSystem, NullLogger<RuntimeConfigValidator>.Instance);
        return new MetadataProviderFactory(
            provider,
            validator,
            queryManagerFactory: new Mock<IAbstractQueryManagerFactory>().Object,
            logger: NullLogger<ISqlMetadataProvider>.Instance,
            fileSystem: fileSystem,
            handler: handler,
            isValidateOnly: false);
    }

    [TestMethod]
    public async Task MetadataProviderFactory_ScopedRebuild_ReplacesOnlyChangedDataSource()
    {
        // Arrange: two Cosmos data sources sharing the same database type.
        RuntimeConfigProvider provider = BuildProvider(BuildRuntimeConfig(
            defaultDataSourceName: "A",
            dataSources: new Dictionary<string, DataSource>
            {
                ["A"] = BuildCosmosDataSource("https://a.example/"),
                ["B"] = BuildCosmosDataSource("https://b.example/")
            }));

        MetadataProviderFactory factory = BuildMetadataProviderFactory(provider, handler: null);
        ISqlMetadataProvider a1 = factory.GetMetadataProvider("A");
        ISqlMetadataProvider b1 = factory.GetMetadataProvider("B");

        // Act: scoped change set touching only data source "A".
        HotReloadScope.Current = new ConfigDiff(
            changedDataSourceNames: new[] { "A" },
            changedDatabaseTypes: new[] { DatabaseType.CosmosDB_NoSQL });
        try
        {
            factory.OnConfigChanged(this, new HotReloadEventArgs(DabConfigEvents.METADATA_PROVIDER_FACTORY_ON_CONFIG_CHANGED, message: "test"));
        }
        finally
        {
            HotReloadScope.Clear();
        }

        // Assert
        Assert.AreNotSame(a1, factory.GetMetadataProvider("A"), "The changed data source's metadata cache must be invalidated and rebuilt.");
        Assert.AreSame(b1, factory.GetMetadataProvider("B"), "The unchanged data source must keep its existing in-memory metadata (no rebuild, no roundtrip).");
    }

    [TestMethod]
    public async Task MetadataProviderFactory_WithoutScope_FallsBackToFullRebuild()
    {
        RuntimeConfigProvider provider = BuildProvider(BuildRuntimeConfig(
            defaultDataSourceName: "A",
            dataSources: new Dictionary<string, DataSource>
            {
                ["A"] = BuildCosmosDataSource("https://a.example/"),
                ["B"] = BuildCosmosDataSource("https://b.example/")
            }));

        MetadataProviderFactory factory = BuildMetadataProviderFactory(provider, handler: null);
        ISqlMetadataProvider a1 = factory.GetMetadataProvider("A");
        ISqlMetadataProvider b1 = factory.GetMetadataProvider("B");

        // Act: no HotReloadScope installed → DAB's original rebuild-everything behavior.
        factory.OnConfigChanged(this, new HotReloadEventArgs(DabConfigEvents.METADATA_PROVIDER_FACTORY_ON_CONFIG_CHANGED, message: "test"));

        // Assert
        Assert.AreNotSame(a1, factory.GetMetadataProvider("A"), "The full-rebuild fallback must rebuild every provider.");
        Assert.AreNotSame(b1, factory.GetMetadataProvider("B"), "The full-rebuild fallback must rebuild every provider.");
    }

    [TestMethod]
    public void QueryManagerFactory_ScopedRebuild_ReplacesOnlyChangedDatabaseType()
    {
        // Arrange: MSSQL + MySQL data sources; the connection-pool layer is keyed by database type.
        RuntimeConfigProvider provider = BuildProvider(BuildRuntimeConfig(
            defaultDataSourceName: "MSSQLDB",
            dataSources: new Dictionary<string, DataSource>
            {
                ["MSSQLDB"] = new(DatabaseType.MSSQL, "Server=localhost;Database=mssql;User Id=sa;Password=x;", Options: null),
                ["MYSQLDB"] = new(DatabaseType.MySQL, "Server=localhost;Database=mysql;User Id=root;Password=x;", Options: null)
            }));

        QueryManagerFactory factory = new(
            provider,
            NullLogger<IQueryExecutor>.Instance,
            new HttpContextAccessor(),
            handler: null);

        IQueryExecutor mssql1 = factory.GetQueryExecutor(DatabaseType.MSSQL);
        IQueryExecutor mysql1 = factory.GetQueryExecutor(DatabaseType.MySQL);

        // Act: scoped change set touching only the MSSQL data source.
        HotReloadScope.Current = new ConfigDiff(
            changedDataSourceNames: new[] { "MSSQLDB" },
            changedDatabaseTypes: new[] { DatabaseType.MSSQL });
        try
        {
            factory.OnConfigChanged(this, new HotReloadEventArgs(DabConfigEvents.QUERY_MANAGER_FACTORY_ON_CONFIG_CHANGED, message: "test"));
        }
        finally
        {
            HotReloadScope.Clear();
        }

        // Assert
        Assert.AreNotSame(mssql1, factory.GetQueryExecutor(DatabaseType.MSSQL), "The changed database type's logical connection-pool layer must be rebuilt.");
        Assert.AreSame(mysql1, factory.GetQueryExecutor(DatabaseType.MySQL), "The unchanged database type must keep its existing executor (connection pool).");
    }

    [TestMethod]
    public void RuntimeConfigDiffer_DetectsOnlyChangedDataSource()
    {
        RuntimeConfig oldConfig = BuildRuntimeConfig(
            defaultDataSourceName: "A",
            dataSources: new Dictionary<string, DataSource>
            {
                ["A"] = BuildCosmosDataSource("https://a.example/"),
                ["B"] = BuildCosmosDataSource("https://b.example/")
            });

        RuntimeConfig newConfig = BuildRuntimeConfig(
            defaultDataSourceName: "A",
            dataSources: new Dictionary<string, DataSource>
            {
                ["A"] = BuildCosmosDataSource("https://a.example/"),
                ["B"] = BuildCosmosDataSource("https://changed.example/")
            });

        ConfigDiff diff = RuntimeConfigDiffer.ComputeDiff(oldConfig, newConfig);

        Assert.AreEqual(1, diff.ChangedDataSourceNames.Count, "Only data source B changed.");
        Assert.IsTrue(diff.ChangedDataSourceNames.Contains("B"), "Data source B must be reported as changed.");
        Assert.IsTrue(diff.ChangedDatabaseTypes.Contains(DatabaseType.CosmosDB_NoSQL), "The changed database type must be reported.");
        Assert.IsTrue(diff.HasDataSourceChanges, "The diff must report changes.");
    }

    [TestMethod]
    public void RuntimeConfigDiffer_IdenticalConfigs_ProduceEmptyDiff()
    {
        RuntimeConfig config = BuildRuntimeConfig(
            defaultDataSourceName: "A",
            dataSources: new Dictionary<string, DataSource>
            {
                ["A"] = BuildCosmosDataSource("https://a.example/"),
                ["B"] = BuildCosmosDataSource("https://b.example/")
            });

        ConfigDiff diff = RuntimeConfigDiffer.ComputeDiff(config, config);

        Assert.AreEqual(0, diff.ChangedDataSourceNames.Count, "Identical configurations must produce no changes.");
        Assert.IsFalse(diff.HasDataSourceChanges, "Identical configurations must produce no changes.");
    }
}
