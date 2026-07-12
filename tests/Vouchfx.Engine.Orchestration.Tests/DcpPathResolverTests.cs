// Non-docker unit tests for DcpPathResolver — the pure decision core behind the
// packaged-tool DCP path self-heal (fix/packaged-tool-dcp-resolution).
//
// Every scenario is driven purely through Resolve()'s explicit parameters (an injected
// fileExists predicate, an injected NUGET_PACKAGES value, an injected user-profile
// directory, an injected rid, and an injected Aspire.Hosting informational version) —
// no disk, assembly, or real environment variable is touched. These tests run in the
// standard unit-CI gate (dotnet test --filter "requires!=docker").
//
// OS-AGNOSTIC CANDIDATE CONSTRUCTION — read before adding a new test here.
// ---------------------------------------------------------------------------
// DcpPathResolver.Resolve builds its candidate path via Path.Combine(cacheRoot,
// packageId, version, "tools", exeName). Path.Combine does NOT normalise a separator
// character that is already EMBEDDED INSIDE a supplied segment — it only ever INSERTS
// the CURRENT platform's own separator BETWEEN segments (and only when the preceding
// segment does not already end with a separator the current platform recognises). So a
// Windows-style cache root such as "D:\nuget-alt" keeps its literal backslash even when
// Path.Combine runs on Linux (where '\' is just an ordinary character, not a
// recognised separator) — Path.Combine there only inserts '/' between segments, giving
// e.g. "D:\nuget-alt/aspire.hosting.orchestration.win-x64/13.4.2/tools/dcp.exe". A test
// that hardcoded a SEPARATE, fully-backslashed literal as "the expected candidate" was
// therefore only ever correct on Windows — on Linux CI it diverged from what the
// resolver actually computed and the test failed with Unresolvable instead of Override.
// The fix used throughout this file: NEVER hardcode an expected candidate as an
// independent literal. Always derive it via the BuildCandidate / BuildDefaultCacheCandidate
// helpers below, fed the IDENTICAL raw segments (especially the identical cache-root /
// user-profile-directory string) that Resolve() itself combines — so both sides run the
// SAME Path.Combine call on whichever OS the test executes on and are therefore always
// equal, regardless of what the literal string value happens to look like on that OS.

using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Unit tests for <see cref="DcpPathResolver"/>.
/// </summary>
public sealed class DcpPathResolverTests
{
    private const string StaleLinuxMetadataPath =
        "/home/runner/.nuget/packages/aspire.hosting.orchestration.linux-x64/13.4.2/tools/dcp";

    // -----------------------------------------------------------------------
    // ASPIRE_DCP_PATH short-circuit — the user's own escape hatch must never be
    // shadowed (MAJOR 2, gatekeeper review): Aspire honours it itself at priority 2,
    // above the dead metadata this self-heal reacts to.
    // -----------------------------------------------------------------------

    [Fact]
    public void Resolve_AspireDcpPathSet_ReturnsUseEmbedded_EvenWhenMetadataDeadAndCacheEmpty()
    {
        // Arrange — the WORST case for a naive implementation: the embedded metadata path
        // is dead AND no local-cache fallback exists at all (fileExists is false for
        // everything). Without the short-circuit this would throw Unresolvable, pre-empting
        // Aspire's own ASPIRE_DCP_PATH resolution from ever running. With the short-circuit
        // it must be a pure no-op instead.
        var fileExists = Exists(); // nothing exists anywhere.

        // Act
        var result = DcpPathResolver.Resolve(
            metadataDcpCliPath: StaleLinuxMetadataPath,
            fileExists: fileExists,
            nugetPackagesEnvironmentVariable: null,
            userProfileDirectory: @"C:\Users\dev",
            runtimeIdentifier: "win-x64",
            aspireHostingInformationalVersion: "13.4.2",
            aspireDcpPathEnvironmentVariable: "/opt/dcp-bundle");

        // Assert
        Assert.Equal(DcpPathResolutionKind.UseEmbedded, result.Kind);
        Assert.Null(result.OverridePath);
        Assert.Null(result.UnresolvableDetail);
    }

