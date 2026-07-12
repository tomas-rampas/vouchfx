// Vouchfx.Engine.Orchestration — DcpPathResolver (packaged-tool DCP path self-heal).
//
// THE PROBLEM
// -----------
// The `vouchfx` dotnet global tool is packed on GitHub Actions. Aspire.AppHost.Sdk
// (13.4.2) bakes ABSOLUTE, machine-specific DCP paths into AssemblyMetadata attributes
// on the `vouchfx` assembly at build time, e.g.:
//   dcpclipath=/home/runner/.nuget/packages/aspire.hosting.orchestration.linux-x64/13.4.2/tools/dcp
// On ANY other machine that exact path does not exist — the absolute root is wrong AND,
// for a non-Linux user, so is the RID — so DistributedApplication.StartAsync fails with
// `FileNotFoundException: The Aspire orchestration component is not installed at "..."`,
// even when the user's OWN NuGet cache DOES hold the matching
// `aspire.hosting.orchestration.<their-rid>` package (true of any machine that has
// previously restored an Aspire project, which is most .NET developer and CI machines).
//
// THE FIX
// -------
// Verified against the pinned Aspire 13.4.2 source
// (Aspire.Hosting.Dcp.ConfigureDefaultDcpOptions / ValidateDcpOptions):
//   • DcpOptions.CliPath resolves with precedence (1) the `DcpPublisher:CliPath`
//     configuration key — if set, ExtensionsPath is auto-derived as
//     `Path.Combine(Path.GetDirectoryName(CliPath), "ext")` — (2) the ASPIRE_DCP_PATH
//     env var, (3) the `DcpCliPath` AssemblyMetadataAttribute (case-insensitive key) on
//     the resolved host assembly.
//   • ValidateDcpOptions only requires CliPath and DashboardPath to be non-empty strings;
//     it does NOT check they exist on disk. Existence is checked later by
//     DcpDependencyCheck when DCP is actually launched.
// So overriding `DcpPublisher:CliPath` via IConfiguration BEFORE `builder.Build()` is a
// legitimate, higher-precedence channel that Aspire itself already exposes — no private
// API, no reflection into Aspire internals. HeadlessTopology.StartAsync sets this
// override when (and only when) the embedded metadata path does not exist on the current
// machine but the equivalent package/version DOES exist under the current machine's own
// NuGet cache. DashboardPath is deliberately left at its stale-but-non-empty metadata
// value: it passes ValidateDcpOptions and is never actually read because
// HeadlessTopology always sets DisableDashboard = true.
//
// DO NOT SHADOW THE USER'S OWN ASPIRE_DCP_PATH ESCAPE HATCH
// -----------------------------------------------------------
// ASPIRE_DCP_PATH is priority 2 — ABOVE the assembly metadata (priority 3) this self-heal
// reacts to, but BELOW the `DcpPublisher:CliPath` configuration key (priority 1) this
// self-heal WRITES. Two ways that ordering can bite if this class is not careful:
//   • If the self-heal throws Unresolvable whenever the embedded metadata path is dead
//     and no local-cache fallback exists, it would never give Aspire's own priority-2
//     ASPIRE_DCP_PATH resolution a chance to run at all — even though it is fully
//     sufficient on its own.
//   • If the self-heal instead falls through to the Override branch, its
//     `DcpPublisher:CliPath` write would silently OUTRANK a user-set ASPIRE_DCP_PATH
//     (priority 1 beats priority 2), overriding the very escape hatch our own
//     Unresolvable error message recommends.
// So <see cref="Resolve"/> checks `aspireDcpPathEnvironmentVariable` FIRST, before even
// looking at the metadata: when it is set and non-whitespace, this class is a no-op
// (<see cref="DcpPathResolutionKind.UseEmbedded"/>) regardless of what the metadata or the
// local cache probe would otherwise have found, and Aspire's own priority-2 resolution
// runs untouched. Per the pinned ConfigureDefaultDcpOptions source, ASPIRE_DCP_PATH names
// a BUNDLE DIRECTORY, not the executable file itself — Aspire derives CliPath from it via
// `BundleDiscovery.GetDcpExecutablePath(dir)` and ExtensionsPath via
// `Path.Combine(dir, "ext")` — which is why the Unresolvable remedy message below asks the
// user to set it to "the directory containing the dcp executable", not to the executable.
//
// This class is the PURE decision core: every input is an explicit parameter — no
// Assembly, File, or Environment access happens here — so every branch is exhaustively
// unit-testable without touching disk, an assembly, or a real environment variable.
// HeadlessTopology performs the (impure) assembly-metadata read and environment/profile
// lookups, and applies the DcpPathResolution this class returns.

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// Discriminates the three possible outcomes of <see cref="DcpPathResolver.Resolve"/>.
/// </summary>
internal enum DcpPathResolutionKind
{
    /// <summary>
    /// Nothing to self-heal: either the host assembly carries no <c>DcpCliPath</c>
    /// metadata at all (a host that never referenced <c>Aspire.AppHost.Sdk</c> — the
    /// documented R-1 contract: it keeps failing with Aspire's own
    /// <c>OptionsValidationException</c>, unchanged), or the embedded path already
    /// exists on this machine (a local dev build, or a CI runner that happens to match
    /// the packaging runner). Aspire's own resolution is left untouched.
    /// </summary>
    UseEmbedded,

