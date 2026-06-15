# Example.Steps.Echo — Provider SDK dry-run (`echo.text`)

A worked example that an **outside contributor** can copy to build a **non-Core**
vouchfx provider against the frozen v1 `Platform.Sdk` contract, and prove it works
**end to end without Docker** via the published `Platform.Sdk.Testing` harness.

`echo.text` is the smallest provider that demonstrates `{placeholder}` substitution: it
resolves its `text` field against the shared `Vars` global at execution time (the same
mechanism the Core `http.rest` provider uses for its `path`) and asserts the resolved
text equals a constant `expect`. It has **no** infrastructure dependency, so the
integration fixture (`Example.Steps.Echo.Tests`) runs the full compile-once / isolate /
run pipeline directly — no container required.

## Authoring a provider in four steps

This is the entire surface a contributor touches; the engine discovers and drives the
rest reflectively.

1. **Add a project that references only `Platform.Sdk`.**
   See `Example.Steps.Echo.csproj`. A non-Core provider depends solely on the frozen v1
   SDK contract — it takes **no** reference to any `Platform.Engine.*` assembly. The
   project lives in a **non-reserved** namespace (`Example.Steps.Echo`); `Platform.Steps.*`
   and `Platform.Engine.*` are reserved for the engine and its Core providers and a
   customer DLL declaring them is refused at startup (§5.6).

2. **Define a strongly-typed model record implementing `IStepModel`.**
   See `EchoTextModel.cs` — `public sealed record EchoTextModel(string Text, string Expect)`.
   One immutable record per step kind, one property per author-supplied field. Never a
   `Dictionary<string, object>` (§13).

3. **Implement the v1 contract on one `[StepProvider]`-decorated class.**
   See `EchoTextProvider.cs` — a single class implementing `IStepProvider` (identity +
   schema fragment), `IStepBinder<T>` (YAML → model), `IStepValidator<T>` (model rules),
   and `IStepCompiler<T>` (model → `CsxFragment`). The optional
   `IResourceContributor<T>` is **deliberately not** implemented — that omission is
   exactly why the fixture runs without Docker.

4. **Let the reflective `StepKindRegistry` discover it.**
   There is no registration code. The registry is built from the provider assembly and
   discovers every `[StepProvider]`-decorated type at startup. The test fixture passes
   `typeof(EchoTextProvider).Assembly` to `ProviderTestHarness.RunSingleStepAsync` and
   the provider is found, schema-composed, bound, validated, compiled once, and run
   isolated — with the verdict returned as **data** (Pass / Fail / null-with-errors),
   never as an exception.

## Running the dry-run

```powershell
dotnet test examples/Example.Steps.Echo.Tests
```

Four tests prove the four behaviours that matter:

| Test | Proves |
| --- | --- |
| `…TextEqualsExpect…_Pass` | happy path: schema → bind → validate → emit → compile-once → run-isolated yields `Pass`, with the echoed text in the observation and a non-negative duration |
| `…TextNotEqualExpect…_Fail` | the assertion is real — a mismatch yields `Fail` (not an exception, not `EnvironmentError`) |
| `…MissingRequiredField_SchemaRejected_VerdictNull` | the provider's `SchemaFragment` is actually composed in and enforced — omitting the required `expect` is schema-rejected (`Verdict == null`, `SchemaErrors` populated) before the step runs |
| `…PlaceholderText_ResolvesAgainstVars…` | `{placeholder}` substitution is wired into the emitted block via the shared `Substitute_Helpers.Resolve`, exactly as `http.rest` does for its `path` |

## Friction log

Findings from running the dry-run from an outside-contributor's point of view. The
intent is to surface every rough edge a real contributor would hit, and whether it was a
cheap fix.

### F1 — the single-step harness starts with empty `Vars` and does not seed `variables:`

`ProviderTestHarness.RunSingleStepAsync` starts with an **empty** `Vars` map and does
**not** seed the scenario's `variables:` block before the step runs (only the engine's
production `ScenarioRunner` does that). Under the substitution contract an absent
placeholder key resolves to the **empty string**, so a bare `text: "{greeting}"` resolves
to `""` even when the YAML declares `variables: { greeting: "hello, world" }`.

This is **expected and documented** behaviour, not a bug — but it is a genuine trap for a
contributor whose mental model is "the harness runs my scenario like the engine does".
The placeholder fixture therefore asserts the `{greeting}` step Passes against an empty
`expect: ""`, and its remarks call this out. The non-empty-value substitution path is
exercised by the in-repo Docker fixtures, where the engine seeds the `variables:` block.

**Fix cost:** none in code — it is a harness contract to *understand*. The cheap mitigation
for a contributor who wants a non-empty value under the single-step harness is to assert
against the empty result (as the fixture does) or move to a Docker fixture for the seeded
path.

### F2 — the emitted CSX must stay inside the minimal Roslyn reference set

First implementation built the step's structured observation at runtime via
`System.Text.Json.JsonSerializer.Serialize`. That compiled fine in the *provider* (which
references `System.Text.Json`), but the **emitted CSX** failed Roslyn compilation with
`CS0234: The type or namespace name 'Json' does not exist in the namespace 'System.Text'`.

Root cause: the engine compiles the assembled script against a **minimal** base reference
set (`System.Private.CoreLib` / `System.Runtime` / `System.Collections` /
`System.Text.RegularExpressions` + `Platform.Engine.Abstractions`). `System.Text.Json` is
**not** in that set. A provider can extend the reference set by implementing the optional
`ICompileReferenceContributor` (as `http.rest` does to pull in `JsonPath.Net`), but
`echo.text` is intentionally dependency-free and the single-step harness does not run that
contributor stage anyway. So the emitted body must depend only on what the minimal set
guarantees — exactly the constraint the `hello.console` template calls out in its comments.

**Fix:** replaced the runtime `JsonSerializer.Serialize` call in the emitted body with a
tiny, dependency-free JSON string escaper inside `EchoText_Helpers` (escaping quote,
backslash and the C0 controls; `\uXXXX` for the rest). `JsonSerializer.Serialize` is still
used at **emit time** in the provider to turn author text into safe C# string literals —
that runs in the provider, not in the emitted CSX, so it is fine.

**Fix cost:** cheap — a handful of lines and one re-test. The lesson generalises: anything
the emitted block calls at runtime must be in the engine's minimal reference set, or the
provider must contribute the reference explicitly. The `hello.console` template documents
this; following it from the start would have avoided the detour.

### Everything else was friction-free

- Mirroring `HelloConsoleProvider` (structure, comment density, §13.3.1 rules) and
  `HttpRestProvider`'s `SubstituteHelper.Source` append pattern made the implementation
  mechanical.
- `dotnet sln add` generated the project GUIDs and build configurations correctly; the two
  Echo projects appear alongside the `Example.Steps.Hello` entries with no manual editing.
- The build was clean first time (0 warnings, 0 errors) and `dotnet format --verify-no-changes`
  reported no changes.
