// Regression coverage for the packaged-tool DCP path self-heal GLUE
// (fix/packaged-tool-dcp-resolution, PR #206) — HeadlessTopology.ApplyDcpPathSelfHeal.
//
// WHY A SEPARATE FILE FROM DcpPathResolverTests
// ----------------------------------------------
// DcpPathResolverTests.cs exhaustively unit-tests DcpPathResolver.Resolve — the PURE decision
// core (every parameter injected, no disk/assembly/environment access). That coverage proves
// the DECISION LOGIC is correct for every branch, on both win-x64 and linux-x64.
//
// It does NOT prove the GLUE around it — HeadlessTopology.ApplyDcpPathSelfHeal — still wires
// that decision to the real world: resolving the host assembly via Assembly.Load /
// Assembly.GetEntryAssembly, reading its "DcpCliPath" AssemblyMetadataAttribute, reading the
// real NUGET_PACKAGES / ASPIRE_DCP_PATH environment variables, and — on an Override resolution —
// actually WRITING builder.Configuration["DcpPublisher:CliPath"]. That last write IS the entire
// fix: a regression that silently deleted just that one assignment (behind an otherwise
// correctly-computed override path) would still pass every DcpPathResolverTests case, because
// those tests never touch HeadlessTopology at all. Before this file existed, that specific
// glue-only regression was caught only by the release pipeline's cross-machine smoke gate —
// i.e. after merge, at packaging time, not in PR CI.
//
// These tests never start Docker, DCP, or a real Aspire host: DistributedApplication.CreateBuilder
// is cheap and side-effect-free (it only starts doing real work at Build()/StartAsync, neither of
// which is ever called here); only builder.Configuration is inspected afterwards. They run in the
// standard unit-CI gate (dotnet test --filter "requires!=docker") on both win-x64 and linux-x64.
//
// SYNTHETIC HOST ASSEMBLIES
// -------------------------
// ApplyDcpPathSelfHeal resolves the host assembly by calling
// Assembly.Load(new AssemblyName(appHostAssemblyName)) — there is no injectable seam. To control
// what "DcpCliPath" metadata that assembly carries (a dead/never-existed path, for the
// Override/Unresolvable/short-circuit scenarios), this file compiles a tiny synthetic assembly at
// test time via Roslyn (CSharpCompilation) carrying exactly one assembly-level
// [assembly: AssemblyMetadata("DcpCliPath", "...")] attribute, and loads it via
// AssemblyLoadContext.Default.LoadFromStream — the SAME technique already established in
// tests/Vouchfx.Engine.Compilation.Tests/ReservedNamespaceGuardTests.cs (there via
// Assembly.Load(byte[]) instead; this file uses LoadFromStream specifically because — verified by
// hand before writing this file — only LoadFromStream registers the assembly in the Default
// AssemblyLoadContext's name-indexed cache, which a LATER, independent
// Assembly.Load(new AssemblyName(sameSimpleName)) call (exactly what ApplyDcpPathSelfHeal performs
// internally) then finds directly. Assembly.Load(byte[]) does NOT get cached the same way: a
// later by-name lookup for that same simple name throws FileNotFoundException. Each synthetic
// assembly is given a fresh Guid-suffixed simple name, so no AssemblyLoadContext.Resolving handler
// is ever needed — nothing to register or leak between tests. (The synthetic assemblies themselves
// DO stay resident in the non-collectible Default ALC for the process lifetime — intentional and
// bounded at one tiny assembly per test, and inert: each unique Guid-suffixed name is resolved by
// nothing else in the process.)
//
// For the "metadata already resolves on this machine" (UseEmbedded) scenario, no synthetic
// assembly is needed at all: THIS test project itself carries <IsAspireHost>true</IsAspireHost> +
// Aspire.AppHost.Sdk (see the .csproj), so its own build already embeds a real "DcpCliPath"
// AssemblyMetadata pointing at wherever this assembly was most recently built/restored.
// HeadlessTopologyTests' Docker-gated tests already rely on that exact fact — they pass
// "Vouchfx.Engine.Orchestration.Tests" as appHostAssemblyName to make live DCP orchestration
// resolve at all — so if that assumption were ever false, those tests would already be red. This
// file adds an explicit runtime precondition assertion anyway, so a future break surfaces here
// with an actionable message rather than as a mysterious failure elsewhere.
//
// ENVIRONMENT VARIABLES / PARALLELISM
// -------------------------------------
// Every test snapshots and restores NUGET_PACKAGES and ASPIRE_DCP_PATH via EnvironmentVariableScope
// so ambient values on a developer's or CI's machine never leak in, and so the mutation never
// survives the test. No dedicated xUnit collection is declared for that mutation: AssemblyInfo.cs
// already disables ALL intra-assembly parallelism for this project
// ([assembly: CollectionBehavior(DisableTestParallelization = true)]), and `dotnet test <solution>`
// runs one test host per project sequentially — so no test in this assembly, in this file or any
// other, ever runs concurrently with another.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Aspire.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Pins <see cref="HeadlessTopology.ApplyDcpPathSelfHeal"/> — the glue between
/// <see cref="DcpPathResolver.Resolve"/>'s pure decision core (exhaustively covered by
/// <c>DcpPathResolverTests</c>) and the real assembly-metadata / environment-variable reads and
/// the <c>builder.Configuration["DcpPublisher:CliPath"]</c> write that is the actual fix behind
/// PR #206 (fix/packaged-tool-dcp-resolution). See the file-header remarks for the full rationale.
/// </summary>
public sealed class HeadlessTopologySelfHealTests
{
    private const string NugetPackagesVariable = "NUGET_PACKAGES";
    private const string AspireDcpPathVariable = "ASPIRE_DCP_PATH";

