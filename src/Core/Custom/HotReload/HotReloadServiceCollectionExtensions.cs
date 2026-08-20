// Custom fork: Single-database configuration and in-memory schema hot reloading.
// Licensed under the MIT License.

using Azure.DataApiBuilder.Core.Resolvers.Factories;
using Azure.DataApiBuilder.Core.Services.MetadataProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Azure.DataApiBuilder.Core.Custom.HotReload;

/// <summary>
/// Registration entry point for the custom single-database hot reload engine.
/// </summary>
public static class HotReloadServiceCollectionExtensions
{
    /// <summary>
    /// Registers the dual-trigger signal providers (<see cref="FileWatcherHotReloadSignal"/> and
    /// <see cref="AdminEndpointHotReloadSignal"/>), the <see cref="HotReloadEngine"/> (started as
    /// an <see cref="Microsoft.Extensions.Hosting.IHostedService"/> so its subscriptions are
    /// wired before the host serves traffic), and their dependencies.
    ///
    /// <para>Call site: <c>builder.Services.AddCustomHotReloadEngine();</c> in host setup (Startup.ConfigureServices).</para>
    /// </summary>
    public static IServiceCollection AddCustomHotReloadEngine(this IServiceCollection services)
    {
        // Startup registers the factories under their interfaces only; alias the concrete types
        // through explicit Func<> delegates so the engine can resolve them lazily (Microsoft DI
        // has no built-in Func<T> argument support, and lazy resolution preserves upstream's
        // deferred construction of the query/metadata/authorization graph). The resolution must
        // happen inside the returned delegate — resolving in the registration lambda would
        // defeat the laziness.
        services.TryAddSingleton<Func<MetadataProviderFactory>>(serviceProvider =>
            () => (MetadataProviderFactory)serviceProvider.GetRequiredService<IMetadataProviderFactory>());
        services.TryAddSingleton<Func<QueryManagerFactory>>(serviceProvider =>
            () => (QueryManagerFactory)serviceProvider.GetRequiredService<IAbstractQueryManagerFactory>());
        services.TryAddSingleton<Func<QueryEngineFactory>>(serviceProvider =>
            () => (QueryEngineFactory)serviceProvider.GetRequiredService<IQueryEngineFactory>());
        services.TryAddSingleton<Func<MutationEngineFactory>>(serviceProvider =>
            () => (MutationEngineFactory)serviceProvider.GetRequiredService<IMutationEngineFactory>());

        services.TryAddSingleton<FileWatcherHotReloadSignal>();
        services.TryAddSingleton<AdminEndpointHotReloadSignal>();
        services.TryAddSingleton<HotReloadEngine>();
        services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<HotReloadEngine>());
        return services;
    }
}
