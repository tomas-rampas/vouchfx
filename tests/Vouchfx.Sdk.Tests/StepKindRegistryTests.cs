// Tests for StepKindRegistry — S02-F-02.
// Covers: assembly discovery, schema capture, key format, freeze-by-construction,
// duplicate-key rejection, and IProviderModule discovery path.
using System.Reflection;
using Vouchfx.Sdk;
using Vouchfx.Sdk.Tests.Providers;
using Xunit;

namespace Vouchfx.Sdk.Tests;

// ── Module-path test fixture ──────────────────────────────────────────────────
// This provider is NOT decorated with [StepProvider]; it is exposed exclusively
// through ModuleOnlyProviderModule so the assembly-scan test can confirm the
// IProviderModule discovery path independently.

/// <summary>
/// Stub provider contributed only via <see cref="ModuleOnlyProviderModule"/>,
/// never via <see cref="StepProviderAttribute"/>.  Uses the distinct key
/// <c>noopmod.echo</c> so it does not collide with <c>noop.echo</c>.
/// </summary>
file sealed class ModuleOnlyProvider : IStepProvider
{
    private static readonly IReadOnlyList<string> s_authors = new[] { "test" };

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("noopmod", "echo");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "0.0.1",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: s_authors);
}

/// <summary>
/// <see cref="IProviderModule"/> that surfaces <see cref="ModuleOnlyProvider"/>
/// for the assembly-scan module-path test.  The class itself has a public
/// parameterless constructor so the registry can instantiate it.
/// </summary>
file sealed class ModuleOnlyProviderModule : IProviderModule
{
    /// <inheritdoc />
    public IEnumerable<IStepProvider> Providers()
    {
        yield return new ModuleOnlyProvider();
    }
}

// ─────────────────────────────────────────────────────────────────────────────

public sealed class StepKindRegistryTests
{
    // ── 1. Assembly discovery finds the reference provider ────────────────────

