// The standing guard on EVERY child-process launch site in the censused trees — issues #475, #481.
//
// WHY THIS FILE EXISTS, AND WHY THE BEHAVIOURAL TEST BESIDE IT IS NOT ENOUGH
// ─────────────────────────────────────────────────────────────────────────
// `using var process = Process.Start(...)` reads like a resource guard and is not one. Disposing a
// Process releases the HANDLE; it never stops the process. Three sites in this assembly were
// written that way and killed their child on no path at all:
//
//     PinnedHostPortDockerTests.DockerAsync             short `docker port` / `ps` calls
//     ServerArtifactInjectionDockerTests.RunAsync       short `docker ps` / `exec` / `inspect` calls
//     SutEnvConfigDockerTests.InitializeAsync           `docker build` under a BuildTimeout CTS
//
// The third is the one with teeth: when BuildTimeout blew, WaitForExitAsync threw, the object was
// disposed, and the docker client kept running while the outer finally deleted the build context
// out from under it — the orphan shape #378 closed in the drill lane. A fourth site,
// TopologyTeardownLeakTests.RunCli, killed on its timeout path only, which is the same
// half-guard #378 found in ExamplesCompileTests.
//
// A behavioural test can prove the helper kills (ChildProcessKillTreeTests does). It cannot prove
// that the NEXT launch site somebody adds calls it, and "the next one" is how all four of these
// arrived — nobody wrote a site intending to leak. So the property that needs a gate is
// syntactic: every child-process launch in every censused tree sits in a member with a `finally`
// that kills.
//
// ROSLYN, NOT A REGEX, for the reason the house idiom gives (see
// Vouchfx.Engine.Runtime.Tests/DrillHostSweepCallSiteCensusTests and
// Vouchfx.Cli.Tests/AsciiRuntimeOutputCensusTests): telling a real launch from the same words in
// prose is exactly what a regex gets wrong, and this file names both methods many times over.
//
// The mechanism is NODE KIND. `descendIntoTrivia: false` keeps this file's `//` comments out, but
// it does nothing about the assertion strings below, which are ordinary syntax and not trivia. What
// makes those invisible is FindLaunchSites: it switches on InvocationExpressionSyntax and
// ObjectCreationExpressionSyntax and returns false for everything else, and a string literal —
// interpolated or not — is neither.
//
// SCOPE: THREE ROOTS — THE PRODUCTION TREE AND TWO TEST ASSEMBLIES. This census reads the whole of
// src/, plus Vouchfx.Engine.Orchestration.Tests and Vouchfx.Engine.Runtime.Tests — the latter being
// the drill lane, where issue #378 found the same defect first, and where the blast radius is worse
// than here: an unguarded launch there strands a CLI holding DCP, its containers and its
// aspire-session-network-*, which surfaces later as a build failure naming no test.
//
// WHY src/ JOINED, AND WHAT ITS ABSENCE COST — issue #481. Until then this census read the two test
// projects and nothing else, so the production tree had never been censused at all. src/ holds
// exactly ONE child-process launch, Vouchfx.Cli.Selection.SystemProcessRunner, on the
// customer-facing `--changed-since` path — and it carried the defect this file exists to catch,
// with additions. It disposed the Process on no path; the launch sat outside any try that owned
// cleanup, so a throw out of the output capture abandoned a live child with nothing left holding a
// reference to it; and it had no time bound at all. No gate could see any of that, because every
// gate was aimed at the test lanes. That is the wrong way round. A leaked child in a test lane
// costs a developer a rerun; the same leak in src/ costs a customer a wedged CLI holding a git
// process they were never handed a way to reclaim. A census that watches only the test lanes is
// watching the lane where the blast radius is smallest.
//
// It can read trees it does not reference. CensusFiles enumerates .cs files off disk and
// FindLaunchSites parses them as text, so assembly boundaries, internals visibility and project
// references are all irrelevant — the only thing needed is a directory path, and every root is
// reachable by walking up from this project's own directory to the repository root. Roslyn is a
// dependency of this project, which is the only assembly that has to load at all.
//
// This is also what makes the `new Process` half of the detection worth its lines: the ONLY such
// launch in the repository is Sprint11ReferenceCapstoneTests, in the drill lane. Over the
// Orchestration project alone that branch never fired, and src/ does not fire it either — the
// production launch uses the static `Process.Start` spelling.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Pins that every child-process launch in the three censused trees — <c>src/</c> and the two test
/// projects named in the header — is paired with a <c>finally</c> that calls
/// <see cref="Vouchfx.TestSupport.ChildProcess.KillTreeQuietly(System.Diagnostics.Process)"/>.
/// </summary>
/// <remarks>
/// Both launch spellings are recognised, because the repository uses both: the static
/// <c>Process.Start(...)</c>, and the <c>new Process { StartInfo = psi }</c> + <c>proc.Start()</c>
/// pair that <c>Vouchfx.Engine.Runtime.Tests.Sprint11ReferenceCapstoneTests</c> uses.
/// </remarks>
public sealed class ChildProcessKillCallSiteCensusTests
{
    /// <summary>The type whose <c>Start</c> this census looks for — spelled apart from the member.</summary>
    /// <remarks>
    /// Two constants rather than one <c>"Process.Start"</c> string purely so the assertion messages
    /// below can name the type and the member separately. It is NOT a guard against this file
    /// counting itself: it could not be, because the detection switches on node kind and a string
    /// literal is never an invocation or an object creation whatever it spells.
    /// </remarks>
    private const string ProcessType = "Process";