    // -----------------------------------------------------------------------
    // 1 — Override glue: dead metadata + a matching candidate present under the
    //     current machine's own NUGET_PACKAGES cache -> config is written.
    // -----------------------------------------------------------------------

    [Fact]
    public void ApplyDcpPathSelfHeal_DeadMetadata_CandidateInLocalCache_WritesConfigOverride()
    {
        // Arrange
        var fakeAssemblyName = UniqueFakeAssemblyName();
        var deadMetadataPath = UniqueDeadPath();
        EmitAndLoadFakeHostAssembly(fakeAssemblyName, deadMetadataPath);

        using var cache = new TempDirectory();
        var candidate = BuildCandidatePath(cache.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(candidate)!);
        File.WriteAllText(candidate, "synthetic dcp binary — test fixture only");

        using var nugetPackages = EnvironmentVariableScope.Set(NugetPackagesVariable, cache.Path);
        using var aspireDcpPath = EnvironmentVariableScope.Set(AspireDcpPathVariable, null);

        var builder = CreateBuilder();

        // Act
        HeadlessTopology.ApplyDcpPathSelfHeal(builder, fakeAssemblyName);

        // Assert — the Override branch's config write is the entire fix; this is the one
        // assertion a "the write got silently deleted" regression must fail.
        Assert.Equal(candidate, builder.Configuration["DcpPublisher:CliPath"]);
    }

    // -----------------------------------------------------------------------
    // 2 — UseEmbedded glue (live metadata): the embedded path already exists on
    //     this machine -> config is left untouched.
    // -----------------------------------------------------------------------

