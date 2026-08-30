// Custom fork: Tenant-Aware Dynamic Schema.
// Licensed under the MIT License.

namespace Azure.DataApiBuilder.Core.Custom;

/// <summary>
/// Contract for the dynamic tenant schema registry (<c>dbo.sys_TenantSchemaFields</c>).
///
/// <para>
/// The registry keeps per-tenant field mappings in a thread-safe in-memory cache so no database
/// roundtrip is performed per API request. The cache is hydrated once before entity metadata is
/// populated and re-hydrated by the dual-trigger hot reload engine
/// (<c>Azure.DataApiBuilder.Core.Custom.HotReload.HotReloadEngine</c>), which binds to this
/// contract and skips the step gracefully when the service is not registered.
/// </para>
///
/// <para>
/// Implementations must swap the cache atomically so in-flight HTTP requests always observe a
/// complete, self-consistent snapshot, and must keep the last-known-good snapshot active when
/// <c>dbo.sys_TenantSchemaFields</c> is unreadable.
/// </para>
/// </summary>
public interface ITenantSchemaRegistryService
{
    /// <summary>
    /// True once a load has successfully populated the cache. Remains true after a subsequent
    /// failed reload, because the last-known-good snapshot stays active.
    /// </summary>
    bool IsHydrated { get; }

    /// <summary>
    /// Monotonically increasing version of the currently published snapshot. Incremented only when
    /// a reload actually installs a new snapshot, so callers can detect whether a hot reload
    /// changed the dynamic schema and therefore needs to rebuild entity metadata.
    /// </summary>
    long SnapshotVersion { get; }

    /// <summary>
    /// Re-reads <c>dbo.sys_TenantSchemaFields</c> and atomically swaps the in-memory cache.
    /// Never throws: on a database read error the last-known-good snapshot is retained, a warning
    /// is logged, and the returned value is <c>false</c>.
    /// </summary>
    /// <returns>True when a fresh snapshot was installed, false when the last-known-good snapshot was retained.</returns>
    Task<bool> ReloadRegistryAsync();

    /// <summary>
    /// Hydrates the cache if it has never been loaded; a no-op once <see cref="IsHydrated"/> is
    /// true. Called before entity metadata population so virtual columns can be injected during
    /// schema inference. Concurrent callers share a single load.
    /// </summary>
    Task EnsureHydratedAsync();

    /// <summary>
    /// Returns every registered field for the given tenant entity, or an empty list when the
    /// tenant/entity pair has no registered fields.
    /// </summary>
    /// <param name="tenantId">Tenant owning the entity, as declared by the data source's <c>tenant-id</c> option.</param>
    /// <param name="entityName">DAB entity name.</param>
    IReadOnlyList<TenantSchemaField> GetFields(string tenantId, string entityName);
}

/// <summary>
/// Query helpers layered over <see cref="ITenantSchemaRegistryService"/>. Kept as extension methods
/// so the interface surface stays minimal and easy to fake in tests.
/// </summary>
public static class TenantSchemaRegistryServiceExtensions
{
    /// <summary>
    /// Returns only the projectable <see cref="TenantFieldSourceType.JsonAttribute"/> fields for a
    /// tenant entity — those carrying both a container column and a JSON path. Standard columns are
    /// served by native dab-config mappings and reserved <see cref="TenantFieldSourceType.ForeignLookup"/>
    /// rows are excluded because the engine performs no projection for them yet.
    /// </summary>
    public static IReadOnlyList<TenantSchemaField> GetJsonAttributes(
        this ITenantSchemaRegistryService registry,
        string tenantId,
        string entityName)
    {
        ArgumentNullException.ThrowIfNull(registry);

        IReadOnlyList<TenantSchemaField> fields = registry.GetFields(tenantId, entityName);
        if (fields.Count == 0)
        {
            return Array.Empty<TenantSchemaField>();
        }

        List<TenantSchemaField> jsonAttributes = new();
        foreach (TenantSchemaField field in fields)
        {
            if (field.IsProjectableJsonAttribute)
            {
                jsonAttributes.Add(field);
            }
        }

        return jsonAttributes.Count == 0 ? Array.Empty<TenantSchemaField>() : jsonAttributes;
    }
}
