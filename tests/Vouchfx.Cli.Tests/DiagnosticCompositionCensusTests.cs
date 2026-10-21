using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Vouchfx.Engine.Abstractions;
using Xunit;

namespace Vouchfx.Cli.Tests;

/// <summary>
/// Cross-assembly composition gate (#405). Roslyn excludes comments and string lookalikes and
/// follows calls in lambdas/local functions through their enclosing method. This is a bounded
/// source inventory, not a general data-flow analyser: every primitive operation needs review,
/// including a separate-statement scrub that would evade a nested-call-only check.
/// Primitive method-group captures are refused, so renamed delegates cannot hide operations.
/// </summary>
public sealed class DiagnosticCompositionCensusTests
{
    // Entries name the method and tokenised invocation, rather than line numbers or magic counts.
    // Preserve duplicates: adding a second copy at an approved site must also require review.
    // No value/display composition here: public validation and secret references (including
    // earlyMessage) stay sanitise-only. Existing provider-fault fallbacks with no ledger in
    // scope are also inventoried; this gate does not claim all terminal writes are scrubbed.
    private const string ReviewedSanitiseOnly = """
        src/Cli/Vouchfx.Cli/ProvidersCommand.cs:ProvidersCommand.GuardAsync | DisplaySanitiser.SanitiseForDisplay(exception.Message)
        src/Cli/Vouchfx.Cli/ProvidersCommand.cs:ProvidersCommand.WriteLine | DisplaySanitiser.SanitiseForDisplay(value)
        src/Cli/Vouchfx.Cli/ProviderOptions.cs:ProviderOptions.InvokeAsync | DisplaySanitiser.SanitiseForDisplay(exception.Message)
        src/Cli/Vouchfx.Cli/RunCommand.cs:RunCommand.ExecuteAsync | DisplaySanitiser.SanitiseForDisplay($"vouchfx run: the engine failed unexpectedly and could not reach a verdict "+$"({ex.GetType().FullName}: {ex.Message}).  This is an engine or provider "+"defect, not a suite failure - please report it with the suite that "+"triggered it.  Reported as Inconclusive (section 12.1).")
        src/Cli/Vouchfx.Cli/RunCommand.cs:RunCommand.ExecuteRunPipelineAsync | DisplaySanitiser.SanitiseForDisplay($"{failure.AbsolutePath}: {failure.ParseError} (Inconclusive)")
        src/Cli/Vouchfx.Cli/RunCommand.cs:RunCommand.ExecuteRunPipelineAsync | DisplaySanitiser.SanitiseForDisplay(ex.Message)
        src/Cli/Vouchfx.Cli/ValidateCommand.cs:ValidateCommand.WriteHumanReport | DisplaySanitiser.SanitiseForDisplay(diagnostic.Message)
        src/Cli/Vouchfx.Cli/Watch/WatchRunner.cs:WatchRunner.RunAsync | DisplaySanitiser.SanitiseForDisplay($"--watch: '{scenario.AbsolutePath}' did not parse: {scenario.ParseError}")
        src/Cli/Vouchfx.Cli/Watch/WatchRunner.cs:WatchRunner.RunAsync | DisplaySanitiser.SanitiseForDisplay($"Watching '{filePath}'.  Saving re-runs the suite (topology re-used when the selected "+"environment permits it and its inputs are unchanged).  Press Ctrl-C to stop.")
        src/Engine/Vouchfx.Engine.Runtime/ParallelSuiteRunner.cs:ParallelSuiteRunner.RunOneSlotAsync | DisplaySanitiser.SanitiseForDisplay($"[environment-error] scenario '{scenarioName}' did not complete: {ex.GetType().Name}")
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.CompleteWithoutTopologyAsync | DisplaySanitiser.SanitiseForDisplay(earlyMessage)
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RenderWatchRefusal | DisplaySanitiser.SanitiseForDisplay(plan.Diagnostic)
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.ReportDiffRendererFault | DisplaySanitiser.SanitiseForDisplay($"step kind '{kind}': the provider's diff renderer {member} threw "+$"{faultType}: {flatMessage}  {attribution} - the expected-vs-observed diff is "+"omitted wherever this recurs. The verdict, the exit code and every "+"report artefact are unaffected; this line is reported once per step kind, "+"member and exception type.")
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunPlannedScenarioAgainstKeptTopologyAsync | DisplaySanitiser.SanitiseForDisplay(confirmation.ToString())
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunPlannedScenarioAgainstKeptTopologyAsync | DisplaySanitiser.SanitiseForDisplay(notice.ToString())
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunPlannedScenarioAgainstKeptTopologyAsync | DisplaySanitiser.SanitiseForDisplay(notice.ToString())
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunScenarioCoreAsync | DisplaySanitiser.SanitiseForDisplay(secretFault)
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunScenarioOwningTopologyAsync | DisplaySanitiser.SanitiseForDisplay(authoringFault)
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunScenarioOwningTopologyAsync | DisplaySanitiser.SanitiseForDisplay(confirmation.ToString())
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunScenarioOwningTopologyAsync | DisplaySanitiser.SanitiseForDisplay(error.Message)
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunScenarioOwningTopologyAsync | DisplaySanitiser.SanitiseForDisplay(notice.ToString())
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunScenarioOwningTopologyAsync | DisplaySanitiser.SanitiseForDisplay(notice.ToString())
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunScenarioOwningTopologyAsync | DisplaySanitiser.SanitiseForDisplay(parseFault)
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunSuiteAsync | DisplaySanitiser.SanitiseForDisplay(confirmation.ToString())
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunSuiteAsync | DisplaySanitiser.SanitiseForDisplay(divergence)
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunSuiteAsync | DisplaySanitiser.SanitiseForDisplay(divergentEnvironment)
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunSuiteAsync | DisplaySanitiser.SanitiseForDisplay(earlyMessage)
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunSuiteAsync | DisplaySanitiser.SanitiseForDisplay(notice.ToString())
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunSuiteAsync | DisplaySanitiser.SanitiseForDisplay(notice.ToString())
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunSuiteAsync | DisplaySanitiser.SanitiseForDisplay(protocolConflict)
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunSuiteAsync | DisplaySanitiser.SanitiseForDisplay(seedRootSplit)
        """;

