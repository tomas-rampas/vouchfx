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
//   • `IResourceBuilder<out T>` is COVARIANT — it converts UP and never back down. A widened
//     `IResourceBuilder<IResource>` therefore converts to `IResourceBuilder<ContainerResource>`
//     ONLY when the runtime builder's own type argument IS, or derives from, `ContainerResource`
//     (the four `AddContainer`-backed registrations — mailpit, azureservicebus, dynamodb, minio —
//     are the identity case; `AddKafka` is the derived case, its runtime type being
//     `DistributedApplicationResourceBuilder<KafkaServerResource>`), and yields null otherwise —
//     e.g. for an `AddDatabase` child. That is why `Apply` takes a container-typed builder and
//     each caller narrows BEFORE calling: the mapper resolves a dependency's own container by
//     name, so no widened builder is ever in play (#426).
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
    /// <param name="pathDisclosures">
    /// The run's <see cref="SecurityPathDisclosureLedger"/>, or <see langword="null"/> for a caller
    /// that has none (every non-production <c>Map</c> call site).
    /// <para>
    /// <strong>This is the recording site #473 exists for, and it is here because this is where
    /// both halves of the pair are in hand.</strong> Every diagnostic THIS method raises names the
    /// declared text by construction — that is #357's rule and the throws below observe it. What
    /// this method cannot constrain is the text of a failure raised by the code it hands
    /// <paramref name="resolvedSuiteDirectory"/>-rooted absolute paths to:
    /// <c>WithContainerFiles</c> streams the bytes through the Docker daemon API, and a file that
    /// becomes unreadable, or a stage the daemon rejects, between this eager check and container
    /// start produces a message the engine did not write. That message escapes the
    /// <c>Configure</c> closure, is wrapped as <c>OrchestrationErrorInfo.Detail</c> by
    /// <c>SuiteTopology.StartAsync</c>, and reaches the §14 event stream, <c>--events</c>, JUnit
    /// and the HTML report — the same channel, and the same shape, as the librdkafka leak that
    /// created the ledger. Recording the pair here is what lets the three scrub chokepoints
    /// substitute the author's own text back into it.
    /// </para>
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
        string resolvedSuiteDirectory,
        SecurityPathDisclosureLedger? pathDisclosures = null)
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

            // #473: the pair, recorded the moment it exists and BEFORE the existence check below.
            //
            // BEFORE, not after, and the ordering is the point rather than an accident. The
            // existence check's own throw already names only the declared text, so recording after
            // it would buy nothing for that path — while a `source` that resolves cleanly and is
            // then rejected by the daemon is precisely the case the ledger is for, and it reaches
            // that daemon whether or not File.Exists agreed a moment earlier. Recording at the
            // resolution keeps "the ledger knows every resolved path this method handed onward"
            // true by construction, rather than true for the subset of them that survived a
            // sibling guard.
            //
            // Record ignores a null/blank/equal pair itself, so no guard here: `resolvedSource` is
            // non-null whenever containment returned null, and an author who wrote an absolute
            // path was already refused by containment.
            pathDisclosures?.Record(resolvedSource, artifacts[i].Source);

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
                    + "'/etc/kafka/secrets/kafka.keystore.jks' - this engine does not normalise a "
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
                    + $"'{ownerName}'. Two artefacts cannot land on one in-container path - which "
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
    /// The CONTAINER the artefacts are copied into — a service's own image-form container builder,
    /// or, for a dependency, the container the mapper resolved by the declared dependency name.
    /// </param>
    /// <param name="groups">The planned groups from <see cref="Plan"/>.</param>
    /// <remarks>
    /// The parameter is container-TYPED on purpose (#426). It was previously widened to
    /// <c>IResourceBuilder&lt;IResource&gt;</c>, guarded by an <c>as</c> cast and a throw for the
    /// null case — and that width is precisely what let the dependency call site pass a resource
    /// with no container filesystem. Narrowing it makes that class of mistake unrepresentable at
    /// compile time instead of diagnosed at topology-build time; covariance explains why the wide
    /// type compiled, which is not a reason to keep it.
    /// </remarks>
    /// <remarks>
    /// The owner's kind and name are no longer parameters: they existed solely to compose the
    /// deleted throw's message, and <see cref="Plan"/> — which owns every author-facing diagnostic
    /// this feature emits — still takes both.
    /// </remarks>
    internal static void Apply(
        IResourceBuilder<ContainerResource> builder,
        IReadOnlyList<ServerArtifactGroup> groups)
    {
        if (groups.Count == 0)
        {
            return;
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

            builder.WithContainerFiles(group.DestinationDirectory, entries);
        }
    }
}
