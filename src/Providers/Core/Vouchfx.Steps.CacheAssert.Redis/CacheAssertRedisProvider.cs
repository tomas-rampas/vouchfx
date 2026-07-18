// Vouchfx.Steps.CacheAssert.Redis — cache-assert.redis step provider (DSL §5, §13).
//
// The first member of the cache-assert family (the same new-family precedent
// mail-expect.smtp established).  Steps always name the dotted form
// (`cache-assert.redis`) — bare family names are not part of the language.
//
// One [StepProvider] class implements the six provider interfaces plus two optional
// extension interfaces (ICompileReferenceContributor, IStepDiffRenderer):
//   IStepProvider, IStepBinder<T>, IStepValidator<T>, IStepCompiler<T>,
//   IResourceContributor<T>, ICompileReferenceContributor, IStepDiffRenderer.
//
// Memory model (§5): the emitted helper builds a StackExchange.Redis ConnectionMultiplexer
// per invocation and Dispose()s it in a finally.  The multiplexer owns a heartbeat timer,
// a socket, and a reconnect thread; it MUST be disposed before the collectible
// AssemblyLoadContext unloads, or those handles would pin the context.  'using var' is
// prohibited in a Roslyn script body (§13.3.1) — plain var + explicit Dispose() in finally.
// StackExchange.Redis is contributed as a compile-time MetadataReference via
// ICompileReferenceContributor; it is already loaded in the Default ALC (the provider
// project references it) and is NEVER loaded into the collectible ALC.
//
// Substitution model (S04-B-03): the key, hash field, and expected value are emitted as
// RAW template literals and resolved at EXECUTION time inside the helper via
// Substitute_Helpers.Resolve — {placeholder} tokens survive verbatim in the emitted IL,
// so no captured/variable value is ever baked into the compiled delegate (§17, §5).
//
// TTL design choice (documented in the schema): the `ttl` operation asserts that the key
// HAS (or has NOT) a positive time-to-live — KeyTimeToLive returning a non-null, strictly
// positive TimeSpan → treated as Exists=true.  Exact or lower-bound TTL assertions are
// inherently time-sensitive and would flake in CI, so `ttl` uses the boolean `exists`
// expectation member, never `length`.
//
// CsxFragment rules (§13.3.1):
//   • RequiredUsings: bare namespace strings only (no inline 'using' lines).
//   • RequiredHelpers: 'static class CacheAssertRedis_Helpers' plus the shared
//     Substitute_Helpers source (byte-identical; CsxAssembler dedupes by class name).
//   • StatementBlock: a single C# 11 $$"""…""" block with {{expr}} interpolation holes;
//     no 'using var'; step id sanitised via CsxFragment.SanitiseId before splicing.
using System.Globalization;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Sdk;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.CacheAssert.Redis;

