// S01-B-04 — Library bootstrap & version-conflict fail-fast spike.
//
// Part A: proves §5.7 canonical client libraries load and unload inside a
//         collectible AssemblyLoadContext.
// Part B: proves the AssemblyGraphGuard detects version conflicts at suite
//         start (§5.6) and that the real loaded closure is conflict-free.
//
// BDD order: this file was written BEFORE AssemblyGraphGuard existed (red),
// then the production type was added to make it green.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Scripting;
using Platform.Engine.Abstractions;
using Platform.Engine.Compilation;
using Xunit;
using Xunit.Abstractions;

namespace Platform.Engine.Compilation.Tests;

/// <summary>
/// S01-B-04: library bootstrap and version-conflict fail-fast spike (§5.6, §5.7).
/// </summary>
public sealed class AssemblyGraphGuardTests
{
    private readonly ITestOutputHelper _output;

    public AssemblyGraphGuardTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // =========================================================================
    // Part A — §5.7 client libraries resolve and load in the collectible context
    // =========================================================================

    // -------------------------------------------------------------------------
    // A1: each client assembly resolves at runtime with a non-null Assembly object.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Asserts that all four §5.7 canonical client assemblies are resolvable at
    /// runtime in the test process, and logs their resolved versions for the
    /// spike evidence record.
    /// </summary>
    [Fact]
    public void A1_ClientAssemblies_ResolveAtRuntime_WithExpectedVersions()
    {
        // Arrange — obtain each Assembly via typeof(…).Assembly.
        var npgsql = typeof(Npgsql.NpgsqlConnection).Assembly;
        var kafka = typeof(Confluent.Kafka.ProducerConfig).Assembly;
        var mongo = typeof(MongoDB.Driver.MongoClient).Assembly;
        var redis = typeof(StackExchange.Redis.ConnectionMultiplexer).Assembly;

        // Assert — each is non-null and has a non-empty name.
        Assert.NotNull(npgsql);
        Assert.NotNull(kafka);
        Assert.NotNull(mongo);
        Assert.NotNull(redis);

        // Log resolved versions as spike evidence.
        _output.WriteLine($"Npgsql           : {npgsql.GetName().Version} ({npgsql.GetName().Name})");
        _output.WriteLine($"Confluent.Kafka   : {kafka.GetName().Version} ({kafka.GetName().Name})");
        _output.WriteLine($"MongoDB.Driver    : {mongo.GetName().Version} ({mongo.GetName().Name})");
        _output.WriteLine($"StackExchange.Redis: {redis.GetName().Version} ({redis.GetName().Name})");

        // Confirm at least a 2-part version is present (major.minor > 0 guards against
        // 0.0.0.0 placeholders that indicate the assembly did not load properly).
        Assert.True(npgsql.GetName().Version!.Major > 0,
            "Npgsql assembly version reported as 0 — assembly may not have loaded correctly.");
        Assert.True(kafka.GetName().Version!.Major > 0,
            "Confluent.Kafka assembly version reported as 0 — assembly may not have loaded correctly.");
        Assert.True(mongo.GetName().Version!.Major > 0,
            "MongoDB.Driver assembly version reported as 0 — assembly may not have loaded correctly.");
        Assert.True(redis.GetName().Version!.Major > 0,
            "StackExchange.Redis assembly version reported as 0 — assembly may not have loaded correctly.");
    }