    [Fact]
    public void ApplyDcpPathSelfHeal_LiveEmbeddedMetadata_LeavesConfigUnset()
    {
        // Arrange — this test project's OWN assembly carries real, live DcpCliPath metadata
        // (see the file-header remarks). Confirm the precondition explicitly rather than
        // assume it, so a future break is diagnosable here.
        var thisAssembly = typeof(HeadlessTopologySelfHealTests).Assembly;
        var thisAssemblyName = thisAssembly.GetName().Name!;
        var liveMetadataPath = thisAssembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(m => string.Equals(m.Key, "DcpCliPath", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        Assert.False(
            string.IsNullOrWhiteSpace(liveMetadataPath),
            "Precondition: this test assembly must carry DcpCliPath metadata — it declares " +
            "<IsAspireHost>true</IsAspireHost> and Aspire.AppHost.Sdk in its .csproj specifically " +
            "so HeadlessTopologyTests' Docker-gated tests can resolve DCP; if this ever fails, " +
            "those tests are already broken too.");
        Assert.True(
            File.Exists(liveMetadataPath),
            $"Precondition: the baked DcpCliPath metadata path '{liveMetadataPath}' must exist on " +
            "whatever machine most recently built/restored this test assembly (the same fact " +
            "HeadlessTopologyTests' Docker-gated tests already depend on).");

        using var nugetPackages = EnvironmentVariableScope.Set(NugetPackagesVariable, null);
        using var aspireDcpPath = EnvironmentVariableScope.Set(AspireDcpPathVariable, null);

        var builder = CreateBuilder();

        // Act
        HeadlessTopology.ApplyDcpPathSelfHeal(builder, thisAssemblyName);

        // Assert — UseEmbedded: nothing to heal, Aspire's own resolution is left untouched.
        Assert.Null(builder.Configuration["DcpPublisher:CliPath"]);
    }

    // -----------------------------------------------------------------------
    // 3 — ASPIRE_DCP_PATH short-circuit glue: the user's own escape hatch must
    //     never be shadowed, even in the worst case (dead metadata, empty cache).
    // -----------------------------------------------------------------------

    [Fact]
    public void ApplyDcpPathSelfHeal_AspireDcpPathSet_DeadMetadataEmptyCache_LeavesConfigUnset_NoThrow()
    {
        // Arrange — the worst case for a naive implementation: metadata is dead AND no local
        // fallback exists either. Without the short-circuit this would throw Unresolvable.
        var fakeAssemblyName = UniqueFakeAssemblyName();
        var deadMetadataPath = UniqueDeadPath();
        EmitAndLoadFakeHostAssembly(fakeAssemblyName, deadMetadataPath);

        using var emptyCache = new TempDirectory();

        using var nugetPackages = EnvironmentVariableScope.Set(NugetPackagesVariable, emptyCache.Path);
        using var aspireDcpPath = EnvironmentVariableScope.Set(AspireDcpPathVariable, "/opt/dcp-bundle-dir");

        var builder = CreateBuilder();

        // Act
        var exception = Record.Exception(() => HeadlessTopology.ApplyDcpPathSelfHeal(builder, fakeAssemblyName));

        // Assert
        Assert.Null(exception);
        Assert.Null(builder.Configuration["DcpPublisher:CliPath"]);
    }

    // -----------------------------------------------------------------------
    // 4 — Unresolvable glue: dead metadata + empty cache + no ASPIRE_DCP_PATH ->
    //     throws the classified OrchestrationException (Kind=Provision).
    // -----------------------------------------------------------------------

    [Fact]
    public void ApplyDcpPathSelfHeal_DeadMetadataEmptyCacheNoEnv_ThrowsOrchestrationException_KindProvision()
    {
        // Arrange
        var fakeAssemblyName = UniqueFakeAssemblyName();
        var deadMetadataPath = UniqueDeadPath();
        EmitAndLoadFakeHostAssembly(fakeAssemblyName, deadMetadataPath);

        using var emptyCache = new TempDirectory();

        using var nugetPackages = EnvironmentVariableScope.Set(NugetPackagesVariable, emptyCache.Path);
        using var aspireDcpPath = EnvironmentVariableScope.Set(AspireDcpPathVariable, null);

        var builder = CreateBuilder();

        // Act
        var exception = Assert.Throws<OrchestrationException>(
            () => HeadlessTopology.ApplyDcpPathSelfHeal(builder, fakeAssemblyName));

        // Assert — the GLUE throws (not writes config); the classification itself (§12.1
        // Environment error, never a test Fail) is already pinned by DcpPathResolverTests'
        // UnresolvableFailure_WrappedAsOrchestrationException_ClassifiesAsEnvironmentError.
        Assert.Equal(OrchestrationErrorKind.Provision, exception.Info.Kind);
        Assert.Equal("dcp", exception.Info.ResourceName);
        Assert.Null(builder.Configuration["DcpPublisher:CliPath"]);
    }

    // -----------------------------------------------------------------------
    // 5 — No-metadata / unresolvable-assembly glue: a host assembly name that
    //     cannot be loaded at all -> silent no-op, preserving Aspire's own
    //     OptionsValidationException path untouched.
    // -----------------------------------------------------------------------

    [Fact]
    public void ApplyDcpPathSelfHeal_AppHostAssemblyCannotBeLoaded_NoOp_NoThrow()
    {
        // Arrange — a name that was never compiled or loaded by anything in this process.
        var neverLoadedAssemblyName = UniqueFakeAssemblyName();

        using var nugetPackages = EnvironmentVariableScope.Set(NugetPackagesVariable, null);
        using var aspireDcpPath = EnvironmentVariableScope.Set(AspireDcpPathVariable, null);

        var builder = CreateBuilder();

        // Act
        var exception = Record.Exception(
            () => HeadlessTopology.ApplyDcpPathSelfHeal(builder, neverLoadedAssemblyName));

        // Assert — preserves the documented R-1 contract: Assembly.Load failures are
        // deliberately swallowed so Aspire's own OptionsValidationException surfaces
        // unchanged, rather than this self-heal raising a new failure mode it cannot diagnose.
        Assert.Null(exception);
        Assert.Null(builder.Configuration["DcpPublisher:CliPath"]);
    }

    // -----------------------------------------------------------------------
    // Test helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a cheap, never-started <see cref="IDistributedApplicationBuilder"/>. Only
    /// <c>builder.Configuration</c> is ever inspected afterwards — <c>Build()</c>/<c>StartAsync</c>
    /// are never called, so no Docker, DCP, or Aspire host is involved.
    /// </summary>
    private static IDistributedApplicationBuilder CreateBuilder() =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            DisableDashboard = true,
            // Prevent Aspire from parsing the xUnit/VSTest host's own argv.
            Args = Array.Empty<string>(),
        });

