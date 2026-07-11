# Changelog

All notable changes to the vouchfx VSCode extension are documented here. The
format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - Unreleased

### Added

- Initial scaffold of the vouchfx VSCode extension.
- Bundled, offline-safe copy of the frozen **v1** composed JSON Schema, bound to
  `*.e2e.yaml` files via `contributes.yamlValidation` (delegating schema
  completion, hover and validation to the Red Hat YAML language server).
- `vouchfx.schemaPath` setting: an optional override pointing at an
  internal/enterprise schema copy, registered programmatically with the YAML
  language server.
- **C# syntax highlighting** inside `script.csharp` `code:` block scalars, via a
  TextMate injection grammar (`syntaxes/csharp-in-e2eyaml.injection.json`) that
  embeds the built-in C# grammar (`source.cs`) into the YAML block scalar,
  bounded by the YAML indentation rule. Registered through
  `contributes.grammars` and bundled in the `.vsix`. An example fixture
  (`examples/script-csharp-highlighting.e2e.yaml`) is included for visual
  verification.
- A "C# IntelliSense (status)" note recording that full in-block C# completion /
  diagnostics is a documented **fast-follow** (see `docs/csharp-intellisense.md`)
  — v1 ships schema intelligence + C# syntax highlighting.
- Node `node:test` unit tests validating the manifest wiring and the bundled
  schema (existence, parseability, `x-vouchfx-schema-version: v1`), plus
  structural and real-tokenisation (`vscode-textmate` + `vscode-oniguruma`)
  tests proving the C# injection grammar's block-scalar boundary.
- Schema support for `script.csharp`'s `file` field (an external `.csx` file
  reference, mutually exclusive with `code:`): autocomplete/hover/validation via
  the bundled schema, kept in sync with the engine by the same byte-for-byte CI
  gate. An example fixture (`examples/script-csharp-external-file.e2e.yaml`) is
  included alongside its referenced `.csx` file.
- **S11-D-01 reference fixture** (`src/test/fixtures/reference-four-tech.e2e.yaml`):
  a faithful mirror of the canonical four-technology reference scenario
  (`examples/reference/reference.e2e.yaml`) used by the editor-surface tests.
  Ten `node:test` assertions in `src/test/referenceFourTech.test.ts` verify that
  `parseE2eOutline` discovers all seven id-bearing steps as TestItems in document
  order with non-overlapping line ranges (covering `code: |`, `verifyMode: RETRY`,
  and `capture:` paths), and that `mapVerdict('PASS')` correctly maps a Pass event
  for the reference scenario to the `passed` editor state.
