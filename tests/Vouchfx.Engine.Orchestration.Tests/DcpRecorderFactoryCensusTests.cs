using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Structural census over the #420 flight recorder's PROVENANCE: that production code has exactly
/// one way to obtain a recorder — <see cref="DcpFlightRecorder.CreateUnlessDisabled()"/> — and that
/// <see cref="HeadlessTopology.StartAsync"/> uses it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this file exists.</strong> The opt-out (<c>VOUCHFX_DCP_CAPTURE=0</c>) lives in the
/// factory and nowhere else. Its whole force therefore rests on an unstated structural fact:
/// that no production path constructs a recorder without asking. Nothing checked that fact. The
/// unit row <c>DcpFlightRecorderTests.CreateUnlessDisabled_HonoursTheEnvironmentVariableInBothDirections</c>
/// proves the factory answers correctly, and
/// <c>DcpFlightRecorderDockerTests.Register_InsideTheRealAspireHost_CapturesDcpTrafficAndNothingElse</c>
/// asks it the same way production asks it — but both interrogate the FACTORY. A change that made
/// <c>StartAsync</c> write <c>new DcpFlightRecorder()</c> directly would leave every one of them
/// green while the documented opt-out silently stopped working, which is precisely the regression
/// the Copilot review of PR #532 named.
/// </para>
/// <para>
/// <strong>Why a census rather than an integration assertion through <c>StartAsync</c>.</strong>
/// The obvious alternative — start a topology with the opt-out set, fail it, and assert that no
/// capture file appeared — is the shape issue #489 removed from the Docker row above, and it was
/// removed because it was unsound, not because it was slow: it counted files in a SHARED,
/// per-user capture directory that any concurrent vouchfx process on the same machine perturbs.
/// Pointing it at an isolated directory fixes the sharing but not the second defect: the effect
/// it observes only exists when the start FAILS, so the assertion is conditional on a coincidence
/// and proves nothing on a run where the topology comes up. This census asserts the DECISION
/// instead of its effect — unconditionally, in the blocking non-docker lane, with no containers
/// and no filesystem to race.
/// </para>
/// <para>
/// <strong>What it pins.</strong> Rule 1: within every production source under <c>src/</c>, each
/// construction of the recorder sits inside the parameterless factory. Rule 2: every exit from
/// <c>HeadlessTopology.StartAsync</c> hands back a topology whose recorder is the value that
/// factory produced, unmodified between the two. The two are complements, and neither alone is
/// the property: rule 1 without rule 2 admits a <c>StartAsync</c> that calls the factory, drops
/// the answer on the floor and hands over <see langword="null"/>; rule 2 without rule 1 admits a
/// recorder constructed and registered elsewhere in the assembly, past the opt-out, on a path the
/// hand-off never touches.
/// </para>
/// <para>
/// <strong>ARITY is part of both rules, and it is not pedantry.</strong> The opt-out is read by
/// <c>CreateUnlessDisabled()</c> — the parameterless one. An overload
/// (<c>CreateUnlessDisabled(bool ignoreOptOut)</c>) that constructs a recorder without consulting
/// the environment is a perfectly ordinary thing for somebody to add, and a census keyed on the
/// method NAME alone would sanction both its construction site and a <c>StartAsync</c> that called
/// it. Every name match below is therefore paired with an argument- or parameter-count check.
/// </para>
/// <para>
/// <strong>What it CANNOT pin, stated so it is not quoted as more than it is.</strong> This reads
/// SOURCE TEXT, so:
/// </para>
/// <list type="bullet">
///   <item>
///     Reflection is invisible. <c>Activator.CreateInstance(typeof(DcpFlightRecorder))</c>, or any
///     DI registration that constructs the type by service descriptor rather than by
///     <c>new</c>, satisfies both rules while bypassing the factory entirely.
///   </item>
///   <item>
///     A target-typed <c>new()</c> is invisible to rule 1. Deciding that
///     <c>DcpFlightRecorder? r = new();</c> constructs a recorder needs a semantic model, and this
///     census — like every other in this assembly — has no compilation. The explicit form is the
///     one anybody writes by accident; the implicit one would have to be chosen. (Rule 2 does see
///     it, because there it is a return this rule cannot trace and is reported as one.)
///   </item>
///   <item>
///     Type ALIASES are resolved per file but not across files. <c>using R = …DcpFlightRecorder;</c>
///     followed by <c>new R()</c> is matched, because the alias directive is read out of the same
///     compilation unit. A <c>global using</c> alias declared in a DIFFERENT file is not: resolving
///     it needs the whole compilation, which this census does not build.
///   </item>
///   <item>
///     Only <c>src/</c> is censused. Test assemblies construct recorders directly and must keep
///     doing so — tuned entry/char limits are how the bounding rules are tested at all — so their
///     construction sites are deliberately out of scope rather than overlooked.
///   </item>
///   <item>
///     It says nothing about whether the factory's answer is CORRECT (that is
///     <c>DcpFlightRecorderTests</c>), whether the recorder is actually registered with the logger
///     factory (that is <c>DcpFlightRecorderDockerTests</c> and <c>DcpCaptureTests</c>), or when
///     the arming window closes (that is <see cref="DcpArmingWindowCensusTests"/>).
///   </item>
/// </list>
/// <para>
/// <strong>Scope is <c>src/</c>, not the orchestration project alone, and the difference is
/// load-bearing.</strong> <c>DcpFlightRecorder</c> is <c>internal</c>, so it is tempting to census
/// its own project and call that complete. It is not: this project's csproj grants
/// <c>InternalsVisibleTo</c> to <c>Vouchfx.Engine.Runtime</c>, which is production code and could
/// construct one today. Reading the whole production tree costs a few hundred parses, removes the
/// caveat, and survives the next grant without anybody remembering to widen this file.
/// </para>
/// </remarks>
public sealed class DcpRecorderFactoryCensusTests
{
    private const string RecorderTypeName = "DcpFlightRecorder";
    private const string FactoryMethodName = "CreateUnlessDisabled";
    private const string TopologyTypeName = "HeadlessTopology";