    /// <summary>The launching member.</summary>
    private const string StartMethod = "Start";

    /// <summary>The guarded tree-kill a launch site's <c>finally</c> must call.</summary>
    private const string KillMethod = "KillTreeQuietly";

    /// <summary>
    /// The fewest <c>.cs</c> files EACH censused root can plausibly hold. Below it, the census is
    /// assumed to have failed to find that source tree rather than to have found a small one.
    /// </summary>
    /// <remarks>
    /// A floor rather than an exact count, for the reason the sibling census in
    /// Vouchfx.Engine.Runtime.Tests gives: an exact count is a second thing to maintain and would
    /// redden on every unrelated file added. Each test project held well over sixty files when this
    /// was written, and <c>src/</c> — a tree of dozens of projects — several times that again, so
    /// one floor comfortably serves all three. Applied PER ROOT, so a root that silently resolves to
    /// somewhere thin cannot hide behind another one's size.
    /// </remarks>
    private const int MinimumCensusFiles = 20;

    /// <summary>The sibling test project this census reads in addition to its own.</summary>
    private const string DrillLaneProjectName = "Vouchfx.Engine.Runtime.Tests";

    /// <summary>The production source tree, censused since issue #481 — see the header.</summary>
    /// <remarks>
    /// A directory name rather than a project name, unlike <see cref="DrillLaneProjectName"/>:
    /// <c>src/</c> is a tree of dozens of projects and this census wants all of them, present and
    /// future. Nothing here reads a <c>.csproj</c> or needs one to exist, so a project added under
    /// <c>src/</c> is censused from its first file without anybody remembering to list it here.
    /// </remarks>
    private const string SourceRootName = "src";

    /// <summary>
    /// The one file whose launch sites may NOT satisfy the "this census found something" guard.
    /// </summary>
    /// <remarks>
    /// <c>ChildProcessKillTreeTests</c> is this census's own companion, and it launches a child of
    /// its own. Counting it would let the guard be satisfied by the census's own fixtures: delete
    /// every docker class in this project and the vacuity check would still pass, over a population
    /// consisting entirely of the test that exists to exercise the helper. Excluded so that the
    /// guard measures the code under census rather than the census.
    /// </remarks>
    private const string SelfProvisionedLaunchFile = "ChildProcessKillTreeTests.cs";

    /// <summary>Build output, which holds generated sources this census has no business reading.</summary>
    /// <remarks>
    /// Enough for all three roots, checked rather than assumed when <c>src/</c> joined: over that
    /// tree <c>git status --ignored</c> reports <c>bin/</c> and <c>obj/</c> and nothing else, and no
    /// root holds a vendored or generated directory these two do not already cover. Should one
    /// appear, adding it here is the whole fix — the exclusion is applied per root.
    /// </remarks>
    private static readonly string[] s_excludedDirectories = { "bin", "obj" };