    private static string UniqueFakeAssemblyName() =>
        $"VouchfxDcpSelfHealFakeHost_{Guid.NewGuid():N}";

    /// <summary>
    /// A path guaranteed never to exist: a fresh Guid-suffixed subdirectory of the OS temp
    /// directory that this test never creates.
    /// </summary>
    private static string UniqueDeadPath() =>
        Path.Combine(Path.GetTempPath(), "vouchfx-dcp-selfheal-dead", Guid.NewGuid().ToString("N"), "dcp");

    /// <summary>
    /// Computes the exact candidate path <c>HeadlessTopology.ApplyDcpPathSelfHeal</c> ->
    /// <c>DcpPathResolver.Resolve</c> compute internally for the CURRENT machine's portable RID
    /// and the CURRENTLY loaded <c>Aspire.Hosting</c> version — mirroring
    /// <c>DcpPathResolverTests</c>' <c>BuildCandidate</c> helper (never hardcode this as an
    /// independent literal; see that file's header remarks on OS-agnostic candidate
    /// construction).
    /// </summary>
    private static string BuildCandidatePath(string cacheRoot) =>
        Path.Combine(
            cacheRoot,
            $"aspire.hosting.orchestration.{ResolveCurrentPortableRid()}",
            ResolveCurrentAspireHostingVersion(),
            "tools",
            ResolveCurrentDcpExeName());