    /// <summary>
    /// The embedded path does not exist here, but the matching DCP package/version was
    /// found under this machine's own NuGet cache. The caller should override
    /// <c>DcpPublisher:CliPath</c> with <see cref="DcpPathResolution.OverridePath"/>.
    /// </summary>
    Override,

    /// <summary>
    /// The embedded path does not exist here, and no matching package could be found
    /// under this machine's own NuGet cache either. The caller should raise an
    /// actionable, Environment-error-classified failure using
    /// <see cref="DcpPathResolution.UnresolvableDetail"/>.
    /// </summary>
    Unresolvable,
}

/// <summary>
/// The outcome of <see cref="DcpPathResolver.Resolve"/> — a small closed result type so
/// callers switch on <see cref="Kind"/> instead of inferring intent from a grab-bag of
/// nullable fields.
/// </summary>
/// <param name="Kind">Which of the three outcomes this instance represents.</param>
/// <param name="OverridePath">
/// Populated only when <see cref="Kind"/> is <see cref="DcpPathResolutionKind.Override"/>:
/// the absolute path to the DCP executable found under the current machine's own NuGet
/// cache.
/// </param>
/// <param name="UnresolvableDetail">
/// Populated only when <see cref="Kind"/> is <see cref="DcpPathResolutionKind.Unresolvable"/>:
/// a human-readable, actionable diagnosis naming the missing package id/version, the
/// probed path, and the remedy.
/// </param>
internal sealed record DcpPathResolution(
    DcpPathResolutionKind Kind,
    string? OverridePath,
    string? UnresolvableDetail)
{
    /// <summary>Nothing to self-heal — see <see cref="DcpPathResolutionKind.UseEmbedded"/>.</summary>
    public static DcpPathResolution UseEmbedded() =>
        new(DcpPathResolutionKind.UseEmbedded, OverridePath: null, UnresolvableDetail: null);

    /// <summary>A fallback DCP executable was found — see <see cref="DcpPathResolutionKind.Override"/>.</summary>
    public static DcpPathResolution Override(string overridePath) =>
        new(DcpPathResolutionKind.Override, overridePath, UnresolvableDetail: null);

    /// <summary>No usable DCP executable could be found anywhere — see <see cref="DcpPathResolutionKind.Unresolvable"/>.</summary>
    public static DcpPathResolution Unresolvable(string detail) =>
        new(DcpPathResolutionKind.Unresolvable, OverridePath: null, detail);
}

/// <summary>
/// Pure decision core for the packaged-tool DCP path self-heal. See the file-header
/// remarks for the full problem/fix rationale.
/// </summary>
internal static class DcpPathResolver
{
    // NuGet package id prefix for the per-RID DCP orchestration package. Package folder
    // ids in the NuGet global-packages cache are always lower-cased.
    private const string DcpPackageIdPrefix = "aspire.hosting.orchestration.";

    // Split on both separators: the STALE metadata path is text captured on a possibly
    // different OS than the one this code is running on, so it may use '/' even when
    // this process is on Windows (or vice versa) — never assume Path.DirectorySeparatorChar.
    private static readonly char[] PathSeparators = { '/', '\\' };

