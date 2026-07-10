// Vouchfx.Steps.StorageAssert.S3 — storage-assert.s3 step model (DSL §5.9, brand-new
// storage-assert family). Strongly-typed records; Dictionary<string,object> is explicitly
// prohibited (§13).
using Vouchfx.Sdk;

namespace Vouchfx.Steps.StorageAssert.S3;

/// <summary>
/// The assertion block for a <c>storage-assert.s3</c> step. Every member other than
/// <see cref="Exists"/> is a raw template string — resolved at execution time via a single
/// <c>{placeholder}</c> + <c>${secret:source/path}</c> pass (mirrors
/// <c>metrics-assert.prometheus</c>'s <c>Secret_Helpers.ResolveTemplate</c> usage exactly).
/// </summary>
/// <param name="Exists">
/// Whether the object is expected to exist. Defaults to <see langword="true"/> when the
/// author omits the field (bound at <see cref="StorageAssertS3Provider.Bind"/> time).
/// </param>
/// <param name="Size">
/// Exact expected object size in bytes (a decimal-string template). Mutually exclusive with
/// <see cref="MinSize"/> (enforced by <see cref="StorageAssertS3Provider.Validate"/>).
/// </param>
/// <param name="MinSize">
/// Inclusive minimum expected object size in bytes (a decimal-string template). Mutually
/// exclusive with <see cref="Size"/>.
/// </param>
/// <param name="Sha256">
/// Expected lowercase hex SHA-256 digest of the object body. Declaring this (or
/// <see cref="ContentContains"/>) triggers a bounded GET of the body (capped at 16 MiB — see
/// the provider's file header); an oversize object is a Fail noting the cap, never fetched.
/// </param>
/// <param name="ContentContains">
/// A substring the object body (decoded as UTF-8) must contain. Never echoed into the
/// observation — only a boolean match flag and the body length are reported (§17 spirit: no
/// body content in observations).
/// </param>
/// <param name="ContentType">
/// Expected <c>Content-Type</c>, compared against the HEAD response.
/// </param>
/// <param name="Metadata">
/// Optional map of user-metadata key to expected value, compared against the HEAD response's
/// object metadata (case-insensitive keys, exact-value ordinal comparison).
/// </param>
public sealed record StorageExpectation(
    bool? Exists,
    string? Size,
    string? MinSize,
    string? Sha256,
    string? ContentContains,
    string? ContentType,
    IReadOnlyDictionary<string, string>? Metadata);

/// <summary>
/// Strongly-typed model for the <c>storage-assert.s3</c> step kind — the first (and, in v1,
/// only) provider of the new <c>storage-assert</c> family.
/// </summary>
/// <param name="Target">
/// Logical name of the <c>minio</c> dependency to query, as declared under
/// <c>environment.dependencies</c>.
/// </param>
/// <param name="Bucket">
/// The S3 bucket name. May contain <c>{placeholder}</c> / <c>${secret:source/path}</c> tokens.
/// </param>
/// <param name="Key">
/// The S3 object key. May contain <c>{placeholder}</c> / <c>${secret:source/path}</c> tokens.
/// </param>
/// <param name="Expect">
/// The assertion block declaring the expected object state.
/// </param>
public sealed record StorageAssertS3Model(
    string Target,
    string Bucket,
    string Key,
    StorageExpectation Expect) : IStepModel;