    /// <summary>
    /// A floor on the number of production sources rule 1 reads, so a walk that resolved the wrong
    /// root cannot pass by finding nothing to complain about.
    /// </summary>
    /// <remarks>
    /// A floor rather than an exact count, for the reason
    /// <c>ChildProcessKillCallSiteCensusTests.MinimumCensusFiles</c> gives: an exact count is a
    /// second thing to maintain and reddens on every unrelated file added. MEASURED at 271
    /// hand-written <c>.cs</c> files under <c>src/</c> when this was written, so the floor has
    /// room to be a tripwire rather than a maintenance burden.
    /// </remarks>
    private const int MinimumCensusFiles = 100;

    /// <summary>
    /// Rule 1 — every construction of the recorder in production source is inside the
    /// parameterless factory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Node kind rather than a regex, for the reason the house idiom gives (see
    /// <c>ChildProcessKillCallSiteCensusTests</c>): the string <c>new DcpFlightRecorder()</c>
    /// appears in this repository's prose — including in this very file — and telling a real
    /// construction from a discussion of one is exactly what a text search gets wrong.
    /// <c>DescendantNodes(descendIntoTrivia: false)</c> keeps comments and XML docs out, and a
    /// string literal is not an <see cref="ObjectCreationExpressionSyntax"/> whatever it spells.
    /// </para>
    /// <para>
    /// Three things are checked about the enclosing factory, and dropping any one of them opens a
    /// bypass: its NAME, its owning TYPE (so a <c>CreateUnlessDisabled</c> added to some other
    /// class is not sanctioned by name alone), and its ARITY (so an overload that never reads the
    /// environment is not sanctioned either). The offender line says which of the three failed.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryRecorderConstructionUnderSrc_SitsInsideTheFactory()
    {
        var repositoryRoot = RepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "src");

