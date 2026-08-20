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
    /// </summary>
    public static IEndpointRouteBuilder MapAdminHotReloadEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(AdminEndpointHotReloadSignal.ENDPOINT_PATH, async (HttpContext context) =>
        {
            AdminEndpointHotReloadSignal signal = context.RequestServices.GetRequiredService<AdminEndpointHotReloadSignal>();
            HotReloadEngine engine = context.RequestServices.GetRequiredService<HotReloadEngine>();
            await signal.HandleAdminRequestAsync(context, engine);
        });

        return endpoints;
    }
}