    /// <summary>
    /// Every syntactic child-process launch sits in a member whose <c>finally</c> kills the tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The unit is the MEMBER, not the enclosing <c>try</c>.</strong> Some sites start the
    /// child OUTSIDE the try — <c>var proc = Process.Start(psi) ?? throw ...;</c> followed by
    /// <c>try { ... } finally { kill }</c> — which is the correct shape when the start itself must
    /// be allowed to fail before there is anything to kill. A census keyed on "the ancestor try has
    /// a killing finally" would call every one of those an offender and push authors towards the
    /// worse shape. The <c>new Process</c> spelling forces the same choice: the object exists
    /// before <c>Start()</c> is called on it, so there is no single expression to sit inside a try.
    /// (Phrased without a count deliberately: a number in prose is a second thing to maintain that
    /// nothing verifies.)
    /// </para>
    /// <para>
    /// <strong>What this does and does not prove.</strong> It proves a killing <c>finally</c> is
    /// present in the member that launches. It does not prove the finally covers the launch, or
    /// that it kills the right variable — a determined author can satisfy this gate and still leak.
    /// That is the limit of keying on the MEMBER, and NOT a limit of syntax: a strictly stronger
    /// purely syntactic rule exists — require the <c>finally</c>'s kill to name the same local the
    /// launch was assigned to, which is identifier matching <c>AssignedLocalName</c> already does
    /// for the <c>new Process</c> path, with no dataflow. It reddens the case this one clears: a
    /// SECOND, unguarded launch added to a member that already kills a first — which is how three
    /// of the four original sites accreted. Deferred (issue #482) because a launch not assigned to
    /// a named local needs a fallback to this member-level rule, so the gate becomes two rules
    /// rather than one. Do not read this paragraph as "syntax cannot do it"; it can, and the
    /// reason it does not yet is the two-rules cost. It is aimed at the accident (several sites,
    /// several authors, none intending to leak), not at an adversary. The behavioural half lives in
    /// <c>ChildProcessKillTreeTests</c>. Kill-versus-dispose ORDERING is deliberately absent from
    /// that list: the prescribed shape puts the <c>Dispose</c> in <c>using</c>'s own enclosing
    /// <c>finally</c>, so it is the compiler that orders them and there is nothing left for a gate
    /// to check.
    /// </para>
    /// <para>
    /// <strong>Spellings it CANNOT see. Not an exhaustive list — a sample, and read it that way.</strong>
    /// The detection is syntactic: a launch reaches this gate only if it is written as
    /// <c>Process.Start(...)</c> (a member access whose rightmost receiver identifier is
    /// <c>Process</c>) or as a <c>new Process</c> whose <c>Start()</c> is called on the same named
    /// local in the same member. Five shapes known to escape, none of which occurs anywhere in this
    /// repository today (grepped — no target-typed <c>Process x = new()</c>, and no
    /// <c>using static</c> at all, in <c>src/</c>, <c>tests/</c> or <c>examples/</c>):
    /// <list type="bullet">
    ///   <item><description>
    ///     Target-typed <c>Process p = new(); p.Start();</c> — that parses to
    ///     <c>ImplicitObjectCreationExpressionSyntax</c>, which <c>IsProcessConstruction</c> does not
    ///     accept.
    ///   </description></item>
    ///   <item><description>
    ///     <c>using static System.Diagnostics.Process;</c> then a bare <c>Start(psi)</c> — an
    ///     invocation on an <c>IdentifierNameSyntax</c>, where <c>IsProcessStart</c> requires a
    ///     <c>MemberAccessExpressionSyntax</c>. This is NOT the alias case below: the
    ///     rightmost-identifier rule genuinely does cover an aliased type name.
    ///   </description></item>
    ///   <item><description>
    ///     A <c>Process</c> reached through a <c>using</c> alias that renames the TYPE, or through a
    ///     differently-named wrapper.
    ///   </description></item>
    ///   <item><description>
    ///     A <c>new Process</c> whose <c>Start()</c> happens in a DIFFERENT member from the
    ///     construction — a factory returning a live child, which is why
    ///     <c>ChildProcessKillTreeTests</c> hands its child to a callback instead of returning one.
    ///   </description></item>
    ///   <item><description>
    ///     Any launch delegated to a helper in another assembly — the vacuity case the second
    ///     assertion below exists for.
    ///   </description></item>
    /// </list>
    /// The first three are cheap to add the day one appears; the fourth is a real hole a reviewer
    /// must catch.
    /// </para>
    /// <para>
    /// <strong>Vacuity-guarded per root.</strong> "No offending site" is also what a census that
    /// found no FILES reports, and what one that found no LAUNCH SITES reports — the second being
    /// reachable by the last docker class being deleted, or by every launch moving behind a helper
    /// this census cannot see. Both are asserted for EACH root before the real check, so one
    /// healthy tree cannot vouch for a sibling that resolved to nowhere, and the launch guard
    /// discounts this census's own companion file (see <see cref="SelfProvisionedLaunchFile"/>) so
    /// it cannot be satisfied by the fixtures the census brought with it.
    /// </para>
    /// <para>
    /// <strong>Over <c>src/</c> that guard is load-bearing rather than incidental.</strong> Each
    /// test root holds several launch sites, so its guard would survive any one of them moving. The
    /// production root holds exactly ONE — <c>SystemProcessRunner</c> — so its guard is satisfied by
    /// a single site, and the day that launch moves behind a helper this census cannot see, the
    /// <c>src/</c> root reddens on the vacuity assertion instead of passing over an empty
    /// population. That is the intended behaviour, and it is why the guard is asserted per root
    /// rather than over the union: a production tree whose only launch has become invisible is
    /// precisely the state issue #481 found, and it must not be able to hide behind two healthy test
    /// lanes. The correct response to that red is to teach the census the new spelling, or to move
    /// the root, never to delete the assertion.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryProcessLaunch_SitsInAMemberThatKillsTheTreeInAFinally()
    {
        var launches = new List<LaunchSite>();

        foreach (var root in CensusRoots())
        {
            Assert.True(
                Directory.Exists(root),
                $"This census is configured to read '{root}', which does not exist. It reads the "
                + "production source tree and two test projects by PATH, all resolved by walking up "
                + "from this project's own directory, so renaming or moving any of them takes that "
                + "root out from under this census and would otherwise leave part of the population "
                + "silently uncensused.");

            var files = CensusFiles(root);

            Assert.True(
                files.Count > MinimumCensusFiles,
                $"This census found only {files.Count} .cs file(s) under '{root}', which is below "
                + $"the floor of {MinimumCensusFiles}. It has almost certainly resolved the wrong "
                + "directory rather than found a small project - and a census over no files passes "
                + "for free.");

            var found = files.SelectMany(file => FindLaunchSites(root, file)).ToList();

            Assert.True(
                found.Any(site => !site.File.Equals(SelfProvisionedLaunchFile, StringComparison.OrdinalIgnoreCase)),
                $"This census found no child-process launch under '{root}' - neither "
                + $"`{ProcessType}.{StartMethod}` nor `new {ProcessType}` followed by "
                + $"`.{StartMethod}()`, discounting {SelfProvisionedLaunchFile}, which is this "
                + "census's own companion and must not be able to vouch for the tree it sits in. "
                + "Either every launch moved out of that root - in which case this gate is now "
                + "watching nothing there and should move with them - or they are being reached "
                + "through a spelling this census does not recognise, which is worse, because the "
                + "gate reports itself green either way.");

            launches.AddRange(found);
        }

        var offenders = launches.Where(site => !site.HasKillingFinally).ToList();

        Assert.True(
            offenders.Count == 0,
            $"Starting a child gives you a {ProcessType} object whose lifetime is not the child's. "
            + $"Disposing it releases a handle and stops nothing, so a launch whose member has no "
            + $"`finally` calling `{KillMethod}` orphans its child on every path that is not a clean "
            + "completion - a cancelled `docker build` keeps building (issue #475), a CLI child "
            + "keeps DCP, its containers and its network alive (issue #378), and a `--changed-since` "
            + "git call abandons a child the caller was never handed a way to reclaim (issue #481). "
            + "Unguarded launch site(s):\n"
            + string.Join("\n", offenders.Select(site => $"  {site.Display}"))
            + "\n\nFix shape (the house shape - it makes the ordering a compiler guarantee rather "
            + "than something you have to remember). `using var proc = ...;` at method scope with "
            + "the kill in an inner `finally` earns the same guarantee and is what most drill-lane "
            + "sites use; what is NOT acceptable is a kill and a `Dispose()` written next to each "
            + "other, where the order is yours to get wrong:\n\n"
            + $"    var proc = {ProcessType}.{StartMethod}(psi) ?? throw ...;\n"
            + "\n"
            + "    using (proc)\n"
            + "    {\n"
            + "        try { ... }\n"
            + $"        finally {{ ChildProcess.{KillMethod}(proc); }}\n"
            + "    }\n\n"
            + "Put the start in its own try/catch above the `using` if the start itself may fail. "
            + "Do NOT write the kill and a `Dispose()` next to each other in one finally: `using` "
            + "already emits the dispose in an enclosing finally, which is what puts the kill first "
            + $"and keeps it there. See the remarks on `{KillMethod}`.");
    }

