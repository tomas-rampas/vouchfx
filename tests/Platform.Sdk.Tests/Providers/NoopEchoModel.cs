// Throwaway reference provider for S01-F-01 contract tests.
// This file contains the step model record for the noop.echo provider.
using Platform.Sdk;

namespace Platform.Sdk.Tests.Providers;

/// <summary>
/// Strongly-typed step model for the <c>noop.echo</c> reference provider.
/// Used only in unit tests to demonstrate that the provider contract is
/// implementable.
/// </summary>
/// <param name="Message">
/// The message text to echo.  Must be non-empty for the model to be valid.
/// </param>
public sealed record NoopEchoModel(string Message) : IStepModel;
