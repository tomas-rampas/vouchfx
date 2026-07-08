// Platform.Steps.StorageAssert.S3 — storage-assert.s3 step provider (DSL §5.9, brand-new
// storage-assert family; §13).
//
// This is the FIRST provider of the storage-assert family. Steps always name the dotted form
// (`storage-assert.s3`) — bare family names are not part of the language.
//
// Intent: observe the SUT's outbound file drop into an S3-compatible object store (MinIO,
// provisioned as the "minio" dependency type — see EnvironmentMapper) and assert on object
// existence, size, content type, user metadata, and (optionally) the body's SHA-256 digest or
// a body substring. This is how a suite proves a business transaction actually WROTE a file —
// an export, a generated report, an uploaded attachment — the same "did the system actually do
// the thing" signal metrics-assert provides for counters/gauges.
//
// Connection-string form (produced by EnvironmentMapper's "minio" registration):
//   ServiceURL=http://<host>:<port>;AccessKey=<user>;SecretKey=<password>
// ParseConnectionString (in the emitted helper) splits this key=value;… form and builds an
// AmazonS3Config { ServiceURL, ForcePathStyle = true } + BasicAWSCredentials pair.
// ForcePathStyle=true is REQUIRED for MinIO (virtual-hosted-style bucket addressing does not
// resolve against a plain host:port endpoint).
//
// Behaviour per attempt (idempotent — HEAD then, only if declared, a bounded GET; fully
// RETRY-compatible with no special-case code, exactly like every other assert/expect provider):
//   1. HEAD the object (GetObjectMetadataAsync). A 404 AmazonS3Exception means "absent" — this
//      is NOT an EnvironmentError; it is the expected, observable "not written yet" state the
//      RETRY eventually-consumed idiom polls for.
//   2. absent + expect.exists:true (the default)  → Fail, {"exists":false}.
//      present + expect.exists:false               → Fail, {"exists":true}.
//   3. Otherwise verify size/minSize/contentType/metadata from the HEAD response (each mismatch
//      uses the SAME {"field":…,"expected":…,"actual":…} shape db-assert.mongodb/dynamodb use,
//      so the same table-rendering IStepDiffRenderer logic applies unchanged).
//   4. Only if sha256 or contentContains is declared, and only once every HEAD-based check has
//      passed, GET the body — capped at 16 MiB (mirrors http.rest's / metrics-assert's own
//      MaxResponseContentBufferSize precedent): an oversize object is a Fail noting the cap,
//      the body is never fetched. SHA-256 digests are safe to report verbatim (a one-way,
//      fixed-length hash cannot leak body content); a contentContains mismatch reports ONLY a
//      boolean match flag and the body length — the substring and the body are NEVER echoed
//      into the observation (§17 spirit: no content in observations, exactly as
//      webhook-listen.http/mail-expect.smtp never echo captured bodies verbatim).
//   5. Otherwise → Pass, {"exists":…,"etag":…,"versionId":…,"size":…}.
//
// Capture support: the synthesised Pass observation exposes etag/versionId/size/exists to
// `capture:` via JSONPath ($.etag, $.versionId, $.size, $.exists) — mirrors
// MetricsAssertPrometheus_Helpers.ScrapeAsync's capture mechanism exactly (same JsonPath.Net
// evaluation against a JsonNode-parsed observation object, same captureStatusKey bookkeeping,
// same Inconclusive-on-unmet-capture mapping).
//
// Placeholders/secrets: bucket, key, and every expect.* template resolve via
// Secret_Helpers.ResolveTemplate (single left-to-right pass: {placeholder} substitution +
// ${secret:source/path} resolution) INSIDE the guarded try region, exactly as
// metrics-assert.prometheus resolves its own author-text fields — a missing/unknown secret
// throws SecretResolutionException, caught below, EnvironmentError (§12.1, §17).
//
// Verdict taxonomy (§12.1):
//   • conn:: key absent from Vars                                → EnvironmentError.
//   • SecretResolutionException                                  → EnvironmentError.
//   • any other AWS SDK / connection exception (client build, network,
//     credentials, bucket not found, etc.)                       → EnvironmentError. Exception
//     TYPE names only are emitted (never ex.Message) — the AWS SDK's own exception messages can
//     embed the ServiceURL, mirroring db-assert.dynamodb / mail-expect.smtp's pattern.
//   • object absent when exists:true (the default)               → Fail.
//   • object present when exists:false                           → Fail.
//   • size/minSize/contentType/metadata/sha256/contentContains
//     mismatch, or the 16 MiB body cap                           → Fail.
//   • otherwise                                                  → Pass.
//
// Schema composition invariants (§13.3.1, §13.6):
//   • SchemaFragment describes ONLY the provider's own fields.
//   • CsxFragment rules: RequiredUsings are bare namespace strings; RequiredHelpers contains
//     the full provider-id-prefixed static class definition; StatementBlock is a C# 11
//     $$"""…""" block; 'using var' is illegal (the helper uses plain 'var' plus explicit
//     .Dispose() in 'finally' throughout).
using System.Globalization;
using System.Text.Json;
using Platform.Engine.Abstractions;
using Platform.Sdk;
using YamlDotNet.RepresentationModel;