    /// <summary>
    /// Scanning the test assembly by the assembly-overload discovers
    /// <see cref="NoopEchoProvider"/> (decorated with [StepProvider]) and
    /// makes it retrievable via <c>TryGet("noop.echo", …)</c>.
    /// </summary>
    [Fact]
    public void BuildAndFreeze_FromAssembly_DiscoversReferenceProvider()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(NoopEchoProvider).Assembly });

        var found = registry.TryGet("noop.echo", out var provider);

        Assert.True(found);
        Assert.NotNull(provider);
        Assert.Equal("noop", provider!.Kind.Family);
        Assert.Equal("echo", provider.Kind.Provider);
        Assert.IsType<NoopEchoProvider>(provider.Instance);
    }

    // ── 2. Schema fragment is captured from IStepBinder<> ────────────────────

    /// <summary>
    /// The discovered <c>noop.echo</c> provider implements
    /// <see cref="IStepBinder{TModel}"/>; the registry must capture its
    /// <see cref="JsonSchemaFragment"/> and the JSON must mention the
    /// <c>message</c> field declared in the schema.
    /// </summary>
    [Fact]
    public void BuildAndFreeze_CapturesSchemaFragment()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(NoopEchoProvider).Assembly });

        var found = registry.TryGet("noop.echo", out var provider);

        Assert.True(found);
        Assert.NotNull(provider!.SchemaFragment);
        Assert.Contains("message", provider.SchemaFragment!.Json, StringComparison.Ordinal);
    }

    // ── 3. Key format is <family>.<provider> ─────────────────────────────────

    /// <summary>
    /// The key stored in <see cref="StepKindRegistry.All"/> for
    /// <c>noop.echo</c> must be exactly the string <c>"noop.echo"</c>.
    /// </summary>
    [Fact]
    public void Key_Follows_FamilyDotProvider()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(NoopEchoProvider).Assembly });

        var keys = registry.All.Select(p => $"{p.Kind.Family}.{p.Kind.Provider}").ToList();

        Assert.Contains("noop.echo", keys);
    }

    // ── 4. Freeze-by-construction: no public mutation API ────────────────────

    /// <summary>
    /// <see cref="StepKindRegistry"/> must expose no public instance method
    /// whose name suggests mutation (Add / Remove / Clear / Set*), and
    /// <see cref="StepKindRegistry.All"/> must be assignable to
    /// <see cref="IReadOnlyCollection{T}"/> proving freeze-by-construction.
    /// </summary>
    [Fact]
    public void Registry_HasNoPublicMutationApi()
    {
        var type = typeof(StepKindRegistry);
        var mutatorNames = new[] { "Add", "Remove", "Clear" };

        var publicInstanceMethods = type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToList();

        foreach (var name in mutatorNames)
        {
            Assert.DoesNotContain(name, publicInstanceMethods);
        }

        // No Set* public instance methods.
        Assert.DoesNotContain(
            publicInstanceMethods,
            m => m.StartsWith("Set", StringComparison.Ordinal));

        // All is typed as IReadOnlyCollection<RegisteredProvider>.
        var allProperty = type.GetProperty("All");
        Assert.NotNull(allProperty);
        Assert.True(
            typeof(IReadOnlyCollection<RegisteredProvider>)
                .IsAssignableFrom(allProperty!.PropertyType),
            $"All must be assignable to IReadOnlyCollection<RegisteredProvider>, " +
            $"but property type is {allProperty.PropertyType}.");
    }

    // ── 5. Duplicate key throws DuplicateStepKindException ───────────────────

    /// <summary>
    /// Passing two providers with identical <c>noop.echo</c> keys to the
    /// provider-collection overload must throw
    /// <see cref="DuplicateStepKindException"/> whose message contains the
    /// colliding key.
    /// </summary>
    [Fact]
    public void BuildAndFreeze_DuplicateKey_Throws()
    {
        var providers = new IStepProvider[]
        {
            new NoopEchoProvider(),
            new NoopEchoProvider(),
        };

        var ex = Assert.Throws<DuplicateStepKindException>(
            () => StepKindRegistry.BuildAndFreeze(providers));

        Assert.Contains("noop.echo", ex.Message, StringComparison.Ordinal);
    }

    // ── 6. IProviderModule discovery path ────────────────────────────────────

    /// <summary>
    /// The assembly scan must discover providers surfaced via
    /// <see cref="IProviderModule"/> even when those providers are not
    /// themselves decorated with <see cref="StepProviderAttribute"/>.
    /// The module-contributed key <c>noopmod.echo</c> must be retrievable.
    /// </summary>
    [Fact]
    public void BuildAndFreeze_DiscoversViaProviderModule()
    {
        // The test assembly contains ModuleOnlyProviderModule which is
        // IProviderModule but NOT [StepProvider].
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(StepKindRegistryTests).Assembly });

        var found = registry.TryGet("noopmod.echo", out var provider);

        Assert.True(found, "Expected noopmod.echo to be discovered via IProviderModule.");
        Assert.NotNull(provider);
        Assert.IsType<ModuleOnlyProvider>(provider!.Instance);
    }

    // ── 7. ReflectionTypeLoadException tolerance ──────────────────────────────

    /// <summary>
    /// A normal assembly (one whose types all load cleanly) must still be scanned
    /// correctly after the <see cref="ReflectionTypeLoadException"/> handling was
    /// added.  This confirms the happy-path behaviour is unchanged.
    /// </summary>
    [Fact]
    public void BuildAndFreeze_CleanAssembly_StillScannedCorrectly()
    {
        // Use the test assembly itself — all types load cleanly.
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(NoopEchoProvider).Assembly });

        // The reference provider must still be discoverable.
        Assert.True(registry.TryGet("noop.echo", out var provider));
        Assert.NotNull(provider);
    }
}