    // Event observations/causes and declared-path substitutions retain their raw content
    // scrubs. They must not acquire terminal rewriting or new primitive calls unnoticed.
    private const string ReviewedRawContent = """
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.BuildStepObservation | ScrubDiagnostic(accessor,pathLedger,rawObservation)
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.EnvironmentErrorLine | sharedLedger?.Scrub(info.Detail)
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.EnvironmentErrorLine | sharedPathLedger?.Scrub(scrubbed)
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunScenarioOwningTopologyAsync | DiagnosticText.Scrub(rawEnvironmentFault,runSecretLedger,runPathLedger)
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunSuiteAsync | DiagnosticText.Scrub(rawEnvironmentFault,runSecretLedger,runPathLedger)
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunSuiteAsync | DiagnosticText.Scrub(rawTopologyFailure,runSecretLedger,runPathLedger)
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.ScrubDiagnostic | DiagnosticText.Scrub(text,(accessorasSecretAccessor)?.ResolvedSecrets,pathLedger)
        src/Engine/Vouchfx.Engine.Runtime/SecurityConfigurationAccessor.cs:SecurityCertificateMaterial.WithDeclaredPaths | ledger.Scrub(message)
        src/Engine/Vouchfx.Engine.Runtime/StepEventBuilder.cs:StepEventBuilder.ScenarioCompletedLine | pathLedger?.Scrub(cause)
        src/Engine/Vouchfx.Engine.Runtime/StepEventBuilder.cs:StepEventBuilder.ScenarioCompletedLine | ledger?.Scrub(message)
        """;

