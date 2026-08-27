// Vouchfx.Engine.Orchestration — ServerArtifactInjection (authenticated-infrastructure-mtls,
// slice E — REQ-016, EDGE-007).
//
// Copies author-declared `security.serverArtifacts` host files INTO a container at
// topology-build time, so a customer-supplied broker can find the keystore its own entrypoint
// looks for.
//
// WHY A COPY AND NOT A BIND MOUNT — the decision REQ-016 turns on. A bind mount depends on the
// host filesystem and the Docker daemon sharing a view of the same path: under a remote daemon
// or Docker-in-Docker they do not, and the mount then surfaces an EMPTY directory inside the
// container rather than failing. The broker's entrypoint tests for the keystore's EXISTENCE, so
// an empty mount reproduces EDGE-005 exactly — a healthy broker with no SSL listener and no
// error anywhere. `WithContainerFiles` streams the bytes through the daemon API instead, so it
// carries no host/daemon filesystem co-location assumption at all. That is what makes
// topological parity (§3: a suite runs unchanged local / SaaS / CI) hold for this feature.
//
// MEASURED against the pinned Aspire 13.4.2, by reflection over the loaded assembly and by
// building a real application model:
//   • `WithContainerFiles<T>(this IResourceBuilder<T>, string destinationPath,
//     IEnumerable<ContainerFileSystemItem> entries, …) where T : ContainerResource` — the
//     entries overload used here, because `serverArtifacts[].target` names a FILE and the
//     `(destinationPath, sourcePath)` overload names a directory pair.
//   • It lands ONE `ContainerFileSystemCallbackAnnotation { DestinationPath, Callback }` on the
//     resource; resolving that callback returns the `ContainerFile { Name, SourcePath }` entries
//     verbatim. No `WithBindMount`, no `ContainerMountAnnotation`.
//   • `IResourceBuilder<out T>` is COVARIANT, so the `IResourceBuilder<IResource>` this mapper
//     retains for a dependency casts back to `IResourceBuilder<ContainerResource>` with `as`
//     whenever the concrete resource derives from `ContainerResource` (verified against a real
//     `AddKafka` builder, whose runtime type is `DistributedApplicationResourceBuilder<
//     KafkaServerResource>`).
//
// EDGE-007: `ContainerFile.Contents` is `string?` — TEXT only — and a Java keystore is binary,
// which is why the schema offers no inline `contents:` alternative and this file only ever sets
// `SourcePath`. Setting `Contents` from a file read here would silently corrupt every JKS.
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Vouchfx.Engine.Authoring.Model;

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// One container directory's worth of declared server artefacts for a single service or
/// dependency, resolved to absolute host paths and grouped by their in-container directory.
/// </summary>
/// <param name="DestinationDirectory">
/// The absolute POSIX directory inside the container the files are copied into.
/// </param>
/// <param name="Files">
/// The <c>(in-container file name, absolute host source path)</c> pairs landing in that
/// directory, in declared order.
/// </param>
internal sealed record ServerArtifactGroup(
    string DestinationDirectory,
    IReadOnlyList<(string FileName, string SourcePath)> Files);

