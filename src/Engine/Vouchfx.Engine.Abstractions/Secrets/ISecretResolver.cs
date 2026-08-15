// Vouchfx.Engine.Abstractions — ISecretResolver + EnvironmentSecretResolver
// (S05-B-02, §17).
//
// A resolver knows how to turn a source-specific PATH into a SecretString for a
// single source (e.g. "env"). The EnvironmentSecretResolver is the first place in
// the engine that an environment variable is actually read — and it is read at run
// time, never at compile time (§17). Vault drops in as a sibling resolver in
// Sprint 8 with no change to this contract.
//
// "At run time" rather than "at step-execution time", deliberately, and for the reason
// SecretReference's own header states at length: which run-time moment a resolver is
// called at is the CALLER's property, not this interface's. A step's substitutable field
// resolves at step-execution time; environment-level security.clientKeyPassword resolves
// when the certificate material is first used — after the topology is up and BEFORE any
// step runs. The compile-time prohibition is the invariant common to both, and it is the
// only one this file may state.

using System;

namespace Vouchfx.Engine.Abstractions.Secrets;

/// <summary>
/// Resolves the lookup <c>path</c> of a <c>${secret:source/path}</c> reference for
/// a single source into a redacted <see cref="SecretString"/> (§17).
/// </summary>
/// <remarks>
/// Implementations resolve <strong>at run time, never at compile time</strong>: no value
/// is read at compile time, so no value is ever baked into the emitted IL (§17).
/// <para>
/// WHICH run-time moment belongs to the CALLING field, not to this interface. A step's
/// substitutable field resolves at step-execution time; environment-level
/// <c>security.clientKeyPassword</c> resolves when the certificate material is first used,
/// after the topology is up and before any step runs. See <see cref="SecretReference"/>'s own
/// remarks — an implementation may be called at either moment and must assume neither.
/// </para>
/// </remarks>
public interface ISecretResolver
{
    /// <summary>
    /// The source identifier this resolver serves (e.g. <c>env</c>). Compared
    /// ordinally against the <c>source</c> segment of a reference.
    /// </summary>
    string Source { get; }

    /// <summary>
    /// Resolves <paramref name="path"/> into a <see cref="SecretString"/>.
    /// </summary>
    /// <param name="path">The source-specific lookup path.</param>
    /// <returns>The resolved value, redacted at the source.</returns>
    /// <exception cref="SecretResolutionException">
    /// Thrown when the value is absent at the source.
    /// </exception>
    SecretString Resolve(string path);
}

/// <summary>
/// Resolves secrets from process environment variables — the MVP <c>env</c> source
/// (§17). This is the sole place an environment variable is read, and it happens at run
/// time, never at compile time.
/// </summary>
public sealed class EnvironmentSecretResolver : ISecretResolver
{
    /// <inheritdoc />
    public string Source => "env";

    /// <inheritdoc />
    /// <exception cref="SecretResolutionException">
    /// Thrown when the named environment variable is unset (a missing secret is an
    /// environment/configuration problem; §12.1, §17). The message names the
    /// variable but never any value.
    /// </exception>
    public SecretString Resolve(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new SecretResolutionException(
                Source,
                path ?? string.Empty,
                "the secret reference '${secret:env/}' is missing a lookup path; " +
                "the expected form is '${secret:env/VARIABLE_NAME}'.");
        }

        var value = Environment.GetEnvironmentVariable(path);
        if (value is null)
        {
            throw new SecretResolutionException(
                Source,
                path,
                $"the environment variable '{path}' referenced by '${{secret:env/{path}}}' " +
                "is not set; define it in the run environment before executing the suite.");
        }

        return new SecretString(value);
    }
}