    /// <summary>
    /// Decides whether the DCP path embedded in the host assembly's metadata needs to be
    /// overridden for this machine, and if so, with what.
    /// </summary>
    /// <param name="metadataDcpCliPath">
    /// The value of the <c>DcpCliPath</c> <see cref="System.Reflection.AssemblyMetadataAttribute"/>
    /// on the resolved host assembly, or <see langword="null"/>/empty when the host
    /// assembly carries no such attribute (a host that never referenced
    /// <c>Aspire.AppHost.Sdk</c>).
    /// </param>
    /// <param name="fileExists">
    /// Injected file-existence predicate (production callers pass <see cref="File.Exists(string)"/>)
    /// so every branch is testable without touching disk.
    /// </param>
    /// <param name="nugetPackagesEnvironmentVariable">
    /// The value of the <c>NUGET_PACKAGES</c> environment variable, or <see langword="null"/>
    /// when unset.
    /// </param>
    /// <param name="userProfileDirectory">
    /// The current user's profile directory (production callers pass
    /// <c>Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)</c>), used to
    /// derive the default NuGet cache root (<c>&lt;profile&gt;/.nuget/packages</c>) when
    /// <paramref name="nugetPackagesEnvironmentVariable"/> is unset.
    /// </param>
    /// <param name="runtimeIdentifier">
    /// The CURRENT machine's runtime identifier (production callers pass
    /// <c>System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier</c>, e.g.
    /// <c>"win-x64"</c>), used both to pick the DCP executable name (<c>dcp.exe</c> for a
    /// <c>win-*</c> rid, <c>dcp</c> otherwise) and to compute the candidate package id.
    /// </param>
    /// <param name="aspireHostingInformationalVersion">
    /// The <see cref="System.Reflection.AssemblyInformationalVersionAttribute"/> value of
    /// the loaded <c>Aspire.Hosting</c> assembly (production callers pass
    /// <c>typeof(Aspire.Hosting.DistributedApplication).Assembly</c>'s attribute), or
    /// <see langword="null"/> when unavailable — in which case the version is instead
    /// parsed out of <paramref name="metadataDcpCliPath"/> (see <see cref="ResolveVersion"/>).
    /// </param>
    /// <param name="aspireDcpPathEnvironmentVariable">
    /// The value of the <c>ASPIRE_DCP_PATH</c> environment variable, or <see langword="null"/>
    /// when unset. When set and non-whitespace, <see cref="Resolve"/> is a no-op
    /// (<see cref="DcpPathResolutionKind.UseEmbedded"/>) regardless of every other
    /// parameter — see the file-header "DO NOT SHADOW" remarks for why this must be
    /// checked before anything else. Optional (defaults to <see langword="null"/>) so
    /// every existing call site that predates this parameter is unaffected, mirroring
    /// <c>OrchestrationErrorClassifier.Classify</c>'s <c>containerNeverCreated</c> parameter.
    /// </param>
    /// <returns>The resolution the caller must apply — see <see cref="DcpPathResolution"/>.</returns>
    public static DcpPathResolution Resolve(
        string? metadataDcpCliPath,
        Func<string, bool> fileExists,
        string? nugetPackagesEnvironmentVariable,
        string userProfileDirectory,
        string runtimeIdentifier,
        string? aspireHostingInformationalVersion,
        string? aspireDcpPathEnvironmentVariable = null)
    {
        ArgumentNullException.ThrowIfNull(fileExists);

        // ASPIRE_DCP_PATH short-circuit — MUST be the very first check. See the
        // file-header "DO NOT SHADOW THE USER'S OWN ASPIRE_DCP_PATH ESCAPE HATCH" remarks:
        // Aspire resolves this env var itself at priority 2 (above the dead metadata this
        // class reacts to); this self-heal must neither pre-empt it with an early
        // Unresolvable throw nor outrank it with a DcpPublisher:CliPath override
        // (priority 1). No-op unconditionally when it is set.
        if (!string.IsNullOrWhiteSpace(aspireDcpPathEnvironmentVariable))
        {
            return DcpPathResolution.UseEmbedded();
        }

        // No metadata at all — preserve the documented R-1 contract untouched (§"Aspire
        // (§4, §19)", CLAUDE.md): a host without Aspire.AppHost.Sdk metadata keeps
        // failing with Aspire's own OptionsValidationException.
        if (string.IsNullOrWhiteSpace(metadataDcpCliPath))
        {
            return DcpPathResolution.UseEmbedded();
        }

        // The embedded path is valid on THIS machine — nothing to heal.
        if (fileExists(metadataDcpCliPath))
        {
            return DcpPathResolution.UseEmbedded();
        }

        var version = ResolveVersion(aspireHostingInformationalVersion, metadataDcpCliPath);
        if (version is null)
        {
            return DcpPathResolution.Unresolvable(BuildUnresolvableDetail(
                packageId: null, version: null, probedPath: null, metadataDcpCliPath));
        }

var cacheRoot = ResolveCacheRoot(nugetPackagesEnvironmentVariable, userProfileDirectory);

// Normalize to portable RIDs (RuntimeInformation.RuntimeIdentifier can be OS-version/distro-specific).
var rid = runtimeIdentifier.Trim().ToLowerInvariant();
if (rid.StartsWith("win", StringComparison.Ordinal))
{
    rid = rid.Contains("arm64", StringComparison.Ordinal) ? "win-arm64"
        : rid.Contains("x86", StringComparison.Ordinal) ? "win-x86"
        : "win-x64";
}
else if (rid.StartsWith("osx", StringComparison.Ordinal))
{
    rid = rid.Contains("arm64", StringComparison.Ordinal) ? "osx-arm64" : "osx-x64";
}
else if (rid.Contains('.') &&
         (rid.EndsWith("-x64", StringComparison.Ordinal) || rid.EndsWith("-arm64", StringComparison.Ordinal)))
{
    // Typical distro-specific Linux RIDs look like "ubuntu.22.04-x64"; Aspire packages are "linux-x64"/"linux-arm64".
    rid = rid.EndsWith("-arm64", StringComparison.Ordinal) ? "linux-arm64" : "linux-x64";
}

var packageId = DcpPackageIdPrefix + rid;
var exeName = IsWindowsRid(rid) ? "dcp.exe" : "dcp";
var candidate = Path.Combine(cacheRoot, packageId, version, "tools", exeName);
        return fileExists(candidate)
            ? DcpPathResolution.Override(candidate)
            : DcpPathResolution.Unresolvable(
                BuildUnresolvableDetail(packageId, version, candidate, metadataDcpCliPath));
    }

