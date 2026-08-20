// Custom fork: Single-database configuration and in-memory schema hot reloading.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Azure.DataApiBuilder.Core.Custom.HotReload;

/// <summary>
/// Cloud/admin hot reload signal. Exposes <c>POST /admin/hot-reload</c> (mapped via
/// <see cref="AdminHotReloadEndpointExtensions.MapAdminHotReloadEndpoint"/>) so production
/// containers (e.g. Azure Container Apps), webhooks, and CI/CD pipelines can programmatically
/// force a single-database configuration and schema refresh without touching files on disk.
///
/// <para>
/// Security mirrors the existing <c>POST /configuration</c> bootstrap pattern
/// (<c>DAB_CONFIG_AUTH_TOKEN</c> / <c>X-DAB-CONFIG-AUTH</c> in <c>Startup</c>): the endpoint is
/// enabled only when the <see cref="API_KEY_ENV_VAR"/> environment variable is set, and the
/// request must present the key in the <see cref="API_KEY_HEADER"/> header. Comparison uses
/// <see cref="CryptographicOperations.FixedTimeEquals"/> to resist timing attacks. There is no
/// loopback restriction because cloud CI/CD callers arrive from arbitrary network addresses;
/// the key is the single source of authorization.
/// </para>
/// </summary>
public sealed class AdminEndpointHotReloadSignal : IHotReloadSignal
{
    /// <summary>Environment variable that enables the endpoint and holds the required API key.</summary>
    public const string API_KEY_ENV_VAR = "DAB_HOT_RELOAD_API_KEY";

    /// <summary>Request header that must carry the API key.</summary>
    public const string API_KEY_HEADER = "X-DAB-HOT-RELOAD-KEY";

    /// <summary>Endpoint path. Must be exempted from the client-role header authorization middleware in Startup.</summary>
    public const string ENDPOINT_PATH = "/admin/hot-reload";

    private readonly Func<string?> _apiKeyProvider;
    private readonly ILogger<AdminEndpointHotReloadSignal>? _logger;

    public event Func<Task>? OnReloadRequested;

    public AdminEndpointHotReloadSignal(
        Func<string?>? apiKeyProvider = null,
        ILogger<AdminEndpointHotReloadSignal>? logger = null)
    {
        _apiKeyProvider = apiKeyProvider ?? (() => Environment.GetEnvironmentVariable(API_KEY_ENV_VAR));
        _logger = logger;
    }

    /// <summary>
    /// True when an API key has been configured, i.e. the admin endpoint is armed.
    /// </summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(_apiKeyProvider());

    /// <summary>
    /// Validates the request's API key using a constant-time comparison.
    /// </summary>
    public bool IsAuthorized(HttpContext context)
    {
        string? expectedKey = _apiKeyProvider();
        if (string.IsNullOrEmpty(expectedKey))
        {
            return false;
        }

        string? providedKey = context.Request.Headers[API_KEY_HEADER].FirstOrDefault();
        if (string.IsNullOrEmpty(providedKey))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expectedKey),
            Encoding.UTF8.GetBytes(providedKey));
    }

    /// <summary>
    /// Endpoint handler: enforces the API key, then raises <see cref="OnReloadRequested"/> and
    /// reports the engine's outcome (202 on success; 503 when the reload failed and the
    /// last-known-good state remains active; 403 when the endpoint is disabled; 401 on a
    /// missing/wrong key).
    /// </summary>
    public async Task HandleAdminRequestAsync(HttpContext context, HotReloadEngine? engine)
    {
        if (!IsEnabled)
        {
            await WriteJsonResponseAsync(context, StatusCodes.Status403Forbidden,
                $"Admin hot-reload endpoint is disabled. Set the {API_KEY_ENV_VAR} environment variable to enable it.");
            return;
        }

        if (!IsAuthorized(context))
        {
            await WriteJsonResponseAsync(context, StatusCodes.Status401Unauthorized,
                $"Unauthorized. Provide the configured key in the {API_KEY_HEADER} header.");
            return;
        }

        try
        {
            await TriggerReloadAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(exception: ex, message: "Admin hot reload trigger threw an unexpected exception.");
            await WriteJsonResponseAsync(context, StatusCodes.Status503ServiceUnavailable,
                "Hot reload failed with an unexpected exception. See the engine logs for details.");
            return;
        }

        HotReloadResult result = engine?.LastResult ?? HotReloadResult.NotRun;
        if (result == HotReloadResult.Succeeded)
        {
            await WriteJsonResponseAsync(context, StatusCodes.Status202Accepted,
                "Hot reload accepted and applied. Unchanged databases were not affected.");
        }
        else
        {
            await WriteJsonResponseAsync(context, StatusCodes.Status503ServiceUnavailable,
                "Hot reload failed. The last-known-good configuration and schema state remain active. See the engine logs for details.");
        }
    }

    /// <inheritdoc/>
    public async Task TriggerReloadAsync()
    {
        Func<Task>? subscribers = OnReloadRequested;
        if (subscribers is null)
        {
            return;
        }

        foreach (Func<Task> subscriber in subscribers.GetInvocationList().Cast<Func<Task>>())
        {
            await subscriber();
        }
    }

    private static async Task WriteJsonResponseAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new { message });
    }
}
