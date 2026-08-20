// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Threading.Tasks;
using Azure.DataApiBuilder.Config;
using Azure.DataApiBuilder.Core.Custom.HotReload;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Azure.DataApiBuilder.Service.Tests.UnitTests;

/// <summary>
/// Unit tests for the dual-trigger hot reload signal providers:
/// <see cref="FileWatcherHotReloadSignal"/> (Development-mode adapter over DAB's existing
/// config-file watcher events) and <see cref="AdminEndpointHotReloadSignal"/> (authenticated
/// <c>POST /admin/hot-reload</c> trigger for production/cloud).
/// </summary>
[TestClass]
public class HotReloadSignalsUnitTests
{
    [TestMethod]
    public async Task FileWatcherSignal_DebouncesConfigurationEventBurstIntoSingleReloadRequest()
    {
        // Arrange
        HotReloadEventHandler<HotReloadEventArgs> handler = new();
        FileWatcherHotReloadSignal signal = new(handler: handler, debounceDelay: TimeSpan.FromMilliseconds(50));
        int reloadRequests = 0;
        signal.OnReloadRequested += () =>
        {
            reloadRequests++;
            return Task.CompletedTask;
        };

        HotReloadEventArgs args = new(DabConfigEvents.METADATA_PROVIDER_FACTORY_ON_CONFIG_CHANGED, message: "test");

        // Act — DAB raises several hot reload events per config file change; they must coalesce.
        handler.OnConfigChangedEvent(this, args);
        handler.OnConfigChangedEvent(this, args);
        handler.OnConfigChangedEvent(this, args);
        await Task.Delay(millisecondsDelay: 300);

        // Assert
        Assert.AreEqual(expected: 1, actual: reloadRequests, message: "A burst of config events must produce exactly one reload request after the debounce delay.");
        signal.Dispose();
    }

    [TestMethod]
    public async Task FileWatcherSignal_TriggerReloadAsync_InvokesSubscriber()
    {
        FileWatcherHotReloadSignal signal = new();
        TaskCompletionSource<bool> invoked = new();
        signal.OnReloadRequested += () =>
        {
            invoked.TrySetResult(true);
            return Task.CompletedTask;
        };

        await signal.TriggerReloadAsync();

        Assert.IsTrue(await invoked.Task.WaitAsync(TimeSpan.FromSeconds(1)), "TriggerReloadAsync must invoke OnReloadRequested subscribers.");
    }

    [TestMethod]
    public async Task AdminSignal_TriggerReloadAsync_InvokesSubscriber()
    {
        AdminEndpointHotReloadSignal signal = new(apiKeyProvider: () => "test-key");
        TaskCompletionSource<bool> invoked = new();
        signal.OnReloadRequested += () =>
        {
            invoked.TrySetResult(true);
            return Task.CompletedTask;
        };

        await signal.TriggerReloadAsync();

        Assert.IsTrue(await invoked.Task.WaitAsync(TimeSpan.FromSeconds(1)), "TriggerReloadAsync must invoke OnReloadRequested subscribers.");
    }

    [TestMethod]
    public void AdminSignal_Authorization_RequiresMatchingKey()
    {
        AdminEndpointHotReloadSignal signal = new(apiKeyProvider: () => "super-secret");
        DefaultHttpContext context = new();

        Assert.IsTrue(signal.IsEnabled, "The endpoint must be enabled when a key is configured.");

        Assert.IsFalse(signal.IsAuthorized(context), "A request without the key header must be rejected.");
        context.Request.Headers[AdminEndpointHotReloadSignal.API_KEY_HEADER] = "wrong";
        Assert.IsFalse(signal.IsAuthorized(context), "A request with a wrong key must be rejected.");
        context.Request.Headers[AdminEndpointHotReloadSignal.API_KEY_HEADER] = "super-secret";
        Assert.IsTrue(signal.IsAuthorized(context), "A request with the correct key must be authorized.");
    }

    [TestMethod]
    public async Task AdminSignal_EndpointDisabledWithoutKey_ReturnsForbidden()
    {
        AdminEndpointHotReloadSignal signal = new(apiKeyProvider: () => null);
        DefaultHttpContext context = new();
        context.Response.Body = new MemoryStream();

        await signal.HandleAdminRequestAsync(context, engine: null);

        Assert.AreEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode, "The endpoint must be disabled (403) when no API key is configured.");
    }

    [TestMethod]
    public async Task AdminSignal_EndpointUnauthorizedWithWrongKey_ReturnsUnauthorized()
    {
        AdminEndpointHotReloadSignal signal = new(apiKeyProvider: () => "secret");
        DefaultHttpContext context = new();
        context.Response.Body = new MemoryStream();

        await signal.HandleAdminRequestAsync(context, engine: null);

        Assert.AreEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode, "A request without the API key must be rejected (401).");
    }
}