    // -------------------------------------------------------------------------
    // A2: CSX snippet referencing client types compiles, runs, and its collectible
    //     ALC is reclaimed after dispose/unload.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Compiles a CSX snippet that references all four §5.7 client types (via
    /// <c>typeof(T).FullName</c> — no real connections, no pooled resources),
    /// loads it through <see cref="RoslynScriptCompiler.Load"/>, invokes it, and
    /// verifies: (i) the script resolves all four type names; (ii) the collectible
    /// ALC is reclaimed by the GC after <see cref="LoadedScript.Dispose"/>.
    /// </summary>
    /// <remarks>
    /// The snippet intentionally uses only <c>typeof(T).FullName</c> (a pure static
    /// metadata query) rather than constructing any client objects.  Constructing
    /// Npgsql/Kafka/MongoDB/Redis pools initialises process-wide static state (thread
    /// pools, socket managers) that constitutes the §5.4 closure-leak risk; that is
    /// a Sprint-2 deliverable.  This spike proves the collectible ALC can host a
    /// reference to each client assembly without preventing unload.
    /// </remarks>
    [Fact]
    public async Task A2_CsxSnippet_ReferencingClientTypes_RunsAndAlcIsReclaimed()
    {
        // Arrange — build ScriptOptions with MetadataReferences for the 4 client assemblies.
        var clientOptions = BuildClientScriptOptions();

        // CSX body — pure type-name lookups, zero connections / pooling.
        const string csxSource = """
            var npgsql = typeof(Npgsql.NpgsqlConnection).FullName!;
            var kafka  = typeof(Confluent.Kafka.ProducerConfig).FullName!;
            var mongo  = typeof(MongoDB.Driver.MongoClient).FullName!;
            var redis  = typeof(StackExchange.Redis.ConnectionMultiplexer).FullName!;
            Vars["npgsql_type"] = npgsql;
            Vars["kafka_type"]  = kafka;
            Vars["mongo_type"]  = mongo;
            Vars["redis_type"]  = redis;
            """;

        // Act — compile once.
        var compiled = RoslynScriptCompiler.CompileOnce(csxSource, clientOptions);

        // Load into a single ALC; capture the ALC weak reference BEFORE dispose.
        var (weakRef, vars) = await LoadInvokeClientSnippetAndDisposeAsync(compiled);

        // Assert (i): all four type names resolved inside the CSX body.
        Assert.Equal("Npgsql.NpgsqlConnection", (string?)vars["npgsql_type"]);
        Assert.Equal("Confluent.Kafka.ProducerConfig", (string?)vars["kafka_type"]);
        Assert.Equal("MongoDB.Driver.MongoClient", (string?)vars["mongo_type"]);
        Assert.Equal("StackExchange.Redis.ConnectionMultiplexer", (string?)vars["redis_type"]);

        _output.WriteLine($"Script returned: npgsql={vars["npgsql_type"]}, kafka={vars["kafka_type"]}, " +
                          $"mongo={vars["mongo_type"]}, redis={vars["redis_type"]}");

        // Assert (ii): the collectible ALC is reclaimed within a bounded GC loop.
        const int maxGcCycles = 10;
        for (var i = 0; i < maxGcCycles && weakRef.IsAlive; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.False(weakRef.IsAlive,
            "The collectible ALC hosting the client-type CSX snippet was NOT reclaimed after " +
            "Dispose — a root from one of the §5.7 client assemblies is keeping the ALC alive. " +
            "This is the §5.4 closure-leak risk; investigate which client type is rooting it.");
    }

    /// <summary>
    /// Loads the compiled snippet, invokes it once, disposes the
    /// <see cref="LoadedScript"/>, and returns the ALC weak reference together
    /// with the populated <c>vars</c> dictionary.
    /// </summary>
    /// <remarks>
    /// <see cref="MethodImplOptions.NoInlining"/> ensures the JIT cannot keep any
    /// reference to the <see cref="LoadedScript"/> or the collectible ALC alive on
    /// the caller's stack frame, which is the pre-condition for the GC assertion.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference AlcRef, Dictionary<string, object?> Vars)>
        LoadInvokeClientSnippetAndDisposeAsync(CompiledScript compiled)
    {
        WeakReference alcRef;
        var vars = new Dictionary<string, object?>();
        var globals = new ScriptGlobalVariables(vars);

        var loaded = RoslynScriptCompiler.Load(compiled);
        try
        {
            await loaded.InvokeAsync(globals).ConfigureAwait(false);
            alcRef = loaded.AlcWeakReferenceForTest();
        }
        finally
        {
            loaded.Dispose();
        }

        return (alcRef, vars);
    }

    // =========================================================================
    // Part B — version-conflict fail-fast at suite start (§5.6)
    // =========================================================================

    // -------------------------------------------------------------------------
    // B1: conflicting versions → AssemblyVersionConflictException with clear message.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Constructs a SYNTHETIC set of two <see cref="AssemblyName"/> entries for
    /// <c>Npgsql</c> at version <c>8.0.7.0</c> and <c>10.0.0.0</c>, then asserts
    /// that <see cref="AssemblyGraphGuard.ThrowIfConflicting(IEnumerable{AssemblyName})"/>
    /// throws <see cref="AssemblyVersionConflictException"/> whose message names the
    /// assembly and both versions and includes the recommended action text.
    /// </summary>
    /// <remarks>
    /// Synthetic assembly names are used deliberately — we never actually load two
    /// different Npgsql majors into the process.  CPM pins exactly one version; this
    /// test verifies the guard logic against arbitrary inputs.
    /// </remarks>
    [Fact]
    public void B1_ThrowIfConflicting_TwoVersionsOfSameAssembly_ThrowsWithClearMessage()
    {
        // Arrange — two synthetic Npgsql names at different versions.
        var v1 = new AssemblyName("Npgsql") { Version = new Version(8, 0, 7, 0) };
        var v2 = new AssemblyName("Npgsql") { Version = new Version(10, 0, 0, 0) };
        var names = new[] { v1, v2 };

        // Act + Assert.
        var ex = Assert.Throws<AssemblyVersionConflictException>(
            () => AssemblyGraphGuard.ThrowIfConflicting(names));

        // The message must contain the assembly simple name.
        Assert.Contains("Npgsql", ex.Message, StringComparison.Ordinal);

        // Both conflicting versions must be named.
        Assert.Contains("8.0.7.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("10.0.0.0", ex.Message, StringComparison.Ordinal);

        // The recommended action must be present.
        Assert.Contains("Directory.Packages.props", ex.Message, StringComparison.Ordinal);

        _output.WriteLine($"B1 exception message: {ex.Message}");
    }

    // -------------------------------------------------------------------------
    // B2: non-conflicting sets → no exception.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Asserts that <see cref="AssemblyGraphGuard.ThrowIfConflicting(IEnumerable{AssemblyName})"/>
    /// does NOT throw for: (a) entirely distinct simple names; (b) the same simple name
    /// at exactly one version; (c) an empty set.
    /// </summary>
    [Fact]
    public void B2_ThrowIfConflicting_NonConflictingSets_DoesNotThrow()
    {
        // (a) Distinct simple names — no conflict possible.
        var distinctNames = new[]
        {
            new AssemblyName("Npgsql")          { Version = new Version(8, 0, 7, 0) },
            new AssemblyName("Confluent.Kafka")  { Version = new Version(2, 14, 2, 0) },
            new AssemblyName("MongoDB.Driver")   { Version = new Version(3, 6, 0, 0) },
        };
        // Must not throw.
        AssemblyGraphGuard.ThrowIfConflicting(distinctNames);

        // (b) Same simple name, exactly one distinct version — no conflict.
        var duplicateSameVersion = new[]
        {
            new AssemblyName("Npgsql") { Version = new Version(8, 0, 7, 0) },
            new AssemblyName("Npgsql") { Version = new Version(8, 0, 7, 0) },
        };
        AssemblyGraphGuard.ThrowIfConflicting(duplicateSameVersion);

        // (c) Empty set — trivially no conflict.
        AssemblyGraphGuard.ThrowIfConflicting(Array.Empty<AssemblyName>());
    }

    // -------------------------------------------------------------------------
    // B3: real loaded closure is conflict-free.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs <see cref="AssemblyGraphGuard.ThrowIfConflicting(IEnumerable{Assembly})"/>
    /// over the actual assemblies loaded in the test process (filtered to the client
    /// and engine assemblies this spike exercises) and asserts it does NOT throw —
    /// proving that the real CPM-pinned closure is conflict-free.
    /// </summary>
    [Fact]
    public void B3_ThrowIfConflicting_RealLoadedClosure_DoesNotThrow()
    {
        // Gather all loaded assemblies in the current AppDomain.
        // We pass the full set so the guard exercises everything the process resolved.
        var allLoaded = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .ToList();

        _output.WriteLine($"B3: scanning {allLoaded.Count} loaded assemblies for version conflicts.");

        // Log the §5.7 clients specifically for the spike evidence record.
        foreach (var client in allLoaded.Where(a => a.GetName().Name is
            "Npgsql" or "Confluent.Kafka" or "MongoDB.Driver" or "StackExchange.Redis"))
        {
            _output.WriteLine($"  Client: {client.GetName().Name} {client.GetName().Version}");
        }

        // Must not throw — CPM pins exactly one version of each package.
        AssemblyGraphGuard.ThrowIfConflicting(allLoaded);
    }

    // =========================================================================
    // Private helper — build ScriptOptions for the 4 client assemblies
    // =========================================================================

    /// <summary>
    /// Builds <see cref="ScriptOptions"/> containing <see cref="Microsoft.CodeAnalysis.MetadataReference"/>
    /// entries for all four §5.7 canonical client assemblies plus the required
    /// imports, so the CSX body can reference their types without fully-qualified
    /// namespace paths.
    /// </summary>
    private static ScriptOptions BuildClientScriptOptions()
    {
        static Microsoft.CodeAnalysis.MetadataReference RefFromType(Type t)
        {
            var loc = t.Assembly.Location;
            if (string.IsNullOrEmpty(loc))
                throw new InvalidOperationException(
                    $"Could not resolve assembly location for {t.Assembly.GetName().Name}. " +
                    "Ensure the client assembly is not bundled inside a single-file publish.");
            return Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(loc);
        }

        return ScriptOptions.Default
            .AddReferences(
                RefFromType(typeof(Npgsql.NpgsqlConnection)),
                RefFromType(typeof(Confluent.Kafka.ProducerConfig)),
                RefFromType(typeof(MongoDB.Driver.MongoClient)),
                RefFromType(typeof(StackExchange.Redis.ConnectionMultiplexer)))
            .AddImports(
                "Npgsql",
                "Confluent.Kafka",
                "MongoDB.Driver",
                "StackExchange.Redis");
    }
}