namespace Platform.Steps.StorageAssert.S3;

/// <summary>
/// Core provider for the <c>storage-assert.s3</c> step kind — the first member of the new
/// <c>storage-assert</c> family. HEADs (and, if declared, GETs) an S3-compatible object and
/// asserts on its existence, size, content type, metadata, and/or body digest/substring.
/// </summary>
[StepProvider]
public sealed class StorageAssertS3Provider
    : IStepProvider,
      IStepBinder<StorageAssertS3Model>,
      IStepValidator<StorageAssertS3Model>,
      IStepCompiler<StorageAssertS3Model>,
      IResourceContributor<StorageAssertS3Model>,
      ICompileReferenceContributor,
      IStepDiffRenderer
{
    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("storage-assert", "s3");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    // ── IStepBinder<StorageAssertS3Model> ─────────────────────────────────────

    /// <inheritdoc />
    public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
        """
        {
          "description": "HEADs (and, if declared, GETs) an S3-compatible object and asserts on its existence, size, content type, metadata, and/or body digest/substring.",
          "type": "object",
          "required": ["target", "bucket", "key", "expect"],
          "properties": {
            "target": {
              "description": "Logical name of the minio dependency to query, as declared under environment.dependencies.",
              "type": "string"
            },
            "bucket": {
              "description": "The S3 bucket name. May contain {placeholder} and ${secret:source/path} tokens.",
              "type": "string"
            },
            "key": {
              "description": "The S3 object key. May contain {placeholder} and ${secret:source/path} tokens.",
              "type": "string"
            },
            "expect": {
              "description": "The assertion block declaring the expected object state. exists:false excludes every content expectation; size and minSize are mutually exclusive.",
              "type": "object",
              "properties": {
                "exists": {
                  "description": "Whether the object is expected to exist. Defaults to true when omitted.",
                  "type": "boolean"
                },
                "size": {
                  "description": "Exact expected object size in bytes. Mutually exclusive with minSize. May contain {placeholder} / ${secret:...} tokens.",
                  "type": ["string", "integer"]
                },
                "minSize": {
                  "description": "Inclusive minimum expected object size in bytes. Mutually exclusive with size. May contain {placeholder} / ${secret:...} tokens.",
                  "type": ["string", "integer"]
                },
                "sha256": {
                  "description": "Expected lowercase hex SHA-256 digest of the object body. Declaring this (or contentContains) triggers a bounded GET of the body (capped at 16 MiB).",
                  "type": "string"
                },
                "contentContains": {
                  "description": "A substring the object body (decoded as UTF-8) must contain. Never echoed into the observation.",
                  "type": "string"
                },
                "contentType": {
                  "description": "Expected Content-Type, compared against the HEAD response.",
                  "type": "string"
                },
                "metadata": {
                  "description": "Map of user-metadata key to expected value, compared against the HEAD response's object metadata.",
                  "type": "object",
                  "additionalProperties": { "type": "string" }
                }
              },
              "additionalProperties": false
            }
          },
          "additionalProperties": true
        }
        """);

    /// <inheritdoc />
    public StorageAssertS3Model Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
        {
            return new StorageAssertS3Model(
                Target: string.Empty,
                Bucket: string.Empty,
                Key: string.Empty,
                Expect: new StorageExpectation(null, null, null, null, null, null, null));
        }

        var target = GetScalar(mapping, "target");
        var bucket = GetScalar(mapping, "bucket");
        var key = GetScalar(mapping, "key");
        var expect = BindExpectation(mapping);

        return new StorageAssertS3Model(target, bucket, key, expect);
    }

    private static StorageExpectation BindExpectation(YamlMappingNode mapping)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode("expect"), out var expectNode)
            || expectNode is not YamlMappingNode expectMap)
        {
            return new StorageExpectation(null, null, null, null, null, null, null);
        }

        bool? exists = null;
        if (expectMap.Children.TryGetValue(new YamlScalarNode("exists"), out var existsNode)
            && existsNode is YamlScalarNode existsScalar
            && bool.TryParse(existsScalar.Value, out var parsedExists))
        {
            exists = parsedExists;
        }

        var size = GetOptionalScalar(expectMap, "size");
        var minSize = GetOptionalScalar(expectMap, "minSize");
        var sha256 = GetOptionalScalar(expectMap, "sha256");
        var contentContains = GetOptionalScalar(expectMap, "contentContains");
        var contentType = GetOptionalScalar(expectMap, "contentType");
        var metadata = BindStringMap(expectMap, "metadata");

        return new StorageExpectation(exists, size, minSize, sha256, contentContains, contentType, metadata);
    }

    private static Dictionary<string, string>? BindStringMap(YamlMappingNode parent, string field)
    {
        if (!parent.Children.TryGetValue(new YamlScalarNode(field), out var node)
            || node is not YamlMappingNode map)
        {
            return null;
        }

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in map.Children)
        {
            if (k is YamlScalarNode ks && v is YamlScalarNode vs)
                dict[ks.Value ?? string.Empty] = vs.Value ?? string.Empty;
        }
        return dict;
    }

    // ── IStepValidator<StorageAssertS3Model> ──────────────────────────────────

    /// <inheritdoc />
    public ValidationResult Validate(StorageAssertS3Model model, IProjectContext ctx)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(model.Target))
            errors.Add("storage-assert.s3: 'target' must not be empty.");

        if (string.IsNullOrWhiteSpace(model.Bucket))
            errors.Add("storage-assert.s3: 'bucket' must not be empty.");

        if (string.IsNullOrWhiteSpace(model.Key))
            errors.Add("storage-assert.s3: 'key' must not be empty.");

        var exists = model.Expect.Exists ?? true;
        var hasContentExpectation =
            model.Expect.Size is not null
            || model.Expect.MinSize is not null
            || model.Expect.Sha256 is not null
            || model.Expect.ContentContains is not null
            || model.Expect.ContentType is not null
            || model.Expect.Metadata is { Count: > 0 };

        // Coherence rule 1: exists:false excludes every content expectation — an absent object
        // has no content to assert on.
        if (!exists && hasContentExpectation)
        {
            errors.Add(
                "storage-assert.s3: 'expect.exists: false' cannot be combined with any content " +
                "expectation (size/minSize/sha256/contentContains/contentType/metadata) — an " +
                "absent object has no content to assert on.");
        }

        // Coherence rule 2: size and minSize are mutually exclusive (an exact size already
        // implies a lower bound; declaring both is redundant-at-best, contradictory-at-worst).
        if (model.Expect.Size is not null && model.Expect.MinSize is not null)
        {
            errors.Add(
                "storage-assert.s3: 'expect.size' and 'expect.minSize' are mutually exclusive " +
                "— declare an exact size or a lower bound, not both.");
        }

        // Dependency reconciliation: target must name a declared minio dependency (the only
        // S3-compatible dependency type this provider supports in v1).
        if (!string.IsNullOrWhiteSpace(model.Target))
        {
            if (!ctx.DeclaredDependencies.TryGetValue(model.Target, out var depType))
            {
                errors.Add(
                    $"storage-assert.s3: 'target' '{model.Target}' is not a " +
                    "minio dependency declared in environment.dependencies.");
            }
            else if (!string.Equals(depType, "minio", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"storage-assert.s3: 'target' '{model.Target}' is not a " +
                    "minio dependency declared in environment.dependencies.");
            }
        }

        return errors.Count == 0
            ? ValidationResult.Success
            : ValidationResult.Failure(errors.ToArray());
    }

    // ── CsxFragment components ────────────────────────────────────────────────

    private static readonly IReadOnlyList<string> s_usings = new[]
    {
        "System",
        "System.Collections.Generic",
        "System.Diagnostics",
        "System.Threading.Tasks",
        "Amazon.S3",
        "Amazon.S3.Model",
        "Amazon.Runtime",
        "Platform.Engine.Abstractions",
    };

    /// <summary>
    /// Full source of the provider-id-prefixed helper class (§13.3.1). Byte-identical across
    /// every step instance of this provider within a suite — no per-step interpolation.
    /// </summary>
    private static readonly IReadOnlyList<string> s_helpers = new[]
    {
        """
        static class StorageAssertS3_Helpers
        {
            // 16 MiB — mirrors http.rest's / metrics-assert.prometheus's own
            // MaxResponseContentBufferSize precedent for a bounded, untrusted body read.
            private const long BodyCapBytes = 16L * 1024 * 1024;

            /// <summary>
            /// Parses the "ServiceURL=…;AccessKey=…;SecretKey=…" connection-string form
            /// EnvironmentMapper's "minio" registration produces.
            /// </summary>
            private static void ParseConnectionString(
                string connStr, out string serviceUrl, out string accessKey, out string secretKey)
            {
                serviceUrl = string.Empty;
                accessKey = string.Empty;
                secretKey = string.Empty;
                foreach (var part in connStr.Split(';', System.StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = part.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    var k = part.Substring(0, eq).Trim();
                    var v = part.Substring(eq + 1).Trim();
                    if (string.Equals(k, "ServiceURL", System.StringComparison.OrdinalIgnoreCase))
                        serviceUrl = v;
                    else if (string.Equals(k, "AccessKey", System.StringComparison.OrdinalIgnoreCase))
                        accessKey = v;
                    else if (string.Equals(k, "SecretKey", System.StringComparison.OrdinalIgnoreCase))
                        secretKey = v;
                }
                if (string.IsNullOrEmpty(serviceUrl))
                    throw new System.InvalidOperationException("storage-assert.s3: connection string is missing 'ServiceURL'.");
            }

            /// <summary>
            /// HEADs (and, if declared, GETs) the object, evaluates the exists/size/minSize/
            /// contentType/metadata/sha256/contentContains expectations, optionally applies
            /// capture: (JSONPath only, over a small synthesised JSON observation object), and
            /// writes a typed StepOutcome into Vars. See the provider's file header for the
            /// full verdict-mapping table.
            /// </summary>
            public static async System.Threading.Tasks.Task ExecuteAsync(
                System.Collections.Generic.IDictionary<string, object?> vars,
                Platform.Engine.Abstractions.Secrets.ISecretAccessor secrets,
                string outcomeKey,
                string captureStatusKey,
                string connKey,
                string bucketTemplate,
                string keyTemplate,
                bool expectExists,
                string? sizeTemplate,
                string? minSizeTemplate,
                string? sha256Template,
                string? contentContainsTemplate,
                string? contentTypeTemplate,
                string[] metadataFieldNames,
                string[] metadataValueTemplates,
                string[] captureVarNames,
                string[] captureExprs,
                string[] captureKinds)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Platform.Engine.Abstractions.Verdict verdict;
                string observation;

                var connStr = vars.TryGetValue(connKey, out var c) && c is string s ? s : null;
                if (string.IsNullOrEmpty(connStr))
                {
                    sw.Stop();
                    vars[outcomeKey] = new Platform.Engine.Abstractions.StepOutcome(
                        Platform.Engine.Abstractions.Verdict.EnvironmentError,
                        sw.ElapsedMilliseconds,
                        "{\"error\":" + System.Text.Json.JsonSerializer.Serialize("connection string not found for key '" + connKey + "'") + "}");
                    return;
                }

                Amazon.S3.AmazonS3Client? client = null;
                try
                {
                    // Resolve every author-text field INSIDE the guarded region (§17) via
                    // ResolveTemplate (single pass: {placeholder} substitution + ${secret}
                    // resolution). A missing secret throws SecretResolutionException -> caught
                    // below -> EnvironmentError.
                    var bucket = Secret_Helpers.ResolveTemplate(secrets, vars, bucketTemplate);
                    var key = Secret_Helpers.ResolveTemplate(secrets, vars, keyTemplate);

                    ParseConnectionString(connStr, out var serviceUrl, out var accessKey, out var secretKey);
                    // MaxErrorRetry=0 + a bounded Timeout/ConnectTimeout: the engine-owned
                    // RetryRunner already owns the retry/backoff cadence for verifyMode: RETRY
                    // (§7); stacking the AWS SDK's own default retries (which can otherwise wait
                    // up to ~100s per attempt against an unreachable endpoint) underneath that
                    // would make a single poll attempt hang far longer than the step's own
                    // timeout budget. A local MinIO container responds in milliseconds when
                    // healthy, so 5s is generous headroom, not a tight race.
                    var config = new Amazon.S3.AmazonS3Config
                    {
                        ServiceURL = serviceUrl,
                        ForcePathStyle = true,
                        MaxErrorRetry = 0,
                        Timeout = System.TimeSpan.FromSeconds(5),
                        ConnectTimeout = System.TimeSpan.FromSeconds(5),
                    };
                    var creds = new Amazon.Runtime.BasicAWSCredentials(accessKey, secretKey);
                    client = new Amazon.S3.AmazonS3Client(creds, config);

                    Amazon.S3.Model.GetObjectMetadataResponse? head = null;
                    try
                    {
                        head = await client.GetObjectMetadataAsync(new Amazon.S3.Model.GetObjectMetadataRequest
                        {
                            BucketName = bucket,
                            Key = key,
                        }).ConfigureAwait(false);
                    }
                    catch (Amazon.S3.AmazonS3Exception notFoundEx) when (notFoundEx.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // Absent is an expected, observable outcome (the RETRY
                        // eventually-consumed idiom) — never an EnvironmentError.
                        head = null;
                    }

                    var found = head is not null;
                    string? failObservation = null;

                    if (!found)
                    {
                        if (expectExists)
                            failObservation = "{\"exists\":false}";
                    }
                    else if (!expectExists)
                    {
                        failObservation = "{\"exists\":true}";
                    }

                    long actualSize = 0;
                    string? actualEtag = null;
                    string? actualVersionId = null;

                    if (found && failObservation is null)
                    {
                        actualSize = head!.Headers.ContentLength;
                        actualEtag = head.ETag;
                        actualVersionId = head.VersionId;

                        if (sizeTemplate is not null)
                        {
                            var expectedSize = long.Parse(
                                Secret_Helpers.ResolveTemplate(secrets, vars, sizeTemplate),
                                System.Globalization.CultureInfo.InvariantCulture);
                            if (actualSize != expectedSize)
                            {
                                failObservation = "{\"field\":\"size\",\"expected\":" +
                                    System.Text.Json.JsonSerializer.Serialize(expectedSize.ToString(System.Globalization.CultureInfo.InvariantCulture)) +
                                    ",\"actual\":" +
                                    System.Text.Json.JsonSerializer.Serialize(actualSize.ToString(System.Globalization.CultureInfo.InvariantCulture)) + "}";
                            }
                        }

                        if (failObservation is null && minSizeTemplate is not null)
                        {
                            var expectedMinSize = long.Parse(
                                Secret_Helpers.ResolveTemplate(secrets, vars, minSizeTemplate),
                                System.Globalization.CultureInfo.InvariantCulture);
                            if (actualSize < expectedMinSize)
                            {
                                failObservation = "{\"field\":\"minSize\",\"expected\":" +
                                    System.Text.Json.JsonSerializer.Serialize(expectedMinSize.ToString(System.Globalization.CultureInfo.InvariantCulture)) +
                                    ",\"actual\":" +
                                    System.Text.Json.JsonSerializer.Serialize(actualSize.ToString(System.Globalization.CultureInfo.InvariantCulture)) + "}";
                            }
                        }

                        if (failObservation is null && contentTypeTemplate is not null)
                        {
                            var expectedContentType = Secret_Helpers.ResolveTemplate(secrets, vars, contentTypeTemplate);
                            var actualContentType = head.Headers.ContentType ?? "null";
                            if (!string.Equals(actualContentType, expectedContentType, System.StringComparison.Ordinal))
                            {
                                failObservation = "{\"field\":\"contentType\",\"expected\":" +
                                    System.Text.Json.JsonSerializer.Serialize(expectedContentType) +
                                    ",\"actual\":" +
                                    System.Text.Json.JsonSerializer.Serialize(actualContentType) + "}";
                            }
                        }

                        if (failObservation is null && metadataFieldNames.Length > 0)
                        {
                            // AmazonS3Config.Metadata.Keys enumerates the RAW header names, which
                            // carry the "x-amz-meta-" prefix (verified empirically against a live
                            // MinIO container); the indexer itself accepts either the bare or
                            // prefixed form. Normalise to the bare key so expect.metadata field
                            // names (as authored, without the prefix) match.
                            const string metaPrefix = "x-amz-meta-";
                            var actualMetadata = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
                            foreach (var metaKey in head.Metadata.Keys)
                            {
                                var normalisedKey = metaKey.StartsWith(metaPrefix, System.StringComparison.OrdinalIgnoreCase)
                                    ? metaKey.Substring(metaPrefix.Length)
                                    : metaKey;
                                actualMetadata[normalisedKey] = head.Metadata[metaKey];
                            }

                            for (int i = 0; i < metadataFieldNames.Length && failObservation is null; i++)
                            {
                                var fieldName = metadataFieldNames[i];
                                var expectedVal = Secret_Helpers.ResolveTemplate(secrets, vars, metadataValueTemplates[i]);
                                var actualVal = actualMetadata.TryGetValue(fieldName, out var mv) ? mv : "null";
                                if (!string.Equals(actualVal, expectedVal, System.StringComparison.Ordinal))
                                {
                                    failObservation = "{\"field\":" + System.Text.Json.JsonSerializer.Serialize("metadata." + fieldName) +
                                        ",\"expected\":" + System.Text.Json.JsonSerializer.Serialize(expectedVal) +
                                        ",\"actual\":" + System.Text.Json.JsonSerializer.Serialize(actualVal) + "}";
                                }
                            }
                        }

                        // Only fetch the body when sha256/contentContains are declared, and only
                        // once every HEAD-based check above has passed.
                        if (failObservation is null && (sha256Template is not null || contentContainsTemplate is not null))
                        {
                            if (actualSize > BodyCapBytes)
                            {
                                // Refuse to fetch an oversize body — Fail noting the cap, never
                                // attempt the GET (mirrors http.rest's / metrics-assert's own
                                // bounded-read precedent, applied BEFORE any network read here).
                                failObservation = "{\"field\":\"bodyCap\",\"capBytes\":" +
                                    BodyCapBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                                    ",\"actualBytes\":" + actualSize.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
                            }
                            else
                            {
                                byte[] bodyBytes;
                                Amazon.S3.Model.GetObjectResponse? getResp = null;
                                try
                                {
                                    getResp = await client.GetObjectAsync(new Amazon.S3.Model.GetObjectRequest
                                    {
                                        BucketName = bucket,
                                        Key = key,
                                    }).ConfigureAwait(false);

                                    var mem = new System.IO.MemoryStream();
                                    try
                                    {
                                        await getResp.ResponseStream.CopyToAsync(mem).ConfigureAwait(false);
                                        bodyBytes = mem.ToArray();
                                    }
                                    finally
                                    {
                                        mem.Dispose();
                                    }
                                }
                                finally
                                {
                                    getResp?.Dispose();
                                }

                                if (failObservation is null && sha256Template is not null)
                                {
                                    var expectedHash = Secret_Helpers.ResolveTemplate(secrets, vars, sha256Template).ToLowerInvariant();
                                    var actualHashBytes = System.Security.Cryptography.SHA256.HashData(bodyBytes);
                                    var actualHash = System.Convert.ToHexString(actualHashBytes).ToLowerInvariant();
                                    if (!string.Equals(actualHash, expectedHash, System.StringComparison.Ordinal))
                                    {
                                        // SHA-256 digests are safe to report verbatim: a one-way,
                                        // fixed-length hash cannot leak body content.
                                        failObservation = "{\"field\":\"sha256\",\"expected\":" +
                                            System.Text.Json.JsonSerializer.Serialize(expectedHash) +
                                            ",\"actual\":" + System.Text.Json.JsonSerializer.Serialize(actualHash) + "}";
                                    }
                                }

                                if (failObservation is null && contentContainsTemplate is not null)
                                {
                                    var expectedSubstring = Secret_Helpers.ResolveTemplate(secrets, vars, contentContainsTemplate);
                                    var bodyText = System.Text.Encoding.UTF8.GetString(bodyBytes);
                                    var containsMatch = bodyText.Contains(expectedSubstring, System.StringComparison.Ordinal);
                                    if (!containsMatch)
                                    {
                                        // Never echo the body or the searched substring (§17
                                        // spirit) — only a boolean match flag and the body length.
                                        failObservation = "{\"field\":\"contentContains\",\"matched\":false,\"bodyLength\":" +
                                            bodyBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
                                    }
                                }
                            }
                        }
                    }

                    if (failObservation is not null)
                    {
                        verdict = Platform.Engine.Abstractions.Verdict.Fail;
                        observation = failObservation;
                    }
                    else
                    {
                        verdict = Platform.Engine.Abstractions.Verdict.Pass;
                        observation = "{\"exists\":" + (found ? "true" : "false") +
                            ",\"etag\":" + System.Text.Json.JsonSerializer.Serialize(actualEtag ?? string.Empty) +
                            ",\"versionId\":" + System.Text.Json.JsonSerializer.Serialize(actualVersionId ?? string.Empty) +
                            ",\"size\":" + actualSize.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";

                        // ── capture: (JSONPath only) over the synthesised observation object ──
                        // Mirrors MetricsAssertPrometheus_Helpers.ScrapeAsync's capture block
                        // exactly: $.etag / $.versionId / $.size / $.exists against the SAME
                        // object just built for the Pass observation.
                        if (captureVarNames.Length > 0)
                        {
                            System.Text.Json.Nodes.JsonNode? obsNode;
                            try
                            {
                                obsNode = System.Text.Json.Nodes.JsonNode.Parse(observation);
                            }
                            catch (System.Exception)
                            {
                                obsNode = null;
                            }
                            var matchedFlags = new bool[captureVarNames.Length];
                            for (int ci = 0; ci < captureVarNames.Length; ci++)
                            {
                                var varName = captureVarNames[ci];
                                var captureExpr = captureExprs[ci];
                                var captureKind = captureKinds[ci];
                                bool matched = false;
                                if (obsNode is not null && string.Equals(captureKind, "json", System.StringComparison.Ordinal))
                                {
                                    try
                                    {
                                        var pathResult = Json.Path.JsonPath.Parse(captureExpr).Evaluate(obsNode);
                                        var matches = pathResult.Matches;
                                        if (matches != null && matches.Count > 0 && matches[0].Value is not null)
                                        {
                                            var firstMatch = matches[0].Value;
                                            string capturedStr;
                                            if (firstMatch is System.Text.Json.Nodes.JsonValue jv)
                                            {
                                                var rawElem = jv.GetValue<System.Text.Json.JsonElement>();
                                                capturedStr = rawElem.ValueKind == System.Text.Json.JsonValueKind.String
                                                    ? rawElem.GetString() ?? string.Empty
                                                    : rawElem.GetRawText();
                                            }
                                            else
                                            {
                                                capturedStr = firstMatch.ToJsonString();
                                            }
                                            vars[varName] = capturedStr;
                                            matched = true;
                                        }
                                    }
                                    catch (System.Exception)
                                    {
                                        matched = false;
                                    }
                                }
                                matchedFlags[ci] = matched;
                                if (!matched)
                                {
                                    verdict = Platform.Engine.Abstractions.Verdict.Inconclusive;
                                    observation = "{\"captureUnmet\":" + System.Text.Json.JsonSerializer.Serialize(varName) + "}";
                                }
                            }
                            vars[captureStatusKey] = string.Join(",", System.Array.ConvertAll(matchedFlags, f => f ? "1" : "0"));
                        }
                    }
                }
                catch (Platform.Engine.Abstractions.Secrets.SecretResolutionException sre)
                {
                    verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;
                    observation = "{\"secretError\":\"secret resolution failed\"" +
                        ",\"source\":" + System.Text.Json.JsonSerializer.Serialize(sre.SecretSource) +
                        ",\"path\":" + System.Text.Json.JsonSerializer.Serialize(sre.SecretPath) + "}";
                }
                catch (System.Exception ex)
                {
                    // Any AWS SDK / connection / parse failure = EnvironmentError (§12.1).
                    // Exception TYPE name only — never ex.Message (may embed the ServiceURL).
                    verdict = Platform.Engine.Abstractions.Verdict.EnvironmentError;
                    observation = "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(ex.GetType().Name) + "}";
                }
                finally
                {
                    sw.Stop();
                    client?.Dispose();
                }

                vars[outcomeKey] = new Platform.Engine.Abstractions.StepOutcome(
                    verdict, sw.ElapsedMilliseconds, observation);
            }
        }
        """,
    };

    // ── IStepCompiler<StorageAssertS3Model> ───────────────────────────────────

    /// <inheritdoc />
    public CsxFragment Emit(StorageAssertS3Model model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);

        var expectExistsLiteral = (model.Expect.Exists ?? true) ? "true" : "false";

        var sizeLiteral = model.Expect.Size is null ? "null" : JsonSerializer.Serialize(model.Expect.Size);
        var minSizeLiteral = model.Expect.MinSize is null ? "null" : JsonSerializer.Serialize(model.Expect.MinSize);
        var sha256Literal = model.Expect.Sha256 is null ? "null" : JsonSerializer.Serialize(model.Expect.Sha256);
        var contentContainsLiteral = model.Expect.ContentContains is null ? "null" : JsonSerializer.Serialize(model.Expect.ContentContains);
        var contentTypeLiteral = model.Expect.ContentType is null ? "null" : JsonSerializer.Serialize(model.Expect.ContentType);

        string[] metadataNames;
        string[] metadataValues;
        if (model.Expect.Metadata is { Count: > 0 } metadata)
        {
            metadataNames = metadata.Keys.ToArray();
            metadataValues = metadata.Values.ToArray();
        }
        else
        {
            metadataNames = Array.Empty<string>();
            metadataValues = Array.Empty<string>();
        }

        // Format-aware captures: mirrors MetricsAssertPrometheusProvider.Emit's expansion of
        // ctx.CaptureExprs into three parallel arrays. Only "json" (JsonPath) is meaningfully
        // evaluated by the helper; an "xpath"-kind entry always misses.
        string[] captureVarNames;
        string[] captureExprsArr;
        string[] captureKinds;
        if (ctx.CaptureExprs is { Count: > 0 } captureMap)
        {
            captureVarNames = new string[captureMap.Count];
            captureExprsArr = new string[captureMap.Count];
            captureKinds = new string[captureMap.Count];
            var ci = 0;
            foreach (var (name, expr) in captureMap)
            {
                captureVarNames[ci] = name;
                captureExprsArr[ci] = expr.Expression;
                captureKinds[ci] = expr.Format == CaptureFormat.XPath ? "xpath" : "json";
                ci++;
            }
        }
        else
        {
            captureVarNames = Array.Empty<string>();
            captureExprsArr = Array.Empty<string>();
            captureKinds = Array.Empty<string>();
        }

        var metadataNamesLiteral = BuildStringArrayLiteral(metadataNames);
        var metadataValuesLiteral = BuildStringArrayLiteral(metadataValues);
        var captureVarNamesLiteral = BuildStringArrayLiteral(captureVarNames);
        var captureExprsLiteral = BuildStringArrayLiteral(captureExprsArr);
        var captureKindsLiteral = BuildStringArrayLiteral(captureKinds);

        // StatementBlock is a C# 11 double-dollar raw string ($$"""…"""):
        //   { }       → literal brace in the emitted CSX (the block's own braces)
        //   {{expr}}  → interpolation hole filled here at emit time.
        // 'using var' is explicitly prohibited in Roslyn script bodies (§13.3.1).
        var block = $$"""
            {
                await StorageAssertS3_Helpers.ExecuteAsync(
                    Vars,
                    Secrets,
                    {{JsonSerializer.Serialize(VarKeys.Outcome(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.CaptureStatus(safeId))}},
                    {{JsonSerializer.Serialize(VarKeys.Connection(model.Target))}},
                    {{JsonSerializer.Serialize(model.Bucket)}},
                    {{JsonSerializer.Serialize(model.Key)}},
                    {{expectExistsLiteral}},
                    {{sizeLiteral}},
                    {{minSizeLiteral}},
                    {{sha256Literal}},
                    {{contentContainsLiteral}},
                    {{contentTypeLiteral}},
                    {{metadataNamesLiteral}},
                    {{metadataValuesLiteral}},
                    {{captureVarNamesLiteral}},
                    {{captureExprsLiteral}},
                    {{captureKindsLiteral}});
            }
            """;

        // Substitute_Helpers is included defensively (mirrors metrics-assert.prometheus's own
        // helper set) even though this provider resolves every field via Secret_Helpers.
        var helpers = new List<string>(s_helpers)
        {
            SubstituteHelper.Source,
            SecretHelper.Source,
        };

        return new CsxFragment(
            RequiredUsings: s_usings,
            RequiredHelpers: helpers,
            StatementBlock: block);
    }

    // ── IResourceContributor<StorageAssertS3Model> ────────────────────────────

    /// <inheritdoc />
    public IEnumerable<ResourceRequirement> Resources(StorageAssertS3Model model)
    {
        yield return new ResourceRequirement(
            Family: "minio",
            Name: model.Target,
            Image: null);
    }

    // ── ICompileReferenceContributor ──────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Returns the <c>AWSSDK.S3</c> and <c>JsonPath.Net</c> assemblies so the Roslyn compiler
    /// can resolve <c>AmazonS3Client</c> and <c>Json.Path.JsonPath</c> (capture support) in the
    /// emitted helper class. Both are already loaded in the Default ALC (the provider project
    /// references them directly) and must never be loaded into the collectible ALC (§5
    /// memory-model invariant).
    /// </remarks>
    public IEnumerable<System.Reflection.Assembly> CompileReferenceAssemblies
    {
        get
        {
            yield return typeof(Amazon.S3.AmazonS3Client).Assembly;
            yield return typeof(Json.Path.JsonPath).Assembly;
        }
    }

    // ── IStepDiffRenderer ──────────────────────────────────────────────────────

    /// <summary>
    /// Determines whether <paramref name="observation"/> is the
    /// <c>{"field":…,"expected":…,"actual":…}</c> mismatch shape this provider can render as an
    /// expected-vs-observed diff (size/minSize/contentType/metadata.*/sha256 mismatches all
    /// share this shape — mirrors <c>DbAssertMongodbProvider</c>/<c>DbAssertDynamodbProvider</c>
    /// exactly). Other Fail shapes (exists mismatch, bodyCap, contentContains) fall back to the
    /// renderer's plain verdict line, per <see cref="IStepDiffRenderer"/>'s documented contract.
    /// </summary>
    public bool CanRender(JsonElement observation) =>
        TryReadFieldDiff(observation, out _, out _, out _);

    /// <inheritdoc />
    public string? RenderDiff(JsonElement observation)
    {
        if (TryReadFieldDiff(observation, out var field, out var expected, out var actual))
        {
            return RenderFieldTable(field, expected, actual);
        }

        return null;
    }

    // ── IStepDiffRenderer helpers ──────────────────────────────────────────────

    private static bool TryReadFieldDiff(
        JsonElement observation,
        out string field,
        out string expected,
        out string actual)
    {
        field = string.Empty;
        expected = string.Empty;
        actual = string.Empty;

        if (observation.ValueKind != JsonValueKind.Object)
            return false;

        if (!observation.TryGetProperty("field", out var fieldEl)
            || fieldEl.ValueKind != JsonValueKind.String
            || !observation.TryGetProperty("expected", out var expectedEl)
            || !observation.TryGetProperty("actual", out var actualEl))
        {
            return false;
        }

        field = fieldEl.GetString() ?? string.Empty;
        expected = ScalarText(expectedEl);
        actual = ScalarText(actualEl);
        return true;
    }

    private static string ScalarText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "null",
        JsonValueKind.Null => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => element.GetRawText(),
    };

    private static string RenderFieldTable(string field, string expected, string actual)
    {
        var headers = new[] { "field", "expected", "actual" };
        var values = new[] { field, expected, actual };
        return RenderTable(headers, values);
    }

    private static string RenderTable(string[] headers, string[] values)
    {
        var widths = new int[headers.Length];
        for (int i = 0; i < headers.Length; i++)
        {
            widths[i] = Math.Max(headers[i].Length, values[i].Length);
        }

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

    // ── Private helpers ────────────────────────────────────────────────────────

    private static string BuildStringArrayLiteral(string[] values)
    {
        if (values.Length == 0)
            return "new string[] { }";

        var sb = new System.Text.StringBuilder("new string[] { ");
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(JsonSerializer.Serialize(values[i]));
        }
        sb.Append(" }");
        return sb.ToString();
    }

    private static string? GetOptionalScalar(YamlMappingNode mapping, string key)
    {
        return mapping.Children.TryGetValue(new YamlScalarNode(key), out var node)
            && node is YamlScalarNode scalar
            ? scalar.Value
            : null;
    }

    private static string GetScalar(YamlMappingNode mapping, string key) =>
        GetOptionalScalar(mapping, key) ?? string.Empty;
}
