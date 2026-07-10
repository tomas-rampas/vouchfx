# C# IntelliSense in `script.csharp` blocks — status and fast-follow

This note records the v1 scope decision for editor support of the C# that
authors write inside a `script.csharp` step's `code:` block. It is the
**documented fallback** required by the v1 acceptance criterion ("C#
completion/diagnostics work … **or** the documented fallback is invoked and
recorded").

## What ships in v1

| Capability | v1 | How |
| --- | --- | --- |
| YAML schema autocomplete / hover / validation | ✅ Shipped | Frozen v1 JSON Schema bound via `contributes.yamlValidation`, served by the Red Hat YAML language server (delivered in T6). |
| **C# syntax highlighting** inside `code:` blocks | ✅ Shipped (this task) | A TextMate **injection** grammar (`syntaxes/csharp-in-e2eyaml.injection.json`) embeds VSCode's built-in C# grammar (`source.cs`) into the YAML block scalar that follows a `code:` key. Colours keywords, strings, member access, etc. |
| **In-block C# IntelliSense** (completion, hover, diagnostics, go-to-def) | ❌ Deferred fast-follow | See below. Target: a later sprint (e.g. **S10**). |

Syntax highlighting is colour only — it runs entirely inside the TextMate
tokeniser and needs no language service, no project, and no network. It does
**not** provide completion, type checking, or diagnostics for the embedded C#.

## Why full IntelliSense is deferred (the spike finding)

The intended path is **Option A — an embedded "virtual document"**: when the
cursor is inside a `code:` block, the extension would synthesise an in-memory
`.cs` document by concatenating

1. a **preamble** that declares the engine globals the script runs against —
   the `Vouchfx.Engine.Abstractions.ScriptGlobalVariables` surface
   (`Vars`, `Services`, `Secrets`, `Webhooks`) — so member completion on those
   resolves, plus
2. the author's block-scalar body,

then forward completion/hover/diagnostic requests to the installed C# language
service and map positions back into the `.e2e.yaml` document.

This is deferred for concrete, current reasons:

- **The modern C# Dev Kit / Roslyn LSP does not cleanly expose
  virtual-document completion forwarding** for an in-memory `.cs` file that is
  not bound to an MSBuild project. The older OmniSharp model tolerated
  loose/miscellaneous files; the Roslyn-based language server expects a
  project/workspace, so an un-projected synthetic buffer does not reliably get
  semantic completion or diagnostics. Driving it would mean fabricating (and
  keeping in sync) a throwaway project that references the engine's
  `Vouchfx.Engine.Abstractions` assembly.
- **YAML ↔ virtual-document position mapping is non-trivial.** The block-scalar
  body is dedented by its indentation and may use literal (`|`) or folded (`>`)
  chomping; every request and every diagnostic range has to be translated
  between the two coordinate spaces, including the synthetic preamble offset.
- **A custom Roslyn LSP (Option B)** — hosting our own Roslyn workspace seeded
  with the engine reference and serving completion/diagnostics over the embedded
  range — is the robust long-term answer, but it is a **multi-sprint effort**
  (workspace lifecycle, incremental parse, the same position-mapping, packaging
  the Roslyn host with the extension).

Given that, v1 ships **highlighting now** (high value, low risk, no language
service) and records **IntelliSense as a fast-follow**.

## Planned approach for the fast-follow

When the fast-follow is scheduled:

1. Prefer **Option A** with a minimal generated companion project that
   references `Vouchfx.Engine.Abstractions`, so the Roslyn LSP has a workspace
   and the `ScriptGlobalVariables` globals resolve.
2. Build the preamble from the engine's actual `ScriptGlobalVariables` contract
   (kept in lock-step with the frozen Provider SDK surface) so completions match
   what the compiled delegate really sees.
3. Implement bidirectional position mapping for both literal and folded block
   scalars, including the preamble offset.
4. Fall back to **Option B (custom Roslyn host)** only if Option A cannot be
   made reliable across the C# Dev Kit and the standalone C# extension.

Until then, highlighting plus the YAML schema intelligence is the supported v1
authoring experience.
