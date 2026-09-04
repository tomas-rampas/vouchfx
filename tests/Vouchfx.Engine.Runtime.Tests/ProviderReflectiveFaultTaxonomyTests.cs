// Vouchfx.Engine.Runtime.Tests — issue #466: the REMAINING reflective SDK surfaces.
//
// WHAT WAS UNPINNED. #413 guarded exactly one of the calls `ProviderPipeline` makes into a
// provider — `Bind` — and turned its throw into a `PipelineResult.Failure`. The rest stayed
// unguarded:
//
//   • `ReflectValidate`   (Compile, Pass 2)  — eager, so a throw arrives wrapped in
//                                              TargetInvocationException.
//   • `ReflectResources`  (Compile, Pass 2)  — a LAZY iterator, so the throw surfaces
//                                              during the caller's `foreach`, unwrapped.
//   • `HostResources`     (BindAllSteps → Pass 2 rethrow) — captured in Pass 1 and
//                                              DELIBERATELY rethrown after Validate.
//   • `ReflectEmit`       (Compile, Pass 2)  — eager, wrapped like Validate.
//   • `ICompileReferenceContributor.CompileReferenceAssemblies` (Compile, Pass 2) — the
//                                              fifth, and the only one NOT dispatched
//                                              reflectively; #466's own list omits it, and
//                                              the failure mode is identical, so it is
//                                              guarded here with the other four.
//   • `CsxAssembler.Assemble`   (Compile, after the loop) — the SIXTH, and not a call INTO a
//                                              provider at all: it refuses provider-EMITTED
//                                              CONTENT that breaks §13.3.1. CsxFragment has no
//                                              constructor validation, so the bad fragment is
//                                              built cleanly inside Emit and no per-step guard
//                                              can see it. Guarded separately because
//                                              CsxAssemblyException cannot name a fragment.
//
// An escape from any of them unwound past `ProviderPipeline.Compile`, past the runner, and
// into `ParallelSuiteRunner`'s per-slot catch-all, which classifies ANY escape as
// `Verdict.EnvironmentError` + `SecurityAbortKind.TopologyUnavailable`. On a run where
// nothing executed that is exit 0 (#390) — a green CI build over a provider defect, the exact
// shape #413's own rationale condemns. #466-A closes it by NARROWING what can reach that
// catch-all rather than by reclassifying at the slot: each surface is guarded into the same
// `ValidationFailure` channel `Bind` already uses, so the fault reaches a taxonomy verdict
// (Inconclusive) with artefacts instead of escaping.
//
// WHY THE SLOT CLASSIFIER IS UNTOUCHED. It sees only an exception type and genuinely cannot
// tell an infrastructure fault (for which EnvironmentError is CORRECT, §12.1) from an engine
// defect; and `TopologyUnavailable` sits deliberately outside every `SecurityAssurance.
// Unconfirmed` disjunct, so moving it would move security semantics. See #466's own text.
//
// NO CONTAINERS. Every fault below is reached before `SuiteTopology.StartAsync` on both run
// paths — a property of WHERE the pipeline sits, not of a mock.
//
// THE STUBS `stub.throwing-hostresource` (declared in ProviderPipelineTests) AND the nine
// declared below are discovered by the SAME assembly scan the registry does for real
// providers. The host-resource one is deliberately REUSED rather than duplicated, exactly as
// ProviderBindThrowTaxonomyTests reuses `stub.throwing-bind`.

using System.IO;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Compilation;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Runtime.Tests;

// ── File-scoped stub providers ────────────────────────────────────────────────

file sealed record FaultModel(string Tag) : IStepModel;

/// <summary>
/// <c>Validate</c> throws. EAGER, so <c>MethodInfo.Invoke</c> wraps it in
/// <see cref="TargetInvocationException"/> — the guard must unwrap it or the author reads
/// "Exception has been thrown by the target of an invocation" and learns nothing.
/// </summary>
[StepProvider]
file sealed class StubThrowingValidateProvider
    : IStepProvider,
      IStepBinder<FaultModel>,
      IStepValidator<FaultModel>,
      IStepCompiler<FaultModel>
{
    private static readonly string[] s_authors = new[] { "test" };

    public StepKindId Kind => new("stub", "throwing-validate");

    public ProviderMetadata Metadata => new(
        Version: "0.1.0",
        MinEngineVersion: "0.1.0",
        License: "Apache-2.0",
        Authors: s_authors);

    public JsonSchemaFragment SchemaFragment => new("""{"type":"object"}""");

    public FaultModel Bind(YamlNode node, IBindingContext ctx) => new("throwing-validate");

    public ValidationResult Validate(FaultModel model, IProjectContext ctx) =>
        throw new InvalidOperationException("stub: Validate always throws.");

    public CsxFragment Emit(FaultModel model, ICompileContext ctx) =>
        throw new InvalidOperationException("Should not reach Emit after Validate throws.");
}

