// Custom fork: Tenant-Aware Dynamic Schema.
// Licensed under the MIT License.

namespace Azure.DataApiBuilder.Core.Custom;

/// <summary>
/// Ambient access point to the tenant schema registry for components that cannot receive it through
/// dependency injection.
///
/// <para>
/// <c>MsSqlMetadataProvider</c> is constructed with <c>new</c> by <c>MetadataProviderFactory</c>, so
/// adding a constructor dependency would require modifying that generic, cross-engine factory. This
/// holder keeps the custom feature out of it: the registry is published once during host startup
/// (see <see cref="TenantSchemaServiceCollectionExtensions.UseTenantSchemaRegistry"/>) and read by
/// the metadata provider during schema inference.
/// </para>
///
/// <para>
/// The same ambient-static approach is already used by <c>HotReloadScope</c> in this fork. The value
/// is written once at startup and only read afterwards, so a volatile field is sufficient.
/// </para>
/// </summary>
internal static class TenantSchemaRegistryAccessor
{
    private static ITenantSchemaRegistryService? _current;

    /// <summary>
    /// The registered registry, or <c>null</c> when the tenant dynamic schema feature is not
    /// registered — in which case every consumer degrades to a no-op.
    /// </summary>
    internal static ITenantSchemaRegistryService? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
}