/// <summary>
/// Resolves and applies <c>security.serverArtifacts</c> (REQ-016).
/// </summary>
internal static class ServerArtifactInjection
{
    /// <summary>
    /// Resolves every declared artefact for one owner into container-directory groups, EAGERLY —
    /// before any builder mutation — so a malformed declaration fails <c>Map()</c> with a located,
    /// author-facing diagnostic rather than deep inside Aspire's own file-callback resolution
    /// after other resources have already been added.
    /// </summary>
    /// <param name="security">The owner's declared security block, or <see langword="null"/>.</param>
    /// <param name="ownerKindPlural"><c>"services"</c> or <c>"dependencies"</c>.</param>
    /// <param name="ownerName">The declared service/dependency name.</param>
    /// <param name="resolvedSuiteDirectory">
    /// The fully resolved suite directory every <c>source</c> is taken relative to. It MUST be the
    /// same base <c>EnvironmentSecurityValidator</c> used, or the two stages resolve one declared
    /// path to two different files — containment cannot detect that divergence, because a path
    /// resolved against the wrong base is still contained within THAT base.
    /// </param>
    /// <returns>
    /// The groups to apply, or an empty list when the owner declares no artefacts.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// A declared <c>source</c> is blank, rooted, escapes the suite directory or does not exist;
    /// or a declared <c>target</c> is not an absolute POSIX file path; or two artefacts on the
    /// same owner target the same in-container path.
    /// </exception>
    internal static IReadOnlyList<ServerArtifactGroup> Plan(
        SecuritySpec? security,
        string ownerKindPlural,
        string ownerName,
        string resolvedSuiteDirectory)
    {
        if (security?.ServerArtifacts is not { Count: > 0 } artifacts)
        {
            return Array.Empty<ServerArtifactGroup>();
        }

        // Grouped by destination directory, insertion-ordered so the emitted WithContainerFiles
        // calls follow declaration order — one annotation per directory rather than per file,
        // which keeps a keystore + truststore pair (the common shape) to a single copy operation.
        var groups = new List<(string Directory, List<(string FileName, string SourcePath)> Files)>();
        var claimedTargets = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < artifacts.Count; i++)
        {
            var fieldPath = $"environment.{ownerKindPlural}.{ownerName}.security.serverArtifacts[{i}]";

            var containment = SecurityArtifactPath.TryResolveContained(
                artifacts[i].Source, resolvedSuiteDirectory, out var resolvedSource);
            if (containment is not null)
            {
                throw new ArgumentException($"{fieldPath}.source: {containment}", nameof(security));
            }

            // Existence is REQ-004's check and has already run pre-topology on every production
            // path. Repeated here because this stage is what actually hands the path to the Docker
            // daemon: a file that vanished between validation and topology build would otherwise
            // surface as an opaque container-start failure, which is the exact substitution
            // REQ-004 exists to prevent.
            //
            // NO RESOLVED PATH IN THE MESSAGE (#357's rule, extended). `resolvedSource` is an
            // absolute host path and this ArgumentException's text is carried into the written
            // artefacts, not just the terminal. The declared text is the author's own input and
            // is the actionable half; naming the directory it resolves AGAINST keeps a relative
            // path diagnosable without disclosing the host layout.
            if (!File.Exists(resolvedSource))
            {
                throw new ArgumentException(
                    $"{fieldPath}.source: file '{artifacts[i].Source}' not found, relative to "
                    + "the suite directory.",
                    nameof(security));
            }

            var target = artifacts[i].Target;
            if (string.IsNullOrWhiteSpace(target) || target[0] != '/')
            {
                throw new ArgumentException(
                    $"{fieldPath}.target: '{target}' must be an absolute path inside the container, "
                    + "beginning with '/'.",
                    nameof(security));
            }

            // A container path is POSIX and this engine hands it to the daemon verbatim, so the
            // shapes a POSIX path cannot mean are rejected HERE, with the field named, rather than
            // reaching Docker as an opaque failure. No security boundary is crossed either way —
            // the destination is inside the author's own container and the author chose it — but
            // measured, '/etc/kafka/..' yields a `ContainerFile { Name = ".." }` and
            // '/etc/kafka\secrets\ks.jks' yields a file literally named 'kafka\secrets\ks.jks',
            // and neither diagnoses itself.
            if (target.Contains('\\', StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"{fieldPath}.target: '{target}' contains a backslash. A container path is POSIX: "
                    + "separate its segments with '/', or the whole run of backslashes becomes part "
                    + "of one file NAME rather than a directory path.",
                    nameof(security));
            }

            if (HasDotSegment(target))
            {
                throw new ArgumentException(
                    $"{fieldPath}.target: '{target}' contains a '.' or '..' segment. Give the "
                    + "already-resolved in-container path of the file to create, e.g. "
                    + "'/etc/kafka/secrets/kafka.keystore.jks' — this engine does not normalise a "
                    + "container path, so a '..' segment would be copied through as a literal file "
                    + "name.",
                    nameof(security));
            }

            if (target.Contains("//", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"{fieldPath}.target: '{target}' contains an empty path segment ('//').",
                    nameof(security));
            }

            var lastSlash = target.LastIndexOf('/');
            var fileName = target[(lastSlash + 1)..];
            if (fileName.Length == 0)
            {
                throw new ArgumentException(
                    $"{fieldPath}.target: '{target}' names a directory, not a file. Give the full "
                    + "in-container path of the file to create, e.g. "
                    + "'/etc/kafka/secrets/kafka.keystore.jks'.",
                    nameof(security));
            }

            if (!claimedTargets.Add(target))
            {
                throw new ArgumentException(
                    $"{fieldPath}.target: '{target}' is declared more than once on "
                    + $"'{ownerName}'. Two artefacts cannot land on one in-container path — which "
                    + "one wins is not something this engine will decide silently.",
                    nameof(security));
            }

            // Container paths are POSIX, never host paths: split on '/' by hand rather than via
            // Path.GetDirectoryName, which on Windows would return '\'-separated text and hand
            // Aspire a destination the Linux container cannot have.
            var directory = lastSlash == 0 ? "/" : target[..lastSlash];

            var existing = groups.FindIndex(g => string.Equals(g.Directory, directory, StringComparison.Ordinal));
            if (existing < 0)
            {
                groups.Add((directory, new List<(string, string)> { (fileName, resolvedSource!) }));
            }
            else
            {
                groups[existing].Files.Add((fileName, resolvedSource!));
            }
        }

