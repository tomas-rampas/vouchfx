using System.Globalization;
using Vouchfx.Cli;
using Vouchfx.Cli.Watch;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Orchestration.Environments;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Vouchfx.Steps.Script.Csharp;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class ExternalEnvironmentWatchTests
{
    private static readonly StepKindRegistry s_kinds = StepKindRegistry.BuildAndFreeze(new[] { typeof(ScriptCsharpProvider).Assembly });
    private static readonly string[] s_endpoints = { "http" };
    private const string Yaml = """
        environment:
          source:
            driver: sandbox
            config: ./environment.yaml
        steps:
          - id: actual-script
            type: script.csharp
            code: |
              if ((string)Vars["svc::api"] != "sandbox-endpoint") throw new Exception("endpoint missing");
              if ((string)Vars["conn::db"] != "sandbox-connection") throw new Exception("connection missing");
        """;

    [Fact]
    public async Task NormalRegisteredDriver_TwoWatchRuns_ExecuteRealScriptWithFreshLeasesAndNoReset()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-driver-watch-").FullName;
        var config = Path.Combine(directory, "environment.yaml");
        File.WriteAllText(config, "first-content");
        var path = Path.Combine(directory, "watch.e2e.yaml");
        var driver = new SandboxDriver(config);
        var drivers = new EnvironmentDriverRegistry(new[] { driver });
        var output = new StringWriter();
        await using var session = WatchRunner.CreateSession(path, s_kinds, output, "missing-no-dcp-host",
            new ResolvedSecretLedger(), new SecurityPathDisclosureLedger(), environmentDrivers: drivers);
        await session.OnChangeAsync(Yaml, CancellationToken.None);
        File.WriteAllText(config, "second-content");
        await session.OnChangeAsync(Yaml, CancellationToken.None);
        Assert.Equal(2, driver.Leases.Count);
        Assert.Equal(2, driver.Leases.Select(l => l.Identity).Distinct().Count());
        Assert.All(driver.Leases, l => { Assert.Equal(1, l.DisposeCount); Assert.Equal(0, l.ResetCount); });
        Assert.Contains(config, session.InputFiles);
        Assert.DoesNotContain("FAIL", output.ToString(), StringComparison.Ordinal);
        // Each run renders its executed step and its scenario summary as PASS.
        Assert.Equal(4, output.ToString().Split("PASS", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void BoundInputContentAndStatePolicy_ChangeGenericWatchFingerprint()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-driver-identity-").FullName;
        var config = Path.Combine(directory, "environment.yaml");
        File.WriteAllText(config, "first-content");
        var driver = new SandboxDriver(config);
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(Yaml), s_kinds);
        var drivers = new EnvironmentDriverRegistry(new[] { driver });
        var first = WatchIterationPlan.Create(ast, Yaml, "watch", s_kinds, "missing-host", directory, drivers);
        File.WriteAllText(config, "second-content");
        var second = WatchIterationPlan.Create(ast, Yaml, "watch", s_kinds, "missing-host", directory, drivers);
        Assert.False(first.IsRefused);
        Assert.False(second.IsRefused);
        Assert.NotEqual(first.TopologyFingerprint, second.TopologyFingerprint);
        var reusable = new SandboxDriver(config) { Policy = EnvironmentStatePolicy.Inline };
        var third = WatchIterationPlan.Create(ast, Yaml, "watch", s_kinds, "missing-host", directory,
            new EnvironmentDriverRegistry(new[] { reusable }));
        Assert.False(third.IsRefused);
        Assert.NotEqual(second.TopologyFingerprint, third.TopologyFingerprint);
        Assert.Empty(driver.Leases);
    }

    [Fact]
    public async Task FileObservation_OutsideSuiteDirectory_EditSignalsWithoutScenarioEdit()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-driver-observation-").FullName;
        var config = Path.Combine(directory, "manifest.yaml");
        File.WriteAllText(config, "initial");
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new WatchInputFiles(() => changed.TrySetResult());
        watcher.Refresh(new[] { config });
        File.WriteAllText(config, "modified-content");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RefusedSourceConfig_IsStillObserved_NoAcquisitionOccurs()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-driver-refusal-watch-").FullName;
        var config = Path.Combine(directory, "environment.yaml");
        var driver = new SandboxDriver(config);
        await using var session = WatchRunner.CreateSession(Path.Combine(directory, "watch.e2e.yaml"), s_kinds,
            new StringWriter(), "missing-host", new ResolvedSecretLedger(), new SecurityPathDisclosureLedger(),
            environmentDrivers: new EnvironmentDriverRegistry(new[] { driver }));
        await session.OnChangeAsync(Yaml, CancellationToken.None);
        Assert.Empty(driver.Leases);
        Assert.Contains(config, session.InputFiles);
    }

    [Theory]
    [InlineData("malformed-manifest")]
    [InlineData("missing-manifest")]
    [InlineData("missing-explicit-env-file")]
    [InlineData("missing-service-env-file")]
    [InlineData("missing-manifest-directory")]
    [InlineData("missing-service-env-directory")]
    [InlineData("malformed-adapter-config")]
    public async Task InitiallyRefusedComposeInput_RepairSignalsActualWatcher_AndRebinds(string fault)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-initial-refusal-watch-").FullName;
        var config = Path.Combine(directory, "environment.compose.yaml");
        var manifestRelative = fault == "missing-manifest-directory" ? "./missing/native-compose.yaml" : "./native-compose.yaml";
        var envRelative = fault == "missing-service-env-directory" ? "./missing/missing.env" : "./missing.env";
        var manifest = Path.GetFullPath(manifestRelative, directory);
        var secondManifest = Path.Combine(directory, "second-compose.yaml");
        var envFile = Path.GetFullPath(envRelative, directory);
        const string validManifest = "services: { app: { image: app } }\n";
        var validConfig = "version: 1\nfiles: [" + manifestRelative + ", ./second-compose.yaml]\nprojectDirectory: .\n"
            + (fault == "missing-explicit-env-file" ? "envFiles: [./missing.env]\n" : "")
            + "services:\n  api: {service: app, port: 8080, readiness: {path: /health}}\n";
        const string yaml = "environment:\n  source: {driver: compose, config: ./environment.compose.yaml}\n"
            + "steps:\n  - id: actual-script\n    type: script.csharp\n    code: 'return;'\n";
        await File.WriteAllTextAsync(config, fault == "malformed-adapter-config" ? "files: [" : validConfig);
        await File.WriteAllTextAsync(secondManifest, "services: {}\n");
        if (fault is not ("missing-manifest" or "missing-manifest-directory"))
            await File.WriteAllTextAsync(manifest, fault switch
            {
                "malformed-manifest" => "services: [",
                "missing-service-env-file" or "missing-service-env-directory" => "services: { app: { image: app, env_file: " + envRelative + " } }\n",
                _ => validManifest,
            });
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), s_kinds);
        var refused = WatchIterationPlan.Create(ast, yaml, "initial-refusal", s_kinds, "missing-dcp-host", directory);
        Assert.True(refused.IsRefused);
        Assert.Contains(config, refused.InputFiles);
        Assert.NotEmpty(refused.EventLines);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new WatchInputFiles(() => changed.TrySetResult());
        watcher.Refresh(refused.InputFiles);
        var repairedPath = fault switch
        {
            "malformed-adapter-config" => config,
            "missing-explicit-env-file" or "missing-service-env-file" or "missing-service-env-directory" => envFile,
            _ => manifest,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(repairedPath)!);
        await File.WriteAllTextAsync(repairedPath, repairedPath == config ? validConfig
            : repairedPath == envFile ? "APP_MODE=test\n" : validManifest);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(repairedPath, refused.InputFiles);
        if (fault != "malformed-adapter-config") Assert.Contains(secondManifest, refused.InputFiles);
        var repaired = WatchIterationPlan.Create(ast, yaml, "initial-refusal", s_kinds, "missing-dcp-host", directory);
        Assert.False(repaired.IsRefused, repaired.Diagnostic);
        Assert.Contains(repairedPath, repaired.InputFiles);
        Assert.Empty(repaired.EventLines);
    }

    [Fact]
    public async Task RegisteredDriver_RefusalInputsRemainImmutable_EditRetriesNormalSession()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-neutral-refusal-watch-").FullName;
        var config = Path.Combine(directory, "environment.yaml");
        var required = Path.Combine(directory, "driver-input.yaml");
        await File.WriteAllTextAsync(config, "config-content");
        var originalInputs = new List<string> { config, required };
        var driver = new SandboxDriver(config) { RequiredInput = required, RefusalInputs = originalInputs };
        var output = new StringWriter();
        await using var session = WatchRunner.CreateSession(Path.Combine(directory, "watch.e2e.yaml"), s_kinds,
            output, "missing-host", new ResolvedSecretLedger(), new SecurityPathDisclosureLedger(),
            environmentDrivers: new EnvironmentDriverRegistry(new[] { driver }));
        await session.OnChangeAsync(Yaml, CancellationToken.None);
        Assert.Empty(driver.Leases);
        Assert.Contains("INCONCLUSIVE", output.ToString(), StringComparison.Ordinal);
        originalInputs.Clear();
        Assert.Contains(required, session.InputFiles);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new WatchInputFiles(() => changed.TrySetResult());
        watcher.Refresh(session.InputFiles);
        await File.WriteAllTextAsync(required, "repaired-input");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.OnChangeAsync(Yaml, CancellationToken.None);
        var lease = Assert.Single(driver.Leases);
        Assert.Equal(1, lease.DisposeCount);
        Assert.Equal(0, lease.ResetCount);
        Assert.Equal(2, output.ToString().Split("PASS", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task RegisteredDriver_PostBindTargetRefusal_InputEditSignalsActualWatcher_AndRebinds()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-post-bind-refusal-watch-").FullName;
        var config = Path.Combine(directory, "environment.yaml");
        var declarations = Path.Combine(directory, "declarations.yaml");
        await File.WriteAllTextAsync(config, "config-content");
        await File.WriteAllTextAsync(declarations, "undeclared");
        var driver = new SandboxDriver(config) { DeclarationInput = declarations };
        var drivers = new EnvironmentDriverRegistry(new[] { driver });
        var kinds = StepKindRegistry.BuildAndFreeze(new[] { typeof(HttpRestProvider).Assembly });
        const string yaml = """
            environment:
              source: {driver: sandbox, config: ./environment.yaml}
            steps:
              - id: target-check
                type: http.rest
                target: api
                method: GET
                path: /health
            """;
        var output = new StringWriter();
        await using var session = WatchRunner.CreateSession(Path.Combine(directory, "watch.e2e.yaml"), kinds,
            output, "missing-host", new ResolvedSecretLedger(), new SecurityPathDisclosureLedger(),
            environmentDrivers: drivers);
        await session.OnChangeAsync(yaml, CancellationToken.None);
        Assert.Empty(driver.Leases);
        Assert.Contains("INCONCLUSIVE", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("api", output.ToString(), StringComparison.Ordinal);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new WatchInputFiles(() => changed.TrySetResult());
        watcher.Refresh(session.InputFiles);
        await File.WriteAllTextAsync(declarations, "api");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(declarations, session.InputFiles);
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), kinds);
        var repaired = WatchIterationPlan.Create(ast, yaml, "target-check", kinds, "missing-host", directory, drivers);
        Assert.False(repaired.IsRefused, repaired.Diagnostic);
        Assert.Contains(declarations, repaired.InputFiles);
        Assert.Empty(driver.Leases);
    }

    [Theory]
    [InlineData(true, "external-driver security declarations")]
    [InlineData(false, "borrowed infrastructure")]
    public async Task RegisteredDriver_PostBindPolicyRefusal_KnownInputEditSignalsWithoutAcquisition(
        bool declaredSecurity, string diagnostic)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-policy-refusal-watch-").FullName;
        var config = Path.Combine(directory, "environment.yaml");
        var declarations = Path.Combine(directory, "declarations.yaml");
        await File.WriteAllTextAsync(config, "config-content");
        await File.WriteAllTextAsync(declarations, "api");
        var driver = new SandboxDriver(config)
        {
            DeclarationInput = declarations,
            DeclareSecurity = declaredSecurity,
            // Both restrictions apply to the security case: security retains precedence.
            Policy = new EnvironmentStatePolicy(EnvironmentOwnership.Borrowed, EnvironmentReusePolicy.Recreate, false),
        };
        var drivers = new EnvironmentDriverRegistry(new[] { driver });
        var output = new StringWriter();
        await using var session = WatchRunner.CreateSession(Path.Combine(directory, "watch.e2e.yaml"), s_kinds,
            output, "missing-host", new ResolvedSecretLedger(), new SecurityPathDisclosureLedger(),
            environmentDrivers: drivers);
        await session.OnChangeAsync(Yaml, CancellationToken.None);
        Assert.Empty(driver.Leases);
        Assert.Contains("INCONCLUSIVE", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(diagnostic, output.ToString(), StringComparison.Ordinal);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new WatchInputFiles(() => changed.TrySetResult());
        watcher.Refresh(session.InputFiles);
        await File.WriteAllTextAsync(declarations, "still-refused");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(declarations, session.InputFiles);
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(Yaml), s_kinds);
        var rebound = WatchIterationPlan.Create(ast, Yaml, "policy-check", s_kinds, "missing-host", directory, drivers);
        Assert.True(rebound.IsRefused);
        Assert.Contains(diagnostic, rebound.Diagnostic, StringComparison.Ordinal);
        Assert.Contains(declarations, rebound.InputFiles);
        Assert.Empty(driver.Leases);
    }

    [Fact]
    public async Task ExcessiveKnownInputs_RefuseWithBoundedPaths_ConfigRepairSignalsWatcher()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-bounded-refusal-watch-").FullName;
        var config = Path.Combine(directory, "environment.compose.yaml");
        var manifest = Path.Combine(directory, "native-compose.yaml");
        const string yaml = "environment:\n  source: {driver: compose, config: ./environment.compose.yaml}\n"
            + "steps:\n  - id: actual-script\n    type: script.csharp\n    code: 'return;'\n";
        const string binding = "services:\n  api: {service: app, port: 8080, readiness: {path: /health}}\n";
        var files = Enumerable.Range(0, EnvironmentBindingException.MaximumInputFiles + 1)
            .Select(index => "./missing-" + index.ToString(CultureInfo.InvariantCulture) + ".yaml");
        await File.WriteAllTextAsync(config, "version: 1\nfiles: [" + string.Join(", ", files) + "]\n" + binding);
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), s_kinds);
        var refused = WatchIterationPlan.Create(ast, yaml, "bounded-refusal", s_kinds, "missing-host", directory);
        Assert.True(refused.IsRefused);
        Assert.Contains("256-file authoring input limit", refused.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(EnvironmentBindingException.MaximumInputFiles, refused.InputFiles.Count);
        Assert.All(refused.InputFiles, input => Assert.True(Path.IsPathFullyQualified(input)));
        Assert.Contains(config, refused.InputFiles);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new WatchInputFiles(() => changed.TrySetResult());
        watcher.Refresh(refused.InputFiles);
        await File.WriteAllTextAsync(manifest, "services: {app: {image: app}}\n");
        await File.WriteAllTextAsync(config, "version: 1\nfiles: [./native-compose.yaml]\n" + binding);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var repaired = WatchIterationPlan.Create(ast, yaml, "bounded-refusal", s_kinds, "missing-host", directory);
        Assert.False(repaired.IsRefused, repaired.Diagnostic);
        Assert.Contains(manifest, repaired.InputFiles);
    }

    [Fact]
    public void PlaceholderPath_IsStillRefused_WithoutTrackingOrDisplayingSecretReference()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-path-refusal-watch-").FullName;
        var config = Path.Combine(directory, "environment.compose.yaml");
        const string reference = "${secret:env/DO_NOT_READ_THIS_VARIABLE}";
        File.WriteAllText(config, "version: 1\nfiles: ['" + reference + "']\n");
        const string yaml = "environment:\n  source: {driver: compose, config: ./environment.compose.yaml}\n"
            + "steps:\n  - id: actual-script\n    type: script.csharp\n    code: 'return;'\n";
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), s_kinds);
        var refused = WatchIterationPlan.Create(ast, yaml, "path-refusal", s_kinds, "missing-host", directory);
        Assert.True(refused.IsRefused);
        Assert.Equal(config, Assert.Single(refused.InputFiles));
        Assert.DoesNotContain(reference, refused.Diagnostic!, StringComparison.Ordinal);
        Assert.Contains("paths cannot contain placeholders or secrets", refused.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingParent_CreateOrRenameSignalsOnlyKnownAncestor(bool rename)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-parent-observation-").FullName;
        var parent = Path.Combine(directory, "inputs");
        var target = Path.Combine(parent, "deep", "manifest.yaml");
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new WatchInputFiles(() => changed.TrySetResult());
        watcher.Refresh(new[] { target });
        var sibling = Directory.CreateDirectory(Path.Combine(directory, "inputs-sibling")).FullName;
        await File.WriteAllTextAsync(Path.Combine(sibling, "manifest.yaml"), "unrelated-input");
        var staging = Directory.CreateDirectory(Path.Combine(directory, "staging")).FullName;
        await Task.WhenAny(changed.Task, Task.Delay(250));
        Assert.False(changed.Task.IsCompleted, "An unrelated sibling directory signalled a known input change.");
        if (rename) Directory.Move(staging, parent);
        else Directory.CreateDirectory(parent);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deeperChange = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deeperWatcher = new WatchInputFiles(() => deeperChange.TrySetResult());
        deeperWatcher.Refresh(new[] { target });
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "repaired-input");
        await deeperChange.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ComposeSchemaCommand_ExportsAdapterSchema_UnknownDriverRefuses()
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        Assert.Equal(0, SchemaCommand.Execute(null, output, errors, "compose"));
        Assert.Equal(ComposeEnvironmentDriver.ExportConfigurationSchema().Trim(), output.ToString().Trim());
        Assert.Equal(string.Empty, errors.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(2, SchemaCommand.Execute(null, output, errors, "unknown"));
        Assert.Equal(string.Empty, output.ToString());
    }

    private sealed class SandboxDriver : IEnvironmentDriver
    {
        private readonly string _config;
        internal SandboxDriver(string config) => _config = config;
        public string Id => "sandbox";
        internal EnvironmentStatePolicy Policy { get; init; } = EnvironmentStatePolicy.FreshOwned;
        internal string? RequiredInput { get; init; }
        internal string? DeclarationInput { get; init; }
        internal bool DeclareSecurity { get; init; }
        internal IReadOnlyList<string>? RefusalInputs { get; init; }
        internal List<Lease> Leases { get; } = new();
        public EnvironmentPlan Bind(EnvironmentSourceSpec source, string suiteDirectory)
        {
            if (!File.Exists(_config)) throw new ArgumentException("sandbox configuration is missing");
            if (RequiredInput is { } required && !File.Exists(required))
                throw new EnvironmentBindingException("sandbox required authoring input is missing", RefusalInputs ?? new[] { _config, required });
            var declarations = DeclarationInput is { } input ? File.ReadAllText(input) : "api";
            return new EnvironmentPlan(new EnvironmentDeclarations(
                declarations == "api" ? new Dictionary<string, DeclaredServiceInfo> { ["api"] = new(s_endpoints) }
                    : new Dictionary<string, DeclaredServiceInfo>(),
                new Dictionary<string, string> { ["db"] = "postgres" }), File.ReadAllText(_config) + declarations, Policy,
                (_, _) =>
                {
                    var lease = new Lease();
                    Leases.Add(lease);
                    return Task.FromResult<IKeptTopology>(lease);
                }, declaredSecurity: DeclareSecurity
                    ? new[] { new SecuredTarget("api", "service", new SecuritySpec("tls", "http", null, null, null, null)) }
                    : null, inputFiles: DeclarationInput is { } declarationInput ? new[] { _config, declarationInput } : new[] { _config });
        }
    }

    private sealed class Lease : IKeptTopology
    {
        internal Guid Identity { get; } = Guid.NewGuid();
        internal int DisposeCount { get; private set; }
        internal int ResetCount { get; private set; }
        public IReadOnlyList<SecurityConfirmation> SecurityConfirmations => Array.Empty<SecurityConfirmation>();
        public IReadOnlyList<EndpointSelectionNotice> EndpointSelectionNotices => Array.Empty<EndpointSelectionNotice>();
        public IReadOnlyList<EndpointTrustNotice> EndpointTrustNotices => Array.Empty<EndpointTrustNotice>();
        public IReadOnlyDictionary<string, object> DiscoveredServices { get; } = new Dictionary<string, object>
        {
            ["api"] = "sandbox-endpoint",
            ["db"] = "sandbox-connection",
        };
        public IReadOnlyList<string> DependencyNames { get; } = new[] { "db" };
        public IReadOnlyDictionary<string, string> DependencyTypes { get; } = new Dictionary<string, string> { ["db"] = "postgres" };
        public Task ReseedAsync(CancellationToken cancellationToken) { ResetCount++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }
}