    // Every current resolved-value display caller uses the shared helper with both ledgers.
    private const string ReviewedDisplayCompositions = """
        src/Cli/Vouchfx.Cli/Watch/WatchRunner.cs:WatchRunner.CreateSession | DiagnosticText.ScrubForDisplay(line,sessionSecretLedger,sessionPathLedger)
        src/Cli/Vouchfx.Cli/Watch/WatchRunner.cs:WatchRunner.ProcessChangeGuardedAsync | DiagnosticText.ScrubForDisplay($"--watch: environment error during run: {oex.Message}",sessionSecretLedger,sessionPathLedger)
        src/Cli/Vouchfx.Cli/Watch/WatchRunner.cs:WatchRunner.ProcessChangeGuardedAsync | DiagnosticText.ScrubForDisplay($"--watch: error during run ({ex.GetType().Name}): {ex.Message}",sessionSecretLedger,sessionPathLedger)
        src/Cli/Vouchfx.Cli/Watch/WatchRunner.cs:WatchRunner.RunOnceFromDiskAsync | DiagnosticText.ScrubForDisplay($"--watch: could not read '{filePath}': {ex.Message}",sessionSecretLedger,sessionPathLedger)
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunScenarioCoreAsync | DiagnosticText.ScrubForDisplay(diagnosis,(secretAccessorasSecretAccessor)?.ResolvedSecrets,sharedPathLedger)
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunScenarioOwningTopologyAsync | DiagnosticText.ScrubForDisplay(rawEnvironmentFault,runSecretLedger,runPathLedger)
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunSuiteAsync | DiagnosticText.ScrubForDisplay($"Isolation.EndScenarioAsync failed after '{name}': {oex.Message}; "+"aborting suite - subsequent scenarios may run against "+"unclean state.",runSecretLedger,runPathLedger)
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunSuiteAsync | DiagnosticText.ScrubForDisplay(isolationFault,runSecretLedger,runPathLedger)
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunSuiteAsync | DiagnosticText.ScrubForDisplay(rawEnvironmentFault,runSecretLedger,runPathLedger)
        src/Engine/Vouchfx.Engine.Runtime/ScenarioRunner.cs:ScenarioRunner.RunSuiteAsync | DiagnosticText.ScrubForDisplay(rawTopologyFailure,runSecretLedger,runPathLedger)
        """;

    // The sole display composition and its content implementation. Exact token expressions
    // pin nesting; behavioural hostile-byte tests independently pin execution order.
    private const string ReviewedHelper = """
        src/Engine/Vouchfx.Engine.Runtime/DiagnosticText.cs:DiagnosticText.Scrub | pathLedger?.Scrub(scrubbed)
        src/Engine/Vouchfx.Engine.Runtime/DiagnosticText.cs:DiagnosticText.Scrub | ledger.Scrub(text)
        src/Engine/Vouchfx.Engine.Runtime/DiagnosticText.cs:DiagnosticText.ScrubForDisplay | DisplaySanitiser.SanitiseForDisplay(Scrub(text,ledger,pathLedger))
        src/Engine/Vouchfx.Engine.Runtime/DiagnosticText.cs:DiagnosticText.ScrubForDisplay | Scrub(text,ledger,pathLedger)
        """;

