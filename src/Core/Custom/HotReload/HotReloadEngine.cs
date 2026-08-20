// Custom fork: Single-database configuration and in-memory schema hot reloading.
// Licensed under the MIT License.

using Azure.DataApiBuilder.Config;
using Azure.DataApiBuilder.Config.ObjectModel;
using Azure.DataApiBuilder.Core.Configurations;
using Azure.DataApiBuilder.Core.Models;
using Azure.DataApiBuilder.Core.Resolvers.Factories;
using Azure.DataApiBuilder.Core.Services.MetadataProviders;
using Azure.DataApiBuilder.Service.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using static Azure.DataApiBuilder.Config.DabConfigEvents;

namespace Azure.DataApiBuilder.Core.Custom.HotReload;

/// <summary>Outcome of the most recent hot reload executed by <see cref="HotReloadEngine"/>.</summary>
public enum HotReloadResult
{
    /// <summary>No hot reload has been executed yet.</summary>
    NotRun,

    /// <summary>The reload completed and the new configuration/schema state is active.</summary>
    Succeeded,

    /// <summary>The reload failed; the last-known-good configuration and schema state remain active.</summary>
    Failed
}

/// <summary>
/// Core hot reload execution engine. Subscribes to the dual-trigger
/// <see cref="IHotReloadSignal"/> providers (<see cref="FileWatcherHotReloadSignal"/> in
/// Development mode and <see cref="AdminEndpointHotReloadSignal"/> for cloud/CI triggers) and,
/// on a request:
///
/// <list type="number">
/// <item>Re-parses the runtime configuration file (with environment/AKV variable replacement).</item>
/// <item>Validates the new configuration using DAB's configuration-only validators (JSON schema,
/// properties, permissions, relationship correctness) — no database roundtrips.</item>
/// <item>Computes the set of data sources whose configuration actually changed
/// (<see cref="RuntimeConfigDiffer"/>) and installs it in <see cref="HotReloadScope"/>.</item>
/// <item>Signals DAB's change token (and, in Development mode, DAB's full hot reload event
/// cascade) so the factory <c>OnConfigChanged</c> handlers rebuild ONLY the changed data
/// sources / database types — the connection-pool layer — while untouched databases keep
/// serving from their existing in-memory state.</item>
/// <item>Evicts the Hot Chocolate request executor (via DAB's existing
/// <c>GRAPHQL_SCHEMA_EVICTION_ON_CONFIG_CHANGED</c> wiring) so the schema is lazily
/// re-stitched from the new configuration on the next GraphQL request.</item>
/// <item>Invokes <see cref="ITenantSchemaRegistryService.ReloadRegistryAsync"/> when the
/// (future) tenant schema registry feature is registered.</item>
/// </list>
///
/// <para>
/// Last-known-good (LKG) protection: a JSON parse failure is already handled by
/// <see cref="FileSystemRuntimeConfigLoader.TryLoadConfig"/> (which keeps the previous config
/// active). Any validation, diff, or scoped-rebuild failure is caught here: the LKG config is
/// restored, the affected data sources are rebuilt from the LKG config, a warning is logged,
/// and the process keeps serving — never crashing the application.
/// </para>
/// </summary>
public sealed class HotReloadEngine : IHostedService
{
    private readonly RuntimeConfigProvider _runtimeConfigProvider;
    private readonly FileSystemRuntimeConfigLoader _configLoader;
    private readonly RuntimeConfigValidator _runtimeConfigValidator;
    private readonly Func<MetadataProviderFactory> _metadataProviderFactory;
    private readonly Func<QueryManagerFactory> _queryManagerFactory;
    private readonly Func<QueryEngineFactory> _queryEngineFactory;
    private readonly Func<MutationEngineFactory> _mutationEngineFactory;
    private readonly HotReloadEventHandler<HotReloadEventArgs> _hotReloadEventHandler;
    private readonly FileWatcherHotReloadSignal _fileWatcherSignal;
    private readonly AdminEndpointHotReloadSignal _adminSignal;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<HotReloadEngine> _logger;

    /// <summary>Serializes reload executions so two triggers can never interleave.</summary>
    private readonly SemaphoreSlim _reloadGate = new(initialCount: 1, maxCount: 1);

    /// <summary>
    /// Change-set installed by the admin driver immediately before the config-changed signal is
    /// raised; consumed (and cleared) by the engine's change-token callback which runs
    /// synchronously inside the signal, before any factory event fires.
    /// </summary>
    private ConfigDiff? _pendingDiff;