        Assert.True(
            Directory.Exists(sourceRoot),
            $"'{Display(repositoryRoot, sourceRoot)}' is missing; this census reads every "
            + "production source beneath it.");

        var files = ProductionSources(sourceRoot);

        // A census over no files passes for free, and the cheapest way to get there is a root that
        // silently resolved somewhere thin.
        Assert.True(
            files.Count >= MinimumCensusFiles,
            $"this census found only {files.Count} .cs file(s) under "
            + $"'{Display(repositoryRoot, sourceRoot)}', which is below the floor of "
            + $"{MinimumCensusFiles}. It has almost certainly resolved the wrong directory rather "
            + "than found a small tree.");

        var sites = new List<string>();
        var offenders = new List<string>();

        foreach (var path in files)
        {
            var root = Parse(repositoryRoot, path);
            var recorderNames = RecorderNamesIn(root);

            foreach (var creation in root
                .DescendantNodes(descendIntoTrivia: false)
                .OfType<ObjectCreationExpressionSyntax>())
            {
                if (!IsRecorderType(creation.Type, recorderNames))
                {
                    continue;
                }

                var site = Describe(repositoryRoot, creation);
                sites.Add(site);

                var fault = FactoryFault(creation);

                if (fault is not null)
                {
                    offenders.Add($"{site} — {fault}");
                }
            }
        }

