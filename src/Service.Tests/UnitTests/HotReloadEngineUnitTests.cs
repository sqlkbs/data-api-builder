// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Threading;
using System.Threading.Tasks;
using Azure.DataApiBuilder.Auth;
using Azure.DataApiBuilder.Config;
using Azure.DataApiBuilder.Core.Configurations;
using Azure.DataApiBuilder.Core.Custom.HotReload;
using Azure.DataApiBuilder.Core.Models;
using Azure.DataApiBuilder.Core.Resolvers;
using Azure.DataApiBuilder.Core.Resolvers.Factories;
using Azure.DataApiBuilder.Core.Services;
using Azure.DataApiBuilder.Core.Services.Cache;
using Azure.DataApiBuilder.Core.Services.MetadataProviders;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using ZiggyCreatures.Caching.Fusion;

namespace Azure.DataApiBuilder.Service.Tests.UnitTests;

/// <summary>
/// Unit tests for <see cref="HotReloadEngine"/>: last-known-good protection on invalid
/// configuration, tenant schema registry re-sync, and end-to-end scoped admin-triggered
/// reloads (using a Cosmos data source so no real database is ever contacted).
/// </summary>
[TestClass]
public class HotReloadEngineUnitTests
{
    private const string CONFIG_PATH = @"C:\dab\config\dab-config.json";

    /// <summary>Real temp schema files created for the tests (the validator reads them via the real file system).</summary>
    private static readonly List<string> RealSchemaFiles = new();

    [ClassCleanup]
    public static void ClassCleanup()
    {
        foreach (string schemaFile in RealSchemaFiles)
        {
            try
            {
                File.Delete(schemaFile);
            }
            catch
            {
                // Temp file cleanup is best-effort.
            }
        }

        RealSchemaFiles.Clear();
    }