/// <summary>
/// Core provider for the <c>cache-assert.redis</c> step kind (DSL §5).
/// Performs a single Redis read operation (GET / EXISTS / TTL / HGET / HLEN / LLEN / SCARD)
/// against a declared <c>redis</c> dependency and asserts on the returned value, key
/// presence, or length.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="SchemaFragment"/> describes the provider's own fields only.  The engine's
/// <c>SchemaComposer</c> assembles the unified schema by injecting a <c>const</c>-keyed
/// <c>if</c>/<c>then</c> discriminator derived from <see cref="Kind"/> — the fragment text
/// never repeats that discriminator (§13.6).
/// </para>
/// <para>
/// The <see cref="Emit"/> method produces a <see cref="CsxFragment"/> whose emitted CSX
/// opens a <see cref="StackExchange.Redis.ConnectionMultiplexer"/> keyed by
/// <c>VarKeys.Connection(model.Target)</c>, runs the operation, evaluates the expectation,
/// and writes a typed <see cref="StepOutcome"/> into
/// <c>Vars[VarKeys.Outcome(sanitisedStepId)]</c> for the runner to read after the script
/// returns (§13.3.1).  The multiplexer is disposed in a <c>finally</c> so its heartbeat
/// timer, socket, and reconnect thread are released before the collectible ALC unloads (§5).
/// </para>
/// <para>
/// <strong>TTL design choice.</strong> The <c>ttl</c> operation asserts that the key HAS
/// (or has NOT) a positive time-to-live, evaluated via the boolean <c>expect.exists</c>
/// member.  Exact or lower-bound TTL assertions are time-sensitive and would flake in CI,
/// so this provider deliberately offers only a presence check for TTL.
/// </para>
/// </remarks>
[StepProvider]
public sealed class CacheAssertRedisProvider
    : IStepProvider,
      IStepBinder<CacheAssertRedisModel>,
      IStepValidator<CacheAssertRedisModel>,
      IStepCompiler<CacheAssertRedisModel>,
      IResourceContributor<CacheAssertRedisModel>,
      ICompileReferenceContributor,
      IStepDiffRenderer
{
    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("cache-assert", "redis");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<CacheAssertRedisModel> ────────────────────────────────────

    /// <summary>
    /// Gets the JSON Schema fragment that describes the <c>cache-assert.redis</c>
    /// provider's own fields.
    /// </summary>
    /// <remarks>
    /// The fragment does NOT include the <c>type</c> const discriminator — the
    /// <c>SchemaComposer</c> derives that from <see cref="Kind"/> and injects it as an
    /// <c>if</c>/<c>then</c> clause (§13.6).
    /// </remarks>
    public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
        """
        {
          "type": "object",
          "required": ["target", "key", "operation", "expect"],
          "properties": {
            "target": {
              "description": "Logical name of the redis dependency to inspect, as declared under environment.dependencies.",
              "type": "string"
            },
            "key": {
              "description": "The Redis key to inspect.  May contain {placeholder} tokens resolved at step-execution time.",
              "type": "string"
            },
            "operation": {
              "description": "The Redis read operation. get/hget assert on 'expect.value'; exists asserts on 'expect.exists'; ttl asserts on 'expect.exists' as a has-a-positive-TTL presence check (exact/lower-bound TTLs are deliberately unsupported to avoid CI flakiness); hlen/llen/scard assert on 'expect.length'. Values are case-insensitive.",
              "type": "string",
              "enum": ["get", "exists", "ttl", "hget", "hlen", "llen", "scard"]
            },
            "field": {
              "description": "The hash field name for the hget operation (required for hget only).  May contain {placeholder} tokens.",
              "type": "string"
            },
            "expect": {
              "description": "Assertion block. Exactly one member applies per operation: value (get/hget), exists (exists/ttl), or length (hlen/llen/scard).",
              "type": "object",
              "properties": {
                "value": {
                  "description": "Expected string value for get/hget.  May contain {placeholder} tokens resolved at step-execution time.",
                  "type": "string"
                },
                "exists": {
                  "description": "Expected key presence for exists, or expected has-a-positive-TTL presence for ttl.",
                  "type": "boolean"
                },
                "length": {
                  "description": "Expected length / cardinality for hlen (hash length), llen (list length), or scard (set cardinality).",
                  "type": "integer"
                }
              },
              "additionalProperties": false
            }
          },
          "additionalProperties": true
        }
        """);

    /// <inheritdoc />
    public CacheAssertRedisModel Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new CacheAssertRedisModel(
                Target: string.Empty,
                Key: string.Empty,
                Operation: RedisOp.Get,
                Field: null,
                Expect: new RedisExpectation(Value: null, Exists: null, Length: null));
        }

        var target = GetScalar(mapping, "target");
        var key = GetScalar(mapping, "key");
        var field = GetOptionalScalar(mapping, "field");
        var operation = ParseOperation(GetScalar(mapping, "operation"));
        var expect = BindExpectation(mapping);

        return new CacheAssertRedisModel(
            Target: target,
            Key: key,
            Operation: operation,
            Field: field,
            Expect: expect);
    }

    /// <summary>
    /// Parses the <c>operation</c> scalar into a <see cref="RedisOp"/> (case-insensitive).
    /// Unparseable / absent values default to <see cref="RedisOp.Get"/>; the schema layer
    /// rejects unknown values at author time.
    /// </summary>
    private static RedisOp ParseOperation(string raw) =>
        Enum.TryParse<RedisOp>(raw, ignoreCase: true, out var op) ? op : RedisOp.Get;

    private static RedisExpectation BindExpectation(YamlMappingNode mapping)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode("expect"), out var expectNode)
            || expectNode is not YamlMappingNode expectMap)
        {
            return new RedisExpectation(Value: null, Exists: null, Length: null);
        }

        var value = GetOptionalScalar(expectMap, "value");

        bool? exists = null;
        if (expectMap.Children.TryGetValue(new YamlScalarNode("exists"), out var existsNode)
            && existsNode is YamlScalarNode existsScalar
            && bool.TryParse(existsScalar.Value, out var parsedExists))
        {
            exists = parsedExists;
        }

        long? length = null;
        if (expectMap.Children.TryGetValue(new YamlScalarNode("length"), out var lengthNode)
            && lengthNode is YamlScalarNode lengthScalar
            && long.TryParse(lengthScalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLength))
        {
            length = parsedLength;
        }

        return new RedisExpectation(Value: value, Exists: exists, Length: length);
    }

    // ── IStepValidator<CacheAssertRedisModel> ─────────────────────────────────

    /// <inheritdoc />
    public ValidationResult Validate(CacheAssertRedisModel model, IProjectContext ctx)
    {
        var errors = new List<string>();

        // (a) target must not be empty.
        if (string.IsNullOrWhiteSpace(model.Target))
            errors.Add("cache-assert.redis: 'target' must not be empty.");

        // (b) key must not be empty.
        if (string.IsNullOrWhiteSpace(model.Key))
            errors.Add("cache-assert.redis: 'key' must not be empty.");

        // (c) field is required for the hget operation, and meaningless otherwise.
        if (model.Operation == RedisOp.HGet && string.IsNullOrWhiteSpace(model.Field))
            errors.Add("cache-assert.redis: 'field' is required for operation 'hget'.");

        // (d) the expectation member must match the operation.
        switch (model.Operation)
        {
            case RedisOp.Get:
            case RedisOp.HGet:
                if (model.Expect.Value is null)
                    errors.Add($"cache-assert.redis: operation '{OpToken(model.Operation)}' requires 'expect.value'.");
                break;

            case RedisOp.Exists:
                if (model.Expect.Exists is null)
                    errors.Add("cache-assert.redis: operation 'exists' requires 'expect.exists'.");
                break;

            case RedisOp.Ttl:
                // TTL design choice: assert has-a-positive-TTL presence via expect.exists.
                if (model.Expect.Exists is null)
                    errors.Add("cache-assert.redis: operation 'ttl' requires 'expect.exists' (a has-a-positive-TTL presence check; exact/lower-bound TTLs are unsupported).");
                break;

            case RedisOp.HLen:
            case RedisOp.LLen:
            case RedisOp.SCard:
                if (model.Expect.Length is null)
                    errors.Add($"cache-assert.redis: operation '{OpToken(model.Operation)}' requires 'expect.length'.");
                break;
        }

        // (e) dependency reconciliation: target must name a declared redis dependency.
        if (!string.IsNullOrWhiteSpace(model.Target))
        {
            if (!ctx.DeclaredDependencies.TryGetValue(model.Target, out var depType))
            {
                errors.Add(
                    $"cache-assert.redis: 'target' '{model.Target}' is not a " +
                    "redis dependency declared in environment.dependencies.");
            }
            else if (!string.Equals(depType, "redis", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"cache-assert.redis: 'target' '{model.Target}' is not a " +
                    "redis dependency declared in environment.dependencies.");
            }
        }

        return errors.Count == 0
            ? ValidationResult.Success
            : ValidationResult.Failure(errors.ToArray());
    }

    // ── CsxFragment components ────────────────────────────────────────────────

    /// <summary>
    /// Required namespaces for the emitted step block.  Bare strings only (§13.3.1).
    /// </summary>
    private static readonly IReadOnlyList<string> s_usings =
        new[]
        {
            "System",
            "System.Collections.Generic",
            "System.Diagnostics",
            "Vouchfx.Engine.Abstractions",
        };

    /// <summary>
    /// Full source of the provider-id-prefixed helper class (§13.3.1).
    /// <para>
    /// The class name begins with <c>CacheAssertRedis_</c> to prevent collisions when
    /// multiple providers contribute helpers to the same Roslyn submission.  All types are
    /// fully-qualified so the helper compiles independently of the spliced <c>using</c>
    /// ordering.  <c>using var</c> is absent — the multiplexer is disposed explicitly in a
    /// <c>finally</c> (§5, §13.3.1).
    /// </para>
    /// <para>
    /// The helper is byte-identical across every instance of the same provider within a
    /// suite (§13.3.1 dedup rule); it contains no per-step interpolation.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyList<string> s_helpers = new[]
    {
        """
        static class CacheAssertRedis_Helpers
        {
            // Executes a single Redis read operation, evaluates the expectation, and writes a
            // typed StepOutcome into Vars.
            //   • Missing/empty connection string, connection failure, timeout, or any other
            //     Redis error = EnvironmentError (§12.1) — the connection string (which may
            //     carry a password=/user= token) is redacted from every observation (§17).
            //   • value/exists/ttl/length mismatch = Fail.
            //   • match = Pass.
            // The ConnectionMultiplexer owns a heartbeat timer + socket + reconnect thread and
            // MUST be Disposed before the collectible ALC unloads (§5) — plain var + explicit
            // Dispose() in finally (the using-declaration form is illegal in CSX, §13.3.1).
            public static void Execute(
                System.Collections.Generic.IDictionary<string, object?> vars,
                string outcomeKey,
                string connKey,
                string op,
                string keyTemplate,
                string? fieldTemplate,
                string? expectValueTemplate,
                bool? expectExists,
                long? expectLength,
                System.Threading.CancellationToken ct)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Vouchfx.Engine.Abstractions.Verdict verdict;
                string observation;
                var connStr = vars.TryGetValue(connKey, out var c) && c is string s ? s : null;
                if (string.IsNullOrEmpty(connStr))
                {
                    sw.Stop();
                    vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(
                        Vouchfx.Engine.Abstractions.Verdict.EnvironmentError,
                        sw.ElapsedMilliseconds,
                        "{\"error\":" + System.Text.Json.JsonSerializer.Serialize("connection string not found for key '" + connKey + "'") + "}");
                    return;
                }
                // {placeholder} substitution at EXECUTION time (§17): key/field/expected value
                // are RAW templates; no captured value is ever baked into the emitted IL.
                var key = Substitute_Helpers.Resolve(vars, keyTemplate);
                var field = fieldTemplate is null ? null : Substitute_Helpers.Resolve(vars, fieldTemplate);
                var expectValue = expectValueTemplate is null ? null : Substitute_Helpers.Resolve(vars, expectValueTemplate);
                StackExchange.Redis.ConnectionMultiplexer? mux = null;
                try
                {
                    mux = StackExchange.Redis.ConnectionMultiplexer.Connect(connStr);
                    var db = mux.GetDatabase();
                    string? failObservation = null;
                    switch (op)
                    {
                        case "get":
                        {
                            var val = db.StringGet(key);
                            var actual = val.IsNull ? null : val.ToString();
                            if (!string.Equals(actual, expectValue, System.StringComparison.Ordinal))
                                failObservation = "{\"value\":{\"expected\":" + JsonOrNull(expectValue) + ",\"actual\":" + JsonOrNull(actual) + "}}";
                            break;
                        }
                        case "hget":
                        {
                            var val = db.HashGet(key, field);
                            var actual = val.IsNull ? null : val.ToString();
                            if (!string.Equals(actual, expectValue, System.StringComparison.Ordinal))
                                failObservation = "{\"value\":{\"expected\":" + JsonOrNull(expectValue) + ",\"actual\":" + JsonOrNull(actual) + "}}";
                            break;
                        }
                        case "exists":
                        {
                            var actual = db.KeyExists(key);
                            var expected = expectExists ?? false;
                            if (actual != expected)
                                failObservation = "{\"exists\":{\"expected\":" + (expected ? "true" : "false") + ",\"actual\":" + (actual ? "true" : "false") + "}}";
                            break;
                        }
                        case "ttl":
                        {
                            var ttl = db.KeyTimeToLive(key);
                            var actual = ttl.HasValue && ttl.Value > System.TimeSpan.Zero;
                            var expected = expectExists ?? false;
                            if (actual != expected)
                                failObservation = "{\"ttl\":{\"expected\":" + (expected ? "true" : "false") + ",\"actual\":" + (actual ? "true" : "false") + "}}";
                            break;
                        }
                        case "hlen":
                        {
                            var actual = db.HashLength(key);
                            var expected = expectLength ?? 0L;
                            if (actual != expected)
                                failObservation = "{\"length\":{\"expected\":" + expected.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"actual\":" + actual.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}";
                            break;
                        }
                        case "llen":
                        {
                            var actual = db.ListLength(key);
                            var expected = expectLength ?? 0L;
                            if (actual != expected)
                                failObservation = "{\"length\":{\"expected\":" + expected.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"actual\":" + actual.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}";
                            break;
                        }
                        case "scard":
                        {
                            var actual = db.SetLength(key);
                            var expected = expectLength ?? 0L;
                            if (actual != expected)
                                failObservation = "{\"length\":{\"expected\":" + expected.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"actual\":" + actual.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}";
                            break;
                        }
                        default:
                            throw new System.InvalidOperationException("unknown cache-assert.redis operation '" + op + "'.");
                    }
                    if (failObservation is not null)
                    {
                        verdict = Vouchfx.Engine.Abstractions.Verdict.Fail;
                        observation = failObservation;
                    }
                    else
                    {
                        verdict = Vouchfx.Engine.Abstractions.Verdict.Pass;
                        observation = "{\"matched\":true,\"op\":" + System.Text.Json.JsonSerializer.Serialize(op) + "}";
                    }
                }
                catch (StackExchange.Redis.RedisConnectionException ex)
                {
                    // Connection failure = EnvironmentError (§12.1).  Redact credentials (§17).
                    verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;
                    observation = "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(RedactCredentials(connStr ?? string.Empty, ex.Message)) + "}";
                }
                catch (StackExchange.Redis.RedisTimeoutException ex)
                {
                    // Operation timeout = EnvironmentError (§12.1).  Redact credentials (§17).
                    verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;
                    observation = "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(RedactCredentials(connStr ?? string.Empty, ex.Message)) + "}";
                }
                catch (System.OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Step-token cut (#232): rethrow past this provider's own error handling so
                    // the assembler's wrapper classifies it as Inconclusive(step-timeout) instead
                    // of the generic-error branch below misclassifying it.  The redis client's
                    // synchronous API does not observe 'ct' internally (StackExchange.Redis has
                    // no CancellationToken overloads) — this filter is defensive, guarding
                    // against a future call path that does.
                    throw;
                }
                catch (System.Exception ex)
                {
                    // Any other connection/protocol/parse failure = EnvironmentError (§12.1).
                    verdict = Vouchfx.Engine.Abstractions.Verdict.EnvironmentError;
                    observation = "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(RedactCredentials(connStr ?? string.Empty, ex.Message)) + "}";
                }
                finally
                {
                    sw.Stop();
                    // Explicit Dispose() in finally (the using-declaration form is illegal in
                    // CSX).  Releases the multiplexer's heartbeat timer, socket, and reconnect
                    // thread so the collectible ALC can be reclaimed (§5).
                    mux?.Dispose();
                }
                vars[outcomeKey] = new Vouchfx.Engine.Abstractions.StepOutcome(verdict, sw.ElapsedMilliseconds, observation);
            }

            // Serialises a nullable string to a JSON string literal, or the JSON literal null.
            internal static string JsonOrNull(string? value) =>
                value is null ? "null" : System.Text.Json.JsonSerializer.Serialize(value);

            // Redacts credential material from an exception message (§17 — no secrets in
            // observations).  Redis connection strings carry password=/user= tokens (comma-
            // separated).  Removes: (1) the full connection string if it appears literally;
            // (2) password=/pwd= key-value pairs (whitespace-tolerant values) up to the next
            // comma or semicolon delimiter; (3) user= key-value pairs likewise, preserving
            // the correct key label.
            internal static string RedactCredentials(string connStr, string message)
            {
                if (!string.IsNullOrEmpty(connStr))
                    message = message.Replace(connStr, "***", System.StringComparison.Ordinal);
                message = System.Text.RegularExpressions.Regex.Replace(
                    message,
                    "(?:password|pwd)\\s*=\\s*[^,;]+",
                    "password=***",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                message = System.Text.RegularExpressions.Regex.Replace(
                    message,
                    "user\\s*=\\s*[^,;]+",
                    "user=***",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return message;
            }
        }
        """,
    };

    // ── IStepCompiler<CacheAssertRedisModel> ──────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Emits a CSX block whose execution calls <c>CacheAssertRedis_Helpers.Execute</c> with
    /// the model's connection key, operation token, and the RAW <c>key</c> / <c>field</c> /
    /// <c>expect.value</c> templates (resolved at runtime via <c>Substitute_Helpers.Resolve</c>).
    /// The helper opens a <see cref="StackExchange.Redis.ConnectionMultiplexer"/>, runs the
    /// operation, evaluates the expectation, disposes the multiplexer, and writes a typed
    /// <see cref="StepOutcome"/> into <c>Vars[VarKeys.Outcome(sanitisedStepId)]</c>.
    /// </para>
    /// <para>
    /// CsxFragment rules observed (§13.3.1): bare namespace strings in
    /// <see cref="CsxFragment.RequiredUsings"/>; the full
    /// <c>static class CacheAssertRedis_Helpers</c> definition plus the shared
    /// <c>Substitute_Helpers</c> source in <see cref="CsxFragment.RequiredHelpers"/>; a single
    /// C# 11 <c>$$"""…"""</c> <see cref="CsxFragment.StatementBlock"/> with no <c>using var</c>;
    /// the step id sanitised via <c>CsxFragment.SanitiseId</c> before splicing.
    /// </para>
    /// </remarks>
    public CsxFragment Emit(CacheAssertRedisModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);

        // Operation token: the lowercase enum name the helper switch dispatches on.
        var opLiteral = JsonSerializer.Serialize(OpToken(model.Operation));

        // key/field/expect.value are emitted as RAW template literals (JSON-escaped C#
        // string literals).  Any {placeholder} inside survives as LITERAL text here (not an
        // emit-time interpolation hole — inside $$"""…""", a lone {name} passes through
        // verbatim) and is resolved at runtime inside the helper.
        var keyLiteral = JsonSerializer.Serialize(model.Key);
        var fieldLiteral = model.Field is null ? "null" : JsonSerializer.Serialize(model.Field);
        var expectValueLiteral = model.Expect.Value is null ? "null" : JsonSerializer.Serialize(model.Expect.Value);

        // expect.exists / expect.length are bounded scalars → bare literals, not quoted text.
        var expectExistsLiteral = model.Expect.Exists is bool b
            ? (b ? "true" : "false")
            : "null";
        var expectLengthLiteral = model.Expect.Length is long l
            ? l.ToString(CultureInfo.InvariantCulture) + "L"
            : "null";

        // StatementBlock is a C# 11 double-dollar raw string ($$"""…"""):
        //   { }       → literal brace in the emitted CSX (the block's own braces)
        //   {{expr}}  → interpolation hole filled here at emit time.
        // 'using var' is explicitly prohibited in Roslyn script bodies (§13.3.1).
        var block = $$"""
            {
                CacheAssertRedis_Helpers.Execute(
                    Vars,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.Connection(model.Target))}},
                    {{opLiteral}},
                    {{keyLiteral}},
                    {{fieldLiteral}},
                    {{expectValueLiteral}},
                    {{expectExistsLiteral}},
                    {{expectLengthLiteral}},
                    __stepCt_{{safeId}});
            }
            """;

        // Add Substitute_Helpers.Source to RequiredHelpers (S04-B-03).
        // CsxAssembler deduplicates by class name so it is included at most once.
        var helpers = new List<string>(s_helpers) { SubstituteHelper.Source };

        return new CsxFragment(
            RequiredUsings: s_usings,
            RequiredHelpers: helpers,
            StatementBlock: block);
    }

    /// <summary>
    /// Maps a <see cref="RedisOp"/> to the lowercase token the emitted helper switch
    /// dispatches on (e.g. <see cref="RedisOp.HGet"/> → <c>"hget"</c>).
    /// </summary>
    public static string OpToken(RedisOp op) => op switch
    {
        RedisOp.Get => "get",
        RedisOp.Exists => "exists",
        RedisOp.Ttl => "ttl",
        RedisOp.HGet => "hget",
        RedisOp.HLen => "hlen",
        RedisOp.LLen => "llen",
        RedisOp.SCard => "scard",
        _ => op.ToString().ToLowerInvariant(),
    };

    // ── IResourceContributor<CacheAssertRedisModel> ───────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Yields a single <see cref="ResourceRequirement"/> with <c>Family="redis"</c> and
    /// <c>Name=model.Target</c>.
    /// </remarks>
    public IEnumerable<ResourceRequirement> Resources(CacheAssertRedisModel model)
    {
        yield return new ResourceRequirement(
            Family: "redis",
            Name: model.Target,
            Image: null);
    }

    // ── ICompileReferenceContributor ──────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Returns the <c>StackExchange.Redis</c> assembly so the Roslyn compiler can resolve
    /// <c>ConnectionMultiplexer</c> and related types in the emitted helper class.  The
    /// assembly is already loaded in the Default ALC (the provider project references it
    /// directly) and must never be loaded into the collectible ALC (§5 memory-model invariant).
    /// </remarks>
    public IEnumerable<System.Reflection.Assembly> CompileReferenceAssemblies
    {
        get
        {
            yield return typeof(StackExchange.Redis.ConnectionMultiplexer).Assembly;
        }
    }

    // ── IStepDiffRenderer ─────────────────────────────────────────────────────

    /// <summary>
    /// Determines whether <paramref name="observation"/> is one of the
    /// <c>cache-assert.redis</c> Fail-observation shapes that this provider can render as an
    /// expected-vs-observed diff.
    /// </summary>
    /// <remarks>
    /// Recognised shapes (emitted by <c>CacheAssertRedis_Helpers</c> on a Fail verdict):
    /// <list type="bullet">
    ///   <item><description><c>{"value":{"expected":…,"actual":…}}</c> — a get/hget value mismatch.</description></item>
    ///   <item><description><c>{"exists":{"expected":…,"actual":…}}</c> — a key-presence mismatch.</description></item>
    ///   <item><description><c>{"ttl":{"expected":…,"actual":…}}</c> — a has-a-positive-TTL mismatch.</description></item>
    ///   <item><description><c>{"length":{"expected":…,"actual":…}}</c> — a hlen/llen/scard length mismatch.</description></item>
    /// </list>
    /// </remarks>
    /// <inheritdoc cref="IStepDiffRenderer.CanRender" />
    public bool CanRender(JsonElement observation) =>
        TryReadDiff(observation, out _, out _, out _);

    /// <inheritdoc cref="IStepDiffRenderer.RenderDiff" />
    public string? RenderDiff(JsonElement observation)
    {
        if (!TryReadDiff(observation, out var label, out var expected, out var actual))
            return null;

        return RenderTable(label, expected, actual);
    }

    // ── IStepDiffRenderer helpers ─────────────────────────────────────────────

    private static readonly string[] s_diffKeys = { "value", "exists", "ttl", "length" };

    private static bool TryReadDiff(
        JsonElement observation,
        out string label,
        out string expected,
        out string actual)
    {
        label = string.Empty;
        expected = string.Empty;
        actual = string.Empty;

        if (observation.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var candidate in s_diffKeys)
        {
            if (observation.TryGetProperty(candidate, out var diffEl)
                && diffEl.ValueKind == JsonValueKind.Object
                && diffEl.TryGetProperty("expected", out var expectedEl)
                && diffEl.TryGetProperty("actual", out var actualEl))
            {
                label = candidate;
                expected = ScalarText(expectedEl);
                actual = ScalarText(actualEl);
                return true;
            }
        }

        return false;
    }

    private static string ScalarText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "null",
        JsonValueKind.Null => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => element.GetRawText(),
    };

    private static string RenderTable(string label, string expected, string actual)
    {
        var headers = new[] { label, "expected", "actual" };
        var values = new[] { "(redis)", expected, actual };

        var widths = new int[headers.Length];
        for (int i = 0; i < headers.Length; i++)
            widths[i] = Math.Max(headers[i].Length, values[i].Length);

        var sb = new System.Text.StringBuilder();

        AppendRow(sb, headers, widths);

        for (int i = 0; i < widths.Length; i++)
        {
            if (i > 0)
                sb.Append('┼');
            sb.Append(new string('─', widths[i] + 2));
        }
        sb.Append('\n');

        AppendRow(sb, values, widths);

        return sb.ToString();
    }

    private static void AppendRow(System.Text.StringBuilder sb, string[] cells, int[] widths)
    {
        for (int i = 0; i < cells.Length; i++)
        {
            if (i > 0)
                sb.Append('│');
            sb.Append(' ');
            sb.Append(cells[i].PadRight(widths[i]));
            sb.Append(' ');
        }
        sb.Append('\n');
    }

    // ── Private YAML helpers ──────────────────────────────────────────────────

    private static string GetScalar(YamlMappingNode mapping, string key)
    {
        return mapping.Children.TryGetValue(new YamlScalarNode(key), out var node)
            && node is YamlScalarNode scalar
            ? scalar.Value ?? string.Empty
            : string.Empty;
    }

    private static string? GetOptionalScalar(YamlMappingNode parent, string field)
    {
        if (parent.Children.TryGetValue(new YamlScalarNode(field), out var node)
            && node is YamlScalarNode scalar)
        {
            return scalar.Value ?? string.Empty;
        }
        return null;
    }
}
