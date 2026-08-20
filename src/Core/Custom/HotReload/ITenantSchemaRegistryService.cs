// Custom fork: Single-database configuration and in-memory schema hot reloading.
// Licensed under the MIT License.

namespace Azure.DataApiBuilder.Core.Custom.HotReload;

/// <summary>
/// Contract for the dynamic tenant schema registry (<c>dbo.sys_TenantCustomFields</c>).
///
/// <para>
/// The registry keeps per-tenant custom JSON field mappings in a thread-safe in-memory cache
/// so no database roundtrip is performed per API request. The registry implementation is
/// delivered by a separate feature branch; the hot-reload engine only binds to this contract,
/// so <c>ReloadRegistryAsync</c> is invoked during a hot reload <b>when the service is
/// registered</b> and is skipped gracefully otherwise.
/// </para>
/// </summary>
public interface ITenantSchemaRegistryService
{
    /// <summary>
    /// Re-reads <c>dbo.sys_TenantCustomFields</c> and atomically swaps the in-memory registry
    /// cache. Implementations must keep the last-known-good cache active when the registry
    /// state is unreadable or invalid.
    /// </summary>
    Task ReloadRegistryAsync();
}