    [Fact]
    public void Resolve_AspireDcpPathSet_ReturnsUseEmbedded_EvenWhenLocalFallbackWouldHaveSucceeded()
    {
        // Arrange — the OTHER way a naive implementation could go wrong: a perfectly good
        // local-cache fallback DOES exist, but the self-heal must still step aside rather
        // than write DcpPublisher:CliPath, which would silently OUTRANK (priority 1) the
        // user's own ASPIRE_DCP_PATH (priority 2). The short-circuit means Resolve() never
        // even calls fileExists here, so the candidate's exact string form is immaterial —
        // built via BuildCandidate regardless, for consistency with every other test in
        // this file (see the file-header remarks).
        var cacheRoot = @"D:\nuget-alt";
        var candidate = BuildCandidate(cacheRoot, "win-x64", "13.4.2", "dcp.exe");
        var fileExists = Exists(candidate);

        // Act
        var result = DcpPathResolver.Resolve(
            metadataDcpCliPath: StaleLinuxMetadataPath,
            fileExists: fileExists,
            nugetPackagesEnvironmentVariable: cacheRoot,
            userProfileDirectory: @"C:\Users\dev",
            runtimeIdentifier: "win-x64",
            aspireHostingInformationalVersion: "13.4.2",
            aspireDcpPathEnvironmentVariable: @"C:\dcp-bundle");

        // Assert — no override, even though the fallback candidate exists and would
        // otherwise have resolved.
        Assert.Equal(DcpPathResolutionKind.UseEmbedded, result.Kind);
        Assert.Null(result.OverridePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_AspireDcpPathBlank_DoesNotShortCircuit(string blankValue)
    {
        // Arrange — a blank (empty/whitespace) ASPIRE_DCP_PATH is treated as unset, not as
        // an explicit override; the normal embedded-path-exists check must still run. No
        // Path.Combine is involved on this branch (fileExists is matched directly against
        // the raw metadataDcpCliPath string), so this test is inherently OS-agnostic.
        var fileExists = Exists(StaleLinuxMetadataPath);

        // Act
        var result = DcpPathResolver.Resolve(
            metadataDcpCliPath: StaleLinuxMetadataPath,
            fileExists: fileExists,
            nugetPackagesEnvironmentVariable: null,
            userProfileDirectory: @"C:\Users\dev",
            runtimeIdentifier: "win-x64",
            aspireHostingInformationalVersion: "13.4.2",
            aspireDcpPathEnvironmentVariable: blankValue);

        // Assert — UseEmbedded here because the embedded path exists, NOT because of the
        // short-circuit (distinguishing this from the two tests above).
        Assert.Equal(DcpPathResolutionKind.UseEmbedded, result.Kind);
    }

    [Fact]
    public void Resolve_AspireDcpPathNotSupplied_DefaultsToNull_PreservesExistingCallSites()
    {
        // Arrange — every call site that predates this parameter omits it; it must default
        // to null and behave exactly as before (mirrors
        // OrchestrationErrorClassifier.Classify's containerNeverCreated default-preservation
        // convention).
        var cacheRoot = @"D:\nuget-alt";
        var candidate = BuildCandidate(cacheRoot, "win-x64", "13.4.2", "dcp.exe");
        var fileExists = Exists(candidate);

        // Act — no aspireDcpPathEnvironmentVariable argument supplied.
        var result = DcpPathResolver.Resolve(
            metadataDcpCliPath: StaleLinuxMetadataPath,
            fileExists: fileExists,
            nugetPackagesEnvironmentVariable: cacheRoot,
            userProfileDirectory: @"C:\Users\dev",
            runtimeIdentifier: "win-x64",
            aspireHostingInformationalVersion: "13.4.2");

        // Assert — unaffected: falls through to the normal fallback resolution.
        Assert.Equal(DcpPathResolutionKind.Override, result.Kind);
        Assert.Equal(candidate, result.OverridePath);
    }

    // -----------------------------------------------------------------------
    // UseEmbedded — nothing to self-heal
    // -----------------------------------------------------------------------

    [Fact]
    public void Resolve_EmbeddedPathExists_ReturnsUseEmbedded()
    {
        // Arrange — the embedded metadata path IS present on this machine (a local dev
        // build, or a CI runner matching the packaging runner).
        var fileExists = Exists(StaleLinuxMetadataPath);

        // Act
        var result = DcpPathResolver.Resolve(
            metadataDcpCliPath: StaleLinuxMetadataPath,
            fileExists: fileExists,
            nugetPackagesEnvironmentVariable: null,
            userProfileDirectory: @"C:\Users\dev",
            runtimeIdentifier: "win-x64",
            aspireHostingInformationalVersion: "13.4.2");

        // Assert
        Assert.Equal(DcpPathResolutionKind.UseEmbedded, result.Kind);
        Assert.Null(result.OverridePath);
        Assert.Null(result.UnresolvableDetail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_MetadataAbsentOrEmpty_ReturnsUseEmbedded(string? metadataDcpCliPath)
    {
        // Arrange — a host that never referenced Aspire.AppHost.Sdk carries no DcpCliPath
        // metadata at all; preserve the documented R-1 contract (Aspire's own
        // OptionsValidationException keeps surfacing, unchanged).
        var fileExists = Exists(); // never called for a real check — must not throw either way.

        // Act
        var result = DcpPathResolver.Resolve(
            metadataDcpCliPath: metadataDcpCliPath,
            fileExists: fileExists,
            nugetPackagesEnvironmentVariable: null,
            userProfileDirectory: @"C:\Users\dev",
            runtimeIdentifier: "win-x64",
            aspireHostingInformationalVersion: "13.4.2");

        // Assert
        Assert.Equal(DcpPathResolutionKind.UseEmbedded, result.Kind);
    }

    // -----------------------------------------------------------------------
    // Override — a fallback DCP executable was found under the local cache
    // -----------------------------------------------------------------------

    [Fact]
    public void Resolve_MetadataPathIsADirectoryNotAFile_FileExistsFalse_SelfHealProceeds()
    {
        // Arrange — PINS intended behaviour (MINOR 4b, gatekeeper review): Resolve() only
        // ever calls the injected fileExists predicate; it has no notion of "exists as a
        // directory" versus "exists as a file". Production wires fileExists to File.Exists,
        // which returns false for a path that is a directory rather than a file (an
        // Aspire.AppHost.Sdk metadata bug, corrupted install, or partial extraction could
        // plausibly leave `dcpclipath` pointing at a directory) — the resolver must treat
        // that identically to "does not exist" and proceed to the local-cache fallback,
        // exactly as this fileExists=false-for-the-metadata-path test simulates.
        var cacheRoot = @"D:\nuget-alt";
        var candidate = BuildCandidate(cacheRoot, "win-x64", "13.4.2", "dcp.exe");
        var fileExists = Exists(candidate); // metadata path deliberately absent from the map.

        // Act
        var result = DcpPathResolver.Resolve(
            metadataDcpCliPath: StaleLinuxMetadataPath,
            fileExists: fileExists,
            nugetPackagesEnvironmentVariable: cacheRoot,
            userProfileDirectory: @"C:\Users\dev",
            runtimeIdentifier: "win-x64",
            aspireHostingInformationalVersion: "13.4.2");

        // Assert
        Assert.Equal(DcpPathResolutionKind.Override, result.Kind);
        Assert.Equal(candidate, result.OverridePath);
    }

    [Fact]
    public void Resolve_StalePath_FallbackExists_NugetPackagesEnvSet_ReturnsOverride_WindowsExe()
    {
        // Arrange — NUGET_PACKAGES is set (explicit env override), rid is win-x64, so the
        // candidate must use the "dcp.exe" executable name under that exact cache root.
        var cacheRoot = @"D:\nuget-alt";
        var candidate = BuildCandidate(cacheRoot, "win-x64", "13.4.2", "dcp.exe");
        var fileExists = Exists(candidate); // stale path is NOT in the map -> false.

        // Act
        var result = DcpPathResolver.Resolve(
            metadataDcpCliPath: StaleLinuxMetadataPath,
            fileExists: fileExists,
            nugetPackagesEnvironmentVariable: cacheRoot,
            userProfileDirectory: @"C:\Users\dev",
            runtimeIdentifier: "win-x64",
            aspireHostingInformationalVersion: "13.4.2");

        // Assert
        Assert.Equal(DcpPathResolutionKind.Override, result.Kind);
        Assert.Equal(candidate, result.OverridePath);
        Assert.Null(result.UnresolvableDetail);
    }

    [Fact]
    public void Resolve_StalePath_FallbackExists_NugetPackagesEnvUnset_UsesUserProfileDefault()
    {
        // Arrange — NUGET_PACKAGES is unset (null): the cache root must default to
        // "<user profile>/.nuget/packages".
        var userProfileDirectory = @"C:\Users\dev";
        var candidate = BuildDefaultCacheCandidate(userProfileDirectory, "win-x64", "13.4.2", "dcp.exe");
        var fileExists = Exists(candidate);

        // Act
        var result = DcpPathResolver.Resolve(
            metadataDcpCliPath: StaleLinuxMetadataPath,
            fileExists: fileExists,
            nugetPackagesEnvironmentVariable: null,
            userProfileDirectory: userProfileDirectory,
            runtimeIdentifier: "win-x64",
            aspireHostingInformationalVersion: "13.4.2");

        // Assert
        Assert.Equal(DcpPathResolutionKind.Override, result.Kind);
        Assert.Equal(candidate, result.OverridePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_StalePath_FallbackExists_NugetPackagesEnvBlank_UsesUserProfileDefault(
        string blankEnvironmentValue)
    {
        // Arrange — a blank (empty/whitespace) NUGET_PACKAGES must be treated the same as
        // unset, not as an explicit empty cache root.
        var userProfileDirectory = @"C:\Users\dev";
        var candidate = BuildDefaultCacheCandidate(userProfileDirectory, "linux-x64", "13.4.2", "dcp");
        var fileExists = Exists(candidate);

        // Act
        var result = DcpPathResolver.Resolve(
            metadataDcpCliPath: StaleLinuxMetadataPath,
            fileExists: fileExists,
            nugetPackagesEnvironmentVariable: blankEnvironmentValue,
            userProfileDirectory: userProfileDirectory,
            runtimeIdentifier: "linux-x64",
            aspireHostingInformationalVersion: "13.4.2");

        // Assert
        Assert.Equal(DcpPathResolutionKind.Override, result.Kind);
        Assert.Equal(candidate, result.OverridePath);
    }

    [Fact]
    public void Resolve_NugetPackagesEnv_WrappedInDoubleQuotes_QuotesAreStripped()
    {
        // Arrange — PINS intended behaviour (MINOR 4c, gatekeeper review): Windows
        // batch/cmd's `set VAR="value"` idiom embeds the quote characters literally in the
        // environment-variable value (unlike POSIX shells, which strip quoting before the
        // process ever sees it). A NUGET_PACKAGES value set that way — or pasted with
        // surrounding quotes into a CI step — must resolve to the SAME cache root as the
        // unquoted value, not to a broken path with literal quote characters embedded in it.
        // The expected candidate is built from the UNQUOTED cache root (what
        // CleanEnvironmentValue is expected to produce after stripping); the RAW quoted
        // string is what's actually passed as nugetPackagesEnvironmentVariable.
        var unquotedCacheRoot = @"D:\nuget-alt";
        var candidate = BuildCandidate(unquotedCacheRoot, "win-x64", "13.4.2", "dcp.exe");
        var fileExists = Exists(candidate);

        // Act — the raw env value carries literal leading/trailing '"' characters.
        var result = DcpPathResolver.Resolve(
            metadataDcpCliPath: StaleLinuxMetadataPath,
            fileExists: fileExists,
            nugetPackagesEnvironmentVariable: $"\"{unquotedCacheRoot}\"",
            userProfileDirectory: @"C:\Users\dev",
            runtimeIdentifier: "win-x64",
            aspireHostingInformationalVersion: "13.4.2");

        // Assert — resolves exactly as the unquoted value would; no embedded quote
        // characters leak into the probed candidate path.
        Assert.Equal(DcpPathResolutionKind.Override, result.Kind);
        Assert.Equal(candidate, result.OverridePath);
    }

    [Fact]
    public void Resolve_NugetPackagesEnv_TrailingSeparator_StillResolves()
    {
        // Arrange — PINS intended behaviour (MINOR 4c, gatekeeper review): a NUGET_PACKAGES
        // value with a trailing directory separator needs no special-case handling in this
        // resolver — Path.Combine already avoids inserting a DOUBLED separator when the
        // preceding segment already ends with one — but this test documents and locks in
        // that Path.Combine reliance so a future refactor cannot silently regress it (e.g.
        // by switching to raw string concatenation).
        //
        // Uses Path.DirectorySeparatorChar (NOT a hardcoded '\') so the trailing separator
        // always matches the CURRENT platform's own convention: Path.Combine only skips
        // inserting an extra separator when the preceding segment already ends with a
        // separator THIS platform recognises — a hardcoded '\' would be a no-op on Linux
        // (backslash isn't a recognised separator there) and would insert an extra '/'
        // anyway, which is exactly the cross-platform trap this whole file was rewritten
        // to avoid (see the file-header remarks).
        var cacheRootNoSeparator = @"D:\nuget-alt";
        var cacheRootWithTrailingSeparator = cacheRootNoSeparator + Path.DirectorySeparatorChar;
        var candidate = BuildCandidate(cacheRootNoSeparator, "win-x64", "13.4.2", "dcp.exe");
        var fileExists = Exists(candidate);

        // Act — trailing separator on the cache root.
        var result = DcpPathResolver.Resolve(
            metadataDcpCliPath: StaleLinuxMetadataPath,
            fileExists: fileExists,
            nugetPackagesEnvironmentVariable: cacheRootWithTrailingSeparator,
            userProfileDirectory: @"C:\Users\dev",
            runtimeIdentifier: "win-x64",
            aspireHostingInformationalVersion: "13.4.2");

        // Assert — resolves to the SAME candidate as the no-trailing-separator form.
        Assert.Equal(DcpPathResolutionKind.Override, result.Kind);
        Assert.Equal(candidate, result.OverridePath);
    }

    [Theory]
    [InlineData("linux-x64", "dcp")]
    [InlineData("linux-arm64", "dcp")]
    [InlineData("osx-x64", "dcp")]
    [InlineData("osx-arm64", "dcp")]
    [InlineData("win-x64", "dcp.exe")]
    [InlineData("win-x86", "dcp.exe")]
    [InlineData("win-arm64", "dcp.exe")]
    public void Resolve_StalePath_FallbackExists_UsesCorrectExecutableNamePerRid(
        string runtimeIdentifier, string expectedExeName)
    {
        // Arrange
        var candidate = BuildCandidate("/cache", runtimeIdentifier, "13.4.2", expectedExeName);
        var fileExists = Exists(candidate);

        // Act
        var result = DcpPathResolver.Resolve(
            metadataDcpCliPath: StaleLinuxMetadataPath,
            fileExists: fileExists,
            nugetPackagesEnvironmentVariable: "/cache",
            userProfileDirectory: "/home/dev",
            runtimeIdentifier: runtimeIdentifier,
            aspireHostingInformationalVersion: "13.4.2");

        // Assert
        Assert.Equal(DcpPathResolutionKind.Override, result.Kind);
        Assert.EndsWith(expectedExeName, result.OverridePath, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_PackageIdIsLowerCased_EvenWhenRidIsMixedCase()
    {
        // Arrange — NuGet package folder ids are always lower-cased; a mixed-case rid
        // input must still probe the lower-cased folder name.
        var candidate = BuildCandidate("/cache", "win-x64", "13.4.2", "dcp.exe");
        var fileExists = Exists(candidate);

        // Act
        var result = DcpPathResolver.Resolve(
            metadataDcpCliPath: StaleLinuxMetadataPath,
            fileExists: fileExists,
            nugetPackagesEnvironmentVariable: "/cache",
            userProfileDirectory: "/home/dev",
            runtimeIdentifier: "WIN-X64",
            aspireHostingInformationalVersion: "13.4.2");

        // Assert
        Assert.Equal(DcpPathResolutionKind.Override, result.Kind);
        Assert.Equal(candidate, result.OverridePath);
    }

    // -----------------------------------------------------------------------
    // Unresolvable — no usable DCP executable anywhere
    // -----------------------------------------------------------------------

    [Fact]
    public void Resolve_StalePath_FallbackMissing_ReturnsUnresolvable_WithPackageIdVersionAndProbedPath()
    {
        // Arrange — neither the embedded path nor the local-cache candidate exists.
        var cacheRoot = @"D:\nuget-alt";
        var probedPath = BuildCandidate(cacheRoot, "win-x64", "13.4.2", "dcp.exe");
        var fileExists = Exists(); // nothing exists.

        // Act
        var result = DcpPathResolver.Resolve(
            metadataDcpCliPath: StaleLinuxMetadataPath,
            fileExists: fileExists,
            nugetPackagesEnvironmentVariable: cacheRoot,
            userProfileDirectory: @"C:\Users\dev",
            runtimeIdentifier: "win-x64",
            aspireHostingInformationalVersion: "13.4.2");

        // Assert
        Assert.Equal(DcpPathResolutionKind.Unresolvable, result.Kind);
        Assert.Null(result.OverridePath);
        Assert.NotNull(result.UnresolvableDetail);
        var detail = result.UnresolvableDetail!;
        Assert.Contains("aspire.hosting.orchestration.win-x64", detail, StringComparison.Ordinal);
        Assert.Contains("13.4.2", detail, StringComparison.Ordinal);
        Assert.Contains(probedPath, detail, StringComparison.Ordinal);
        Assert.Contains(StaleLinuxMetadataPath, detail, StringComparison.Ordinal);
        // The remedy must be actionable.
        Assert.Contains("ASPIRE_DCP_PATH", detail, StringComparison.Ordinal);
        Assert.Contains("Aspire.AppHost.Sdk", detail, StringComparison.Ordinal);
        Assert.Contains("dotnet restore", detail, StringComparison.Ordinal);
        // MAJOR 2 (gatekeeper review): ASPIRE_DCP_PATH names a DIRECTORY, not the
        // executable file — the remedy wording must say so, matching the pinned
        // ConfigureDefaultDcpOptions/BundleDiscovery.GetDcpExecutablePath source.
        Assert.Contains("directory containing the dcp executable", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_VersionFolderPresent_DcpBinaryMissing_UnresolvableNamesProbedPath()
    {
        // Arrange — PINS intended behaviour (MINOR 4d, gatekeeper review): a real-world
        // shape distinct from "nothing was ever restored" — the user's cache DOES contain
        // OTHER files under the exact version folder (simulated here via a sibling path
        // that fileExists reports as present, e.g. an extension DLL), but the dcp
        // executable itself is absent (partial extraction, antivirus quarantine, a
        // corrupted package). Resolve() only ever probes the exact candidate exe path, so
        // this is mechanically the same "fileExists(candidate) == false" branch as the
        // fully-empty-cache case — this test locks in that the Unresolvable detail still
        // names the exact probed path even when neighbouring files ARE present.
        var cacheRoot = @"D:\nuget-alt";
        var siblingExtensionFile = Path.Combine(
            cacheRoot, "aspire.hosting.orchestration.win-x64", "13.4.2", "tools", "ext", "Some.Extension.dll");
        var missingBinary = BuildCandidate(cacheRoot, "win-x64", "13.4.2", "dcp.exe");
        var fileExists = Exists(siblingExtensionFile); // the folder "has content" but not the exe.

        // Act
        var result = DcpPathResolver.Resolve(
            metadataDcpCliPath: StaleLinuxMetadataPath,
            fileExists: fileExists,
            nugetPackagesEnvironmentVariable: cacheRoot,
            userProfileDirectory: @"C:\Users\dev",
            runtimeIdentifier: "win-x64",
            aspireHostingInformationalVersion: "13.4.2");

        // Assert
        Assert.Equal(DcpPathResolutionKind.Unresolvable, result.Kind);
        Assert.NotNull(result.UnresolvableDetail);
        Assert.Contains(missingBinary, result.UnresolvableDetail, StringComparison.Ordinal);
        Assert.Contains("13.4.2", result.UnresolvableDetail, StringComparison.Ordinal);
        Assert.Contains("aspire.hosting.orchestration.win-x64", result.UnresolvableDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_VersionCannotBeDetermined_ReturnsUnresolvable_WithVersionUnknownDetail()
    {
        // Arrange — no informational version AND a stale path that does not contain the
        // expected "aspire.hosting.orchestration.<rid>/<version>" shape to fall back on.
        // No Path.Combine is ever reached on this branch (Resolve returns before computing
        // any candidate when the version cannot be determined at all), so this test is
        // inherently OS-agnostic.
        var fileExists = Exists();

        // Act
        var result = DcpPathResolver.Resolve(
            metadataDcpCliPath: "/some/unrelated/path/to/dcp",
            fileExists: fileExists,
            nugetPackagesEnvironmentVariable: null,
            userProfileDirectory: @"C:\Users\dev",
            runtimeIdentifier: "win-x64",
            aspireHostingInformationalVersion: null);

        // Assert
        Assert.Equal(DcpPathResolutionKind.Unresolvable, result.Kind);
        Assert.NotNull(result.UnresolvableDetail);
        Assert.Contains("version could not be determined", result.UnresolvableDetail, StringComparison.Ordinal);
        Assert.Contains("ASPIRE_DCP_PATH", result.UnresolvableDetail, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // ResolveVersion — version derivation (pure string parsing; no Path.Combine
    // involved anywhere in this section, so every test here is inherently OS-agnostic)
    // -----------------------------------------------------------------------

    [Fact]
    public void ResolveVersion_InformationalVersionWithBuildMetadata_StripsAtFirstPlus()
    {
        // Act
        var version = DcpPathResolver.ResolveVersion(
            assemblyInformationalVersion: "13.4.2+a1b2c3d4e5f6",
            staleMetadataPath: StaleLinuxMetadataPath);

        // Assert
        Assert.Equal("13.4.2", version);
    }

    [Fact]
    public void ResolveVersion_InformationalVersionWithoutBuildMetadata_ReturnedAsIs()
    {
        // Act
        var version = DcpPathResolver.ResolveVersion(
            assemblyInformationalVersion: "13.4.2",
            staleMetadataPath: StaleLinuxMetadataPath);

        // Assert
        Assert.Equal("13.4.2", version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveVersion_InformationalVersionAbsent_FallsBackToStalePathSegment(
        string? assemblyInformationalVersion)
    {
        // Act — informational version is unavailable; parse the version segment out of the
        // stale metadata path instead.
        var version = DcpPathResolver.ResolveVersion(
            assemblyInformationalVersion: assemblyInformationalVersion,
            staleMetadataPath: StaleLinuxMetadataPath);

        // Assert
        Assert.Equal("13.4.2", version);
    }

    [Fact]
    public void ResolveVersion_InformationalVersionAbsent_StalePathUsesBackslashes_StillParses()
    {
        // Arrange — a stale path captured on Windows uses '\' separators.
        const string windowsStalePath =
            @"C:\Users\runner\.nuget\packages\aspire.hosting.orchestration.win-x64\13.4.2\tools\dcp.exe";

        // Act
        var version = DcpPathResolver.ResolveVersion(
            assemblyInformationalVersion: null,
            staleMetadataPath: windowsStalePath);

        // Assert
        Assert.Equal("13.4.2", version);
    }

    [Fact]
    public void ResolveVersion_NeitherSourceAvailable_ReturnsNull()
    {
        // Act — no informational version, and a stale path with no
        // "aspire.hosting.orchestration.<rid>/<version>" shape to parse.
        var version = DcpPathResolver.ResolveVersion(
            assemblyInformationalVersion: null,
            staleMetadataPath: "/completely/unrelated/path");

        // Assert
        Assert.Null(version);
    }

    // -----------------------------------------------------------------------
    // The thrown OrchestrationException classifies as an Environment error (§12.1)
    // -----------------------------------------------------------------------

    [Fact]
    public void UnresolvableFailure_WrappedAsOrchestrationException_ClassifiesAsEnvironmentError()
    {
        // Arrange — reproduce exactly how HeadlessTopology.ApplyDcpPathSelfHeal wraps an
        // Unresolvable resolution: Kind=Provision, ResourceName="dcp". fileExists is false
        // for everything, so the Unresolvable outcome is guaranteed regardless of what the
        // actual (OS-dependent) candidate string looks like — this test asserts only the
        // classification, not any specific path content, so it is inherently OS-agnostic.
        var fileExists = Exists();
        var resolution = DcpPathResolver.Resolve(
            metadataDcpCliPath: StaleLinuxMetadataPath,
            fileExists: fileExists,
            nugetPackagesEnvironmentVariable: null,
            userProfileDirectory: @"C:\Users\dev",
            runtimeIdentifier: "win-x64",
            aspireHostingInformationalVersion: "13.4.2");
        Assert.Equal(DcpPathResolutionKind.Unresolvable, resolution.Kind);

        var info = new OrchestrationErrorInfo(
            Kind: OrchestrationErrorKind.Provision,
            ResourceName: "dcp",
            RegistryHost: null,
            AuthStatus: null,
            Detail: resolution.UnresolvableDetail!);

        // Act
        var exception = new OrchestrationException(info);
        var evt = EnvironmentErrorEvents.Create(exception.Info, "run-1", DateTimeOffset.UtcNow);

        // Assert — always an Environment error, never a test Fail (§12.1 hard invariant).
        Assert.Equal(OrchestrationErrorKind.Provision, exception.Info.Kind);
        Assert.Equal("dcp", exception.Info.ResourceName);
        Assert.Equal(Verdict.EnvironmentError, evt.Verdict);
        Assert.Equal(EventTypes.EnvironmentError, evt.Type);
    }

    // -----------------------------------------------------------------------
    // Test helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a <c>fileExists</c> predicate that returns <see langword="true"/> only for
    /// the supplied set of paths (ordinal comparison) — every other path is "missing".
    /// </summary>
    private static Func<string, bool> Exists(params string[] existingPaths)
    {
        var set = new HashSet<string>(existingPaths, StringComparer.Ordinal);
        return path => set.Contains(path);
    }

    /// <summary>
    /// Builds the exact candidate path <see cref="DcpPathResolver.Resolve"/> computes
    /// internally when a cache root is available — <c>Path.Combine(cacheRoot,
    /// "aspire.hosting.orchestration.&lt;rid&gt;", version, "tools", exeName)</c> — from the
    /// SAME raw <paramref name="cacheRoot"/> string a test also passes (directly, or via a
    /// wrapper such as quoting) to <c>Resolve</c>'s <c>nugetPackagesEnvironmentVariable</c>
    /// parameter. See the file-header remarks for why the expected candidate must always be
    /// DERIVED this way rather than hardcoded as an independent literal.
    /// </summary>
    private static string BuildCandidate(string cacheRoot, string rid, string version, string exeName) =>
        Path.Combine(cacheRoot, $"aspire.hosting.orchestration.{rid}", version, "tools", exeName);

    /// <summary>
    /// Builds the exact candidate path <see cref="DcpPathResolver.Resolve"/> computes when
    /// NO cache root is supplied and it falls back to <c>&lt;user profile&gt;/.nuget/packages</c>
    /// — see <see cref="BuildCandidate"/> for why this must be derived via
    /// <see cref="Path.Combine(string, string)"/> from the identical raw segments rather than
    /// hardcoded.
    /// </summary>
    private static string BuildDefaultCacheCandidate(
        string userProfileDirectory, string rid, string version, string exeName) =>
        Path.Combine(
            userProfileDirectory, ".nuget", "packages",
            $"aspire.hosting.orchestration.{rid}", version, "tools", exeName);
}