    [Fact]
    public void EveryPrimitiveAndSharedComposition_AcrossRuntimeAndCli_HasAReviewedClassification()
    {
        var expected = string.Join('\n', ReviewedSanitiseOnly, ReviewedRawContent,
                ReviewedDisplayCompositions, ReviewedHelper).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()).OrderBy(line => line, StringComparer.Ordinal).ToArray();
        var actual = Sources().SelectMany(pair => Operations(pair.Key, pair.Value))
            .OrderBy(line => line, StringComparer.Ordinal).ToArray();
        Assert.True(expected.SequenceEqual(actual, StringComparer.Ordinal),
            "Review new/removed diagnostic operations; resolved-value displays must use DiagnosticText.ScrubForDisplay.\n"
            + "Unreviewed:\n" + string.Join('\n', actual.Except(expected, StringComparer.Ordinal))
            + "\nMissing:\n" + string.Join('\n', expected.Except(actual, StringComparer.Ordinal)));
    }

    [Theory]
    [InlineData("Runtime")]
    [InlineData("Cli")]
    public void GateRejectsNewDuplicateOrReversedCompositions_InEitherAssembly(string assembly)
    {
        var reviewed = Sources().SelectMany(pair => Operations(pair.Key, pair.Value)).ToArray();
        var path = assembly == "Runtime" ? "src/Engine/Vouchfx.Engine.Runtime/NewDiagnostic.cs"
            : "src/Cli/Vouchfx.Cli/NewDiagnostic.cs";
        var bodies = new[]
        {
            "return DisplaySanitiser.SanitiseForDisplay(ledger.Scrub(raw));",
            "return ledger.Scrub(DisplaySanitiser.SanitiseForDisplay(raw));",
            "var text = ledger.Scrub(raw); return DisplaySanitiser.SanitiseForDisplay(text);",
            "var text = DisplaySanitiser.SanitiseForDisplay(raw); return ledger.Scrub(text);",
        };
        foreach (var body in bodies)
        {
            var mutant = "class NewDiagnostic { string Render(string raw) { " + body + " } }";
            var unreviewed = Operations(path, mutant).Except(reviewed, StringComparer.Ordinal).ToArray();
            Assert.NotEmpty(unreviewed);
            Assert.Contains(unreviewed, operation => operation.Contains("SanitiseForDisplay", StringComparison.Ordinal));
            Assert.Contains(unreviewed, operation => operation.Contains("Scrub", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("Runtime")]
    [InlineData("Cli")]
    public void GateRejectsCompiledMethodGroupAliases_InEitherAssembly(string assembly)
    {
        var path = assembly == "Runtime" ? "src/Engine/Vouchfx.Engine.Runtime/NewDiagnostic.cs"
            : "src/Cli/Vouchfx.Cli/NewDiagnostic.cs";
        foreach (var clean in new[] { "DisplaySanitiser.SanitiseForDisplay", "SanitiseForDisplay" })
        {
            foreach (var expression in new[] { "redact(clean(raw))", "redact.Invoke(clean.Invoke(raw))",
                         "clean(redact(raw))", "clean.Invoke(redact.Invoke(raw))" })
            {
                var source = CompiledSource($"Func<string?, string?> clean = {clean}; "
                    + $"Func<string?, string?> redact = ledger.Scrub; return {expression};");
                var error = Assert.Throws<InvalidOperationException>(() => Operations(path, source).ToArray());
                Assert.Contains("method-group/reference", error.Message, StringComparison.Ordinal);
            }
        }

        // Neither primitive may rely on the other capture being seen first.
        foreach (var body in new[] { "Func<string?, string?> redact = ledger.Scrub; return redact(raw);",
                     "Func<string?, string?> clean = SanitiseForDisplay; return clean(raw);" })
        {
            var source = CompiledSource(body);
            Assert.Throws<InvalidOperationException>(() => Operations(path, source).ToArray());
        }
    }

    [Fact]
    public void InventoryPreservesDuplicateOperations_InTheSameMethod()
    {
        var source = CompiledSource("var first = ledger.Scrub(raw); return ledger.Scrub(raw);");
        var operations = Operations("NewDiagnostic.cs", source).ToArray();
        Assert.Equal(2, operations.Length);
        Assert.Equal(operations[0], operations[1]);
        Assert.False(operations.Take(1).SequenceEqual(operations, StringComparer.Ordinal));
    }

    [Fact]
    public void InventoryAcceptsCompiledTypeAliasesAndDirectUsingStaticCalls()
    {
        var source = CompiledSource("var first = DS.SanitiseForDisplay(raw); "
            + "return SanitiseForDisplay(ledger.Scrub(first));");
        var operations = Operations("NewDiagnostic.cs", source).ToArray();
        Assert.Equal(3, operations.Length);
        Assert.Contains(operations, operation => operation.EndsWith(" | DS.SanitiseForDisplay(raw)", StringComparison.Ordinal));
        Assert.Contains(operations, operation => operation.EndsWith(" | SanitiseForDisplay(ledger.Scrub(first))", StringComparison.Ordinal));
    }

    private static string CompiledSource(string body)
    {
        var source = "using System; using Vouchfx.Engine.Abstractions; "
            + "using Vouchfx.Engine.Abstractions.Secrets; "
            + "using DS = Vouchfx.Engine.Abstractions.DisplaySanitiser; "
            + "using static Vouchfx.Engine.Abstractions.DisplaySanitiser; "
            + "class NewDiagnostic { string? Render(string? raw, ResolvedSecretLedger ledger) { " + body + " } }";
        var platformAssemblies = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var references = platformAssemblies.Split(Path.PathSeparator)
            .Append(typeof(DisplaySanitiser).Assembly.Location).Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("DiagnosticCensusMutation",
            new[] { CSharpSyntaxTree.ParseText(source) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        Assert.True(result.Success, string.Join('\n', result.Diagnostics));
        return source;
    }

    private static IEnumerable<string> Operations(string path, string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        Assert.DoesNotContain(root.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (!IsOperation(identifier.Identifier.ValueText))
            {
                continue;
            }

            // A qualified member's name and a bare using-static reference both reach here.
            // Require a direct invocation; conservatively refuse captures instead of trying
            // to follow delegate assignments, renamed invocations, or explicit .Invoke calls.
            SyntaxNode reference = identifier.Parent switch
            {
                MemberAccessExpressionSyntax member when member.Name == identifier => member,
                MemberBindingExpressionSyntax binding when binding.Name == identifier => binding,
                _ => identifier,
            };
            if (reference.Parent is not InvocationExpressionSyntax call || call.Expression != reference)
            {
                throw new InvalidOperationException(path + ": diagnostic method-group/reference requires review: " + reference);
            }
        }

        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = call.Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                _ => "",
            };
            if (!IsOperation(name))
            {
                continue;
            }

            var method = call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            Assert.NotNull(method);
            var type = method.Ancestors().OfType<TypeDeclarationSyntax>().First();
            // Preserve token text (including literal spaces), discard only formatting trivia.
            var expressionNode = call.Expression is MemberBindingExpressionSyntax
                ? call.Parent! : call;
            var expression = string.Concat(expressionNode.DescendantTokens().Select(token => token.Text));
            yield return path + ":" + type.Identifier.ValueText + "." + method.Identifier.ValueText + " | " + expression;
        }
    }

    private static bool IsOperation(string name)
        => name is "Scrub" or "SanitiseForDisplay" or "ScrubForDisplay" or "ScrubDiagnostic";

    private static Dictionary<string, string> Sources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "vouchfx.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var boundary in new[] { "src/Engine/Vouchfx.Engine.Runtime", "src/Cli/Vouchfx.Cli" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(directory.FullName, boundary), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(directory.FullName, path).Replace('\\', '/');
                if (!relative.Split('/').Any(part => part is "bin" or "obj"))
                {
                    sources.Add(relative, File.ReadAllText(path));
                }
            }
        }

        Assert.Contains(sources.Keys, path => path.EndsWith("/WatchRunner.cs", StringComparison.Ordinal));
        Assert.Contains(sources.Keys, path => path.EndsWith("/ScenarioRunner.cs", StringComparison.Ordinal));
        return sources;
    }
}