    /// <summary>Snapshot of the last successfully applied configuration; the diff baseline.</summary>
    private RuntimeConfig? _lastSeenConfig;

    private IDisposable? _changeTokenRegistration;
    private bool _started;

    /// <summary>
    /// The four factory dependencies are injected as <see cref="Func{TResult}"/> factories so
    /// that constructing the engine (an eager <c>IHostedService</c>) does not eagerly construct
    /// DAB's query/metadata/authorization graph at host startup. Upstream constructs those
    /// services lazily (on first request / during config-change handling); the engine preserves
    /// that ordering by resolving them only when a reload is actually executed.
    /// </summary>
    public HotReloadEngine(
        RuntimeConfigProvider runtimeConfigProvider,
        FileSystemRuntimeConfigLoader configLoader,
        RuntimeConfigValidator runtimeConfigValidator,
        Func<MetadataProviderFactory> metadataProviderFactory,
        Func<QueryManagerFactory> queryManagerFactory,
        Func<QueryEngineFactory> queryEngineFactory,
        Func<MutationEngineFactory> mutationEngineFactory,
        HotReloadEventHandler<HotReloadEventArgs> hotReloadEventHandler,
        FileWatcherHotReloadSignal fileWatcherSignal,
        AdminEndpointHotReloadSignal adminSignal,
        IServiceProvider serviceProvider,
        ILogger<HotReloadEngine> logger)
    {
        _runtimeConfigProvider = runtimeConfigProvider;
        _configLoader = configLoader;
        _runtimeConfigValidator = runtimeConfigValidator;
        _metadataProviderFactory = metadataProviderFactory;
        _queryManagerFactory = queryManagerFactory;
        _queryEngineFactory = queryEngineFactory;
        _mutationEngineFactory = mutationEngineFactory;
        _hotReloadEventHandler = hotReloadEventHandler;
        _fileWatcherSignal = fileWatcherSignal;
        _adminSignal = adminSignal;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>Outcome of the most recent reload; consumed by the admin endpoint to pick its status code.</summary>
    public HotReloadResult LastResult { get; private set; } = HotReloadResult.NotRun;

    /// <summary>Snapshot of the last successfully applied configuration (diff baseline).</summary>
    internal RuntimeConfig? LastSeenConfig => _lastSeenConfig;

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return Task.CompletedTask;
        }

        _started = true;

        // Baseline snapshot. If no config is available yet (late-configured/hosted scenario)
        // the diffing stays disabled and DAB's original rebuild-everything fallback applies.
        if (_runtimeConfigProvider.TryGetLoadedConfig(out RuntimeConfig? loadedConfig))
        {
            _lastSeenConfig = loadedConfig;
        }

        IHotReloadSignal fileSignal = _fileWatcherSignal;
        IHotReloadSignal adminSignal = _adminSignal;
        fileSignal.OnReloadRequested += HandleFileWatcherReloadRequestedAsync;
        adminSignal.OnReloadRequested += HandleAdminReloadRequestedAsync;

        // Registered after RuntimeConfigProvider's own registration, so this callback runs once
        // validation has already passed (or been skipped) and the new config object is live.
        _changeTokenRegistration = ChangeToken.OnChange(_runtimeConfigProvider.GetChangeToken, OnConfigChangeTokenSignaled);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _started = false;
        _changeTokenRegistration?.Dispose();
        _changeTokenRegistration = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Installs the <see cref="HotReloadScope"/> for the reload currently in flight. Runs
    /// synchronously inside the config-changed signal — before any factory event is raised.
    /// </summary>
    private void OnConfigChangeTokenSignaled()
    {
        try
        {
            ConfigDiff? diff = _pendingDiff;
            _pendingDiff = null;

            if (diff is null && _lastSeenConfig is not null && _runtimeConfigProvider.TryGetLoadedConfig(out RuntimeConfig? current))
            {
                // Development file-watcher path: DAB already swapped and validated the config.
                diff = RuntimeConfigDiffer.ComputeDiff(_lastSeenConfig, current);
            }

            HotReloadScope.Current = diff;
        }
        catch (Exception ex)
        {
            HotReloadScope.Clear();
            _logger.LogError(exception: ex, message: "Failed to compute the hot reload change set. Factory handlers will fall back to DAB's rebuild-everything behavior.");
        }
    }