        return groups
            .Select(g => new ServerArtifactGroup(
                g.Directory, g.Files.ToArray()))
            .ToArray();
    }

    /// <summary>
    /// True when any '/'-delimited segment of a container path is <c>.</c> or <c>..</c>.
    /// </summary>
    /// <remarks>
    /// A plain <c>Contains("..")</c> would also reject the perfectly ordinary
    /// <c>/etc/kafka/secrets/keystore..jks</c>, so the test is per segment.
    /// </remarks>
    private static bool HasDotSegment(string target)
    {
        foreach (var segment in target.Split('/'))
        {
            if (segment is "." or "..")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Applies a planned artefact set to a container-backed resource builder via
    /// <see cref="ContainerResourceBuilderExtensions.WithContainerFiles{T}(IResourceBuilder{T},
    /// string, IEnumerable{ContainerFileSystemItem}, int?, int?, UnixFileMode?)"/> — a copy, never
    /// a bind mount (REQ-016).
    /// </summary>
    /// <param name="builder">
    /// The resource builder, which may be the type-erased <c>IResourceBuilder&lt;IResource&gt;</c>
    /// this mapper retains for a dependency. <see cref="IResourceBuilder{T}"/> is covariant, so a
    /// container-backed resource casts back with <c>as</c>.
    /// </param>
    /// <param name="groups">The planned groups from <see cref="Plan"/>.</param>
    /// <param name="ownerKindPlural"><c>"services"</c> or <c>"dependencies"</c>.</param>
    /// <param name="ownerName">The declared service/dependency name.</param>
    /// <exception cref="ArgumentException">
    /// The resource is not container-backed, so there is no container filesystem to copy into.
    /// </exception>
    internal static void Apply(
        IResourceBuilder<IResource> builder,
        IReadOnlyList<ServerArtifactGroup> groups,
        string ownerKindPlural,
        string ownerName)
    {
        if (groups.Count == 0)
        {
            return;
        }

        if (builder as IResourceBuilder<ContainerResource> is not { } containerBuilder)
        {
            // Unreachable through the schema today — every dependency kind is container-backed and
            // `security` is rejected outright on a project-form service (REQ-023) — but a resource
            // that is not a container has no filesystem to copy into, and silently skipping the
            // copy is the EDGE-005 shape: a healthy container whose entrypoint finds no keystore.
            throw new ArgumentException(
                $"environment.{ownerKindPlural}.{ownerName}.security.serverArtifacts: "
                + $"'{ownerName}' is not backed by a container, so declared artefacts cannot be "
                + "copied into it.",
                nameof(builder));
        }

        foreach (var group in groups)
        {
            var entries = group.Files
                .Select(f => (ContainerFileSystemItem)new ContainerFile
                {
                    Name = f.FileName,

                    // SourcePath, never Contents (EDGE-007): `ContainerFile.Contents` is `string?`
                    // and a Java keystore is binary, so routing a JKS through it would corrupt it
                    // silently. The path is absolute and already containment-checked by Plan.
                    SourcePath = f.SourcePath,
                })
                .ToArray();

            containerBuilder.WithContainerFiles(group.DestinationDirectory, entries);
        }
    }
}