    /// <summary>
    /// Derives the Aspire orchestration package version to probe: the informational
    /// version of the loaded <c>Aspire.Hosting</c> assembly, truncated at the first
    /// <c>+</c> (source-control metadata suffix, e.g. <c>"13.4.2+abcdef0123"</c> →
    /// <c>"13.4.2"</c>); or, when that is unavailable, the version segment parsed out of
    /// the stale embedded path itself — the path segment immediately following
    /// <c>aspire.hosting.orchestration.&lt;rid&gt;</c> (e.g.
    /// <c>".../aspire.hosting.orchestration.linux-x64/13.4.2/tools/dcp"</c> → <c>"13.4.2"</c>).
    /// The packaging build and the running engine always reference the SAME pinned
    /// Aspire minor (CLAUDE.md §"Libraries (§5.7)": "Pin Aspire to a stable minor per
    /// engine release"), so either source yields the correct probe version.
    /// </summary>
    /// <param name="assemblyInformationalVersion">
    /// The <c>Aspire.Hosting</c> assembly's informational version, or <see langword="null"/>.
    /// </param>
    /// <param name="staleMetadataPath">
    /// The stale embedded <c>DcpCliPath</c> value, used as the fallback source.
    /// </param>
    /// <returns>The version string to probe, or <see langword="null"/> when neither source yields one.</returns>
    internal static string? ResolveVersion(string? assemblyInformationalVersion, string? staleMetadataPath)
    {
        if (!string.IsNullOrWhiteSpace(assemblyInformationalVersion))
        {
            var plusIndex = assemblyInformationalVersion.IndexOf('+', StringComparison.Ordinal);
            return plusIndex >= 0 ? assemblyInformationalVersion[..plusIndex] : assemblyInformationalVersion;
        }

        return ParseVersionFromStalePath(staleMetadataPath);
    }

