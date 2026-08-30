// Custom fork: Tenant-Aware Dynamic Schema.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Text;
using Azure.DataApiBuilder.Config.ObjectModel;
using Azure.DataApiBuilder.Core.Configurations;
using Azure.DataApiBuilder.Core.Models;
using Azure.DataApiBuilder.Core.Resolvers;
using Azure.DataApiBuilder.Core.Resolvers.Factories;
using Microsoft.Extensions.Logging;

namespace Azure.DataApiBuilder.Core.Custom;

/// <summary>
/// In-memory registry of the tenant dynamic schema declared in <c>dbo.sys_TenantSchemaFields</c>.
///
/// <para>
/// <b>Snapshot semantics.</b> The cache is a <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by
/// a <c>(TenantId, EntityName)</c> tuple. A reload never mutates the published dictionary: it builds
/// a complete replacement off to the side and installs it with
/// <see cref="Interlocked.Exchange{T}(ref T, T)"/>, so an in-flight HTTP request either observes the
/// entire old snapshot or the entire new one and can never see a half-applied schema change.
/// Readers use <see cref="Volatile.Read{T}(ref T)"/> to guarantee they observe the swap.
/// </para>
///
/// <para>
/// <b>Last-known-good protection.</b> <see cref="ReloadRegistryAsync"/> never throws. If
/// <c>dbo.sys_TenantSchemaFields</c> is unreadable — missing table, transient connectivity failure,
/// permission error — the previous snapshot stays published, a warning is logged, and the method
/// reports <c>false</c> so the hot reload engine can continue applying the rest of the reload
/// instead of rolling the configuration back.
/// </para>
///
/// <para>
/// <b>No per-request database access.</b> The cache is hydrated once, lazily, immediately before
/// entity metadata is populated (see <c>MsSqlTenantSchemaExtensions</c>) and thereafter only when a
/// hot reload signal fires.
/// </para>
/// </summary>
public class TenantSchemaRegistryService : ITenantSchemaRegistryService
{
    /// <summary>Fully qualified name of the tenant schema metadata table.</summary>
    public const string TENANT_SCHEMA_FIELDS_TABLE = "dbo.sys_TenantSchemaFields";

    private readonly RuntimeConfigProvider _runtimeConfigProvider;
    private readonly IAbstractQueryManagerFactory _queryManagerFactory;
    private readonly ILogger<TenantSchemaRegistryService>? _logger;

    /// <summary>Serializes loads so a hot reload and the lazy startup hydration cannot race.</summary>
    private readonly SemaphoreSlim _loadGate = new(initialCount: 1, maxCount: 1);

    /// <summary>
    /// Currently published snapshot. Only ever replaced wholesale via
    /// <see cref="Interlocked.Exchange{T}(ref T, T)"/>; never mutated after publication.
    /// </summary>
    private ConcurrentDictionary<(string TenantId, string EntityName), IReadOnlyList<TenantSchemaField>> _cache
        = CreateEmptySnapshot();

    /// <summary>Content signature of the published snapshot, used to detect real schema changes.</summary>
    private long _snapshotSignature;

    private long _snapshotVersion;
    private bool _isHydrated;
    private bool _loadAttempted;

