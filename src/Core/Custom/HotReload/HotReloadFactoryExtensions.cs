// Custom fork: Single-database configuration and in-memory schema hot reloading.
// Licensed under the MIT License.

using Azure.DataApiBuilder.Config;
using Azure.DataApiBuilder.Config.ObjectModel;
using Azure.DataApiBuilder.Core.Resolvers.Factories;
using Azure.DataApiBuilder.Core.Services.MetadataProviders;

namespace Azure.DataApiBuilder.Core.Custom.HotReload;

/// <summary>
/// Scoped-rebuild entry points invoked from the single-line guards added to DAB's factory
/// <c>OnConfigChanged</c> handlers. Each factory keeps its default (rebuild-everything) behavior
/// when no <see cref="HotReloadScope"/> is installed, so DAB's original hot reload semantics are
/// fully preserved when this feature is not driving the reload.
///
/// <para>
/// Scoping contract:
/// <list type="bullet">
/// <item><see cref="MetadataProviderFactory"/> is keyed by data source name and holds the
/// per-database in-memory schema caches — only changed data sources are rebuilt (the only step
/// that issues database roundtrips).</item>
/// <item><see cref="QueryManagerFactory"/> holds the logical connection-pool layer (query
/// builders/executors/parsers with per-data-source connection-string builders), keyed by
/// <see cref="DatabaseType"/> — only the changed types are rebuilt.</item>
/// <item><see cref="QueryEngineFactory"/> and <see cref="MutationEngineFactory"/> are keyed by
/// <see cref="DatabaseType"/> and hold no per-data-source state; they are rebuilt for changed
/// types for parity with DAB's existing behavior.</item>
/// </list>
/// </para>
/// </summary>
internal static class HotReloadFactoryExtensions
{
    /// <summary>
    /// Guard used by <see cref="MetadataProviderFactory.OnConfigChanged"/>. Returns <c>true</c>
    /// when a scoped rebuild was applied (the caller must skip its rebuild-everything fallback).
    /// </summary>
    internal static bool TryApplyScopedMetadataRebuild(this MetadataProviderFactory factory, HotReloadEventArgs args)
    {
        ConfigDiff? diff = HotReloadScope.Current;
        if (diff is null)
        {
            return false;
        }

        foreach (string dataSourceName in diff.ChangedDataSourceNames)
        {
            factory.RebuildDataSourceMetadataProvider(dataSourceName).GetAwaiter().GetResult();
        }

        return true;
    }

    /// <summary>
    /// Guard used by <see cref="QueryManagerFactory.OnConfigChanged"/>.
    /// </summary>
    internal static bool TryApplyScopedQueryManagerRebuild(this QueryManagerFactory factory, HotReloadEventArgs args)
    {
        ConfigDiff? diff = HotReloadScope.Current;
        if (diff is null)
        {
            return false;
        }

        foreach (DatabaseType databaseType in diff.ChangedDatabaseTypes)
        {
            factory.RebuildQueryManager(databaseType);
        }

        return true;
    }

    /// <summary>
    /// Guard used by <see cref="QueryEngineFactory.OnConfigChanged"/>.
    /// </summary>
    internal static bool TryApplyScopedQueryEngineRebuild(this QueryEngineFactory factory, HotReloadEventArgs args)
    {
        ConfigDiff? diff = HotReloadScope.Current;
        if (diff is null)
        {
            return false;
        }

        foreach (DatabaseType databaseType in diff.ChangedDatabaseTypes)
        {
            factory.RebuildQueryEngine(databaseType);
        }

        return true;
    }

    /// <summary>
    /// Guard used by <see cref="MutationEngineFactory.OnConfigChanged"/>.
    /// </summary>
    internal static bool TryApplyScopedMutationEngineRebuild(this MutationEngineFactory factory, HotReloadEventArgs args)
    {
        ConfigDiff? diff = HotReloadScope.Current;
        if (diff is null)
        {
            return false;
        }

        foreach (DatabaseType databaseType in diff.ChangedDatabaseTypes)
        {
            factory.RebuildMutationEngine(databaseType);
        }

        return true;
    }
}
