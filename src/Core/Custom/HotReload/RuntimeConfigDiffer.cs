// Custom fork: Single-database configuration and in-memory schema hot reloading.
// Licensed under the MIT License.

using System.Text.Json;
using Azure.DataApiBuilder.Config;
using Azure.DataApiBuilder.Config.ObjectModel;

namespace Azure.DataApiBuilder.Core.Custom.HotReload;

/// <summary>
/// Computes the set of data sources whose configuration changed between two
/// <see cref="RuntimeConfig"/> instances. The comparison is performed per data source on the
/// "slice" of the configuration that affects that database: the <see cref="DataSource"/> itself
/// plus the entities and autoentities mapped to it.
///
/// <para>
/// Slices are serialized to JSON and compared textually. Both sides of a comparison always use
/// the same serializer, so attribute/format conventions cannot skew the result. Global runtime
/// options (REST/GraphQL paths, host mode, telemetry, ...) are intentionally not part of a data
/// source slice: they are picked up by the cheap global refreshes (authorization resolver,
/// OpenAPI document, GraphQL executor eviction) without requiring any database roundtrip.
/// </para>
/// </summary>
internal static class RuntimeConfigDiffer
{
    public static ConfigDiff ComputeDiff(RuntimeConfig previous, RuntimeConfig current)
    {
        List<KeyValuePair<string, DataSource>> previousDataSources = previous.GetDataSourceNamesToDataSourcesIterator().ToList();
        List<KeyValuePair<string, DataSource>> currentDataSources = current.GetDataSourceNamesToDataSourcesIterator().ToList();

        // A single-datasource config regenerates its default datasource name (a GUID) on every
        // parse. When both sides have exactly one datasource whose slice is identical, the name
        // churn must not masquerade as a change.
        if (previousDataSources.Count == 1
            && currentDataSources.Count == 1
            && string.Equals(
                SerializeSlice(previous, previousDataSources[0].Key),
                SerializeSlice(current, currentDataSources[0].Key),
                StringComparison.Ordinal))
        {
            return ConfigDiff.Empty;
        }

        HashSet<string> allDataSourceNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, DataSource> dataSource in previousDataSources)
        {
            allDataSourceNames.Add(dataSource.Key);
        }

        foreach (KeyValuePair<string, DataSource> dataSource in currentDataSources)
        {
            allDataSourceNames.Add(dataSource.Key);
        }

        HashSet<string> changedDataSourceNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (string dataSourceName in allDataSourceNames)
        {
            if (!string.Equals(SerializeSlice(previous, dataSourceName), SerializeSlice(current, dataSourceName), StringComparison.Ordinal))
            {
                changedDataSourceNames.Add(dataSourceName);
            }
        }

        HashSet<DatabaseType> changedDatabaseTypes = new();
        foreach (string dataSourceName in changedDataSourceNames)
        {
            AddDatabaseTypeIfPresent(previous, dataSourceName, changedDatabaseTypes);
            AddDatabaseTypeIfPresent(current, dataSourceName, changedDatabaseTypes);
        }

        return new ConfigDiff(changedDataSourceNames, changedDatabaseTypes);
    }

    private static void AddDatabaseTypeIfPresent(RuntimeConfig config, string dataSourceName, HashSet<DatabaseType> changedDatabaseTypes)
    {
        if (config.CheckDataSourceExists(dataSourceName))
        {
            changedDatabaseTypes.Add(config.GetDataSourceFromDataSourceName(dataSourceName).DatabaseType);
        }
    }

    private static string SerializeSlice(RuntimeConfig config, string dataSourceName)
    {
        if (!config.CheckDataSourceExists(dataSourceName))
        {
            return "<data-source-missing>";
        }

        DataSource dataSource = config.GetDataSourceFromDataSourceName(dataSourceName);

        Dictionary<string, Entity> entities = config.Entities
            .Where(kvp => string.Equals(config.GetDataSourceNameFromEntityName(kvp.Key), dataSourceName, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, Autoentity> autoentities = config.Autoentities
            .Where(kvp => string.Equals(config.GetDataSourceNameFromAutoentityName(kvp.Key), dataSourceName, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        // Serialize with DAB's own options: several config object model types carry
        // [JsonConverter] attributes whose converters are registered there and are not
        // usable with default JsonSerializerOptions.
        JsonSerializerOptions options = RuntimeConfigLoader.GetSerializationOptions(replacementSettings: null);
        return JsonSerializer.Serialize(new { dataSource, entities, autoentities }, options);
    }
}