    /// <summary>One child-process launch, and whether its member kills the tree in a finally.</summary>
    private sealed record LaunchSite(string Root, string File, int Line, string Member, bool HasKillingFinally)
    {
        /// <summary>The site as an offender message names it: root, file, line, member.</summary>
        /// <remarks>
        /// The root is named by its last path segment, so a test-project site reads
        /// <c>Vouchfx.Engine.Runtime.Tests/Foo.cs</c> and a production one reads <c>src/Foo.cs</c> —
        /// the tree, not the project, on that side. Locating the file is left to
        /// <see cref="Member"/>, which is qualified by its namespace and type path.
        /// </remarks>
        internal string Display => $"{Path.GetFileName(Root)}/{File}({Line}): {Member}";
    }

    /// <summary>
    /// The directories this census reads: this project, the drill lane, and the production source
    /// tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Paths, not assembly references - which is the whole reason anything beyond this project is
    /// reachable at all. Nothing here loads the drill lane's assembly, or any of the assemblies
    /// under <c>src/</c>, and nothing needs to see their internals; the files are read as text.
    /// </para>
    /// <para>
    /// Resolved by walking up from this project's own directory, which
    /// <see cref="ProjectDirectory"/> derives from the compiled assembly: one <c>..</c> reaches
    /// <c>tests/</c>, where the drill lane is a sibling, and a second reaches the repository root,
    /// where <c>src/</c> sits. Each is checked for existence before it is enumerated, so a move that
    /// invalidates one of these relative walks reddens rather than quietly censusing nothing.
    /// </para>
    /// </remarks>
    private static string[] CensusRoots() => new[]
    {
        ProjectDirectory(),
        Path.GetFullPath(Path.Combine(ProjectDirectory(), "..", DrillLaneProjectName)),
        Path.GetFullPath(Path.Combine(ProjectDirectory(), "..", "..", SourceRootName)),
    };

