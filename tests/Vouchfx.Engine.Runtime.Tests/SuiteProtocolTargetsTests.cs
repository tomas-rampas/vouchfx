// REQ-005 / REQ-011 — SuiteProtocolTargets, and SuiteTopology.StartAsync's no-accessor guard
// (authenticated-infrastructure-mtls, slice E).
//
// Non-Docker throughout. SuiteProtocolTargets is a pure function over an AST, and the guard it
// feeds fires at StartAsync's Step 0 — before EnvironmentMapper.Map and long before DCP — so both
// are provable without a container.
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Sdk;
using Vouchfx.Steps.MqPublish.Kafka;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// DECISION-1's inference: the confirmation level follows the protocol the suite's own STEPS will
/// speak, not the kind the target happens to be declared as.
/// </summary>
public sealed class SuiteProtocolTargetsTests
{
    private static readonly System.Reflection.Assembly[] ProviderAssemblies =
        new[]
        {
            typeof(MqPublishKafkaProvider).Assembly,
            typeof(Vouchfx.Steps.MqExpect.Kafka.MqExpectKafkaProvider).Assembly,
            typeof(Vouchfx.Steps.HttpRest.HttpRestProvider).Assembly,
        };

    private static readonly StepKindRegistry Registry = StepKindRegistry.BuildAndFreeze(ProviderAssemblies);

    // Expected-value arrays hoisted to fields (CA1861).
    private static readonly string[] OnlyKafkaBroker = new[] { "kafka-broker" };
    private static readonly string[] BothBrokers = new[] { "broker-a", "broker-b" };

    private static ScenarioAst Ast(string yaml) =>
        AstBuilder.Build(YamlDocumentParser.Parse(yaml), Registry);

    /// <summary>
    /// The shape REQ-011 exists for and the whole reason this inference was built: the customer's
    /// broker is a declared SERVICE, and a <c>mq-publish.kafka</c> step naming it is what says so.
    /// </summary>
    [Fact]
    public void KafkaSpeaking_ServiceTargetedByAPublishStep_IsIncluded()
    {
        var targets = SuiteProtocolTargets.KafkaSpeaking(Ast("""
            environment:
              services:
                kafka-broker:
                  image: acme/broker:1
                  ports: [9093]
            steps:
              - id: publish
                type: mq-publish.kafka
                target: kafka-broker
                topic: orders
                payload: "{}"
            """));

        Assert.Equal(OnlyKafkaBroker, targets.OrderBy(t => t, StringComparer.Ordinal));
    }

    /// <summary>
    /// <c>mq-expect.kafka</c> counts too — a consumer authenticates exactly as a producer does.
    /// </summary>
    [Fact]
    public void KafkaSpeaking_ServiceTargetedByAnExpectStep_IsIncluded()
    {
        var targets = SuiteProtocolTargets.KafkaSpeaking(Ast("""
            environment:
              services:
                kafka-broker:
                  image: acme/broker:1
                  ports: [9093]
            steps:
              - id: consume
                type: mq-expect.kafka
                target: kafka-broker
                topic: orders
                expect:
                  count: 1
            """));

        Assert.Contains("kafka-broker", targets);
    }

    /// <summary>
    /// Nothing is guessed: a target no Kafka step names contributes nothing, which is what keeps
    /// the probe from writing Kafka framing into a connection that might be HTTP.
    /// </summary>
    [Fact]
    public void KafkaSpeaking_TargetOfANonKafkaStep_IsNotIncluded()
    {
        var targets = SuiteProtocolTargets.KafkaSpeaking(Ast("""
            environment:
              services:
                api:
                  image: acme/api:1
            steps:
              - id: get
                type: http.rest
                target: api
                method: GET
                path: /
                expect:
                  status: 200
            """));

        Assert.Empty(targets);
    }

