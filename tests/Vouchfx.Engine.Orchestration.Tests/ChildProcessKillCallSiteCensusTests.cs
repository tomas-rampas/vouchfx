// The standing guard on EVERY child-process launch site in this assembly — issue #475.
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
// TopologyTeardownLeakTests.RunDocker, killed on its timeout path only, which is the same
// half-guard #378 found in ExamplesCompileTests.
//
// A behavioural test can prove the helper kills (ChildProcessKillTreeTests does). It cannot prove
// that the NEXT launch site somebody adds calls it, and "the next one" is how all four of these
// arrived — nobody wrote a site intending to leak. So the property that needs a gate is
// syntactic: every child-process launch in this project sits in a member with a `finally` that
// kills.
//
// ROSLYN, NOT A REGEX, for the reason the house idiom gives (see
// Vouchfx.Engine.Runtime.Tests/DrillHostSweepCallSiteCensusTests and
// Vouchfx.Cli.Tests/AsciiRuntimeOutputCensusTests): telling a real launch from the same words in
// prose is exactly what a regex gets wrong, and this file names both methods many times over.
//
// The mechanism is NODE KIND, not trivia-skipping — an earlier draft credited
// `descendIntoTrivia: false`, which is only half true and misses the more important half. That flag
// keeps this file's `//` comments out; it does nothing about the assertion strings below, which are
// ordinary syntax, not trivia. What makes those invisible is FindLaunchSites: it switches on
// InvocationExpressionSyntax and ObjectCreationExpressionSyntax and returns false for everything
// else, and a string literal — interpolated or not — is neither.
//
// SCOPE. This project only, and the reason is NOT a missing Roslyn reference — an earlier draft of
// this comment said it was, and that was false: Vouchfx.Engine.Runtime.Tests already parses C# with
// CSharpSyntaxTree.ParseText in DrillHostSweepCallSiteCensusTests, so Roslyn arrives there
// transitively with nothing to add. The real obstacle is that ONE definition cannot serve both
// assemblies: neither can see the other's internals, and the shared home (Vouchfx.TestSupport)
// deliberately carries no package references beyond the BCL. Two copies of a census is how a census
// drifts, which is the same argument that moved ChildProcess out of Runtime.Tests in the first
// place.
//
// RESIDUAL RISK OF THAT DEFERRAL, stated rather than left implicit: Runtime.Tests' eight
// child-process launch sites are all guarded today, but nothing gates the ninth — and that is the
// assembly with the WORSE blast radius, because an unguarded launch there strands a CLI holding DCP,
// its containers and its aspire-session-network-*, which surfaces later as a build failure naming no
// test (issue #378).
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
/// Pins that every child-process launch in this project is paired with a <c>finally</c> that calls
/// <see cref="Vouchfx.TestSupport.ChildProcess.KillTreeQuietly(System.Diagnostics.Process)"/>.
/// </summary>
/// <remarks>
/// BOTH launch spellings are recognised, because the repository uses both: the static
/// <c>Process.Start(...)</c> that every site in THIS assembly uses, and the
/// <c>new Process { StartInfo = psi }</c> + <c>proc.Start()</c> pair that
/// <c>Vouchfx.Engine.Runtime.Tests.Sprint11ReferenceCapstoneTests</c> uses. An earlier draft matched
/// only the first and would have watched a door while the adjacent one stood open in a file next
/// door.
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
    /// The fewest <c>.cs</c> files this project can plausibly hold. Below it, the census is assumed
    /// to have failed to find the source tree rather than to have found a small one.
    /// </summary>
    /// <remarks>
    /// A floor rather than an exact count, for the reason the sibling census in
    /// Vouchfx.Engine.Runtime.Tests gives: an exact count is a second thing to maintain and would
    /// redden on every unrelated file added. This project held well over sixty files when this was
    /// written.
    /// </remarks>
    private const int MinimumCensusFiles = 20;

    /// <summary>Build output, which holds generated sources this census has no business reading.</summary>
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
    /// (Phrased without a count deliberately. The count changed inside the very commit that
    /// introduced this paragraph — the commit that added the census also added a launch site — and a
    /// number in prose is a second thing to maintain that nothing verifies.)
    /// </para>
    /// <para>
    /// <strong>What this does and does not prove.</strong> It proves a killing <c>finally</c> is
    /// present in the member that launches. It does not prove the finally covers the launch, that
    /// it kills the right variable, or that the kill precedes the dispose — a determined author can
    /// satisfy this gate and still leak. That is the accepted limit of a syntactic gate: it is
    /// aimed at the accident (four sites, four different authors, none intending to leak), not at
    /// an adversary. The behavioural half lives in <c>ChildProcessKillTreeTests</c>.
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
    /// <strong>Vacuity-guarded twice.</strong> "No offending site" is also what a census that found
    /// no FILES reports, and what one that found no LAUNCH SITES at all reports — the second being
    /// reachable by the last docker class being deleted or by every launch being wrapped in a
    /// helper this census cannot see. Both are asserted before the real check.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryProcessLaunch_SitsInAMemberThatKillsTheTreeInAFinally()
    {
        var files = CensusFiles();

        Assert.True(
            files.Count > MinimumCensusFiles,
            $"This census found only {files.Count} .cs file(s) under '{ProjectDirectory()}', which "
            + $"is below the floor of {MinimumCensusFiles}. It has almost certainly resolved the "
            + "wrong directory rather than found a small project - and a census over no files "
            + "passes for free.");

        var launches = files.SelectMany(FindLaunchSites).ToList();

        Assert.True(
            launches.Count > 0,
            $"This census found no child-process launch - neither `{ProcessType}.{StartMethod}` nor "
            + $"`new {ProcessType}` followed by `.{StartMethod}()` - anywhere in the project. Either "
            + "every child-process launch moved out of this assembly - in which case this gate is "
            + "now watching nothing and should move with them - or it is being reached through a "
            + "spelling this census does not recognise, which is worse, because the gate reports "
            + "itself green either way.");

        var offenders = launches.Where(site => !site.HasKillingFinally).ToList();

        Assert.True(
            offenders.Count == 0,
            $"Starting a child gives you a {ProcessType} object whose lifetime is not the child's. "
            + $"`using var` disposes the {ProcessType} OBJECT and stops nothing, so a launch whose "
            + $"member has no `finally` calling `{KillMethod}` orphans its child on every path that "
            + "is not a clean completion - a cancelled `docker build` keeps building (issue #475), "
            + "and a CLI child keeps DCP, its containers and its network alive (issue #378). "
            + "Unguarded launch site(s):\n"
            + string.Join("\n", offenders.Select(site => $"  {site.File}({site.Line}): {site.Member}"))
            + $"\n\nFix shape: capture the {ProcessType} in a local, do the work in a `try`, and in "
            + $"the `finally` call `ChildProcess.{KillMethod}(proc)` and THEN `proc.Dispose()`. The "
            + "order matters and this census cannot check it: dispose-first leaves the child ALIVE "
            + "and throws nothing at all, because the kill's own exception filter swallows the "
            + $"InvalidOperationException it causes. See the remarks on `{KillMethod}`.");
    }

    /// <summary>One child-process launch, and whether its member kills the tree in a finally.</summary>
    private sealed record LaunchSite(string File, int Line, string Member, bool HasKillingFinally);

    /// <summary>Every <c>.cs</c> file in this test project, excluding build output.</summary>
    private static List<string> CensusFiles() =>
        Directory
            .EnumerateFiles(ProjectDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsUnderExcludedDirectory(path))
            .ToList();

    private static bool IsUnderExcludedDirectory(string path)
    {
        var relative = Path.GetRelativePath(ProjectDirectory(), path);
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
    private static IEnumerable<LaunchSite> FindLaunchSites(string path)
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
    /// <strong>Agrees with <see cref="EnclosingMemberBody(SyntaxNode)"/> about lambdas.</strong> That
    /// method treats a lambda as its own guarding scope; an earlier version of this one walked
    /// straight past lambdas to the enclosing method, so an offender inside one was LABELLED with a
    /// member whose <c>finally</c> was not the one being judged. The label now says so explicitly.
    /// Only the message is affected — the verdict always came from
    /// <see cref="EnclosingMemberBody(SyntaxNode)"/> — but a message that names the wrong member
    /// sends the reader to the wrong code.
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
