// This file contains assembly-level diagnostic suppressions.
// CA1707 (identifiers should not contain underscores) is suppressed for the
// test assembly only: xUnit's Given_When_Then naming convention for test
// methods uses underscores deliberately and is the established .NET testing
// community standard. The suppression does not apply to production assemblies.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "xUnit test methods use Given_When_Then underscore convention.",
    Scope = "module")]
