// EmittedCodeBaseline - renders what a suite binds to and the C# it compiles to, in a plain-text,
// deterministic form that the emitted-code baseline gate (EmittedCodeBaselineTests) commits as a golden.
//
// WHAT IT DRIVES. The same front end `vouchfx run` uses before it touches a topology, and nothing
// after it: DocumentValidator.Validate, YamlDocumentParser.Parse, AstBuilder.Build and
// ProviderPipeline.Compile over the frozen 25-provider Core registry. No topology, container or
// network is started, and the assembled script is never handed to Roslyn, loaded or run.
//
// BOUND MODELS FOR EVERY SUITE THAT REACHES THE AST. Compile can refuse a suite before any step is
// bound (the environment binding, the security preflight, the protocol-conflict check) or after it
// (a step's Validate, a host-resource collision). Either way the binders would go unrecorded, so for
// every suite whose AST builds, the renderer also calls ProviderPipeline.BindAllSteps itself, with
// the same three inputs Compile passes it (the AST, the registry and the suite directory; nothing
// from the preflight), and records each step's model (EmittedCodeBaselineModels), its host-resource
// requirements, or the bind failure. BindAllSteps stops at the first step whose Bind throws, as
// Compile does, so the steps after it have no model to record.
//
// WHY A SECOND WALK OVER THE STEPS. ProviderPipeline.Compile returns only the assembled script, not
// the fragment each provider returned, and a baseline that says "step 4 of this suite changed" needs
// the fragment. So for a suite Compile accepts, the renderer builds the same project context from the
// bound steps and calls each provider's Emit exactly as Compile's Pass 2 does, then reassembles the
// recorded fragments with CsxAssembler and requires the result to equal the script Compile produced.
// If Compile ever changes how it builds an Emit context, that equality check fails loudly rather than
// letting the per-step record drift from what the engine really emits.
//
// FORMAT. The first lines (format version, suite, outcome, and stage or namespace, and the step
// count) form the preamble. Every later section starts with a line beginning with Marker, and no two
// sections share a header: refusal diagnostics are numbered ("diagnostic 2 of 3"), and each step has
// a header section ("step 2 of 5": id, type, provider, model type, retry, timeout) followed by
// "step 2 model", "step 2 host-resources" (or "step 2 bind-failure") and, when the suite emits,
// "step 2 usings", "step 2 helpers" and "step 2 statement-block". An emitted suite ends with the
// assembled script.
//
// DETERMINISM. Everything written is a function of the suite file, the providers and the engine:
// line endings are normalised to LF, helper bodies are recorded by name and SHA-256 over their LF
// text, and no path, timestamp or identifier of the machine is written. A diagnostic or model value
// that embeds an absolute path of this checkout is refused here rather than silently written.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Compilation;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Engine.Orchestration.Environments;
using Vouchfx.Sdk;

namespace Vouchfx.Engine.Runtime.Tests;

internal static class EmittedCodeBaseline
{
    /// <summary>The suite namespace the engine passes to every provider (ScenarioRunner.SuiteNamespace).</summary>
    internal const string SuiteNamespace = "VouchfxGenerated";

    /// <summary>Prefix of every structural line of a golden; no recorded line may start with it.</summary>
    internal const string Marker = "@@@@ ";

    private const string FormatVersion = "vouchfx emitted-code baseline v2";

