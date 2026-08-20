// Custom fork: Single-database configuration and in-memory schema hot reloading.
// Licensed under the MIT License.

namespace Azure.DataApiBuilder.Core.Custom.HotReload;

/// <summary>
/// Abstraction over the triggers that can request a single-database configuration and
/// in-memory schema hot reload.
///
/// <para>
/// Implementations are "signals" only: they raise <see cref="OnReloadRequested"/> and must
/// not perform reload work themselves. The <see cref="HotReloadEngine"/> subscribes to the
/// signal(s), re-parses and validates the runtime configuration, computes the set of data
/// sources that actually changed, and drives the scoped refresh so that databases whose
/// configuration was not touched keep serving from their existing in-memory state.
/// </para>
/// </summary>
public interface IHotReloadSignal
{
    /// <summary>
    /// Raised whenever a hot reload is requested. The <see cref="HotReloadEngine"/> is the
    /// intended (typically sole) subscriber.
    /// </summary>
    event Func<Task> OnReloadRequested;

    /// <summary>
    /// Programmatically requests a hot reload by raising <see cref="OnReloadRequested"/>.
    /// Implementations must await every subscribed handler.
    /// </summary>
    Task TriggerReloadAsync();
}
