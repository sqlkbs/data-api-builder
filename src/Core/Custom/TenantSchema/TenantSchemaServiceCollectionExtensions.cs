// Custom fork: Tenant-Aware Dynamic Schema.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Azure.DataApiBuilder.Core.Custom;

/// <summary>
/// Registration entry points for the tenant dynamic schema registry.
/// </summary>
public static class TenantSchemaServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="TenantSchemaRegistryService"/> as the singleton
    /// <see cref="ITenantSchemaRegistryService"/>. The hot reload engine discovers it by interface
    /// and re-hydrates it on every reload signal.
    ///
    /// <para>Call site: <c>builder.Services.AddTenantSchemaRegistry();</c> in host setup (Startup.ConfigureServices).</para>
    /// </summary>
    public static IServiceCollection AddTenantSchemaRegistry(this IServiceCollection services)
    {
        services.TryAddSingleton<TenantSchemaRegistryService>();
        services.TryAddSingleton<ITenantSchemaRegistryService>(
            serviceProvider => serviceProvider.GetRequiredService<TenantSchemaRegistryService>());
        return services;
    }

    /// <summary>
    /// Publishes the registered registry to <see cref="TenantSchemaRegistryAccessor"/> so MS SQL
    /// schema inference can inject each tenant's JSON attributes as virtual columns.
    ///
    /// <para>
    /// Must be called before entity metadata is populated — that is, before
    /// <c>IMetadataProviderFactory.InitializeAsync()</c> — because the virtual columns have to exist
    /// in <c>SourceDefinition.Columns</c> by the time DAB builds its exposed-to-backing name maps,
    /// GraphQL schema and authorization metadata. Host startup runs <c>Startup.Configure</c> before
    /// any <c>IHostedService</c>, which is why this is an explicit call rather than a hosted service.
    /// </para>
    ///
    /// <para>Safe to call when the feature is not registered: the accessor is simply left null.</para>
    /// </summary>
    /// <param name="serviceProvider">Application service provider.</param>
    public static IServiceProvider UseTenantSchemaRegistry(this IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        TenantSchemaRegistryAccessor.Current = serviceProvider.GetService<ITenantSchemaRegistryService>();
        return serviceProvider;
    }
}