    /// <summary>Every <c>.cs</c> file under one root, excluding build output.</summary>
    private static List<string> CensusFiles(string root) =>
        Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsUnderExcludedDirectory(root, path))
            .ToList();

    private static bool IsUnderExcludedDirectory(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return segments.Any(segment => s_excludedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// This test project's directory, resolved from the compiled assembly rather than from the
    /// working directory, which <c>dotnet test</c> does not guarantee.
    /// </summary>
    private static string ProjectDirectory()
    {
        var assemblyDirectory = Path.GetDirectoryName(
            typeof(ChildProcessKillCallSiteCensusTests).Assembly.Location)!;

        // bin/<cfg>/net8.0 -> the project directory.
        return Path.GetFullPath(Path.Combine(assemblyDirectory, "..", "..", ".."));
    }

    /// <summary>Every child-process launch in one file, with its member's guard state.</summary>
    /// <remarks>
    /// Two spellings, one rule. The static <c>Process.Start(...)</c> IS the launch. A
    /// <c>new Process</c> is not — the object exists un-started — so it counts only when the same
    /// member calls <c>Start()</c> on the local it was assigned to. That qualification is what
    /// keeps the never-started <c>new Process()</c> in <c>ChildProcessKillTreeTests</c>, which
    /// exists precisely to prove the helper tolerates a Process with no child attached, from being
    /// reported as an unguarded launch.
    /// </remarks>
    private static IEnumerable<LaunchSite> FindLaunchSites(string root, string path)
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);

        foreach (var node in tree.GetRoot().DescendantNodes(descendIntoTrivia: false))
        {
            var member = EnclosingMemberBody(node);

            var launches = node switch
            {
                InvocationExpressionSyntax invocation => IsProcessStart(invocation),
                ObjectCreationExpressionSyntax creation =>
                    IsProcessConstruction(creation) && member is not null && IsStarted(creation, member),
                _ => false,
            };

            if (!launches)
            {
                continue;
            }

            yield return new LaunchSite(
                root,
                Path.GetFileName(path),
                node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                EnclosingMemberName(node),
                member is not null && KillsTheTreeInAFinally(member));
        }
    }

    /// <summary>
    /// Whether an invocation is <c>Process.Start(...)</c>, however the type is qualified.
    /// </summary>
    /// <remarks>
    /// Syntactic: <c>Process.Start</c> and <c>System.Diagnostics.Process.Start</c> both match, on
    /// the RIGHTMOST identifier of the receiver. A receiver spelled through a <c>using</c> alias
    /// would not — see the named-limits paragraph on the test above, which lists that and the two
    /// other spellings this census cannot see.
    /// </remarks>
    private static bool IsProcessStart(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax access
            || access.Name.Identifier.ValueText != StartMethod)
        {
            return false;
        }

        return access.Expression switch
        {
            IdentifierNameSyntax name => name.Identifier.ValueText == ProcessType,
            MemberAccessExpressionSyntax qualified => qualified.Name.Identifier.ValueText == ProcessType,
            _ => false,
        };
    }

    /// <summary>
    /// Whether an object creation is <c>new Process(...)</c> or <c>new Process { ... }</c>, however
    /// the type is qualified.
    /// </summary>
    private static bool IsProcessConstruction(ObjectCreationExpressionSyntax creation) =>
        creation.Type switch
        {
            IdentifierNameSyntax name => name.Identifier.ValueText == ProcessType,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText == ProcessType,
            _ => false,
        };

    /// <summary>
    /// Whether the <see cref="System.Diagnostics.Process"/> this expression creates is actually
    /// STARTED in the same member — which is what turns a construction into a launch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keyed on the local the creation is assigned to, so <c>proc.Start()</c> counts and an
    /// unrelated <c>stopwatch.Start()</c> in the same member does not. Both the declaration form
    /// (<c>var proc = new Process { ... };</c>) and plain assignment (<c>proc = new Process();</c>)
    /// are read.
    /// </para>
    /// <para>
    /// A construction that is NOT assigned to a named local — passed straight to a call, or started
    /// inline — falls back to "any <c>.Start()</c> in this member", which is the conservative
    /// direction: it can over-report inside a member that both builds a Process and starts
    /// something else, and over-reporting an unguarded launch costs a reviewer a minute, while
    /// under-reporting one costs a session.
    /// </para>
    /// </remarks>
    private static bool IsStarted(ObjectCreationExpressionSyntax creation, SyntaxNode memberBody)
    {
        var local = AssignedLocalName(creation);

        return memberBody
            .DescendantNodes(descendIntoTrivia: false)
            .OfType<InvocationExpressionSyntax>()
            .Any(invocation =>
                invocation.Expression is MemberAccessExpressionSyntax access
                && access.Name.Identifier.ValueText == StartMethod
                && (local is null
                    || (access.Expression is IdentifierNameSyntax receiver
                        && receiver.Identifier.ValueText == local)));
    }

    /// <summary>The local this creation is assigned to, or <see langword="null"/> when it is not.</summary>
    private static string? AssignedLocalName(ObjectCreationExpressionSyntax creation) =>
        creation.Parent switch
        {
            EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator } =>
                declarator.Identifier.ValueText,
            AssignmentExpressionSyntax { Left: IdentifierNameSyntax left } assignment
                when assignment.Right == creation => left.Identifier.ValueText,
            _ => null,
        };

    /// <summary>
    /// The body of the smallest member-like construct the launch sits in — the scope whose
    /// <c>finally</c> clauses are allowed to guard it.
    /// </summary>
    /// <remarks>
    /// Lambdas and local functions are member-like here on purpose: a launch inside one is guarded
    /// by ITS OWN finally, never by one in the enclosing method, which may already have returned.
    /// </remarks>
    private static SyntaxNode? EnclosingMemberBody(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case AnonymousFunctionExpressionSyntax lambda:
                    return lambda;
                case LocalFunctionStatementSyntax local:
                    return local;
                case BaseMethodDeclarationSyntax method:
                    return method;
                case AccessorDeclarationSyntax accessor:
                    return accessor;
                case PropertyDeclarationSyntax property:
                    return property;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether this member body contains a <c>finally</c> that calls the guarded tree-kill.
    /// </summary>
    /// <remarks>
    /// Nested member-like bodies are NOT descended into: a killing finally inside a lambda declared
    /// in this method guards that lambda's own child, not this method's.
    /// </remarks>
    private static bool KillsTheTreeInAFinally(SyntaxNode memberBody) =>
        memberBody
            .DescendantNodes(descendIntoChildren: child => child == memberBody || !IsNestedMemberBody(child))
            .OfType<FinallyClauseSyntax>()
            .Any(NamesTheKill);

    private static bool IsNestedMemberBody(SyntaxNode node) =>
        node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax;

    private static bool NamesTheKill(FinallyClauseSyntax clause) =>
        clause
            .DescendantNodes(descendIntoTrivia: false)
            .OfType<InvocationExpressionSyntax>()
            .Any(invocation => invocation.Expression switch
            {
                MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText == KillMethod,
                IdentifierNameSyntax name => name.Identifier.ValueText == KillMethod,
                _ => false,
            });

    /// <summary>The member a launch sits in, qualified by its namespace and type path.</summary>
    /// <remarks>
    /// <strong>Must agree with <see cref="EnclosingMemberBody(SyntaxNode)"/> about lambdas.</strong>
    /// That method treats a lambda as its own guarding scope, so a label that walked past the lambda
    /// to the enclosing method would send a reader to a <c>finally</c> that was not the one judged.
    /// Only the message is affected — the verdict always comes from
    /// <see cref="EnclosingMemberBody(SyntaxNode)"/> — but a message naming the wrong member is a
    /// message pointing at the wrong code.
    /// </remarks>
    private static string EnclosingMemberName(SyntaxNode node)
    {
        var member = "<file scope>";
        var insideLambda = false;

        foreach (var ancestor in node.Ancestors())
        {
            if (ancestor is AnonymousFunctionExpressionSyntax)
            {
                // Keep walking to name the method the lambda is written in, but record that the
                // guarding scope is the lambda rather than that method.
                insideLambda = true;
                continue;
            }

            if (ancestor is MethodDeclarationSyntax method)
            {
                member = method.Identifier.ValueText;
                break;
            }

            if (ancestor is ConstructorDeclarationSyntax constructor)
            {
                member = constructor.Identifier.ValueText;
                break;
            }

            if (ancestor is PropertyDeclarationSyntax property)
            {
                member = property.Identifier.ValueText;
                break;
            }

            if (ancestor is LocalFunctionStatementSyntax local)
            {
                member = local.Identifier.ValueText;
                break;
            }
        }

        if (insideLambda)
        {
            member += " (lambda)";
        }

        var typePath = node
            .Ancestors()
            .OfType<TypeDeclarationSyntax>()
            .Select(type => type.Identifier.ValueText)
            .Reverse()
            .ToList();

        return typePath.Count == 0 ? member : $"{string.Join(".", typePath)}.{member}";
    }
}