/// <summary>
/// <c>Resources</c> is a real C# iterator whose body does not run until the caller's
/// <c>foreach</c> pulls the first element — so the throw surfaces at the CALL SITE's
/// enumeration, UNWRAPPED (no <see cref="TargetInvocationException"/>, because
/// <c>MethodInfo.Invoke</c> returned the enumerable and completed long before).
/// </summary>
[StepProvider]
file sealed class StubThrowingResourcesProvider
    : IStepProvider,
      IStepBinder<FaultModel>,
      IStepValidator<FaultModel>,
      IStepCompiler<FaultModel>,
      IResourceContributor<FaultModel>
{
    private static readonly string[] s_authors = new[] { "test" };

    public StepKindId Kind => new("stub", "throwing-resources");

    public ProviderMetadata Metadata => new(
        Version: "0.1.0",
        MinEngineVersion: "0.1.0",
        License: "Apache-2.0",
        Authors: s_authors);

    public JsonSchemaFragment SchemaFragment => new("""{"type":"object"}""");

    public FaultModel Bind(YamlNode node, IBindingContext ctx) => new("throwing-resources");

    public ValidationResult Validate(FaultModel model, IProjectContext ctx) =>
        ValidationResult.Success;

    public CsxFragment Emit(FaultModel model, ICompileContext ctx) =>
        new CsxFragment(
            RequiredUsings: Array.Empty<string>(),
            RequiredHelpers: Array.Empty<string>(),
            StatementBlock: $"{{ /* throwing-resources step: {CsxFragment.SanitiseId(ctx.StepId)} */ }}");

    public IEnumerable<ResourceRequirement> Resources(FaultModel model)
    {
        // Lazy: nothing here runs until MoveNext(). The throw is factored into a helper
        // rather than written inline so the iterator has no unreachable `yield`, which
        // -warnaserror would reject.
        yield return ThrowOnDemand();
    }

    private static ResourceRequirement ThrowOnDemand() =>
        throw new InvalidOperationException("stub: Resources always throws.");
}

/// <summary>
/// <c>Emit</c> throws. Eager, wrapped, and reached only after Bind/Validate/Resources all
/// succeed — so a test hitting it proves the guard sits at the LAST reflective surface too.
/// </summary>
[StepProvider]
file sealed class StubThrowingEmitProvider
    : IStepProvider,
      IStepBinder<FaultModel>,
      IStepValidator<FaultModel>,
      IStepCompiler<FaultModel>
{
    private static readonly string[] s_authors = new[] { "test" };

    public StepKindId Kind => new("stub", "throwing-emit");

    public ProviderMetadata Metadata => new(
        Version: "0.1.0",
        MinEngineVersion: "0.1.0",
        License: "Apache-2.0",
        Authors: s_authors);

    public JsonSchemaFragment SchemaFragment => new("""{"type":"object"}""");

    public FaultModel Bind(YamlNode node, IBindingContext ctx) => new("throwing-emit");

    public ValidationResult Validate(FaultModel model, IProjectContext ctx) =>
        ValidationResult.Success;

    public CsxFragment Emit(FaultModel model, ICompileContext ctx) =>
        throw new InvalidOperationException("stub: Emit always throws.");
}

/// <summary>
/// <c>CompileReferenceAssemblies</c> throws. The FIFTH provider call in Pass 2 and the only
/// one that is NOT dispatched reflectively — a direct interface PROPERTY read — which is why
/// #466's own list omits it and why it is guarded anyway: the failure mode is identical.
/// </summary>
[StepProvider]
file sealed class StubThrowingCompileReferencesProvider
    : IStepProvider,
      IStepBinder<FaultModel>,
      IStepValidator<FaultModel>,
      IStepCompiler<FaultModel>,
      ICompileReferenceContributor
{
    private static readonly string[] s_authors = new[] { "test" };

    public StepKindId Kind => new("stub", "throwing-compilerefs");

    public ProviderMetadata Metadata => new(
        Version: "0.1.0",
        MinEngineVersion: "0.1.0",
        License: "Apache-2.0",
        Authors: s_authors);

    public JsonSchemaFragment SchemaFragment => new("""{"type":"object"}""");

    public FaultModel Bind(YamlNode node, IBindingContext ctx) => new("throwing-compilerefs");

    public ValidationResult Validate(FaultModel model, IProjectContext ctx) =>
        ValidationResult.Success;

    public CsxFragment Emit(FaultModel model, ICompileContext ctx) =>
        new CsxFragment(
            RequiredUsings: Array.Empty<string>(),
            RequiredHelpers: Array.Empty<string>(),
            StatementBlock: $"{{ /* throwing-compilerefs step: {CsxFragment.SanitiseId(ctx.StepId)} */ }}");

    // A throwing GETTER rather than a throwing enumerator: the engine reads the property and
    // then enumerates it, and the guard has to cover both. This half is the eager one.
    public IEnumerable<Assembly> CompileReferenceAssemblies =>
        throw new InvalidOperationException("stub: CompileReferenceAssemblies always throws.");
}

