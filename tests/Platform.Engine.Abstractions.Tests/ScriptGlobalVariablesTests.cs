// Tests for S05-B-02: ScriptGlobalVariables — the SOLE host↔script boundary.
//
// Pins two invariants:
//   1. Secret-accessor wiring: legacy ctors expose a NullSecretAccessor (throws on
//      use); the 3-arg ctor exposes the provided accessor.
//   2. THE MEMORY INVARIANT (§5): the boundary has NO static state members. A static
//      field/property would root the collectible AssemblyLoadContext into the Default
//      context and break the compile-once/unload memory model. This test fails the
//      moment anyone adds a static to the boundary.

using System;
using System.Collections.Generic;
using System.Reflection;
using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Secrets;
using Xunit;

namespace Platform.Engine.Abstractions.Tests;

/// <summary>
/// Verifies the secret-accessor wiring of <see cref="ScriptGlobalVariables"/> and
/// the no-static-state memory invariant (§5).
/// </summary>
public sealed class ScriptGlobalVariablesTests
{
    private static Dictionary<string, object?> NewVars()
        => new(StringComparer.Ordinal);

    [Fact]
    public void OneArgCtor_ExposesNullSecretAccessor_ThatThrowsOnUse()
    {
        var globals = new ScriptGlobalVariables(NewVars());

        Assert.NotNull(globals.Secrets);
        Assert.IsType<NullSecretAccessor>(globals.Secrets);
        Assert.Throws<SecretResolutionException>(
            () => globals.Secrets.Resolve("${secret:env/API}"));
    }

    [Fact]
    public void TwoArgCtor_ExposesNullSecretAccessor_ThatThrowsOnUse()
    {
        var globals = new ScriptGlobalVariables(
            NewVars(),
            new Dictionary<string, object>(StringComparer.Ordinal));

        Assert.IsType<NullSecretAccessor>(globals.Secrets);
        Assert.Throws<SecretResolutionException>(
            () => globals.Secrets.Resolve("${secret:env/API}"));
    }

    [Fact]
    public void ThreeArgCtor_ExposesProvidedAccessor()
    {
        var accessor = new SecretAccessor(
            new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));

        var globals = new ScriptGlobalVariables(
            NewVars(),
            new Dictionary<string, object>(StringComparer.Ordinal),
            accessor);

        Assert.Same(accessor, globals.Secrets);
    }

    [Fact]
    public void ThreeArgCtor_NullAccessor_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ScriptGlobalVariables(
            NewVars(),
            new Dictionary<string, object>(StringComparer.Ordinal),
            secrets: null!));
    }

    /// <summary>
    /// THE MEMORY INVARIANT (§5): the boundary type must declare no static state —
    /// no static fields and no static properties.  A static member could root the
    /// collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/> and defeat
    /// the unload model.  This pins it permanently.
    /// </summary>
    [Fact]
    public void Boundary_HasNoStaticStateMembers()
    {
        const BindingFlags staticAny =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        var type = typeof(ScriptGlobalVariables);

        // No static fields (this also catches compiler-generated backing fields of
        // any static auto-property).
        var staticFields = type.GetFields(staticAny);
        Assert.Empty(staticFields);

        // No static properties.
        var staticProperties = type.GetProperties(staticAny);
        Assert.Empty(staticProperties);
    }
}