    /// <summary>
    /// Mirrors the private local <c>ResolvePortableRid</c> function nested inside
    /// <see cref="HeadlessTopology.ApplyDcpPathSelfHeal"/> exactly. That function is a C# local
    /// function — it compiles to a private method with no accessible metadata token contract,
    /// so it cannot be reused via reflection or InternalsVisibleTo; kept in sync manually. A
    /// drift here would only affect where this test pre-places its candidate fixture file, not
    /// the resolver's own decision logic, which <c>DcpPathResolverTests</c> already covers
    /// exhaustively for every rid string shape.
    /// </summary>
    private static string ResolveCurrentPortableRid()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return $"win-{arch}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return $"osx-{arch}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return $"linux-{arch}";
        return RuntimeInformation.RuntimeIdentifier;
    }

    private static string ResolveCurrentDcpExeName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dcp.exe" : "dcp";

    /// <summary>
    /// Reads the SAME <c>Aspire.Hosting</c> informational version (truncated at the first
    /// <c>+</c>) that <see cref="HeadlessTopology.ApplyDcpPathSelfHeal"/> itself reads — this is
    /// not reimplemented logic, just the identical live attribute read, so it is always exactly
    /// in sync (no drift risk, unlike <see cref="ResolveCurrentPortableRid"/> above).
    /// </summary>
    private static string ResolveCurrentAspireHostingVersion()
    {
        var informational = typeof(DistributedApplication).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        Assert.False(
            string.IsNullOrWhiteSpace(informational),
            "Precondition: Aspire.Hosting must carry an AssemblyInformationalVersionAttribute.");

        var plusIndex = informational!.IndexOf('+', StringComparison.Ordinal);
        return plusIndex >= 0 ? informational[..plusIndex] : informational;
    }

    /// <summary>
    /// Compiles a tiny synthetic assembly named <paramref name="assemblySimpleName"/> carrying
    /// exactly one assembly-level <c>[assembly: AssemblyMetadata("DcpCliPath", dcpCliPathValue)]</c>
    /// attribute, and loads it into <see cref="AssemblyLoadContext.Default"/> via
    /// <see cref="AssemblyLoadContext.LoadFromStream(Stream)"/>. See the file-header remarks for
    /// why <c>LoadFromStream</c> specifically (not <c>Assembly.Load(byte[])</c>) is required for
    /// the later by-name <c>Assembly.Load(new AssemblyName(...))</c> lookup inside
    /// <see cref="HeadlessTopology.ApplyDcpPathSelfHeal"/> to find it.
    /// </summary>
    private static void EmitAndLoadFakeHostAssembly(string assemblySimpleName, string dcpCliPathValue)
    {
        var source =
            $$"""[assembly: System.Reflection.AssemblyMetadata("DcpCliPath", @"{{dcpCliPathValue.Replace("\"", "\"\"", StringComparison.Ordinal)}}")]""";

        // Collect TPA (Trusted Platform Assembly) paths for BCL references — the same
        // technique already established in ReservedNamespaceGuardTests.cs /
        // RoslynScriptCompilerTests.cs for compiling a synthetic assembly at test time without
        // shipping a full reference-assembly set.
        var tpaData = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty;
        var tpaPaths = tpaData.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var wantedBcl = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.Private.CoreLib.dll",
            "System.Runtime.dll",
        };

        var references = new List<MetadataReference>();
        foreach (var path in tpaPaths)
        {
            if (wantedBcl.Contains(Path.GetFileName(path)))
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
        }

        var compilation = CSharpCompilation.Create(
            assemblySimpleName,
            new[] { CSharpSyntaxTree.ParseText(source) },
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                warningLevel: 0,
                nullableContextOptions: NullableContextOptions.Disable));

        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);
        if (!emitResult.Success)
        {
            var errors = string.Join(
                Environment.NewLine,
                emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            throw new InvalidOperationException(
                $"Synthetic fake-host assembly '{assemblySimpleName}' did not compile:{Environment.NewLine}{errors}");
        }

        peStream.Position = 0;
        AssemblyLoadContext.Default.LoadFromStream(peStream);
    }

    /// <summary>
    /// Creates a fresh, empty temporary directory and recursively deletes it on
    /// <see cref="Dispose"/>. Used both as an empty NUGET_PACKAGES cache root (tests 3 and 4)
    /// and as the parent of a pre-placed candidate fixture file (test 1).
    /// </summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vouchfx-dcp-selfheal-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup only — a leaked temp directory under the OS temp root is
                // not worth failing an otherwise-passing test over.
            }
            catch (UnauthorizedAccessException)
            {
                // As above.
            }
        }
    }

    /// <summary>
    /// Snapshots an environment variable's current value, sets it to <c>value</c>
    /// (<see langword="null"/> to unset), and restores the original value on
    /// <see cref="Dispose"/>.
    /// </summary>
    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;

        private EnvironmentVariableScope(string name, string? originalValue)
        {
            _name = name;
            _originalValue = originalValue;
        }

        public static EnvironmentVariableScope Set(string name, string? value)
        {
            var original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
            return new EnvironmentVariableScope(name, original);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _originalValue);
    }
}