/// <summary>
/// Emits a <see cref="CsxFragment"/> whose <c>RequiredUsings</c> entry is NOT a bare namespace
/// — the exact §13.3.1 mistake <c>CsxAssembler.ValidateBareNamespace</c> refuses. Bind,
/// Validate, Resources, HostResources and Emit all SUCCEED; <see cref="CsxFragment"/> performs
/// no constructor validation, so the bad fragment is built cleanly and the refusal happens
/// later, at <c>CsxAssembler.Assemble</c> — past every per-step guard.
/// </summary>
[StepProvider]
file sealed class StubBadFragmentProvider
    : IStepProvider,
      IStepBinder<FaultModel>,
      IStepValidator<FaultModel>,
      IStepCompiler<FaultModel>
{
    private static readonly string[] s_authors = new[] { "test" };

    /// <summary>The offending entry, asserted on so the diagnostic is proved actionable.</summary>
    internal const string NotABareNamespace = "using System.Text;";

    private static readonly string[] s_badUsings = new[] { NotABareNamespace };

    public StepKindId Kind => new("stub", "bad-fragment");

    public ProviderMetadata Metadata => new(
        Version: "0.1.0",
        MinEngineVersion: "0.1.0",
        License: "Apache-2.0",
        Authors: s_authors);

    public JsonSchemaFragment SchemaFragment => new("""{"type":"object"}""");

    public FaultModel Bind(YamlNode node, IBindingContext ctx) => new("bad-fragment");

    public ValidationResult Validate(FaultModel model, IProjectContext ctx) =>
        ValidationResult.Success;

    public CsxFragment Emit(FaultModel model, ICompileContext ctx) =>
        new CsxFragment(
            RequiredUsings: s_badUsings,
            RequiredHelpers: Array.Empty<string>(),
            StatementBlock: $"{{ /* bad-fragment step: {CsxFragment.SanitiseId(ctx.StepId)} */ }}");
}

/// <summary>
/// <c>Emit</c> throws a real <see cref="FileNotFoundException"/> from a genuine
/// <c>File.ReadAllText</c> against a missing file UNDER the suite directory — reproducing
/// <c>ScriptCsharpProvider.Emit</c>'s own TOCTOU race rather than hand-forging a message, so
/// the path in the diagnostic is whatever the BCL actually writes.
/// </summary>
[StepProvider]
file sealed class StubSuitePathLeakingEmitProvider
    : IStepProvider,
      IStepBinder<FaultModel>,
      IStepValidator<FaultModel>,
      IStepCompiler<FaultModel>
{
    private static readonly string[] s_authors = new[] { "test" };

    /// <summary>The file name the provider tries to read; never created.</summary>
    internal const string MissingFileName = "never-written.csx";

    public StepKindId Kind => new("stub", "suite-path-leaking-emit");

    public ProviderMetadata Metadata => new(
        Version: "0.1.0",
        MinEngineVersion: "0.1.0",
        License: "Apache-2.0",
        Authors: s_authors);

    public JsonSchemaFragment SchemaFragment => new("""{"type":"object"}""");

    public FaultModel Bind(YamlNode node, IBindingContext ctx) => new("suite-path-leaking-emit");

    public ValidationResult Validate(FaultModel model, IProjectContext ctx) =>
        ValidationResult.Success;

    public CsxFragment Emit(FaultModel model, ICompileContext ctx)
    {
        // The engine handed this provider ctx.SuiteDirectory; the provider composes an absolute
        // path from it and reads. The BCL's own message then carries that absolute path —
        // "Could not find file 'D:\…\never-written.csx'." — which is precisely the disclosure
        // ProviderPipeline.ScrubSuiteDirectory has to remove before the text is archived.
        var path = Path.GetFullPath(Path.Combine(ctx.SuiteDirectory, MissingFileName));
        var body = File.ReadAllText(path);
        return new CsxFragment(
            RequiredUsings: Array.Empty<string>(),
            RequiredHelpers: Array.Empty<string>(),
            StatementBlock: $"{{ /* {body} */ }}");
    }
}

/// <summary>
/// <c>Emit</c> throws with the suite path embedded in SERIALISED JSON, so the message carries
/// the JSON-ESCAPED spelling (<c>C:\\Users\\…</c>) and not the raw one — the second half of the
/// substitution SEC-MAJOR-1 requires, and the spelling a raw-only scrub misses.
/// </summary>
/// <remarks>
/// Not a contrived shape: a provider that fails while parsing or serialising a payload built
/// from a file path reports exactly this, and the resulting text is recoverable from the
/// on-disk <c>--events</c> artifact by any consumer that JSON-decodes it.
/// <c>SecurityPathDisclosureLedger</c>'s own remarks record that a raw-only match shipped once
/// as a bypass; this is the provider-side twin of that case.
/// </remarks>
[StepProvider]
file sealed class StubJsonEmbeddedPathEmitProvider
    : IStepProvider,
      IStepBinder<FaultModel>,
      IStepValidator<FaultModel>,
      IStepCompiler<FaultModel>
{
    private static readonly string[] s_authors = new[] { "test" };

    public StepKindId Kind => new("stub", "json-embedded-path-emit");

    public ProviderMetadata Metadata => new(
        Version: "0.1.0",
        MinEngineVersion: "0.1.0",
        License: "Apache-2.0",
        Authors: s_authors);

    public JsonSchemaFragment SchemaFragment => new("""{"type":"object"}""");

    public FaultModel Bind(YamlNode node, IBindingContext ctx) => new("json-embedded-path-emit");

    public ValidationResult Validate(FaultModel model, IProjectContext ctx) =>
        ValidationResult.Success;

    public CsxFragment Emit(FaultModel model, ICompileContext ctx) =>
        throw new InvalidOperationException(
            "could not parse the provider manifest at "
            + JsonSerializer.Serialize(ctx.SuiteDirectory));
}

