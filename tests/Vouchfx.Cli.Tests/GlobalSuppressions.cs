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

// CA1861 (prefer static readonly over constant array arguments) is a hot-path performance
// rule; in tests the inline literal arrays passed to Assert.Equal / command.Parse are the
// readable, idiomatic form, and the perf cost is irrelevant in a one-shot test run.
[assembly: SuppressMessage(
    "Performance",
    "CA1861:Avoid constant arrays as arguments",
    Justification = "Inline literal arrays are the readable form for test assertions.",
    Scope = "module")]

// CA1859 (use the concrete type for performance) would force private test helpers to expose
// List<T> rather than the IReadOnlyList<T> they conceptually return; the abstraction reads
// better in a test and the perf delta is immaterial here.
[assembly: SuppressMessage(
    "Performance",
    "CA1859:Use concrete types when possible for improved performance",
    Justification = "Test helpers favour the read-only abstraction over a micro-optimisation.",
    Scope = "module")]
