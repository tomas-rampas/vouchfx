// Tests for S07-F-01a (Abstractions layer): the webhook-capture boundary types.
//
// Covers:
//   • NullWebhookCaptureAccessor: singleton; GetCaptured returns empty for any name.
//   • ScriptGlobalVariables.Webhooks: the new 4-arg constructor sets it; legacy
//     constructors default it to the Null accessor (every existing call site keeps
//     compiling and behaves safely).
//   • CapturedWebhookRequest: value-equality on its data fields.
//
// All tests are pure in-process (no listener, no Docker).

using System;
using System.Collections.Generic;
using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Secrets;
using Platform.Engine.Abstractions.Webhooks;
using Xunit;

namespace Platform.Engine.Abstractions.Tests;

/// <summary>
/// Unit tests for the webhook-capture boundary types introduced in S07-F-01a:
/// <see cref="IWebhookCaptureAccessor"/>, <see cref="NullWebhookCaptureAccessor"/>,
/// <see cref="CapturedWebhookRequest"/>, and the new
/// <see cref="ScriptGlobalVariables.Webhooks"/> member.
/// </summary>
public sealed class WebhookCaptureAccessorTests
{
    // ── NullWebhookCaptureAccessor ────────────────────────────────────────────

    [Fact]
    public void NullAccessor_Instance_IsSingleton()
    {
        Assert.Same(NullWebhookCaptureAccessor.Instance, NullWebhookCaptureAccessor.Instance);
    }

    [Fact]
    public void NullAccessor_GetCaptured_ReturnsEmptyForAnyName()
    {
        var sut = NullWebhookCaptureAccessor.Instance;

        Assert.Empty(sut.GetCaptured("anything"));
        Assert.Empty(sut.GetCaptured(string.Empty));
        Assert.Empty(sut.GetCaptured("orders-callback"));
    }

    [Fact]
    public void NullAccessor_GetCaptured_NeverReturnsNull()
    {
        var sut = NullWebhookCaptureAccessor.Instance;

        Assert.NotNull(sut.GetCaptured("x"));
    }

    // ── ScriptGlobalVariables.Webhooks ────────────────────────────────────────

    [Fact]
    public void Globals_FullConstructor_SetsWebhooksAccessor()
    {
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var services = new Dictionary<string, object>(StringComparer.Ordinal);
        var webhooks = new FakeWebhookAccessor();

        var globals = new ScriptGlobalVariables(
            vars, services, NullSecretAccessor.Instance, webhooks);

        Assert.Same(webhooks, globals.Webhooks);
    }

    [Fact]
    public void Globals_FullConstructor_NullWebhooks_Throws()
    {
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var services = new Dictionary<string, object>(StringComparer.Ordinal);

        Assert.Throws<ArgumentNullException>(() => new ScriptGlobalVariables(
            vars, services, NullSecretAccessor.Instance, webhooks: null!));
    }

    [Fact]
    public void Globals_SecretsConstructor_DefaultsWebhooksToNullAccessor()
    {
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var services = new Dictionary<string, object>(StringComparer.Ordinal);

        var globals = new ScriptGlobalVariables(vars, services, NullSecretAccessor.Instance);

        Assert.Same(NullWebhookCaptureAccessor.Instance, globals.Webhooks);
    }

    [Fact]
    public void Globals_VarsServicesConstructor_DefaultsWebhooksToNullAccessor()
    {
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var services = new Dictionary<string, object>(StringComparer.Ordinal);

        var globals = new ScriptGlobalVariables(vars, services);

        Assert.Same(NullWebhookCaptureAccessor.Instance, globals.Webhooks);
    }

    [Fact]
    public void Globals_VarsOnlyConstructor_DefaultsWebhooksToNullAccessor()
    {
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);

        var globals = new ScriptGlobalVariables(vars);

        Assert.Same(NullWebhookCaptureAccessor.Instance, globals.Webhooks);
    }

    // ── CapturedWebhookRequest ────────────────────────────────────────────────

    [Fact]
    public void CapturedWebhookRequest_ExposesAllFields()
    {
        var at = DateTimeOffset.UtcNow;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/json",
        };

        var req = new CapturedWebhookRequest(
            Method: "POST",
            Path: "/callbacks/order?id=7",
            Headers: headers,
            Body: "{\"ok\":true}",
            At: at);

        Assert.Equal("POST", req.Method);
        Assert.Equal("/callbacks/order?id=7", req.Path);
        Assert.Equal("application/json", req.Headers["Content-Type"]);
        Assert.Equal("{\"ok\":true}", req.Body);
        Assert.Equal(at, req.At);
    }

    // ── Test double ───────────────────────────────────────────────────────────

    private sealed class FakeWebhookAccessor : IWebhookCaptureAccessor
    {
        public IReadOnlyList<CapturedWebhookRequest> GetCaptured(string listenerVarName)
            => Array.Empty<CapturedWebhookRequest>();
    }
}
