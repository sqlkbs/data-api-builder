// Custom fork: Single-database configuration and in-memory schema hot reloading.
// Licensed under the MIT License.

using Azure.DataApiBuilder.Config;
using Microsoft.Extensions.Logging;

namespace Azure.DataApiBuilder.Core.Custom.HotReload;

/// <summary>
/// Local file-watcher hot reload signal for Development mode.
///
/// <para>
/// This provider does NOT create a duplicate <c>FileSystemWatcher</c>. It adapts DAB's own
/// configuration change detection: <see cref="FileSystemRuntimeConfigLoader"/> (via
/// <see cref="ConfigFileWatcher"/>) already monitors <c>dab-config.json</c> on local disk, and
/// only raises its configuration-changed events in Development mode. This signal subscribes to
/// that existing event stream and raises <see cref="OnReloadRequested"/> once per reload burst,
/// coalescing DAB's multi-event cascade (query-manager, metadata-provider, query-engine, ...)
/// into a single request through a debounce delay (500 ms by default).
/// </para>
///
/// <para>
/// In Production mode DAB never raises those events, so this signal is naturally inactive there;
/// cloud container instances use the authenticated
/// <see cref="AdminEndpointHotReloadSignal"/> (<c>POST /admin/hot-reload</c>) instead.
/// </para>
/// </summary>
public sealed class FileWatcherHotReloadSignal : IHotReloadSignal, IDisposable
{
    /// <summary>Default debounce delay applied before raising <see cref="OnReloadRequested"/>.</summary>
    public static readonly TimeSpan DEFAULT_DEBOUNCE_DELAY = TimeSpan.FromMilliseconds(500);

    private readonly HotReloadEventHandler<HotReloadEventArgs>? _handler;
    private readonly TimeSpan _debounceDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly ILogger<FileWatcherHotReloadSignal>? _logger;

    private CancellationTokenSource? _debounceCancellation;
    private bool _disposed;

    public event Func<Task>? OnReloadRequested;

    public FileWatcherHotReloadSignal(
        HotReloadEventHandler<HotReloadEventArgs>? handler = null,
        TimeSpan? debounceDelay = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        ILogger<FileWatcherHotReloadSignal>? logger = null)
    {
        _handler = handler;
        _debounceDelay = debounceDelay ?? DEFAULT_DEBOUNCE_DELAY;
        _delayAsync = delayAsync ?? ((delay, token) => Task.Delay(delay, token));
        _logger = logger;

        // DAB raises this event only in Development mode (see RuntimeConfigLoader.SignalConfigChanged),
        // so subscribing unconditionally keeps the signal development-only by construction.
        _handler?.Subscribe(DabConfigEvents.METADATA_PROVIDER_FACTORY_ON_CONFIG_CHANGED, OnConfigChangedEvent);
    }

    /// <inheritdoc/>
    public async Task TriggerReloadAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await RaiseOnReloadRequestedAsync();
    }

    private void OnConfigChangedEvent(object? sender, HotReloadEventArgs args)
    {
        if (_disposed || args.EventName != DabConfigEvents.METADATA_PROVIDER_FACTORY_ON_CONFIG_CHANGED)
        {
            return;
        }

        DebounceSignal();
    }

    /// <summary>
    /// Coalesces a burst of configuration-changed events into a single
    /// <see cref="OnReloadRequested"/> raised after <see cref="_debounceDelay"/> of quiet time.
    /// </summary>
    private void DebounceSignal()
    {
        CancellationTokenSource? previous = Interlocked.Exchange(ref _debounceCancellation, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();

        CancellationToken token = _debounceCancellation.Token;

        // Fire-and-forget by design: the event handler signature is synchronous and the debounce
        // timer must not block DAB's hot reload pipeline.
        _ = DebounceThenSignalAsync(token);
    }

    private async Task DebounceThenSignalAsync(CancellationToken token)
    {
        try
        {
            await _delayAsync(_debounceDelay, token);
            await RaiseOnReloadRequestedAsync();
        }
        catch (OperationCanceledException)
        {
            // A newer change replaced this debounce window; it will raise the signal itself.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(exception: ex, message: "File-watcher hot reload signal failed to raise OnReloadRequested.");
        }
    }

    private async Task RaiseOnReloadRequestedAsync()
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_handler is not null)
        {
            // HotReloadEventHandler only supports additive subscription; the handler instance is a
            // process singleton owned by Startup, so leaving the (idempotent, dev-only) subscription
            // in place is safe. Nothing else needs release here.
        }

        CancellationTokenSource? debounce = Interlocked.Exchange(ref _debounceCancellation, null);
        debounce?.Cancel();
        debounce?.Dispose();
    }
}
