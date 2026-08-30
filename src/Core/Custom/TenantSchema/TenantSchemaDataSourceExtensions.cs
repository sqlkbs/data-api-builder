// Custom fork: Tenant-Aware Dynamic Schema.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Azure.DataApiBuilder.Config.ObjectModel;

namespace Azure.DataApiBuilder.Core.Custom;

/// <summary>
/// Reads the tenant binding of a data source.
///
/// <para>
/// Every tenant is modelled as its own DAB data source, so the tenant a request belongs to is a
/// property of the <see cref="DataSource"/> rather than of the request. That keeps tenant
/// resolution entirely out of the request path: the metadata provider is already created per data
/// source, so its <c>SourceDefinition</c> instances are implicitly per tenant and no
/// <c>HttpContext</c> claim inspection or per-request database roundtrip is needed.
/// </para>
///
/// <para>
/// Declared in dab-config.json as a free-form data source option, which requires no change to
/// DAB's configuration object model or JSON schema:
/// <code>
/// "data-source": {
///   "database-type": "mssql",
///   "connection-string": "...",
///   "options": { "tenant-id": "TENANT-001" }
/// }
/// </code>
/// </para>
/// </summary>
public static class TenantSchemaDataSourceExtensions
{
    /// <summary>Data source option key carrying the tenant identifier.</summary>
    public const string TENANT_ID_OPTION = "tenant-id";

    /// <summary>
    /// Attempts to read the <c>tenant-id</c> option from a data source. Returns false when the
    /// option is absent or blank, in which case the data source participates in no dynamic tenant
    /// schema and is served purely by its native dab-config entity mappings.
    /// </summary>
    /// <param name="dataSource">Data source to inspect.</param>
    /// <param name="tenantId">The declared tenant identifier when present.</param>
    public static bool TryGetTenantId(this DataSource dataSource, [NotNullWhen(true)] out string? tenantId)
    {
        tenantId = null;

        if (dataSource.Options is null
            || !dataSource.Options.TryGetValue(TENANT_ID_OPTION, out object? value)
            || value is null)
        {
            return false;
        }

        // The option arrives as a string when the configuration was built in memory and as a
        // JsonElement when it was deserialized from dab-config.json, so both are accepted.
        string? candidate = value switch
        {
            string stringValue => stringValue,
            JsonElement { ValueKind: JsonValueKind.String } jsonElement => jsonElement.GetString(),
            JsonElement jsonElement => jsonElement.ToString(),
            _ => value.ToString()
        };

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        tenantId = candidate.Trim();
        return true;
    }

    /// <summary>
    /// Enumerates the MS SQL data sources of a configuration that declare a tenant, projecting the
    /// data source name (needed to execute a query against it), the tenant identifier, and the
    /// connection string (used to collapse tenants that share one physical database into a single
    /// registry read).
    /// </summary>
    /// <param name="runtimeConfig">Configuration to inspect.</param>
    public static IEnumerable<(string DataSourceName, string TenantId, string ConnectionString)> GetTenantDataSources(
        this RuntimeConfig runtimeConfig)
    {
        ArgumentNullException.ThrowIfNull(runtimeConfig);

        foreach ((string dataSourceName, DataSource dataSource) in runtimeConfig.GetDataSourceNamesToDataSourcesIterator())
        {
            if (dataSource.DatabaseType is not (DatabaseType.MSSQL or DatabaseType.DWSQL))
            {
                continue;
            }

            if (dataSource.TryGetTenantId(out string? tenantId))
            {
                yield return (dataSourceName, tenantId, dataSource.ConnectionString);
            }
        }
    }
}
