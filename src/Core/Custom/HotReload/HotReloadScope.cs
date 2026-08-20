// Custom fork: Single-database configuration and in-memory schema hot reloading.
// Licensed under the MIT License.

using Azure.DataApiBuilder.Config.ObjectModel;

namespace Azure.DataApiBuilder.Core.Custom.HotReload;

/// <summary>
/// Result of diffing two runtime configurations at data-source granularity. Only data sources
/// whose configuration slice (data source properties + the entities and autoentities mapped to
/// it) changed are reported, which is what allows a hot reload to leave untouched databases
/// fully alone.
/// </summary>
internal sealed class ConfigDiff
{
    /// <summary>
    /// Data source names whose slice changed between the previous and the new configuration.
    /// A data source that was removed from the config is included here as well.
    /// </summary>
    public IReadOnlySet<string> ChangedDataSourceNames { get; }

    /// <summary>
    /// The <see cref="DatabaseType"/> values of the changed data sources, taken from both the
    /// previous and the new configuration (a type switch also counts as a change). Used to scope
    /// the connection-pool layer (query managers, query/mutation engines), which is keyed by
    /// database type rather than data source name.
    /// </summary>
    public IReadOnlySet<DatabaseType> ChangedDatabaseTypes { get; }

    public bool HasDataSourceChanges => ChangedDataSourceNames.Count > 0;

    public ConfigDiff(IEnumerable<string> changedDataSourceNames, IEnumerable<DatabaseType> changedDatabaseTypes)
    {
        ChangedDataSourceNames = new HashSet<string>(changedDataSourceNames, StringComparer.OrdinalIgnoreCase);
        ChangedDatabaseTypes = new HashSet<DatabaseType>(changedDatabaseTypes);
    }

    public static ConfigDiff Empty { get; } = new(
        changedDataSourceNames: Array.Empty<string>(),
        changedDatabaseTypes: Array.Empty<DatabaseType>());
}

/// <summary>
/// Ambient, process-wide scope that carries the <see cref="ConfigDiff"/> of the hot reload
/// currently being applied.
///
/// <para>
/// DAB's existing hot-reload event bus (<see cref="Config.HotReloadEventHandler{TEventArgs}"/>)
/// delivers events without a change-set payload. The factory <c>OnConfigChanged</c> handlers
/// consult this scope first: when present they rebuild only the changed data sources / database
/// types (via the custom extension methods in <see cref="HotReloadFactoryExtensions"/>), and
/// when absent they fall back to DAB's original rebuild-everything behavior. The scope is
/// installed by <see cref="HotReloadEngine"/>'s change-token callback, which runs synchronously
/// on the reloading thread before any factory event is raised, and is cleared when the reload
/// completes. Reloads are serialized by the engine's semaphore, so a single static slot is safe.
/// </para>
/// </summary>
internal static class HotReloadScope
{
    public static ConfigDiff? Current { get; set; }

    public static void Clear() => Current = null;
}