/// <summary>
/// <c>Validate</c> RETURNS a failure (it does not throw) whose text carries the resolved suite
/// directory in BOTH spellings — the raw one and the JSON-escaped one. The engine splices
/// <c>ValidationResult.Errors</c> into the same <c>ValidationFailure</c> channel as a thrown
/// message, so it is bound by the same rule; that a provider returned the string rather than
/// threw it is not a distinction an archived artefact can make.
/// </summary>
[StepProvider]
file sealed class StubPathLeakingValidateProvider
    : IStepProvider,
      IStepBinder<FaultModel>,
      IStepValidator<FaultModel>,
      IStepCompiler<FaultModel>
{
    private static readonly string[] s_authors = new[] { "test" };

    public StepKindId Kind => new("stub", "path-leaking-validate");

    public ProviderMetadata Metadata => new(
        Version: "0.1.0",
        MinEngineVersion: "0.1.0",
        License: "Apache-2.0",
        Authors: s_authors);

    public JsonSchemaFragment SchemaFragment => new("""{"type":"object"}""");

    public FaultModel Bind(YamlNode node, IBindingContext ctx) => new("path-leaking-validate");

    /// <remarks>
    /// Both spellings in ONE failure, so a single row exercises both arms of the substitution:
    /// the raw form is what a provider writes when it interpolates <c>ctx.SuiteDirectory</c>
    /// directly, and the serialised form is what it writes when the directory is inside a
    /// payload it is reporting.
    /// </remarks>
    public ValidationResult Validate(FaultModel model, IProjectContext ctx) =>
        ValidationResult.Failure(
            $"manifest not found under {ctx.SuiteDirectory}; "
            + $"probe context was {JsonSerializer.Serialize(ctx.SuiteDirectory)}");

    public CsxFragment Emit(FaultModel model, ICompileContext ctx) =>
        throw new InvalidOperationException("Should not reach Emit after Validate fails.");
}

/// <summary>
/// <c>Emit</c> throws an exception that WRAPS the real failure, two levels deep — the shape
/// MINOR-3 exists for: a provider's own wrapper over a client library's wrapper over the
/// transport fault. Reporting only the outermost message names the symptom and hides the cause.
/// </summary>
/// <remarks>
/// <see cref="InvalidTimeZoneException"/> is chosen for the innermost link purely because it is
/// an unmistakable, otherwise-unused name: no other diagnostic in this suite can produce it, so
/// asserting on it cannot pass by accident.
/// </remarks>
[StepProvider]
file sealed class StubNestedCauseEmitProvider
    : IStepProvider,
      IStepBinder<FaultModel>,
      IStepValidator<FaultModel>,
      IStepCompiler<FaultModel>
{
    private static readonly string[] s_authors = new[] { "test" };

    public StepKindId Kind => new("stub", "nested-cause-emit");

    public ProviderMetadata Metadata => new(
        Version: "0.1.0",
        MinEngineVersion: "0.1.0",
        License: "Apache-2.0",
        Authors: s_authors);

    public JsonSchemaFragment SchemaFragment => new("""{"type":"object"}""");

    public FaultModel Bind(YamlNode node, IBindingContext ctx) => new("nested-cause-emit");

    public ValidationResult Validate(FaultModel model, IProjectContext ctx) =>
        ValidationResult.Success;

    public CsxFragment Emit(FaultModel model, ICompileContext ctx) =>
        throw new InvalidOperationException(
            "save failed",
            new InvalidTimeZoneException("the actual transport fault"));
}

/// <summary>
/// Issue #466: every reflective SDK surface a provider can throw from becomes a diagnosable
/// <c>PipelineResult.Failure</c> naming the provider, never an escape the slot catch-all
/// mislabels as infrastructure.
/// </summary>
public sealed class ProviderReflectiveFaultTaxonomyTests
{
    private static readonly Assembly[] ProviderAssemblies =
        new[] { typeof(ProviderReflectiveFaultTaxonomyTests).Assembly };

    private static readonly StepKindRegistry Registry =
        StepKindRegistry.BuildAndFreeze(ProviderAssemblies);

    private const string SuiteNamespace = "Vouchfx.Generated.Fault";

    private const string AppHostAssemblyName = "Vouchfx.Engine.Runtime.Tests";

    private static readonly string[] s_scenarioNames = { "reflective-fault-suite" };

    private static string SuiteFor(string stepType) => $"""
        environment:
          services:
            api:
              image: myorg/api:1.0
        steps:
          - id: will-throw
            type: {stepType}
        """;

