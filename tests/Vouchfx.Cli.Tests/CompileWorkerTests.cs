using System.CommandLine;
using System.Diagnostics;
using Vouchfx.Engine.Compilation;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class CompileWorkerTests
{
    [Theory]
    [InlineData("unary")]
    [InlineData("generic")]
    [InlineData("interpolation")]
    public async Task Validate_isolation_survives_hostile_bodies_with_a_bounded_result(string kind)
    {
        var body = kind switch
        {
            "unary" => "var x = " + new string('!', 2200) + "true;",
            "generic" => string.Concat(Enumerable.Repeat("System.Collections.Generic.List<", 701))
                + "int" + new string('>', 701) + " x = null;",
            _ => "var x = " + Enumerable.Range(0, 24).Aggregate("0", (value, _) => "$\"{" + value + "}\"") + ";",
        };
        var path = Path.Combine(Path.GetTempPath(), "vouchfx-isolated-" + Guid.NewGuid().ToString("N") + ".e2e.yaml");
        try
        {
            await File.WriteAllTextAsync(path, "steps:\n  - id: hostile\n    type: script.csharp\n    code: |\n      " + body + "\n");
            var clock = Stopwatch.StartNew();
            var result = await ValidateCommand.Build().Parse(new[] { path, "--isolate-compile" }).InvokeAsync();
            // Stack size and Roslyn's recursion behaviour vary by OS. A valid emitted image is
            // also a safe outcome: isolation guarantees bounded survival, not compiler failure.
            Assert.True(result is ExitCodes.Success or ExitCodes.Inconclusive);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(45));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Worker_returns_emitted_image_without_executing_script()
    {
        var compiled = await CompileWorkerMode.CompileAsync("throw new System.Exception();", null, CancellationToken.None);
        Assert.NotEmpty(compiled.Image);
    }

    [Fact]
    public async Task Invalid_source_does_not_echo_source_or_child_diagnostics()
    {
        var failure = await Assert.ThrowsAsync<ScriptCompilationException>(() =>
            CompileWorkerMode.CompileAsync("secret_12345 is not C#", null, CancellationToken.None));
        Assert.DoesNotContain("secret_12345", failure.ToString());
        Assert.Contains("compilation failed", failure.Message);
    }

    [Theory]
    [InlineData("unary")]
    [InlineData("generic")]
    public async Task Crash_prone_source_cannot_terminate_parent(string kind)
    {
        var source = kind == "unary"
            ? "var x = " + new string('!', 2200) + "true;"
            : "System.Collections.Generic.List<" + string.Concat(Enumerable.Repeat("System.Collections.Generic.List<", 700))
                + "int" + new string('>', 701) + " x = null;";
        var pid = 0;
        var clock = Stopwatch.StartNew();
        try
        {
            var compiled = await CompileWorkerMode.CompileAsync(
                source, null, CancellationToken.None, value => pid = value);
            Assert.NotEmpty(compiled.Image);
        }
        catch (Exception failure) when (failure is CompileWorkerException or ScriptCompilationException)
        {
            Assert.DoesNotContain(source, failure.ToString());
        }
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(45));
        AssertExited(pid);
    }

    [Fact]
    public async Task Forced_worker_exit_is_reported_without_exposing_child_output()
    {
        var pid = 0;
        var failure = await Assert.ThrowsAsync<CompileWorkerException>(() => CompileWorkerMode.CompileAsync(
            "var secret_12345 = 1;", null, CancellationToken.None, value =>
            {
                pid = value;
                using var worker = Process.GetProcessById(value);
                worker.Kill(entireProcessTree: true);
            }));
        Assert.DoesNotContain("secret_12345", failure.ToString());
        AssertExited(pid);
    }

    [Fact]
    public async Task Lifecycle_budget_expiry_kills_worker_and_reports_fixed_diagnostic()
    {
        var pid = 0;
        var clock = Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<CompileWorkerException>(() => CompileWorkerMode.CompileAsync(
            "1", null, CancellationToken.None, value =>
            {
                pid = value;
                // The budget includes request delivery, independently of Roslyn/platform behaviour.
                Thread.Sleep(TimeSpan.FromMilliseconds(1200));
            }, compileBudget: TimeSpan.FromSeconds(1)));
        Assert.Contains("compile budget", failure.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        AssertExited(pid);
    }

    [Fact]
    public async Task Parse_hang_is_cancelled_and_worker_exit_is_confirmed()
    {
        var expression = "0";
        for (var i = 0; i < 24; i++)
            expression = "$\"{" + expression + "}\"";
        using var cancellation = new CancellationTokenSource();
        var pid = 0;
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CompileWorkerMode.CompileAsync(
            "var x = " + expression + ";", null, cancellation.Token, value =>
            {
                pid = value;
                cancellation.CancelAfter(TimeSpan.FromSeconds(1));
            }));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15));
        AssertExited(pid);
    }

    [Fact]
    public async Task Compiler_scope_flows_to_child_tasks_and_restores_without_cross_run_leakage()
    {
        var image = new CompiledScript(new byte[] { 1 });
        using (new ScriptCompilerScope((_, _, _) => image))
        {
            Assert.Same(image, await Task.Run(() => ScriptCompilerScope.Compile("", null, CancellationToken.None)));
            var other = new CompiledScript(new byte[] { 2 });
            using (new ScriptCompilerScope((_, _, _) => other))
            {
                Assert.Same(other, ScriptCompilerScope.Compile("", null, CancellationToken.None));
            }
            Assert.Same(image, ScriptCompilerScope.Compile("", null, CancellationToken.None));
            await Task.Run(() =>
            {
                using var nested = new ScriptCompilerScope((_, _, _) => other);
                Assert.Same(other, ScriptCompilerScope.Compile("", null, CancellationToken.None));
            });
            Assert.Same(image, ScriptCompilerScope.Compile("", null, CancellationToken.None));
        }
        var disposed = new ScriptCompilerScope((_, _, _) => image);
        disposed.Dispose();
        using (new ScriptCompilerScope((_, _, _) => image))
        {
            disposed.Dispose();
            Assert.Same(image, ScriptCompilerScope.Compile("", null, CancellationToken.None));
        }
        Assert.NotEmpty(ScriptCompilerScope.Compile("1", null, CancellationToken.None).Image);
    }

    private static void AssertExited(int pid)
    {
        Assert.True(pid > 0);
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.HasExited);
        }
        catch (ArgumentException)
        {
            // An absent process is the usual outcome after the worker handle is closed.
        }
    }
}