    /// <summary>
    /// Creates a real on-disk GraphQL schema file (required by RuntimeConfigValidator's
    /// ValidateDatabaseType, which uses the real file system) and returns its path. The same path
    /// is added to the mock file system by the callers so the Cosmos metadata provider can read it.
    /// </summary>
    private static string CreateRealSchemaFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dab-hotreload-schema-{Guid.NewGuid():N}.gql");
        File.WriteAllText(path, "type Query { f: String }");
        RealSchemaFiles.Add(path);
        return path;
    }

    private sealed class FakeTenantRegistry : ITenantSchemaRegistryService
    {
        public int ReloadCount { get; private set; }

        public Task ReloadRegistryAsync()
        {
            ReloadCount++;
            return Task.CompletedTask;
        }
    }

    /// <summary>Captures warning/error logs so test failures include the engine's own diagnostics.</summary>
    private sealed class CaptureLogger : Microsoft.Extensions.Logging.ILogger<HotReloadEngine>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                string message = formatter(state, exception);
                if (exception is not null)
                {
                    message += Environment.NewLine + exception;
                }

                Messages.Add(message);
            }
        }
    }

    /// <summary>
    /// Builds a schema-valid single-Cosmos-datasource configuration. When
    /// <paramref name="includeSecondEntity"/> is true, a second entity is present so the reload
    /// produces a non-empty data-source diff.
    /// </summary>
    private static string BuildCosmosConfigJson(bool includeSecondEntity, string schemaPath)
    {
        string moonEntity = includeSecondEntity
            ? @",
    ""Moon"": {
      ""source"": { ""object"": ""graphqldb.planet"", ""type"": ""table"" },
      ""permissions"": [ { ""role"": ""anonymous"", ""actions"": [ { ""action"": ""read"" } ] } ]
    }"
            : string.Empty;

        return $@"{{
  ""$schema"": ""https://github.com/Azure/data-api-builder/releases/download/vmajor.minor.patch/dab.draft.schema.json"",
  ""data-source"": {{
    ""database-type"": ""cosmosdb_nosql"",
    ""connection-string"": ""AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw=="",
    ""options"": {{ ""database"": ""graphqldb"", ""container"": ""planet"", ""schema"": ""{schemaPath.Replace("\\", "\\\\")}"" }}
  }},
  ""runtime"": {{
    ""rest"": {{ ""enabled"": true, ""path"": ""/api"" }},
    ""graphql"": {{ ""enabled"": true, ""path"": ""/graphql"" }},
    ""host"": {{ ""mode"": ""production"", ""authentication"": {{ ""provider"": ""AppService"" }} }}
  }},
  ""entities"": {{
    ""Planet"": {{
      ""source"": {{ ""object"": ""graphqldb.planet"", ""type"": ""table"" }},
      ""permissions"": [ {{ ""role"": ""anonymous"", ""actions"": [ {{ ""action"": ""read"" }} ] }} ]
    }}{moonEntity}
  }}
}}";
    }

    private static HotReloadEngine BuildEngine(
        string initialConfigJson,
        string schemaPath,
        out MockFileSystem fileSystem,
        out FileSystemRuntimeConfigLoader loader,
        out RuntimeConfigProvider provider,
        out MetadataProviderFactory metadataProviderFactory,
        out FileWatcherHotReloadSignal fileWatcherSignal,
        out AdminEndpointHotReloadSignal adminSignal,
        FakeTenantRegistry registry,
        Microsoft.Extensions.Logging.ILogger<HotReloadEngine>? engineLogger = null)
    {
        fileSystem = new MockFileSystem();
        fileSystem.AddFile(CONFIG_PATH, new MockFileData(initialConfigJson));
        fileSystem.AddFile(schemaPath, new MockFileData("type Query { f: String }"));

        loader = new FileSystemRuntimeConfigLoader(fileSystem);
        loader.UpdateConfigFilePath(CONFIG_PATH);
        provider = new RuntimeConfigProvider(loader);
        Assert.IsTrue(provider.TryGetConfig(out _), "The test configuration must load.");

        // The validator uses the real file system so JSON schema validation reads the packaged
        // dab.draft.schema.json located next to the Core assembly (offline-friendly).
        RuntimeConfigValidator validator = new(provider, new FileSystem(), NullLogger<RuntimeConfigValidator>.Instance);

        HotReloadEventHandler<HotReloadEventArgs> bus = new();
        QueryManagerFactory queryManagerFactory = new(
            provider,
            NullLogger<IQueryExecutor>.Instance,
            new HttpContextAccessor(),
            bus);

        metadataProviderFactory = new MetadataProviderFactory(
            provider,
            validator,
            queryManagerFactory,
            NullLogger<ISqlMetadataProvider>.Instance,
            fileSystem,
            bus,
            isValidateOnly: false);

        CosmosClientProvider cosmosClientProvider = new(provider);
        IAuthorizationResolver authorizationResolver = new Mock<IAuthorizationResolver>().Object;
        GQLFilterParser gqlFilterParser = new(provider, metadataProviderFactory);
        DabCacheService cache = new(new Mock<IFusionCache>().Object, logger: null, new HttpContextAccessor());

        QueryEngineFactory queryEngineFactory = new(
            provider,
            queryManagerFactory,
            metadataProviderFactory,
            cosmosClientProvider,
            new HttpContextAccessor(),
            authorizationResolver,
            gqlFilterParser,
            NullLogger<IQueryEngine>.Instance,
            cache,
            bus);

        MutationEngineFactory mutationEngineFactory = new(
            provider,
            queryManagerFactory,
            metadataProviderFactory,
            cosmosClientProvider,
            queryEngineFactory,
            new HttpContextAccessor(),
            authorizationResolver,
            gqlFilterParser,
            bus);

        fileWatcherSignal = new FileWatcherHotReloadSignal(bus);
        adminSignal = new AdminEndpointHotReloadSignal(apiKeyProvider: () => "test-key");

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<ITenantSchemaRegistryService>(registry);
        IServiceProvider serviceProvider = services.BuildServiceProvider();

        // Copy the out parameter so the lazy delegates below can capture it.
        MetadataProviderFactory metadataProviderFactoryLocal = metadataProviderFactory;

        return new HotReloadEngine(
            provider,
            loader,
            validator,
            () => metadataProviderFactoryLocal,
            () => queryManagerFactory,
            () => queryEngineFactory,
            () => mutationEngineFactory,
            bus,
            fileWatcherSignal,
            adminSignal,
            serviceProvider,
            engineLogger ?? NullLogger<HotReloadEngine>.Instance);
    }

    [TestMethod]
    public async Task AdminReload_InvalidJson_KeepsLastKnownGoodConfiguration()
    {
        // Arrange
        string schemaPath = CreateRealSchemaFile();
        HotReloadEngine engine = BuildEngine(
            BuildCosmosConfigJson(includeSecondEntity: false, schemaPath),
            schemaPath,
            out MockFileSystem fileSystem,
            out _,
            out RuntimeConfigProvider provider,
            out _,
            out _,
            out _,
            new FakeTenantRegistry());
        await engine.StartAsync(CancellationToken.None);
        string lkgJson = provider.GetConfig().ToJson();

        // Act: corrupt the configuration file.
        fileSystem.File.WriteAllText(CONFIG_PATH, "{ this is not valid json");

        await engine.HandleAdminReloadRequestedAsync();

        // Assert
        Assert.AreEqual(HotReloadResult.Failed, engine.LastResult, "A corrupt config must fail the reload.");
        Assert.AreEqual(lkgJson, provider.GetConfig().ToJson(), "The last-known-good configuration must remain active.");
    }

    [TestMethod]
    public async Task FileWatcherSignal_ReloadsTenantSchemaRegistry()
    {
        // Arrange
        FakeTenantRegistry registry = new();
        string schemaPath = CreateRealSchemaFile();
        HotReloadEngine engine = BuildEngine(
            BuildCosmosConfigJson(includeSecondEntity: false, schemaPath),
            schemaPath,
            out _,
            out _,
            out _,
            out _,
            out FileWatcherHotReloadSignal fileWatcherSignal,
            out _,
            registry);
        await engine.StartAsync(CancellationToken.None);

        // Act
        await fileWatcherSignal.TriggerReloadAsync();

        // Assert
        Assert.AreEqual(HotReloadResult.Succeeded, engine.LastResult);
        Assert.AreEqual(1, registry.ReloadCount, "The tenant schema registry must be re-synced when the file-watcher signal fires.");
    }

    [TestMethod]
    public async Task AdminReload_ValidConfigChange_AppliesScopedRefreshAndPromotesLkg()
    {
        // Arrange
        FakeTenantRegistry registry = new();
        CaptureLogger logger = new();
        string schemaPath = CreateRealSchemaFile();
        HotReloadEngine engine = BuildEngine(
            BuildCosmosConfigJson(includeSecondEntity: false, schemaPath),
            schemaPath,
            out MockFileSystem fileSystem,
            out FileSystemRuntimeConfigLoader loader,
            out RuntimeConfigProvider provider,
            out MetadataProviderFactory metadataProviderFactory,
            out _,
            out _,
            registry,
            logger);
        await engine.StartAsync(CancellationToken.None);

        string defaultDataSourceName = provider.GetConfig().DefaultDataSourceName;
        ISqlMetadataProvider planetProviderBefore = metadataProviderFactory.GetMetadataProvider(defaultDataSourceName);

        // Act: add a second entity to the configuration file and hot reload.
        fileSystem.File.WriteAllText(CONFIG_PATH, BuildCosmosConfigJson(includeSecondEntity: true, schemaPath));

        await engine.HandleAdminReloadRequestedAsync();

        // Assert
        Assert.AreEqual(HotReloadResult.Succeeded, engine.LastResult, $"A valid configuration change must apply successfully. Engine log: {string.Join(" | ", logger.Messages)}");
        Assert.IsTrue(provider.GetConfig().Entities.ContainsKey("Moon"), "The new configuration must be live.");

        // Single-datasource configs regenerate their default datasource name (a GUID) on every
        // parse, so recapture the name from the reloaded config.
        string reloadedDataSourceName = provider.GetConfig().DefaultDataSourceName;
        Assert.AreNotSame(
            planetProviderBefore,
            metadataProviderFactory.GetMetadataProvider(reloadedDataSourceName),
            "The changed data source's metadata cache must be invalidated and rebuilt.");
        Assert.IsTrue(registry.ReloadCount >= 1, "The tenant schema registry must be re-synced.");
        Assert.AreEqual(
            provider.GetConfig().ToJson(),
            loader.LastValidRuntimeConfig!.ToJson(),
            "The new configuration must be promoted to last-known-good.");
    }
}