    /// <summary>
    /// One row per guarded surface: the throw becomes a failure, not an exception, and the
    /// diagnostic names the step, the canonical step type, the provider's
    /// <see cref="Type.FullName"/>, the SDK member that threw, and the exception's own type
    /// and message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>providerSimpleName</c> is asserted as a SUBSTRING of the full name rather than
    /// spelled out, because these stubs are <c>file</c>-scoped: the compiler mangles their
    /// metadata name (<c>&lt;…&gt;F…__StubThrowingEmitProvider</c>), so pinning the exact
    /// string would pin a compiler implementation detail. The property under test is "the
    /// diagnostic carries the provider TYPE, not just the step type" — a substring of the
    /// mangled name proves that and survives a compiler change.
    /// </para>
    /// <para>
    /// The <c>DoesNotContain(TargetInvocationException)</c> assertion is what separates a
    /// useful diagnostic from a useless one on the two EAGER surfaces, and is asserted on
    /// all four because it must hold for the lazy pair trivially.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("stub.throwing-validate", "Validate", "StubThrowingValidateProvider", "Validate always throws")]
    [InlineData("stub.throwing-resources", "Resources", "StubThrowingResourcesProvider", "Resources always throws")]
    [InlineData("stub.throwing-hostresource", "HostResources", "StubThrowingHostResourceProvider", "")]
    [InlineData("stub.throwing-compilerefs", "CompileReferenceAssemblies", "StubThrowingCompileReferencesProvider", "CompileReferenceAssemblies always throws")]
    [InlineData("stub.throwing-emit", "Emit", "StubThrowingEmitProvider", "Emit always throws")]
    public void Compile_ProviderThrowsFromAReflectiveSurface_ReturnsADiagnosableFailure(
        string stepType,
        string member,
        string providerSimpleName,
        string exceptionMessageFragment)
    {
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(SuiteFor(stepType)), Registry);

        var result = ProviderPipeline.Compile(ast, Registry, SuiteNamespace);

        Assert.NotNull(result.Failure);
        Assert.Null(result.Assembled);

        var message = result.Failure!.Message;
        Assert.Contains("will-throw", message, StringComparison.Ordinal);
        Assert.Contains(stepType, message, StringComparison.Ordinal);
        Assert.Contains(providerSimpleName, message, StringComparison.Ordinal);
        Assert.Contains(member, message, StringComparison.Ordinal);