    private static readonly Regex s_helperClassName = new(
        @"\bstatic\s+class\s+(\w+)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// Renders the golden text for one suite.
    /// </summary>
    /// <param name="suiteRelativePath">The repository-relative path, forward slashes; written as the suite's name.</param>
    /// <param name="suiteAbsolutePath">The suite file on disk.</param>
    /// <param name="registry">The frozen Core registry.</param>
    internal static string Render(string suiteRelativePath, string suiteAbsolutePath, StepKindRegistry registry)
    {
        var yaml = File.ReadAllText(suiteAbsolutePath);
        var suiteDirectory = Path.GetDirectoryName(suiteAbsolutePath)!;

        var text = new StringBuilder();
        text.Append(FormatVersion).Append('\n');
        text.Append("suite: ").Append(suiteRelativePath).Append('\n');

        // 1. Schema validation, exactly as the CLI's validate stage runs it.
        var validation = DocumentValidator.Validate(yaml, registry);
        if (!validation.IsValid)
        {
            AppendRefusal(text, "schema", validation.Errors.Select(e => $"{e.InstanceLocation}\t{e.Message}").ToList());
            return Finish(text, suiteDirectory);
        }

        // 2. Parse and build the AST. Only the authoring exceptions are refusals; anything else is a
        //    defect and propagates so the gate names the suite instead of recording it.
        ScenarioAst ast;
        try
        {
            var document = YamlDocumentParser.Parse(yaml);
            ast = AstBuilder.Build(document, registry);
        }
        catch (YamlParseException ex)
        {
            AppendRefusal(text, "parse", new[] { $"line {ex.Line} column {ex.Column}\t{ex.Message}" });
            return Finish(text, suiteDirectory);
        }
        catch (AstBuildException ex)
        {
            AppendRefusal(
                text, "ast", new[] { $"step {ex.StepId ?? "(none)"} line {ex.Line} column {ex.Column}\t{ex.Message}" });
            return Finish(text, suiteDirectory);
        }

        // 3. The provider pipeline (bind, validate, resources, emit, assemble), then the binders on
        //    their own, whatever the pipeline decided.
        var result = ProviderPipeline.Compile(ast, registry, SuiteNamespace, suiteDirectory);
        var (boundSteps, bindFailure) = ProviderPipeline.BindAllSteps(ast, registry, suiteDirectory);
        var stepCount = ast.Steps.Count.ToString(CultureInfo.InvariantCulture);

        if (result.Failure is { } failure)
        {
            AppendRefusal(text, "pipeline", new[] { DescribeFailure(failure) }, "steps: " + stepCount);
            AppendBoundSteps(text, ast, registry, boundSteps, bindFailure, fragments: null);
            text.Append(Marker).Append("end\n");
            return Finish(text, suiteDirectory);
        }

        var assembled = result.Assembled
            ?? throw new InvalidOperationException($"{suiteRelativePath}: Compile returned neither a failure nor a script.");
        if (bindFailure is not null)
        {
            throw new InvalidOperationException(
                $"{suiteRelativePath}: Compile succeeded but BindAllSteps failed: {bindFailure.Message}");
        }

        // 4. Per-step fragments, recorded the way Compile's Pass 2 obtains them.
        var plans = RecordFragments(ast, suiteDirectory, boundSteps);
        var reassembled = CsxAssembler.Assemble(plans);
        if (!string.Equals(reassembled.CsxSource, assembled.CsxSource, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{suiteRelativePath}: the per-step fragments recorded by the baseline do not reassemble to the "
                + "script ProviderPipeline.Compile produced. ProviderPipeline's Emit context has changed; "
                + "update EmittedCodeBaseline.RecordFragments to match it.");
        }

        text.Append("outcome: emitted\n");
        text.Append("namespace: ").Append(SuiteNamespace).Append('\n');
        text.Append("steps: ").Append(stepCount).Append('\n');

        AppendBoundSteps(text, ast, registry, boundSteps, bindFailure: null, plans);

        AppendSection(
            text, "assembled-script", ElideHelpers(assembled.CsxSource, plans.SelectMany(p => p.Fragment.RequiredHelpers)));
        text.Append(Marker).Append("end\n");
        return Finish(text, suiteDirectory);
    }

    private static string DescribeFailure(ValidationFailure failure)
    {
        var flags = new List<string>();
        if (failure.IsProviderOrEngineFault)
        {
            flags.Add("provider-or-engine-fault");
        }

        if (failure.IsSecurityPreflight)
        {
            flags.Add("security-preflight");
        }

        return (flags.Count == 0 ? "-" : string.Join(",", flags)) + "\t" + failure.Message;
    }

    private static List<StepCompilePlan> RecordFragments(
        ScenarioAst ast, string suiteDirectory, List<BoundStep> boundSteps)
    {
        // Compile builds the environment plan first and hands its declarations to the project
        // context only when the suite names an environment source.
        var environmentPlan = EnvironmentDriverRegistry.BuiltIn.Bind(ast.Environment, suiteDirectory);
        var projectContext = ProviderPipeline.BuildProjectContext(
            ast,
            suiteDirectory,
            boundSteps,
            out _,
            ast.Environment?.Source is null ? null : environmentPlan.Declarations);

        var plans = new List<StepCompilePlan>(boundSteps.Count);
        foreach (var bound in boundSteps)
        {
            var node = bound.Node;
            var context = new RunCompileContext(
                node.Id, SuiteNamespace, suiteDirectory, node.Capture, projectContext.DeclaredServices);

            var compiler = bound.Instance.GetType().GetInterfaces()
                .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IStepCompiler<>));
            var emit = compiler.GetMethod(nameof(IStepCompiler<IStepModel>.Emit), BindingFlags.Public | BindingFlags.Instance)!;
            var fragment = (CsxFragment)emit.Invoke(bound.Instance, new object[] { bound.Model, context })!;

            plans.Add(new StepCompilePlan(
                StepId: node.Id,
                Fragment: fragment,
                Retry: node.VerifyMode == VerifyMode.Retry,
                TimeoutMs: node.Timeout is { } t ? (long)t.TotalMilliseconds : null,
                PollIntervalMs: null));
        }