    /// <summary>
    /// Handler for <see cref="FileWatcherHotReloadSignal"/> (Development mode). DAB's own
    /// pipeline has already re-parsed, validated, and applied the configuration and raised the
    /// factory events (scoped via the change-token callback above). This handler completes the
    /// custom post-reload steps: tenant schema registry re-sync and diff-baseline update.
    /// </summary>
    private async Task HandleFileWatcherReloadRequestedAsync()
    {
        await _reloadGate.WaitAsync();
        try
        {
            await ReloadTenantSchemaRegistryAsync();

            if (_runtimeConfigProvider.TryGetLoadedConfig(out RuntimeConfig? current))
            {
                _lastSeenConfig = current;
            }

            LastResult = HotReloadResult.Succeeded;
            _logger.LogInformation("File-watcher hot reload post-processing completed. Tenant schema registry re-synced.");
        }
        catch (Exception ex)
        {
            LastResult = HotReloadResult.Failed;
            _logger.LogWarning(exception: ex, message: "File-watcher hot reload post-processing failed. Serving continues with the last-known-good state.");
        }
        finally
        {
            HotReloadScope.Clear();
            _reloadGate.Release();
        }
    }

    /// <summary>
    /// Handler for <see cref="AdminEndpointHotReloadSignal"/> (production/cloud trigger).
    /// Drives the complete reload end to end, including the parts DAB only performs in
    /// Development mode.
    /// </summary>
    internal async Task HandleAdminReloadRequestedAsync()
    {
        await _reloadGate.WaitAsync();
        ConfigDiff? diff = null;
        try
        {
            LastResult = HotReloadResult.NotRun;

            if (string.IsNullOrWhiteSpace(_configLoader.ConfigFilePath))
            {
                _logger.LogWarning("Admin hot reload requested but no file-based configuration source is available (late-configured/hosted mode). Reload aborted.");
                LastResult = HotReloadResult.Failed;
                return;
            }

            RuntimeConfig previous = _lastSeenConfig ?? _runtimeConfigProvider.GetConfig();

            // Step 1: re-parse the configuration file. On a parse failure the loader keeps the
            // last-known-good config object active and returns false.
            DeserializationVariableReplacementSettings replacementSettings = new(
                azureKeyVaultOptions: null,
                doReplaceEnvVar: true,
                doReplaceAkvVar: true);

            if (!_configLoader.TryLoadConfig(
                    _configLoader.ConfigFilePath,
                    out RuntimeConfig? newConfig,
                    logger: null,
                    isDevMode: previous.IsDevelopmentMode(),
                    replacementSettings: replacementSettings))
            {
                _logger.LogWarning("Admin hot reload aborted: the configuration file could not be parsed. Last-known-good configuration remains active.");
                LastResult = HotReloadResult.Failed;
                return;
            }

            // Step 2: configuration-only validation — no database roundtrips, so databases whose
            // configuration did not change are not touched at all.
            _runtimeConfigValidator.ValidateConfigProperties();
            _runtimeConfigValidator.ValidatePermissionsInConfig(newConfig);
            _runtimeConfigValidator.ValidateRelationshipConfigCorrectness(newConfig);

            ILoggerFactory? loggerFactory = _serviceProvider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
            JsonSchemaValidationResult schemaResult = await _runtimeConfigValidator.ValidateConfigSchema(newConfig, _configLoader.ConfigFilePath, loggerFactory);
            if (!schemaResult.IsValid)
            {
                throw new DataApiBuilderException(
                    message: $"The hot-reloaded configuration failed JSON schema validation: {schemaResult.ErrorMessage}",
                    statusCode: System.Net.HttpStatusCode.BadRequest,
                    subStatusCode: DataApiBuilderException.SubStatusCodes.ConfigValidationError);
            }

            // Step 3: compute the change set at data-source granularity.
            diff = RuntimeConfigDiffer.ComputeDiff(previous, newConfig);

            // Step 4: raise DAB's config-changed signal. The engine's change-token callback
            // installs the diff as the ambient HotReloadScope before any factory event runs.
            // In Development mode DAB also raises the full factory event cascade here; in
            // Production mode it raises only the change token + log-level event, so we fire the
            // factory events explicitly afterwards.
            _pendingDiff = diff;
            try
            {
                _configLoader.SignalConfigReloaded("admin-hot-reload");
            }
            finally
            {
                _pendingDiff = null;
            }

            if (!newConfig.IsDevelopmentMode())
            {
                FireConfigChangedEvents();
            }

            // Step 5: re-sync the dynamic tenant schema registry (null-tolerant; the registry
            // implementation is delivered by its own feature branch).
            await ReloadTenantSchemaRegistryAsync();

            // Step 6: promote the new configuration to last-known-good and record the baseline.
            _configLoader.SetLkgConfig();
            _lastSeenConfig = newConfig;
            LastResult = HotReloadResult.Succeeded;

            _logger.LogInformation(
                "Admin hot reload succeeded. Changed data sources: {DataSourceNames}.",
                string.Join(", ", diff.ChangedDataSourceNames));
        }
        catch (Exception ex)
        {
            LastResult = HotReloadResult.Failed;
            _logger.LogWarning(exception: ex, message: "Admin hot reload failed. Restoring the last-known-good configuration and rolling back the affected data sources.");

            try
            {
                _configLoader.RestoreLkgConfig();

                if (diff is not null && diff.HasDataSourceChanges)
                {
                    RollbackScopedRebuilds(diff);
                }
            }
            catch (Exception rollbackEx)
            {
                _logger.LogError(exception: rollbackEx, message: "Rollback to the last-known-good state failed. Some data sources may require a restart to recover.");
            }
        }
        finally
        {
            HotReloadScope.Clear();
            _reloadGate.Release();
        }
    }