        if (exceptionMessageFragment.Length > 0)
        {
            Assert.Contains(nameof(InvalidOperationException), message, StringComparison.Ordinal);
            Assert.Contains(exceptionMessageFragment, message, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(
            nameof(TargetInvocationException), message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The host-resource row's own exception identity, split out because
    /// <c>stub.throwing-hostresource</c>'s throw comes from
    /// <see cref="HostResourceRequirement"/>'s ctor validation rather than from a message the
    /// stub chose — so the theory above cannot assert a message fragment for it without
    /// pinning the SDK's argument-validation wording.
    /// </summary>
    [Fact]
    public void Compile_HostResourcesThrows_FailureNamesTheExceptionType()
    {
        var ast = AstBuilder.Build(
            YamlDocumentParser.Parse(SuiteFor("stub.throwing-hostresource")), Registry);

        var result = ProviderPipeline.Compile(ast, Registry, SuiteNamespace);

        Assert.NotNull(result.Failure);
        Assert.Contains(
            nameof(ArgumentException), result.Failure!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE DEFECT ITSELF: <c>--parallel</c> must not report a provider fault as an
    /// infrastructure fault. Before the fix the throw escaped
    /// <c>RunScenarioOwningTopologyAsync</c>, the slot catch-all synthesised
    /// <see cref="Verdict.EnvironmentError"/>, and the run — having executed nothing — exited
    /// 0. The exit code itself is measured in <c>Vouchfx.Cli.Tests</c>
    /// (<c>ReflectiveFaultExitCodeTests</c>), which is the only project that can see
    /// <c>RunCommand.ComputeExitCode</c>; this pins the two inputs that decide it.
    /// </summary>
    [Theory]
    [InlineData("stub.throwing-validate")]
    [InlineData("stub.throwing-resources")]
    [InlineData("stub.throwing-hostresource")]
    [InlineData("stub.throwing-compilerefs")]
    [InlineData("stub.throwing-emit")]
    public async Task RunParallelAsync_ProviderThrowsFromAReflectiveSurface_IsInconclusiveNotEnvironmentError(
        string stepType)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-reflective-fault-");
        try
        {
            var yaml = SuiteFor(stepType);
            var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), Registry);
            var sw = new StringWriter();

            var result = await ParallelSuiteRunner.RunParallelAsync(
                scenarios: new[] { ast },
                scenarioNames: s_scenarioNames,
                yamlTexts: new[] { yaml },
                providerAssemblies: ProviderAssemblies,
                appHostAssemblyName: AppHostAssemblyName,
                output: sw,
                maxConcurrency: 1,
                seedBaseDirectory: directory.FullName);

            Assert.Equal(Verdict.Inconclusive, result.Verdict);
            Assert.False(
                result.ExecutedAnyScenario,
                "nothing ran, so #369's rule must take the exit code off Success.");

            var rendered = sw.ToString();
            Assert.Contains("will-throw", rendered, StringComparison.Ordinal);
            Assert.Contains(stepType, rendered, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The bare <c>run</c> path answers identically — the two paths must not disagree about
    /// one provider fault, which is the divergence #413 closed for <c>Bind</c> and this
    /// closes for the remaining four surfaces.
    /// </summary>
    [Theory]
    [InlineData("stub.throwing-validate")]
    [InlineData("stub.throwing-resources")]
    [InlineData("stub.throwing-hostresource")]
    [InlineData("stub.throwing-compilerefs")]
    [InlineData("stub.throwing-emit")]
    public async Task RunSuiteAsync_ProviderThrowsFromAReflectiveSurface_IsInconclusiveNotEnvironmentError(
        string stepType)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-reflective-fault-run-");
        try
        {
            var yaml = SuiteFor(stepType);
            var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), Registry);
            var sw = new StringWriter();

            var result = await ScenarioRunner.RunSuiteAsync(
                scenarios: new[] { ast },
                scenarioNames: s_scenarioNames,
                yamlTexts: new[] { yaml },
                providerAssemblies: ProviderAssemblies,
                appHostAssemblyName: AppHostAssemblyName,
                output: sw,
                seedBaseDirectory: directory.FullName);

            Assert.Equal(Verdict.Inconclusive, result.Verdict);
            Assert.False(result.ExecutedAnyScenario);

            var rendered = sw.ToString();
            Assert.Contains("will-throw", rendered, StringComparison.Ordinal);
            Assert.Contains(stepType, rendered, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // ── SEC-MAJOR-1: the suite directory is substituted out, in BOTH spellings ───

    /// <summary>
    /// The guard's diagnostic names <c>the suite directory</c>, never the resolved absolute
    /// host path — whether the provider wrote it raw or JSON-escaped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THREE STUBS, AND THE SPELLINGS ARE SPLIT ACROSS THEM DELIBERATELY.
    /// <c>stub.suite-path-leaking-emit</c> reads a missing file and lets the BCL write the raw
    /// path; <c>stub.json-embedded-path-emit</c> serialises the same directory into its message,
    /// so the text carries <c>C:\\Users\\…</c> with doubled separators. A scrub matching only
    /// the raw form passes the first row and fails the second — measured, both ways.
    /// </para>
    /// <para>
    /// <c>stub.path-leaking-validate</c> is the THIRD row and covers a different CHANNEL rather
    /// than a different spelling (it carries both): the provider RETURNS the path in
    /// <c>ValidationResult.Errors</c> instead of throwing it. <c>Compile</c> splices that into
    /// the same <c>ValidationFailure</c>, so the same rule binds it — and it was the one site
    /// left unscrubbed when the six throw-guards were fixed.
    /// </para>
    /// <para>
    /// The written-artefact half of this property (event stream, JUnit, HTML) is asserted in
    /// <c>SecurityDiagnosticPathDisclosureTests</c>, which owns the sibling-site property test
    /// <see cref="SecurityPathDisclosureLedger"/>'s own remarks point at. This row is the fast,
    /// direct pin on the substitution itself.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("stub.suite-path-leaking-emit", "never-written.csx")]
    [InlineData("stub.json-embedded-path-emit", "could not parse the provider manifest")]
    [InlineData("stub.path-leaking-validate", "manifest not found under")]
    public void Compile_ProviderFaultMessageQuotesTheSuiteDirectory_SubstitutesTheConcept(
        string stepType,
        string providerTextFragment)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-466-scrub-");
        try
        {
            var suiteDirectory = directory.FullName;
            var ast = AstBuilder.Build(YamlDocumentParser.Parse(SuiteFor(stepType)), Registry);

            var result = ProviderPipeline.Compile(
                ast, Registry, SuiteNamespace, suiteDirectory);

            Assert.NotNull(result.Failure);
            var message = result.Failure!.Message;

            // NOT VACUOUS, and asserted on the PROVIDER's OWN WORDS rather than on the step
            // type: the validation-failure channel does not name the step type at all (it
            // names the step id), so a type assertion would fail that row for the wrong
            // reason. What every row must show is that the provider's text really did reach
            // the message — because that is the text the scrub had to pass through.
            Assert.Contains("will-throw", message, StringComparison.Ordinal);
            Assert.Contains(providerTextFragment, message, StringComparison.Ordinal);

            // THE ABSENCE ASSERTIONS COME FIRST so a broken scrub fails ON THE DISCLOSURE
            // rather than on the replacement phrase being missing.
            Assert.DoesNotContain(suiteDirectory, message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                JavaScriptEncoder.Default.Encode(suiteDirectory),
                message,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                JsonSerializer.Serialize(suiteDirectory).Trim('"'),
                message,
                StringComparison.OrdinalIgnoreCase);

            // Substitution, not deletion: the concept has to be named or a relative path in a
            // sibling diagnostic stops being resolvable by the reader.
            Assert.Contains("the suite directory", message, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A suite directory that IS a filesystem root is left alone: the substitution would
    /// corrupt the diagnostic far worse than the disclosure it removes, and a root is not a
    /// host fact worth hiding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NO ROOT-DIRECTORY SUITE IS CREATED, and none is needed: <c>Compile</c> takes the suite
    /// directory as a plain string, and on a document with no <c>environment</c> and no seed
    /// nothing reads it from disk — it reaches the provider as <c>ctx.SuiteDirectory</c> and
    /// nothing else. The root is computed from the current directory rather than hard-coded, so
    /// this is <c>C:\</c> on Windows and <c>/</c> elsewhere and the assertion is the same.
    /// </para>
    /// <para>
    /// MEASURED RED without the guard: the scrub replaced the root everywhere it occurred and
    /// the message read <c>Could not find file 'the suite directorynever-written.csx'.</c> —
    /// the path mangled into nonsense. On a POSIX host the same guard is what stops <c>/</c>
    /// being replaced inside every URL and every unrelated path in a diagnostic.
    /// </para>
    /// <para>
    /// The assertion deliberately requires the ROOT-BASED PATH TO SURVIVE VERBATIM. That is the
    /// accepted trade written down as a test rather than left in a comment: at a root there is
    /// nothing to protect, so the scrub stands down entirely.
    /// </para>
    /// </remarks>
    [Fact]
    public void Compile_SuiteDirectoryIsAFilesystemRoot_LeavesTheDiagnosticUnmangled()
    {
        var root = Path.GetPathRoot(Directory.GetCurrentDirectory());
        Assert.False(string.IsNullOrEmpty(root), "the current directory must have a path root.");

        var ast = AstBuilder.Build(
            YamlDocumentParser.Parse(SuiteFor("stub.suite-path-leaking-emit")), Registry);

        var result = ProviderPipeline.Compile(ast, Registry, SuiteNamespace, root);

        Assert.NotNull(result.Failure);
        var message = result.Failure!.Message;

        // The provider's own path, composed against the root, survives intact — not chopped
        // into "the suite directorynever-written.csx" by a substitution of the root prefix.
        Assert.Contains(
            Path.Combine(root!, "never-written.csx"), message, StringComparison.Ordinal);
        Assert.DoesNotContain("the suite directory", message, StringComparison.Ordinal);
    }

    // ── MINOR-1: the attribution sentence is conditioned on the exception type ───

    /// <summary>The categorical claim, spelled once so both rows key on the same string.</summary>
    private const string CategoricalBlame = "This is a defect in the provider";

    /// <summary>
    /// A filesystem-family exception must NOT be reported as categorically the provider's
    /// defect — while an ordinary one still must be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE PRODUCTION SHAPE, NOT A HYPOTHETICAL. <c>stub.suite-path-leaking-emit</c> models
    /// <c>ScriptCsharpProvider.Emit</c>'s accepted TOCTOU race verbatim: it reads a file under
    /// <c>ctx.SuiteDirectory</c> that is not there, and the BCL raises
    /// <see cref="FileNotFoundException"/>. That is the single most likely real trigger of the
    /// <c>Emit</c> guard, and before <c>IsEnvironmentalCondition</c> it produced "This is a
    /// defect in the provider (Vouchfx.Steps.Script.Csharp.ScriptCsharpProvider)" — false, and
    /// an accusation against a Core provider for a file an antivirus scanner happened to lock.
    /// </para>
    /// <para>
    /// THE CONTROL ROW IS WHAT KEEPS THE ARM HONEST. <c>stub.throwing-emit</c> throws an
    /// ordinary <see cref="InvalidOperationException"/> and must STILL get the categorical
    /// claim — without that row, an arm that softened every message would pass.
    /// </para>
    /// <para>
    /// MEASURED RED with <c>IsEnvironmentalCondition</c> removed: the
    /// <c>suite-path-leaking-emit</c> row failed on
    /// <c>DoesNotContain("This is a defect in the provider")</c>; the control row stayed green.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("stub.suite-path-leaking-emit", false)]
    [InlineData("stub.throwing-emit", true)]
    public void Compile_AttributionSentence_IsConditionedOnTheExceptionType(
        string stepType,
        bool expectsCategoricalBlame)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-466-attribution-");
        try
        {
            var ast = AstBuilder.Build(YamlDocumentParser.Parse(SuiteFor(stepType)), Registry);

            var result = ProviderPipeline.Compile(
                ast, Registry, SuiteNamespace, directory.FullName);

            Assert.NotNull(result.Failure);
            var message = result.Failure!.Message;

            // NOT VACUOUS: the guard fired and the message is the provider-fault shape, so the
            // presence/absence below is a statement about the ATTRIBUTION arm and not about
            // having reached some unrelated diagnostic.
            Assert.Contains("will-throw", message, StringComparison.Ordinal);
            Assert.Contains("Emit threw", message, StringComparison.Ordinal);

            if (expectsCategoricalBlame)
            {
                Assert.Contains(CategoricalBlame, message, StringComparison.Ordinal);
                Assert.DoesNotContain("filesystem condition", message, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain(CategoricalBlame, message, StringComparison.Ordinal);
                Assert.Contains("filesystem condition", message, StringComparison.Ordinal);

                // Softened, not silenced: the provider is still NAMED, so an author who does
                // suspect it still knows which one to open.
                Assert.Contains(
                    "StubSuitePathLeakingEmitProvider", message, StringComparison.Ordinal);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// MINOR-3: a provider that wraps its real failure has the inner chain rendered, not
    /// discarded — the symptom alone ("save failed") names nothing an author can act on.
    /// </summary>
    /// <remarks>
    /// The chain is walked to a bounded depth and each link is scrubbed individually; the
    /// STACK is still dropped, on the §17 grounds recorded in
    /// <c>DescribeProviderFault</c>'s remarks (a stack carries PDB source paths, a far larger
    /// disclosure than the suite directory the scrub fights).
    /// </remarks>
    [Fact]
    public void Compile_ProviderWrapsItsRealFailure_RendersTheInnerChain()
    {
        var ast = AstBuilder.Build(
            YamlDocumentParser.Parse(SuiteFor("stub.nested-cause-emit")), Registry);

        var result = ProviderPipeline.Compile(ast, Registry, SuiteNamespace);

        Assert.NotNull(result.Failure);
        var message = result.Failure!.Message;

        // The outer symptom AND the real cause, in order.
        Assert.Contains("save failed", message, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidTimeZoneException), message, StringComparison.Ordinal);
        Assert.Contains("the actual transport fault", message, StringComparison.Ordinal);

        Assert.True(
            message.IndexOf("save failed", StringComparison.Ordinal)
                < message.IndexOf("the actual transport fault", StringComparison.Ordinal),
            "the chain must read outermost-first, the order a reader scans.");
    }

    // ── The sixth escape route: CsxAssembler.Assemble (GATE-MAJOR-1) ──────────

    /// <summary>
    /// A provider that EMITS a fragment breaking §13.3.1 is refused with a diagnosable failure,
    /// not an escape — even though every per-step guard saw a clean <c>Emit</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THIS IS NOT REACHABLE THROUGH ANY OF THE SIX PER-STEP GUARDS, and that is the whole
    /// reason it needed its own. <see cref="CsxFragment"/> has no constructor validation, so
    /// <c>StubBadFragmentProvider.Emit</c> returns normally with a <c>RequiredUsings</c> entry
    /// that is not a bare namespace; <c>CsxAssembler.ValidateBareNamespace</c> refuses it later,
    /// at the <c>Assemble</c> call after the per-step loop has finished. Unguarded, that
    /// <c>CsxAssemblyException</c> took the same route to <c>ParallelSuiteRunner</c>'s slot
    /// catch-all and exit 0. It had never been exercised through <c>Compile</c> at all — only
    /// at <c>CsxAssembler</c>'s own unit level, where the throw is the asserted outcome.
    /// </para>
    /// <para>
    /// NO STEP IS NAMED, deliberately: <c>CsxAssemblyException</c> carries only a message and
    /// neither of its throw sites records which fragment was at fault. The assertions below
    /// therefore require the OFFENDING ENTRY (which the exception does carry, and which the
    /// author can grep for) and require that no step id is invented.
    /// </para>
    /// </remarks>
    [Fact]
    public void Compile_ProviderEmitsFragmentTheAssemblerRefuses_ReturnsADiagnosableFailure()
    {
        var ast = AstBuilder.Build(
            YamlDocumentParser.Parse(SuiteFor("stub.bad-fragment")), Registry);

        var result = ProviderPipeline.Compile(ast, Registry, SuiteNamespace);

        Assert.NotNull(result.Failure);
        Assert.Null(result.Assembled);

        var message = result.Failure!.Message;
        Assert.Contains(nameof(CsxAssemblyException), message, StringComparison.Ordinal);
        Assert.Contains(
            StubBadFragmentProvider.NotABareNamespace, message, StringComparison.Ordinal);
        Assert.Contains("provider", message, StringComparison.Ordinal);

        // The exception cannot identify a fragment, so the diagnostic must not pretend it can.
        Assert.DoesNotContain("will-throw", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same assembler refusal through the runners: a taxonomy verdict on both paths, with
    /// nothing executed — so #369's rule takes the exit code off Success.
    /// </summary>
    [Fact]
    public async Task Runners_ProviderEmitsFragmentTheAssemblerRefuses_AreInconclusiveNotEnvironmentError()
    {
        var yaml = SuiteFor("stub.bad-fragment");
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), Registry);

        var parallel = await ParallelSuiteRunner.RunParallelAsync(
            scenarios: new[] { ast },
            scenarioNames: s_scenarioNames,
            yamlTexts: new[] { yaml },
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: new StringWriter(),
            maxConcurrency: 1);

        var sequential = await ScenarioRunner.RunSuiteAsync(
            scenarios: new[] { ast },
            scenarioNames: s_scenarioNames,
            yamlTexts: new[] { yaml },
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: new StringWriter());

        Assert.Equal(Verdict.Inconclusive, parallel.Verdict);
        Assert.NotEqual(Verdict.EnvironmentError, parallel.Verdict);
        Assert.False(parallel.ExecutedAnyScenario);

        Assert.Equal(Verdict.Inconclusive, sequential.Verdict);
        Assert.False(sequential.ExecutedAnyScenario);
    }

}