        // Guard against a vacuous pass: a census that matches nothing passes for free, and the
        // cheapest way to get there is renaming the type without re-aiming this file.
        Assert.True(
            sites.Count > 0,
            $"no `new {RecorderTypeName}(...)` was found anywhere under src/, so this census is "
            + "asserting nothing. The #420 recorder was renamed, moved out of the production tree, "
            + "or is now constructed in a form this census cannot see - re-aim it rather than "
            + "deleting it.");

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} construction(s) of the #420 flight recorder in production source "
            + $"do not sit inside {RecorderTypeName}.{FactoryMethodName}(), which is where — and "
            + "only where — the VOUCHFX_DCP_CAPTURE=0 opt-out is honoured. A recorder built "
            + "anywhere else captures DCP traffic on a run the operator switched capture OFF. "
            + $"Route it through {RecorderTypeName}.{FactoryMethodName}() and handle the null:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Rule 2 — every exit from <c>HeadlessTopology.StartAsync</c> hands on the factory's recorder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stated as a link between two points rather than as the presence of a call, because
    /// presence is satisfiable without the property: calling the factory, discarding the result
    /// and handing over something else passes any "does it call the factory" check. So the rule
    /// walks the value — from the argument in the <c>return new HeadlessTopology(...)</c> that
    /// occupies the constructor's recorder slot, back to the local it names, to that local's
    /// initialiser, which must be the parameterless factory invocation.
    /// </para>
    /// <para>
    /// <strong>Universal over exits, not existential.</strong> An earlier version collected only
    /// the returns that already WERE a <c>new HeadlessTopology(...)</c>, which made a second exit
    /// — <c>return cached;</c>, <c>return await RetryStartAsync(…);</c>, or even
    /// <c>return new(app, recorder);</c> — invisible while the non-empty guard stayed satisfied by
    /// the surviving one. It now enumerates every return from the method and reports the ones it
    /// cannot trace, which is the difference between a rule and an anecdote. Returns inside a
    /// lambda or a local function are excluded because they leave THAT body rather than
    /// <c>StartAsync</c> — the same exclusion, for the same reason, as
    /// <see cref="DcpArmingWindowCensusTests"/> rule 2.
    /// </para>
    /// <para>
    /// <strong>The declaration is not enough, and the gap was real.</strong> Reading only the
    /// declarator's initialiser admits
    /// <c>var recorder = CreateUnlessDisabled(); recorder = null; return new HeadlessTopology(app,
    /// recorder);</c> — every check green, no <c>new</c> for rule 1 to catch, and the opt-out's
    /// answer discarded. So the rule additionally refuses any REBINDING of that local between its
    /// declaration and the hand-off: an assignment to it (including a deconstruction target), it
    /// being passed <c>ref</c> or <c>out</c>, and <c>ref recorder</c> — which is how a <c>ref</c>
    /// local would alias it. A designation cannot re-declare the name inside the method at all
    /// (CS0136), so there is no fourth form to cover. This is deliberately cruder than
    /// reaching-definitions: a rebinding that happens to assign the factory's own value back is
    /// refused too, and re-aiming this rule is the right answer if anybody ever wants one.
    /// </para>
    /// <para>
    /// The recorder's SLOT is read off the private constructor's parameter list rather than
    /// hard-coded as an index, so reordering the constructor's parameters re-aims this rule
    /// instead of breaking it.
    /// </para>
    /// <para>
    /// <strong>This rule subsumes <see cref="DcpArmingWindowCensusTests"/> rule 5</strong>
    /// (<c>StartAsync_ConstructsTheReturnedTopologyWithTheRecorder</c>), which asserts that SOME
    /// argument of the hand-off is spelled <c>recorder</c>. This one requires every exit to be
    /// such a hand-off, identifies the recorder argument by the constructor's parameter TYPE
    /// rather than by the local's spelling, and then traces where that value came from. The two
    /// are deliberately left in place together — they fail with different messages pointing at
    /// different repairs — but anyone tightening one should read the other rather than assume it
    /// still adds something.
    /// </para>
    /// </remarks>
    [Fact]
    public void HeadlessTopologyStartAsync_TakesItsRecorderFromTheFactory()
    {
        var repositoryRoot = RepositoryRoot();
        var path = Path.Combine(
            repositoryRoot, "src", "Engine", "Vouchfx.Engine.Orchestration", "HeadlessTopology.cs");

        Assert.True(
            File.Exists(path),
            $"census target moved or was renamed: {Display(repositoryRoot, path)}");

        var root = Parse(repositoryRoot, path);
        var recorderNames = RecorderNamesIn(root);

        // The recorder's position in the private constructor, by TYPE rather than by index.
        var constructors = root.DescendantNodes(descendIntoTrivia: false)
            .OfType<ConstructorDeclarationSyntax>()
            .Where(c => c.Identifier.Text == TopologyTypeName)
            .ToList();

        Assert.True(
            constructors.Count == 1,
            $"expected exactly one {TopologyTypeName} constructor; found {constructors.Count}. "
            + "This census reads the recorder's parameter slot off it - re-aim the rule.");

        var slots = constructors[0].ParameterList.Parameters
            .Select((p, index) => (Parameter: p, Index: index))
            .Where(t => t.Parameter.Type is not null && IsRecorderType(t.Parameter.Type, recorderNames))
            .ToList();

        Assert.True(
            slots.Count == 1,
            $"expected exactly one {RecorderTypeName} parameter on the {TopologyTypeName} "
            + $"constructor; found {slots.Count}. The #420 recorder's hand-off changed shape - "
            + "re-aim this rule rather than deleting it.");

        var slotName = slots[0].Parameter.Identifier.Text;
        var slotIndex = slots[0].Index;

        var startAsync = root.DescendantNodes(descendIntoTrivia: false)
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == "StartAsync" && m.Modifiers.Any(SyntaxKind.PublicKeyword))
            .ToList();

        Assert.True(
            startAsync.Count == 1,
            $"expected exactly one public StartAsync in HeadlessTopology.cs; found "
            + $"{startAsync.Count}.");

        var method = startAsync[0];

        var returns = method.DescendantNodes(descendIntoTrivia: false)
            .OfType<ReturnStatementSyntax>()
            .Where(r => !r.Ancestors()
                .TakeWhile(a => a != method)
                .Any(a => a is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            .ToList();

        // Guard against a vacuous pass: no exits means the loop below is satisfied for free.
        Assert.True(
            returns.Count > 0,
            "no return statement was found in HeadlessTopology.StartAsync outside a lambda or "
            + "local function, so this rule is asserting nothing. The method's shape changed - "
            + "re-aim it rather than deleting it.");

        var offenders = new List<string>();

        foreach (var statement in returns)
        {
            if (statement.Expression is not ObjectCreationExpressionSyntax handOff
                || LastSegment(handOff.Type.ToString()) != TopologyTypeName)
            {
                offenders.Add(
                    Describe(repositoryRoot, statement)
                    + $" — this exit does not hand back a `new {TopologyTypeName}(...)`, so the "
                    + "recorder it carries cannot be traced to the factory");
                continue;
            }

            var argument = RecorderArgument(handOff, slotName, slotIndex);

            if (argument is null)
            {
                offenders.Add(
                    Describe(repositoryRoot, handOff)
                    + $" — no argument resolves to the `{slotName}` slot");
                continue;
            }

            if (argument.Expression is not IdentifierNameSyntax identifier)
            {
                offenders.Add(
                    Describe(repositoryRoot, argument)
                    + " — the recorder slot carries an expression rather than a local, so no "
                    + "factory call can be traced to it");
                continue;
            }

            var local = identifier.Identifier.Text;

            var declarators = method.DescendantNodes(descendIntoTrivia: false)
                .OfType<VariableDeclaratorSyntax>()
                .Where(d => d.Identifier.Text == local)
                .ToList();

            if (declarators.Count != 1)
            {
                offenders.Add(
                    Describe(repositoryRoot, identifier)
                    + $" — `{local}` has {declarators.Count} declarations in StartAsync, so this "
                    + "rule cannot tell which value is handed on");
                continue;
            }

            var initialiser = declarators[0].Initializer?.Value;
            var initialiserFault = FactoryCallFault(initialiser, recorderNames);

            if (initialiserFault is not null)
            {
                offenders.Add($"{Describe(repositoryRoot, declarators[0])} — {initialiserFault}");
                continue;
            }

            offenders.AddRange(method
                .DescendantNodes(descendIntoTrivia: false)
                .Where(n => IsRebindingOf(n, local))
                .Select(n => Describe(repositoryRoot, n)
                    + $" — `{local}` is rebound after the factory call, so the value handed to the "
                    + "returned topology need not be the one the opt-out decided"));
        }

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} finding(s) in HeadlessTopology.StartAsync break the link between "
            + $"{RecorderTypeName}.{FactoryMethodName}() and the recorder handed to the returned "
            + "topology, so the VOUCHFX_DCP_CAPTURE=0 opt-out no longer governs whether production "
            + "captures DCP traffic — the factory is the only place that switch is read. Take the "
            + "recorder from the factory and hand THAT value, unmodified, to every exit:\n  "
            + string.Join("\n  ", offenders));
    }

    // -----------------------------------------------------------------------
    // Rules, as predicates
    // -----------------------------------------------------------------------

    /// <summary>
    /// Why <paramref name="creation"/> is not a sanctioned construction site, or
    /// <see langword="null"/> when it is one.
    /// </summary>
    private static string? FactoryFault(SyntaxNode creation)
    {
        var factory = creation.Ancestors()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == FactoryMethodName);

        if (factory is null)
        {
            return $"not inside a method named {FactoryMethodName}";
        }

        var owner = factory.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();

        if (owner?.Identifier.Text != RecorderTypeName)
        {
            return $"inside a {FactoryMethodName} on `{owner?.Identifier.Text ?? "<unknown>"}`, "
                + $"not on {RecorderTypeName}";
        }

        if (factory.ParameterList.Parameters.Count != 0)
        {
            return $"inside a {FactoryMethodName} OVERLOAD taking "
                + $"{factory.ParameterList.Parameters.Count} parameter(s); the opt-out is read by "
                + "the parameterless factory only";
        }

        return null;
    }

    /// <summary>
    /// Why <paramref name="expression"/> is not a call to the parameterless factory, or
    /// <see langword="null"/> when it is one.
    /// </summary>
    private static string? FactoryCallFault(ExpressionSyntax? expression, HashSet<string> recorderNames)
    {
        if (expression is not InvocationExpressionSyntax invocation
            || NameOf(invocation) != FactoryMethodName
            || !recorderNames.Contains(LastSegment(ReceiverOf(invocation) ?? string.Empty)))
        {
            return $"not initialised by {RecorderTypeName}.{FactoryMethodName}()";
        }

        var arguments = invocation.ArgumentList.Arguments.Count;

        return arguments == 0
            ? null
            : $"initialised by a {FactoryMethodName} OVERLOAD taking {arguments} argument(s); the "
                + "opt-out is read by the parameterless factory only";
    }

    /// <summary>
    /// Whether <paramref name="node"/> rebinds the local called <paramref name="name"/>: an
    /// assignment to it, it being passed <c>ref</c>/<c>out</c>, or <c>ref name</c> — the form a
    /// <c>ref</c> local needs in order to alias it.
    /// </summary>
    private static bool IsRebindingOf(SyntaxNode node, string name) => node switch
    {
        AssignmentExpressionSyntax assignment => IsAssignmentTarget(assignment.Left, name),
        ArgumentSyntax argument when argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
            || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) =>
            argument.Expression is IdentifierNameSyntax passed && passed.Identifier.Text == name,
        RefExpressionSyntax aliased =>
            aliased.Expression is IdentifierNameSyntax target && target.Identifier.Text == name,
        _ => false,
    };

    /// <summary>
    /// Whether <paramref name="left"/> assigns to the local called <paramref name="name"/> —
    /// deliberately NOT matching <c>name.Member = …</c>, which mutates the object rather than
    /// rebinding the variable.
    /// </summary>
    private static bool IsAssignmentTarget(ExpressionSyntax left, string name) => left switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.Text == name,
        ParenthesizedExpressionSyntax parenthesised => IsAssignmentTarget(parenthesised.Expression, name),
        TupleExpressionSyntax tuple => tuple.Arguments.Any(a => IsAssignmentTarget(a.Expression, name)),
        _ => false,
    };

    // -----------------------------------------------------------------------
    // Syntax helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// The argument occupying the recorder's constructor slot: matched by NAME when the call site
    /// uses named arguments at all, by POSITION otherwise.
    /// </summary>
    /// <remarks>
    /// Returning <see langword="null"/> when named arguments are present but none names the
    /// recorder is deliberate. C# 7.2 admits non-trailing named arguments, which shift what a
    /// positional index means; rather than quietly reading the wrong argument, the rule reports
    /// that it cannot resolve the slot and asks to be re-aimed.
    /// </remarks>
    private static ArgumentSyntax? RecorderArgument(
        ObjectCreationExpressionSyntax creation, string parameterName, int index)
    {
        var arguments = creation.ArgumentList?.Arguments;

        if (arguments is null)
        {
            return null;
        }

        var named = arguments.Value
            .FirstOrDefault(a => a.NameColon?.Name.Identifier.Text == parameterName);

        if (named is not null)
        {
            return named;
        }

        if (arguments.Value.Any(a => a.NameColon is not null))
        {
            return null;
        }

        return index < arguments.Value.Count ? arguments.Value[index] : null;
    }

    /// <summary>
    /// Every type name denoting the recorder in one compilation unit: its own name, plus any
    /// file-scoped <c>using R = …DcpFlightRecorder;</c> alias. See the class remarks for why a
    /// <c>global using</c> alias in another file is out of reach.
    /// </summary>
    private static HashSet<string> RecorderNamesIn(CompilationUnitSyntax root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { RecorderTypeName };

        foreach (var directive in root
            .DescendantNodes(descendIntoTrivia: false)
            .OfType<UsingDirectiveSyntax>())
        {
            if (directive.Alias is not null
                && LastSegment(directive.Name?.ToString() ?? string.Empty) == RecorderTypeName)
            {
                names.Add(directive.Alias.Name.Identifier.Text);
            }
        }

        return names;
    }

    /// <summary>Whether <paramref name="type"/> names the recorder, nullable, aliased or qualified.</summary>
    private static bool IsRecorderType(TypeSyntax type, HashSet<string> recorderNames) =>
        recorderNames.Contains(LastSegment(type.ToString().TrimEnd('?')));

    /// <summary>The text after the last dot: <c>A.B.C</c> becomes <c>C</c>.</summary>
    private static string LastSegment(string text)
    {
        var dot = text.LastIndexOf('.');
        return dot < 0 ? text.Trim() : text[(dot + 1)..].Trim();
    }

    private static string? NameOf(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        IdentifierNameSyntax i => i.Identifier.Text,
        MemberBindingExpressionSyntax b => b.Name.Identifier.Text,
        _ => null,
    };

    private static string? ReceiverOf(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax m ? m.Expression.ToString() : null;

    /// <summary>A path as the repository sees it, with forward slashes.</summary>
    private static string Display(string repositoryRoot, string path) =>
        Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');

    /// <summary>
    /// A site as <c>path(line): text</c> — repository-relative, so a failure message names the
    /// file to open without leaking the machine's directory layout into CI output.
    /// </summary>
    private static string Describe(string repositoryRoot, SyntaxNode node)
    {
        var line = node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
        var text = node.ToString().Split('\n')[0].Trim();

        return $"{Display(repositoryRoot, node.SyntaxTree.FilePath)}"
            + $"({line.ToString(CultureInfo.InvariantCulture)}): {text}";
    }

    /// <summary>
    /// Every hand-written source under <paramref name="sourceRoot"/>: build output is excluded
    /// because <c>obj/</c> holds generated copies whose construction sites are not anybody's to
    /// fix, and would be reported twice.
    /// </summary>
    /// <remarks>
    /// The <c>bin</c>/<c>obj</c> test is applied to the path RELATIVE to the source root. Applied
    /// to the absolute path it would also match a checkout that happened to live under a directory
    /// called <c>obj</c>, which excludes the entire tree and leaves the census reading nothing —
    /// the failure mode <c>MinimumCensusFiles</c> exists to catch, avoided here rather than
    /// merely detected.
    /// </remarks>
    private static List<string> ProductionSources(string sourceRoot) =>
        Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => !Path.GetRelativePath(sourceRoot, p)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment =>
                    string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Parses one source at the LATEST language version, and fails on an error diagnostic.
    /// </summary>
    /// <remarks>
    /// Both halves matter, and the sibling census <c>DependencyEnvCensusTests</c> names the same
    /// risk. Parsing at <c>LanguageVersion.Latest</c> rather than the repository's pinned
    /// <c>LangVersion</c> keeps the census reading files that start using a newer construct.
    /// Reading the diagnostics is what makes a parse failure VISIBLE: <c>ParseText</c> does not
    /// throw, it returns a recovered tree with nodes missing after the error — so a construction
    /// sitting in an unparsed region would simply drop out of the population, indistinguishable
    /// from a file that constructs nothing.
    /// </remarks>
    private static CompilationUnitSyntax Parse(string repositoryRoot, string path)
    {
        var tree = CSharpSyntaxTree.ParseText(
            File.ReadAllText(path),
            new CSharpParseOptions(LanguageVersion.Latest),
            path: path);

        var errors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Take(3)
            .Select(d => $"{d.Id} at line "
                + $"{(d.Location.GetLineSpan().StartLinePosition.Line + 1).ToString(CultureInfo.InvariantCulture)}: "
                + d.GetMessage(CultureInfo.InvariantCulture))
            .ToList();

        Assert.True(
            errors.Count == 0,
            $"'{Display(repositoryRoot, path)}' does not parse as C#, so this census would read no "
            + "constructions from it at all - indistinguishable from a file that constructs "
            + $"nothing. First error(s): {string.Join("; ", errors)}");

        return (CompilationUnitSyntax)tree.GetRoot();
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "vouchfx.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