    /// <summary>
    /// Fires DAB's hot reload factory events in the upstream order. With the ambient
    /// <see cref="HotReloadScope"/> installed, the factory guards rebuild only the changed data
    /// sources / database types; the remaining handlers (authorization resolver, OpenAPI
    /// documentor, GraphQL schema creator/eviction/refresh) refresh globally and cheaply with
    /// no database roundtrips.
    /// </summary>
    private void FireConfigChangedEvents()
    {
        const string message = "admin-hot-reload";
        RaiseEvent(QUERY_MANAGER_FACTORY_ON_CONFIG_CHANGED, message);
        RaiseEvent(METADATA_PROVIDER_FACTORY_ON_CONFIG_CHANGED, message);
        RaiseEvent(QUERY_ENGINE_FACTORY_ON_CONFIG_CHANGED, message);
        RaiseEvent(MUTATION_ENGINE_FACTORY_ON_CONFIG_CHANGED, message);
        RaiseEvent(DOCUMENTOR_ON_CONFIG_CHANGED, message);
        RaiseEvent(AUTHZ_RESOLVER_ON_CONFIG_CHANGED, message);
        RaiseEvent(GRAPHQL_SCHEMA_EVICTION_ON_CONFIG_CHANGED, message);
        RaiseEvent(GRAPHQL_SCHEMA_CREATOR_ON_CONFIG_CHANGED, message);
        RaiseEvent(GRAPHQL_SCHEMA_REFRESH_ON_CONFIG_CHANGED, message);
    }

    private void RaiseEvent(string eventName, string message)
        => _hotReloadEventHandler.OnConfigChangedEvent(this, new HotReloadEventArgs(eventName, message));

    /// <summary>
    /// Rebuilds the data sources touched by a failed reload from the restored last-known-good
    /// configuration, so their in-memory metadata and connection-pool state match the config
    /// that is actually being served.
    /// </summary>
    private void RollbackScopedRebuilds(ConfigDiff diff)
    {
        foreach (string dataSourceName in diff.ChangedDataSourceNames)
        {
            try
            {
                _metadataProviderFactory().RebuildDataSourceMetadataProvider(dataSourceName).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(exception: ex, message: "Failed to roll back the metadata provider for data source {DataSourceName}.", dataSourceName);
            }
        }

        foreach (DatabaseType databaseType in diff.ChangedDatabaseTypes)
        {
            try
            {
                _queryManagerFactory().RebuildQueryManager(databaseType);
            }
            catch (Exception ex)
            {
                _logger.LogError(exception: ex, message: "Failed to roll back the query manager for database type {DatabaseType}.", databaseType);
            }

            try
            {
                _queryEngineFactory().RebuildQueryEngine(databaseType);
            }
            catch (Exception ex)
            {
                _logger.LogError(exception: ex, message: "Failed to roll back the query engine for database type {DatabaseType}.", databaseType);
            }

            try
            {
                _mutationEngineFactory().RebuildMutationEngine(databaseType);
            }
            catch (Exception ex)
            {
                _logger.LogError(exception: ex, message: "Failed to roll back the mutation engine for database type {DatabaseType}.", databaseType);
            }
        }
    }

    /// <summary>
    /// Invokes the tenant schema registry reload when the service is registered. The registry
    /// feature is delivered by a separate feature branch; until then this is a graceful no-op.
    /// </summary>
    private async Task ReloadTenantSchemaRegistryAsync()
    {
        ITenantSchemaRegistryService? registry = _serviceProvider.GetService<ITenantSchemaRegistryService>();
        if (registry is null)
        {
            return;
        }

        await registry.ReloadRegistryAsync();
    }
}
