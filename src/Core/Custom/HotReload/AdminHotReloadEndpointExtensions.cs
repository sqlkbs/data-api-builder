// Custom fork: Single-database configuration and in-memory schema hot reloading.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Azure.DataApiBuilder.Core.Custom.HotReload;

/// <summary>
/// Maps the admin hot-reload trigger endpoint.
/// </summary>
public static class AdminHotReloadEndpointExtensions
{
    /// <summary>
    /// Maps <c>POST /admin/hot-reload</c>. Must be called before
    /// <see cref="ControllerEndpointRouteBuilderExtensions.MapControllers(IEndpointRouteBuilder)"/>
    /// so the literal route wins over <c>RestController</c>'s global catch-all attribute route
    /// (the same ordering trick used for the embeddings endpoint in Startup).
    ///
    /// <para>
    /// The route is mapped only when <see cref="AdminEndpointHotReloadSignal.API_KEY_ENV_VAR"/> is
    /// set. A deployment that has not configured a key therefore leaves the route unmapped and
    /// answers with <c>404 Not Found</c>, which — unlike a <c>403</c> — does not disclose that the
    /// administrative endpoint exists at all. The signal's own disabled/unauthorized responses remain
    /// in place as defence in depth for hosts that map the route directly.
    /// </para>
    /// </summary>
    public static IEndpointRouteBuilder MapAdminHotReloadEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        AdminEndpointHotReloadSignal? adminSignal = endpoints.ServiceProvider.GetService<AdminEndpointHotReloadSignal>();
        if (adminSignal is null || !adminSignal.IsEnabled)
        {
            return endpoints;
        }

        endpoints.MapPost(AdminEndpointHotReloadSignal.ENDPOINT_PATH, async (HttpContext context) =>
        {
            AdminEndpointHotReloadSignal signal = context.RequestServices.GetRequiredService<AdminEndpointHotReloadSignal>();
            HotReloadEngine engine = context.RequestServices.GetRequiredService<HotReloadEngine>();
            await signal.HandleAdminRequestAsync(context, engine);
        });

        return endpoints;
    }
}