    /// <summary>
    /// A suite with no scenarios, or a scenario with no steps, yields the empty set rather than
    /// throwing — the probe's common path.
    /// </summary>
    [Fact]
    public void KafkaSpeaking_NullInputs_YieldTheEmptySet()
    {
        Assert.Empty(SuiteProtocolTargets.KafkaSpeaking((ScenarioAst?)null));
        Assert.Empty(SuiteProtocolTargets.KafkaSpeaking((IEnumerable<ScenarioAst?>?)null));
        Assert.Empty(SuiteProtocolTargets.KafkaSpeaking(new ScenarioAst?[] { null, null }));
    }

    /// <summary>
    /// A multi-scenario suite shares ONE topology, so the set is the UNION: a target any scenario
    /// speaks Kafka to is one the single shared probe must confirm as a broker.
    /// </summary>
    [Fact]
    public void KafkaSpeaking_ManyScenarios_UnionsTheirTargets()
    {
        const string environmentBlock = """
            environment:
              services:
                broker-a:
                  image: acme/broker:1
                  ports: [9093]
                broker-b:
                  image: acme/broker:1
                  ports: [9094]
            """;

        var first = Ast(environmentBlock + """

            steps:
              - id: publish
                type: mq-publish.kafka
                target: broker-a
                topic: orders
                payload: "{}"
            """);

        var second = Ast(environmentBlock + """

            steps:
              - id: publish
                type: mq-publish.kafka
                target: broker-b
                topic: orders
                payload: "{}"
            """);

        var targets = SuiteProtocolTargets.KafkaSpeaking(new ScenarioAst?[] { first, second });

        Assert.Equal(BothBrokers, targets.OrderBy(t => t, StringComparer.Ordinal));
    }

    // ── REQ-023 (amended): the HTTP half of the same inference, and the conflict ──────────

    /// <summary>
    /// The HTTP-family half: a target named by <c>http.rest</c> is one the engine must stage as a
    /// scheme-carrying URL, and it is reported independently of the Kafka set.
    /// </summary>
    [Fact]
    public void HttpSpeaking_TargetOfAnHttpStep_IsIncludedAndIsNotKafkaSpeaking()
    {
        var ast = Ast("""
            environment:
              services:
                api:
                  image: acme/api:1
            steps:
              - id: get
                type: http.rest
                target: api
                method: GET
                path: /
                expect:
                  status: 200
            """);

        Assert.Equal(OnlyApi, SuiteProtocolTargets.HttpSpeaking(new[] { (ScenarioAst?)ast }));
        Assert.Empty(SuiteProtocolTargets.KafkaSpeaking(ast));
        Assert.Empty(SuiteProtocolTargets.BothHttpAndKafkaSpeaking(new[] { (ScenarioAst?)ast }));
    }

    /// <summary>
    /// One service addressed by BOTH families is the conflict REQ-023's amendment creates and the
    /// validator rejects: the engine stages one value per target and the two families consume
    /// different shapes of it.
    /// </summary>
    [Fact]
    public void BothHttpAndKafkaSpeaking_ServiceAddressedByBothFamilies_IsReported()
    {
        var ast = Ast("""
            environment:
              services:
                broker:
                  image: acme/broker:1
                  ports: [9093]
            steps:
              - id: get
                type: http.rest
                target: broker
                method: GET
                path: /
                expect:
                  status: 200
              - id: publish
                type: mq-publish.kafka
                target: broker
                topic: orders
                payload: "{}"
            """);

        Assert.Equal(OnlyBroker, SuiteProtocolTargets.BothHttpAndKafkaSpeaking(new[] { (ScenarioAst?)ast }));
    }

    /// <summary>
    /// The conflict is per TARGET, not per suite: two different services, one addressed by each
    /// family, is the ordinary shape and must not be rejected.
    /// </summary>
    [Fact]
    public void BothHttpAndKafkaSpeaking_SeparateServicesPerFamily_IsNotAConflict()
    {
        var ast = Ast("""
            environment:
              services:
                api:
                  image: acme/api:1
                broker:
                  image: acme/broker:1
                  ports: [9093]
            steps:
              - id: get
                type: http.rest
                target: api
                method: GET
                path: /
                expect:
                  status: 200
              - id: publish
                type: mq-publish.kafka
                target: broker
                topic: orders
                payload: "{}"
            """);

        Assert.Empty(SuiteProtocolTargets.BothHttpAndKafkaSpeaking(new[] { (ScenarioAst?)ast }));
    }

