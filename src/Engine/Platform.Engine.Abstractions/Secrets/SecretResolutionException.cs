// Platform.Engine.Abstractions — SecretResolutionException (S05-B-02, §17).
//
// Thrown when a secret reference cannot be resolved at step-execution time: the
// source is unknown, or the value is absent at the source. The exception carries
// the SOURCE and PATH (the reference coordinates) but NEVER a value — there is no
// value to carry when resolution fails, and even partial state must not leak.
//
// Verdict mapping (§12.1): a provider that resolves a secret inside its guarded
// region must catch this exception and record a per-step EnvironmentError — a
// missing secret is an infrastructure/configuration problem, not a product defect.

using System;

namespace Platform.Engine.Abstractions.Secrets;

/// <summary>
/// Raised when a <c>${secret:source/path}</c> reference cannot be resolved at
/// step-execution time (§17): the source is not configured for the run, or the
/// value is absent at the source.
/// </summary>
/// <remarks>
/// The exception exposes <see cref="SecretSource"/> and <see cref="SecretPath"/> so
/// callers can compose an actionable message, but it never carries a secret <em>value</em>
/// (none exists when resolution fails). Providers map this to a per-step
/// <see cref="Verdict.EnvironmentError"/> (§12.1).
/// </remarks>
public sealed class SecretResolutionException : Exception
{
    /// <summary>
    /// The resolver source identifier from the reference (e.g. <c>env</c>), or a
    /// best-effort description when the reference itself was unparseable.
    /// </summary>
    /// <remarks>
    /// Named <c>SecretSource</c> (not <c>Source</c>) to avoid hiding the inherited
    /// settable <see cref="System.Exception.Source"/> diagnostic property.
    /// </remarks>
    public string SecretSource { get; }

    /// <summary>
    /// The source-specific lookup path from the reference (e.g. an environment
    /// variable name). Never a value.
    /// </summary>
    public string SecretPath { get; }

    /// <summary>
    /// Initialises a new instance with a fully-formed, British-English message and
    /// the reference coordinates that produced the failure.
    /// </summary>
    /// <param name="source">The resolver source identifier.</param>
    /// <param name="path">The source-specific lookup path.</param>
    /// <param name="message">An actionable British-English description.</param>
    public SecretResolutionException(string source, string path, string message)
        : base(message)
    {
        SecretSource = source ?? string.Empty;
        SecretPath = path ?? string.Empty;
    }
}