    /// <summary>
    /// Parses the version segment out of a stale <c>DcpCliPath</c> value — the path
    /// segment immediately following the <c>aspire.hosting.orchestration.&lt;rid&gt;</c>
    /// segment, regardless of which path separator the ORIGINAL (possibly foreign-OS)
    /// path used.
    /// </summary>
    private static string? ParseVersionFromStalePath(string? staleMetadataPath)
    {
        if (string.IsNullOrWhiteSpace(staleMetadataPath))
        {
            return null;
        }

        var segments = staleMetadataPath.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].StartsWith(DcpPackageIdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return segments[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the NuGet global-packages cache root: the cleaned <c>NUGET_PACKAGES</c>
    /// value when non-empty, else <c>&lt;user profile&gt;/.nuget/packages</c> — the same
    /// precedence the <c>dotnet</c>/NuGet tooling itself applies.
    /// </summary>
    private static string ResolveCacheRoot(string? nugetPackagesEnvironmentVariable, string userProfileDirectory)
    {
        var cleaned = CleanEnvironmentValue(nugetPackagesEnvironmentVariable);
        return string.IsNullOrEmpty(cleaned)
            ? Path.Combine(userProfileDirectory, ".nuget", "packages")
            : cleaned;
    }

    /// <summary>
    /// Trims whitespace and, when present, ONE matching pair of surrounding double quotes
    /// from an environment-variable value.
    /// </summary>
    /// <remarks>
    /// Windows batch/cmd's <c>set VAR="value"</c> idiom embeds the quote characters
    /// LITERALLY in the value (unlike POSIX shells, which strip quoting at the shell
    /// layer before the process ever sees it) — a <c>NUGET_PACKAGES</c> value set that way
    /// in a batch script or a misquoted CI step must not be treated as a literal
    /// <c>"..."</c>-quoted directory name, which would otherwise produce an unusable
    /// candidate path with embedded quote characters. Deliberate design choice: strip at
    /// most one pair (a value that is quotes-within-quotes is left as authored — this
    /// resolver corrects the common copy-paste/batch-idiom case, not arbitrary malformed
    /// input). A trailing path separator needs NO special handling here:
    /// <see cref="Path.Combine(string, string)"/> already collapses a doubled separator.
    /// </remarks>
    private static string? CleanEnvironmentValue(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return trimmed;
        }

        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed;
    }

    /// <summary>
    /// A RID is a Windows RID when it starts with <c>"win"</c> (<c>win-x64</c>,
    /// <c>win-x86</c>, <c>win-arm64</c>, …) — the DCP package ships <c>dcp.exe</c> for
    /// those and the extensionless <c>dcp</c> for every other (linux-*, osx-*) RID.
    /// </summary>
    private static bool IsWindowsRid(string runtimeIdentifier) =>
        runtimeIdentifier.StartsWith("win", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the actionable, human-readable failure detail for an
    /// <see cref="DcpPathResolutionKind.Unresolvable"/> outcome, naming the missing
    /// package id/version (when known), the probed path (when known), the stale embedded
    /// path, and the remedy.
    /// </summary>
    private static string BuildUnresolvableDetail(
        string? packageId, string? version, string? probedPath, string staleMetadataPath)
    {
        var fallbackDescription = packageId is null || version is null || probedPath is null
            ? "the Aspire.Hosting package version could not be determined, so no fallback " +
              "path could be probed"
            : $"no '{packageId}' version '{version}' package was found at '{probedPath}'";

        var versionClause = version is null
            ? "at the matching Aspire.AppHost.Sdk version"
            : $"at version '{version}'";

        return
            "The Aspire DCP orchestration component could not be located. The vouchfx tool was " +
            $"packaged on a different machine, so the DCP path baked into its assembly metadata " +
            $"('{staleMetadataPath}') does not exist here. The runtime fallback also failed: " +
            $"{fallbackDescription}. Remedy: restore any project that carries Aspire.AppHost.Sdk " +
            $"{versionClause} so the DCP orchestration package is populated into your NuGet cache " +
            "— e.g. 'git clone https://github.com/tomas-rampas/vouchfx && dotnet restore " +
            "vouchfx/vouchfx.sln' — or set the ASPIRE_DCP_PATH environment variable to the " +
            "directory containing the dcp executable (not the executable itself — Aspire derives " +
            "the executable and extension paths from that directory).";
    }
}