        return plans;
    }

    /// <summary>
    /// Writes, per step, the header and the bound model with its host-resource requirements and, when
    /// <paramref name="fragments"/> is given, the step's emitted fragment. A bind failure is written
    /// under the step it names; BindAllSteps binds no step after it.
    /// </summary>
    private static void AppendBoundSteps(
        StringBuilder text,
        ScenarioAst ast,
        StepKindRegistry registry,
        List<BoundStep> boundSteps,
        ValidationFailure? bindFailure,
        List<StepCompilePlan>? fragments)
    {
        var total = ast.Steps.Count;
        for (var i = 0; i < boundSteps.Count; i++)
        {
            var bound = boundSteps[i];
            var n = (i + 1).ToString(CultureInfo.InvariantCulture);
            AppendStepHeader(text, i + 1, total, bound.Node, ProviderName(bound.Instance), bound.Model.GetType().FullName);
            AppendSection(text, "step " + n + " model", EmittedCodeBaselineModels.SerialiseModel(bound.Model));
            AppendSection(
                text,
                "step " + n + " host-resources",
                bound.HostResourcesFailure is { } hostFailure
                    ? "failure: " + hostFailure.GetType().FullName + "\t" + hostFailure.Message
                    : EmittedCodeBaselineModels.SerialiseHostResources(bound.HostResources));

            if (fragments is not null)
            {
                AppendFragment(text, n, fragments[i]);
            }
        }

        if (bindFailure is null)
        {
            return;
        }

        if (boundSteps.Count < total)
        {
            var node = ast.Steps[boundSteps.Count];
            var number = boundSteps.Count + 1;
            var provider = registry.TryGet(node.CanonicalType, out var registered) && registered is not null
                ? ProviderName(registered.Instance)
                : "(not in registry)";
            AppendStepHeader(text, number, total, node, provider, modelType: null);
            AppendSection(
                text,
                "step " + number.ToString(CultureInfo.InvariantCulture) + " bind-failure",
                "stage: bind\n" + DescribeFailure(bindFailure));
        }
        else
        {
            AppendSection(text, "bind-failure", "stage: bind\n" + DescribeFailure(bindFailure));
        }
    }

    private static string ProviderName(IStepProvider instance) =>
        instance.GetType().FullName ?? instance.GetType().Name;

    private static void AppendStepHeader(
        StringBuilder text, int number, int total, StepNode node, string provider, string? modelType)
    {
        var n = number.ToString(CultureInfo.InvariantCulture);
        text.Append('\n');
        text.Append(Marker).Append("step ").Append(n).Append(" of ")
            .Append(total.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("id: ").Append(node.Id).Append('\n');
        text.Append("type: ").Append(node.CanonicalType).Append('\n');
        text.Append("provider: ").Append(provider).Append('\n');
        text.Append("model: ").Append(modelType ?? "(not bound)").Append('\n');
        text.Append("retry: ").Append(node.VerifyMode == VerifyMode.Retry ? "true" : "false").Append('\n');
        text.Append("timeout-ms: ")
            .Append(node.Timeout is { } t ? ((long)t.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) : "none")
            .Append('\n');
    }

    private static void AppendFragment(StringBuilder text, string n, StepCompilePlan plan)
    {
        AppendSection(text, "step " + n + " usings", string.Join("\n", plan.Fragment.RequiredUsings));

        var helperLines = new List<string>();
        foreach (var helper in plan.Fragment.RequiredHelpers)
        {
            var (name, hash) = DescribeHelper(helper);
            helperLines.Add($"{name} sha256={hash}");
        }

        AppendSection(text, "step " + n + " helpers", string.Join("\n", helperLines));
        AppendSection(text, "step " + n + " statement-block", plan.Fragment.StatementBlock);
    }

    private static (string Name, string Sha256) DescribeHelper(string helper)
    {
        var normalised = Normalise(helper);
        var match = s_helperClassName.Match(normalised);
        var name = match.Success ? match.Groups[1].Value : "(unnamed)";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalised))).ToLowerInvariant();
        return (name, hash);
    }

    /// <summary>
    /// Replaces each helper body inside the assembled script with a one-line marker carrying the helper's
    /// name and SHA-256. The bodies are large, identical across suites, and already pinned by the SDK
    /// helper-source golden (vouchfx-sdk-helper-sources.v1.txt) for the SDK-supplied ones; what this
    /// baseline adds is where the assembler places them and that each one reaches the script unchanged.
    /// The replacement must match exactly once, so a helper the assembler altered or duplicated is a
    /// failure here rather than a silently elided difference. Line endings are normalised first: the
    /// assembler joins with AppendLine, so the raw text carries the platform's newline.
    /// </summary>
    private static string ElideHelpers(string script, IEnumerable<string> helpers)
    {
        var result = Normalise(script);
        foreach (var helper in helpers.Select(Normalise).Distinct(StringComparer.Ordinal))
        {
            var first = result.IndexOf(helper, StringComparison.Ordinal);
            if (first < 0 || result.IndexOf(helper, first + 1, StringComparison.Ordinal) >= 0)
            {
                throw new InvalidOperationException(
                    "A helper body does not appear exactly once, verbatim, in the assembled script; the baseline "
                    + "cannot elide it. Helper: " + DescribeHelper(helper).Name);
            }

            var (name, hash) = DescribeHelper(helper);
            var marker = $"// [helper {name} sha256={hash}: body elided, recorded by name and hash]";
            result = string.Concat(result.AsSpan(0, first), marker, result.AsSpan(first + helper.Length));
        }

        return result;
    }

    /// <summary>
    /// Writes the refusal preamble and one numbered section per diagnostic. The caller writes the end
    /// marker unless <paramref name="preambleTail"/> is null, in which case the refusal is the whole golden.
    /// </summary>
    private static void AppendRefusal(
        StringBuilder text, string stage, IReadOnlyList<string> diagnostics, string? preambleTail = null)
    {
        text.Append("outcome: refused\n");
        text.Append("stage: ").Append(stage).Append('\n');
        if (preambleTail is not null)
        {
            text.Append(preambleTail).Append('\n');
        }

        var total = diagnostics.Count.ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < diagnostics.Count; i++)
        {
            AppendSection(
                text, "diagnostic " + (i + 1).ToString(CultureInfo.InvariantCulture) + " of " + total, diagnostics[i]);
        }

        if (preambleTail is null)
        {
            text.Append(Marker).Append("end\n");
        }
    }

    private static void AppendSection(StringBuilder text, string name, string body)
    {
        text.Append(Marker).Append(name).Append('\n');
        var normalised = Normalise(body);
        foreach (var line in normalised.Split('\n'))
        {
            if (line.StartsWith(Marker, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Recorded text contains a line starting with the golden's delimiter '{Marker}': {line}");
            }
        }

        text.Append(normalised);
        if (normalised.Length == 0 || normalised[^1] != '\n')
        {
            text.Append('\n');
        }
    }

    private static string Normalise(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string Finish(StringBuilder text, string suiteDirectory)
    {
        var result = text.ToString();
        foreach (var path in new[] { suiteDirectory, RepoRootForLeakCheck() })
        {
            foreach (var spelling in new[] { path, path.Replace('\\', '/') })
            {
                if (spelling.Length > 3 && result.Contains(spelling, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"The rendered baseline embeds the machine path '{spelling}'. A golden must be identical on every "
                        + "machine: neutralise the path in EmittedCodeBaseline before committing it.");
                }
            }
        }

        var headers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in result.Split('\n'))
        {
            if (line.StartsWith(Marker, StringComparison.Ordinal) && !headers.Add(line))
            {
                throw new InvalidOperationException(
                    $"The rendered baseline repeats the section header '{line}'; every header must be unique so a "
                    + "difference can be attributed to one section.");
            }
        }

        return result;
    }

    private static string RepoRootForLeakCheck() => Vouchfx.TestSupport.RepoRoot.Resolve();
}