    public TenantSchemaRegistryService(
        RuntimeConfigProvider runtimeConfigProvider,
        IAbstractQueryManagerFactory queryManagerFactory,
        ILogger<TenantSchemaRegistryService>? logger = null)
    {
        _runtimeConfigProvider = runtimeConfigProvider;
        _queryManagerFactory = queryManagerFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    public bool IsHydrated => Volatile.Read(ref _isHydrated);

    /// <inheritdoc/>
    public long SnapshotVersion => Interlocked.Read(ref _snapshotVersion);

    /// <inheritdoc/>
    public async Task<bool> ReloadRegistryAsync()
    {
        await _loadGate.WaitAsync();
        try
        {
            return await LoadCoreAsync();
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task EnsureHydratedAsync()
    {
        if (Volatile.Read(ref _loadAttempted))
        {
            return;
        }

        await _loadGate.WaitAsync();
        try
        {
            // Re-check inside the gate: a concurrent caller may have completed the load already.
            if (Volatile.Read(ref _loadAttempted))
            {
                return;
            }

            await LoadCoreAsync();
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<TenantSchemaField> GetFields(string tenantId, string entityName)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(entityName))
        {
            return Array.Empty<TenantSchemaField>();
        }

        ConcurrentDictionary<(string TenantId, string EntityName), IReadOnlyList<TenantSchemaField>> snapshot =
            Volatile.Read(ref _cache);

        return snapshot.TryGetValue((tenantId, entityName), out IReadOnlyList<TenantSchemaField>? fields)
            ? fields
            : Array.Empty<TenantSchemaField>();
    }

    /// <summary>
    /// Reads every tenant schema row visible to this process and publishes it as a new snapshot.
    /// Callers must hold <see cref="_loadGate"/>. Never throws.
    /// </summary>
    /// <returns>True when a snapshot was published, false when the last-known-good snapshot was retained.</returns>
    private async Task<bool> LoadCoreAsync()
    {
        // Recorded even on failure so a missing table cannot trigger one failed roundtrip per entity
        // during metadata population. A later hot reload still retries.
        Volatile.Write(ref _loadAttempted, true);

        try
        {
            List<TenantSchemaField> rows = await FetchAllTenantFieldsAsync();

            long signature = ComputeSignature(rows);
            ConcurrentDictionary<(string TenantId, string EntityName), IReadOnlyList<TenantSchemaField>> snapshot =
                BuildSnapshot(rows);

            Interlocked.Exchange(ref _cache, snapshot);
            Volatile.Write(ref _isHydrated, true);

            // The version only advances when the content actually differs, so a hot reload of an
            // unchanged tenant schema does not force an unnecessary metadata rebuild.
            if (Interlocked.Exchange(ref _snapshotSignature, signature) != signature)
            {
                Interlocked.Increment(ref _snapshotVersion);
                _logger?.LogInformation(
                    "Tenant schema registry reloaded: {FieldCount} field mapping(s) across {TenantEntityCount} tenant entity/entities. Snapshot version is now {SnapshotVersion}.",
                    rows.Count,
                    snapshot.Count,
                    SnapshotVersion);
            }
            else
            {
                _logger?.LogDebug(
                    "Tenant schema registry reloaded with no content change: {FieldCount} field mapping(s).",
                    rows.Count);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                exception: ex,
                message: "Failed to read {Table}. The last-known-good tenant schema snapshot (version {SnapshotVersion}) remains active and serving continues.",
                TENANT_SCHEMA_FIELDS_TABLE,
                SnapshotVersion);
            return false;
        }
    }

    /// <summary>
    /// Reads the tenant schema rows for every tenant this process serves. Tenants that share one
    /// physical database are collapsed into a single read, so a multi-tenant deployment whose data
    /// sources all point at the same backend performs exactly one roundtrip.
    /// </summary>
    private async Task<List<TenantSchemaField>> FetchAllTenantFieldsAsync()
    {
        if (!_runtimeConfigProvider.TryGetLoadedConfig(out RuntimeConfig? runtimeConfig))
        {
            _logger?.LogDebug("No runtime configuration is loaded yet; tenant schema registry load skipped.");
            return new List<TenantSchemaField>();
        }

        List<(string DataSourceName, string TenantId, string ConnectionString)> tenantDataSources =
            runtimeConfig.GetTenantDataSources().ToList();

        if (tenantDataSources.Count == 0)
        {
            _logger?.LogInformation(
                "No MS SQL data source declares the '{Option}' option, so the tenant dynamic schema is inactive.",
                TenantSchemaDataSourceExtensions.TENANT_ID_OPTION);
            return new List<TenantSchemaField>();
        }

        List<TenantSchemaField> rows = new();

        foreach (IGrouping<string, (string DataSourceName, string TenantId, string ConnectionString)> group
            in tenantDataSources.GroupBy(dataSource => dataSource.ConnectionString, StringComparer.Ordinal))
        {
            string[] tenantIds = group
                .Select(dataSource => dataSource.TenantId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            rows.AddRange(await QueryTenantFieldsAsync(group.First().DataSourceName, tenantIds));
        }

        return rows;
    }

    /// <summary>
    /// Executes the registry query against one data source, restricted to the given tenants.
    ///
    /// <para>
    /// Uses DAB's own query executor rather than a direct <c>SqlConnection</c> so connection string
    /// resolution, managed identity access tokens, retry policy and logging are inherited. Declared
    /// <c>protected virtual</c> so unit tests can supply rows without a live database.
    /// </para>
    /// </summary>
    /// <param name="dataSourceName">Data source to execute against.</param>
    /// <param name="tenantIds">Tenants whose rows should be read.</param>
    protected virtual async Task<IReadOnlyList<TenantSchemaField>> QueryTenantFieldsAsync(
        string dataSourceName,
        string[] tenantIds)
    {
        Dictionary<string, DbConnectionParam> parameters = new();
        StringBuilder tenantPredicate = new();

        for (int i = 0; i < tenantIds.Length; i++)
        {
            string parameterName = $"{BaseQueryStructure.PARAM_NAME_PREFIX}tenant{i}";
            if (i > 0)
            {
                tenantPredicate.Append(", ");
            }

            tenantPredicate.Append(parameterName);
            parameters.Add(parameterName, new DbConnectionParam(tenantIds[i], DbType.String, SqlDbType.NVarChar));
        }

        string query =
            "SELECT FieldId, TenantId, EntityName, FieldSourceType, PhysicalColumnName, JsonPath, " +
            $"ApiAlias, DataType, DisplayName, IsVisible, IsReadOnly FROM {TENANT_SCHEMA_FIELDS_TABLE} " +
            $"WHERE TenantId IN ({tenantPredicate}) ORDER BY TenantId, EntityName, ApiAlias";

        IQueryExecutor queryExecutor = _queryManagerFactory.GetQueryExecutor(DatabaseType.MSSQL);

        List<TenantSchemaField>? fields = await queryExecutor.ExecuteQueryAsync(
            sqltext: query,
            parameters: parameters,
            dataReaderHandler: ReadTenantFieldsAsync,
            dataSourceName: dataSourceName);

        return fields ?? new List<TenantSchemaField>();
    }

    /// <summary>
    /// Materializes <see cref="TenantSchemaField"/> instances from the reader. Rows whose
    /// <c>FieldSourceType</c> falls outside the values permitted by <c>CK_FieldSourceType</c> are
    /// skipped with a warning rather than failing the whole reload.
    /// </summary>
    private async Task<List<TenantSchemaField>> ReadTenantFieldsAsync(DbDataReader reader, List<string>? args)
    {
        List<TenantSchemaField> fields = new();

        int fieldIdOrdinal = reader.GetOrdinal("FieldId");
        int tenantIdOrdinal = reader.GetOrdinal("TenantId");
        int entityNameOrdinal = reader.GetOrdinal("EntityName");
        int fieldSourceTypeOrdinal = reader.GetOrdinal("FieldSourceType");
        int physicalColumnNameOrdinal = reader.GetOrdinal("PhysicalColumnName");
        int jsonPathOrdinal = reader.GetOrdinal("JsonPath");
        int apiAliasOrdinal = reader.GetOrdinal("ApiAlias");
        int dataTypeOrdinal = reader.GetOrdinal("DataType");
        int displayNameOrdinal = reader.GetOrdinal("DisplayName");
        int isVisibleOrdinal = reader.GetOrdinal("IsVisible");
        int isReadOnlyOrdinal = reader.GetOrdinal("IsReadOnly");

        while (await reader.ReadAsync())
        {
            byte rawFieldSourceType = reader.GetByte(fieldSourceTypeOrdinal);
            if (!Enum.IsDefined(typeof(TenantFieldSourceType), rawFieldSourceType))
            {
                _logger?.LogWarning(
                    "Skipping {Table} row FieldId {FieldId}: FieldSourceType {FieldSourceType} is not a recognized value (expected 1, 2 or 3).",
                    TENANT_SCHEMA_FIELDS_TABLE,
                    reader.GetInt32(fieldIdOrdinal),
                    rawFieldSourceType);
                continue;
            }

            fields.Add(new TenantSchemaField
            {
                FieldId = reader.GetInt32(fieldIdOrdinal),
                TenantId = reader.GetString(tenantIdOrdinal),
                EntityName = reader.GetString(entityNameOrdinal),
                FieldSourceType = (TenantFieldSourceType)rawFieldSourceType,
                PhysicalColumnName = reader.GetString(physicalColumnNameOrdinal),
                JsonPath = reader.IsDBNull(jsonPathOrdinal) ? null : reader.GetString(jsonPathOrdinal),
                ApiAlias = reader.GetString(apiAliasOrdinal),
                DataType = reader.GetString(dataTypeOrdinal),
                DisplayName = reader.GetString(displayNameOrdinal),
                IsVisible = reader.GetBoolean(isVisibleOrdinal),
                IsReadOnly = reader.GetBoolean(isReadOnlyOrdinal)
            });
        }

        return fields;
    }

    /// <summary>
    /// Groups rows into the published snapshot shape: one immutable list per
    /// <c>(TenantId, EntityName)</c> pair.
    /// </summary>
    private static ConcurrentDictionary<(string TenantId, string EntityName), IReadOnlyList<TenantSchemaField>>
        BuildSnapshot(List<TenantSchemaField> rows)
    {
        Dictionary<(string TenantId, string EntityName), List<TenantSchemaField>> grouped =
            new(TenantEntityKeyComparer.Instance);

        foreach (TenantSchemaField field in rows)
        {
            (string TenantId, string EntityName) key = (field.TenantId, field.EntityName);
            if (!grouped.TryGetValue(key, out List<TenantSchemaField>? fieldsForKey))
            {
                fieldsForKey = new List<TenantSchemaField>();
                grouped.Add(key, fieldsForKey);
            }

            fieldsForKey.Add(field);
        }

        ConcurrentDictionary<(string TenantId, string EntityName), IReadOnlyList<TenantSchemaField>> snapshot =
            CreateEmptySnapshot();

        foreach ((var key, List<TenantSchemaField> fieldsForKey) in grouped)
        {
            snapshot[key] = fieldsForKey;
        }

        return snapshot;
    }

    private static ConcurrentDictionary<(string TenantId, string EntityName), IReadOnlyList<TenantSchemaField>>
        CreateEmptySnapshot()
        => new(TenantEntityKeyComparer.Instance);

    /// <summary>
    /// Computes an order-independent-of-nothing, content-sensitive 64-bit FNV-1a signature over every
    /// column of every row. Any change to <c>dbo.sys_TenantSchemaFields</c> changes the signature,
    /// which is what tells the hot reload engine that entity metadata must be rebuilt even when
    /// dab-config.json itself did not change.
    /// </summary>
    private static long ComputeSignature(List<TenantSchemaField> rows)
    {
        const ulong FNV_OFFSET_BASIS = 14695981039346656037;
        const ulong FNV_PRIME = 1099511628211;

        ulong hash = FNV_OFFSET_BASIS;

        void Append(string? value)
        {
            if (value is not null)
            {
                foreach (char character in value)
                {
                    hash ^= character;
                    hash *= FNV_PRIME;
                }
            }

            // Field separator, so "ab|c" and "a|bc" hash differently.
            hash ^= '\u001f';
            hash *= FNV_PRIME;
        }

        // Rows arrive ordered by the query's ORDER BY, so the signature is stable across reloads.
        foreach (TenantSchemaField field in rows)
        {
            Append(field.FieldId.ToString());
            Append(field.TenantId);
            Append(field.EntityName);
            Append(((byte)field.FieldSourceType).ToString());
            Append(field.PhysicalColumnName);
            Append(field.JsonPath);
            Append(field.ApiAlias);
            Append(field.DataType);
            Append(field.DisplayName);
            Append(field.IsVisible ? "1" : "0");
            Append(field.IsReadOnly ? "1" : "0");
        }

        return unchecked((long)hash);
    }

    /// <summary>
    /// Case-insensitive comparer for the <c>(TenantId, EntityName)</c> cache key, matching DAB's
    /// own case-insensitive treatment of entity and column names.
    /// </summary>
    private sealed class TenantEntityKeyComparer : IEqualityComparer<(string TenantId, string EntityName)>
    {
        public static readonly TenantEntityKeyComparer Instance = new();

        public bool Equals((string TenantId, string EntityName) x, (string TenantId, string EntityName) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.TenantId, y.TenantId)
                && StringComparer.OrdinalIgnoreCase.Equals(x.EntityName, y.EntityName);

        public int GetHashCode((string TenantId, string EntityName) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.TenantId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.EntityName));
    }
}