    /// <summary>
    /// The drift guard behind <c>SuiteProtocolTargets</c>'s hand-written family lists: exactly the
    /// five step types those lists name may read <c>VarKeys.Service(model.Target)</c> in their
    /// emitted CSX. A sixth provider adopting that pattern without being classified would be
    /// staged in whatever form the suite's other steps happened to imply, and would silently stop
    /// conflicting with a step of the other family on the same target.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scans the provider SOURCES rather than restating the list, for the same reason
    /// <c>SecurityProfileRegistryTests</c> reads the emitted helpers' own switch arms: a second
    /// hand-maintained list here would be the very drift this test exists to catch.
    /// </para>
    /// <para>
    /// The five split into two groups and the split is the point. The three HTTP-family providers
    /// read that key UNCONDITIONALLY — a dependency target is rejected outright at validation
    /// (REQ-012 as narrowed) — so their targets are always staged as scheme-carrying URLs. The two
    /// Kafka providers read it CONDITIONALLY, only when the target names a declared service, and
    /// read <c>VarKeys.Connection</c> otherwise; their service targets are staged as bootstrap
    /// authorities. Only <c>VarKeys.Service(model.Target)</c> counts: a provider staging a HOST
    /// RESOURCE's own key (a <c>webhook-listen.http</c> listener, a <c>trace-expect.otlp</c>
    /// receiver) or a dependency's sidecar
    /// (<c>VarKeys.Service(avro.SchemaRegistryTarget + "-sr")</c>) is reading a name the engine
    /// itself minted, not the step's <c>target</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void ProtocolFamilyLists_CoverEverySvcKeyConsumingStepType()
    {
        var providersDirectory = Path.Combine(ResolveRepoRoot(), "src", "Providers");
        Assert.True(
            Directory.Exists(providersDirectory),
            $"Provider sources not found at '{providersDirectory}'; this guard cannot run.");

        var consuming = Directory
            .EnumerateFiles(providersDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                       StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                       StringComparison.Ordinal))
            .Where(path => ReadsAServiceKey(File.ReadAllText(path)))
            .Select(Path.GetFileNameWithoutExtension)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(SvcKeyConsumingProviderTypes, consuming);
    }

    /// <summary>
    /// Whether a provider source reads a <c>svc::</c> key in ANY of the spellings available to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately wider than the single literal <c>VarKeys.Service(model.Target)</c> this scan
    /// used to match. That spelling is a convention, not a rule: a provider writing
    /// <c>VarKeys.Service(model.Target!)</c>, <c>VarKeys.Service(target)</c>,
    /// <c>VarKeys.ServicesPrefix + …</c>, or a bare <c>"svc::"</c> inside a CSX string literal
    /// reads exactly the same key and passed the narrow match clean.
    /// </para>
    /// <para>
    /// The brittleness acquired a new consequence with #348. This census is the evidence for
    /// <c>SuiteProtocolTargets.EndpointConsuming</c>'s claim to cover every endpoint-consuming step
    /// type, and that set now decides whether an endpoint-less <c>project:</c>-form service is
    /// REFUSED or waved through. A sixth consumer slipping past this gate would silently narrow a
    /// refusal, not merely go uncounted.
    /// </para>
    /// </remarks>
    private static bool ReadsAServiceKey(string source)
    {
        var code = CodeLinesOnly(source);
        return code.Contains("VarKeys.Service(", StringComparison.Ordinal)
            || code.Contains("VarKeys.ServicesPrefix", StringComparison.Ordinal)
            || code.Contains("svc::", StringComparison.Ordinal);
    }

    /// <summary>
    /// <paramref name="source"/> with the two line shapes that mention <c>svc::</c> without
    /// reading it removed: whole-line comments, and JSON-schema <c>"description":</c> lines.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both exclusions are measured, not anticipated. Widening the match to a bare <c>svc::</c>
    /// pulled in a batch of provider files on prose alone — models and providers whose every
    /// mention was a file-header comment or a JSON-schema description. (No count is given here on
    /// purpose: it moves with each exclusion rule, and a stale number in this position would be
    /// the third wrong enumeration this gate's comments have carried.) Suppressing the OCCURRENCE
    /// rather than the FILE is what keeps those providers under live classification — a genuine
    /// <c>VarKeys.Service(model.Target)</c> added to any of them still reddens this gate, proven
    /// by mutation — and it means a future host-resource provider whose schema description
    /// mentions <c>svc::</c> never raises a false prompt at all.
    /// </para>
    /// <para>
    /// WHOLE-LINE comments only — a trailing <c>// …</c> after code is left alone on purpose.
    /// Stripping from the first <c>//</c> on any line would also cut a genuine
    /// <c>"http://"</c> string literal and everything after it, which could hide a real read on
    /// that line. The residual is a trailing comment mentioning <c>svc::</c> beside unrelated code,
    /// which makes this gate FAIL loudly and get looked at — the safe direction. A missed read is
    /// the unsafe one, and this ordering cannot produce it.
    /// </para>
    /// </remarks>
    private static string CodeLinesOnly(string source)
    {
        var kept = new List<string>();
        foreach (var line in source.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("/*", StringComparison.Ordinal)
                || trimmed.StartsWith('*')
                // A JSON-schema description line: author-facing PROSE that happens to live in a
                // string literal. Both surviving matches after comment-stripping were this shape.
                || trimmed.StartsWith("\"description\":", StringComparison.Ordinal))
            {
                continue;
            }

            kept.Add(line);
        }

        return string.Join('\n', kept);
    }

    /// <summary>
    /// The provider TYPE names behind the five step types <c>SuiteProtocolTargets</c> classifies —
    /// its three HTTP-family entries plus the two Kafka ones — spelled the way the source scan
    /// above finds them, and ordinally sorted to match its ordering.
    /// </summary>
    private static readonly string[] SvcKeyConsumingProviderTypes =
    {
        "HttpRestProvider",
        "HttpSoapProvider",
        "MetricsAssertPrometheusProvider",
        "MqExpectKafkaProvider",
        "MqPublishKafkaProvider",
    };

    private static readonly string[] OnlyApi = { "api" };

    private static readonly string[] OnlyBroker = { "broker" };

    private static readonly string[] ApiAndBroker = { "api", "broker" };

    // -----------------------------------------------------------------------
    // EndpointConsuming (#348) — the union that tells a refused authoring fault from a worker.
    // -----------------------------------------------------------------------

    /// <summary>
    /// <c>EndpointConsuming</c> is the union of both families: a suite addressing one service over
    /// HTTP and another over Kafka yields both names, because both will read a staged endpoint.
    /// </summary>
    /// <remarks>
    /// The union is what makes the mapper's #348 refusal correct in each direction. Keyed on the
    /// Kafka set alone it would miss an <c>http.rest</c> step; keyed on the HTTP set alone it
    /// would miss an <c>mq-publish.kafka</c> step naming a customer-supplied broker declared as a
    /// project-form service.
    /// </remarks>
    [Fact]
    public void EndpointConsuming_IsTheUnionOfTheHttpAndKafkaFamilies()
    {
        var ast = Ast("""
            environment:
              services:
                api:
                  image: acme/api:1
                broker:
                  image: acme/broker:1
                  ports: [9093]
            steps:
              - id: get
                type: http.rest
                target: api
                method: GET
                path: /
                expect:
                  status: 200
              - id: publish
                type: mq-publish.kafka
                target: broker
                topic: orders
                payload: "{}"
            """);

        Assert.Equal(
            ApiAndBroker,
            SuiteProtocolTargets.EndpointConsuming(ast).OrderBy(t => t, StringComparer.Ordinal));
    }

    /// <summary>
    /// A service NO step addresses is absent from the set — the property the mapper relies on to
    /// leave a worker service alone (#348).
    /// </summary>
    [Fact]
    public void EndpointConsuming_ServiceNoStepAddresses_IsAbsent()
    {
        var ast = Ast("""
            environment:
              services:
                api:
                  image: acme/api:1
                order-worker:
                  image: acme/worker:1
            steps:
              - id: get
                type: http.rest
                target: api
                method: GET
                path: /
                expect:
                  status: 200
            """);

        var targets = SuiteProtocolTargets.EndpointConsuming(ast);

        Assert.Equal(OnlyApi, targets.OrderBy(t => t, StringComparer.Ordinal));
        Assert.DoesNotContain("order-worker", targets);
    }

    /// <summary>
    /// A suite with no steps at all addresses nothing — the permissive answer, matching the
    /// <see langword="null"/> default both <c>EnvironmentMapper.Map</c> and
    /// <c>SuiteTopology.StartAsync</c> substitute an empty set for.
    /// </summary>
    [Fact]
    public void EndpointConsuming_NullScenario_IsEmpty() =>
        Assert.Empty(SuiteProtocolTargets.EndpointConsuming((ScenarioAst?)null));

    /// <summary>
    /// EVERY production call site of <c>SuiteTopology.StartAsync</c> passes BOTH target sets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A drift guard, not a style check, and it exists because nothing else can catch this. Both
    /// parameters are OPTIONAL and this project sets no <c>GenerateDocumentationFile</c>, so a
    /// call site that passes <c>kafkaSpeakingTargets</c> and forgets
    /// <c>endpointConsumingTargets</c> compiles clean, runs clean, and silently never refuses an
    /// endpoint-less targeted project-form service — the #348 defect back, on the path that
    /// forgot. <c>--watch</c> is the live example: it builds its own topology through this seam,
    /// so it needs its own pass-through.
    /// </para>
    /// <para>
    /// Scoped to <c>src/</c> deliberately. The ~60 Docker test call sites hand in an
    /// <c>EnvironmentSpec</c> directly and want the permissive default; requiring the argument
    /// there would be ceremony asserting something their own environments already state.
    /// </para>
    /// <para>
    /// Same source-scanning technique, and the same repo-root derivation, as
    /// <see cref="ProtocolFamilyLists_CoverEverySvcKeyConsumingStepType"/> directly above.
    /// </para>
    /// </remarks>
    [Fact]
    public void EverySuiteTopologyStartCallSite_PassesBothTargetSets()
    {
        var sourceDirectory = Path.Combine(ResolveRepoRoot(), "src");
        Assert.True(
            Directory.Exists(sourceDirectory),
            $"Engine sources not found at '{sourceDirectory}'; this guard cannot run.");

        var callSites = Directory
            .EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                       StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                       StringComparison.Ordinal)
                // The DECLARING file is excluded: it is not a call site, and its own <example>
                // shows the minimal two-argument form on purpose, both target sets being optional.
                && !string.Equals(Path.GetFileName(path), "SuiteTopology.cs", StringComparison.Ordinal))
            .Select(path => (Name: Path.GetFileName(path), Text: File.ReadAllText(path)))
            // Offsets computed ONCE per file and reused for both counts below.
            .Select(file => (file.Name, file.Text, Offsets: CallSiteOffsets(file.Text)))
            .Select(file => (
                file.Name,
                Calls: file.Offsets.Count,
                // COUNTED WITHIN EACH CALL'S OWN ARGUMENT LIST, not anywhere in the file. A
                // whole-file search inflates in the FALSE-PASS direction: a comment mentioning
                // `endpointConsumingTargets:` — and this fix added several — would satisfy the
                // guard for a call that never passes it.
                Passes: file.Offsets
                    .Count(offset => ArgumentWindow(file.Text, offset)
                        .Contains("endpointConsumingTargets:", StringComparison.Ordinal))))
            .Where(file => file.Calls > 0)
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToList();

        // COUNTED PER CALL, NOT PER FILE, and that is the point: ScenarioRunner.cs holds TWO call
        // sites (the per-scenario seam and the shared-topology suite seam), so a file-level check
        // would pass with one of them silently unwired. The guard is also worthless if the scan
        // finds nothing, so the expected total is asserted before anything is concluded from it.
        //
        // Assert.True, not Assert.Equal, purely so the count can carry guidance: a legitimate
        // FOURTH caller is a normal event, and `3 != 4` with no message tells whoever added it
        // nothing about what to do.
        var totalCalls = callSites.Sum(file => file.Calls);
        Assert.True(
            totalCalls == 3,
            $"Expected 3 production SuiteTopology.StartAsync call sites, found {totalCalls} in "
            + string.Join(", ", callSites.Select(f => $"{f.Name} x{f.Calls}"))
            + ". If you ADDED a caller: pass both kafkaSpeakingTargets and endpointConsumingTargets, "
            + "derived from the same scenarios, then update this expected count. If you REMOVED one, "
            + "just update the count.");

        var unwired = callSites
            .Where(file => file.Passes < file.Calls)
            .Select(file => $"{file.Name} ({file.Passes} of {file.Calls})")
            .ToList();

        Assert.True(
            unwired.Count == 0,
            "These SuiteTopology.StartAsync call sites pass kafkaSpeakingTargets but not "
            + "endpointConsumingTargets, so #348's refusal can never fire on their path: "
            + string.Join(", ", unwired));
    }

    /// <summary>The offset of every <c>SuiteTopology.StartAsync(</c> invocation in a source file.</summary>
    private static List<int> CallSiteOffsets(string source)
    {
        const string Needle = "SuiteTopology.StartAsync(";
        var offsets = new List<int>();
        var index = source.IndexOf(Needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            offsets.Add(index);
            index = source.IndexOf(Needle, index + Needle.Length, StringComparison.Ordinal);
        }

        return offsets;
    }

    /// <summary>
    /// The text of one call's argument list: from <paramref name="callOffset"/> to its matching
    /// close parenthesis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PARENTHESIS-depth counting rather than a fixed character budget, so a call that grows
    /// another argument does not silently fall outside the window.
    /// </para>
    /// <para>
    /// <c>//</c> comments are stripped from each line BEFORE counting, and that is load-bearing
    /// rather than tidiness. These argument lists carry multi-paragraph explanatory comments —
    /// <c>WatchRunner</c>'s window is roughly 1,700 characters, most of it prose — so a single
    /// unbalanced <c>(</c> inside one of them would run the window past the real close paren and
    /// let a LATER mention of the argument name satisfy the guard for a call that never passes it.
    /// That is the false-pass direction this windowing exists to close, and it was reachable, not
    /// theoretical.
    /// </para>
    /// <para>
    /// Unterminated input yields the remainder of the file. That is the FALSE-PASS direction — the
    /// same direction named above and, twenty lines up, in the guard's own comment — so it is a
    /// weakness, not a safe fallback. It is tolerable only because it cannot occur in a file that
    /// compiles, which is every file this scan reads.
    /// </para>
    /// </remarks>
    private static string ArgumentWindow(string source, int callOffset)
    {
        var open = source.IndexOf('(', callOffset);
        if (open < 0)
        {
            return string.Empty;
        }

        var code = WithoutTrailingComments(source[open..]);
        var depth = 0;
        for (var i = 0; i < code.Length; i++)
        {
            if (code[i] == '(')
            {
                depth++;
            }
            else if (code[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return code[..(i + 1)];
                }
            }
        }

        return code;
    }

    /// <summary>
    /// <paramref name="text"/> with each line truncated at its first <c>//</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately cruder than the file-level stripper above, and safe to be: this runs only on
    /// an argument-list window, where a <c>"http://…"</c> literal — the case that made the
    /// file-level version keep whole lines — does not appear at any of the call sites and could
    /// only cause the window to close EARLY, which is the false-FAIL direction.
    /// </remarks>
    private static string WithoutTrailingComments(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var marker = lines[i].IndexOf("//", StringComparison.Ordinal);
            if (marker >= 0)
            {
                lines[i] = lines[i][..marker];
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Walks up from the test assembly's output directory to the repository root — the same
    /// derivation <c>ExamplesCompileTests.ResolveRepoRoot</c> uses, and for the same reason.
    /// </summary>
    private static string ResolveRepoRoot()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(SuiteProtocolTargetsTests).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."));
    }
}
