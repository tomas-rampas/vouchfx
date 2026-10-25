# vouchfx — VSCode extension

Schema-aware authoring for vouchfx `*.e2e.yaml` integration tests.

This extension binds the **frozen v1 vouchfx JSON Schema** to `*.e2e.yaml`
files so that, inside VSCode, authors get:

- **Completion** — step types and fields offered as you type. The schema uses
  `if`/`then` on the `type` discriminator, so completions are step-type
  specific (e.g. `http.rest` fields differ from `db-assert.postgres` fields).
- **Hover** — the schema carries rich `description`s on every field; hovering a
  key shows its documentation.
- **Inline validation** — invalid keys, wrong types and missing required fields
  are flagged as you edit, before you ever run the suite.

The intelligence itself is provided by the
[Red Hat YAML language server](https://marketplace.visualstudio.com/items?itemName=redhat.vscode-yaml)
(`redhat.vscode-yaml`), which is declared as an **extension dependency** and is
installed automatically alongside this extension. vouchfx supplies the schemas and
file associations, C# syntax highlighting and Test Explorer integration.

## Install

Use VS Code 1.85 or later. Download `vouchfx-<version>.vsix` from the selected
[published GitHub Release](https://github.com/tomas-rampas/vouchfx/releases).
Run **Extensions: Install from VSIX…** in VS Code, or install the downloaded file:

```bash
code --install-extension /absolute/path/vouchfx-<version>.vsix
```

Marketplace publication is optional; the GitHub VSIX is the supported distribution
when that channel is unavailable. For an unreleased candidate, use its supplied
VSIX or build this checkout with the [development commands](#development).
Install a compatible CLI and configure `vouchfx.cliPath` before using Test Explorer;
see the engine's [version-specific installation prerequisites](../../docs/getting-started.md#installing-vouchfx).

The extension also associates vouchfx environment-driver configuration files named
`*.compose.yaml` with the separate Compose environment schema. Native Compose
manifests use their own grammar. Validate the saved `*.e2e.yaml` suite with the
matching CLI to check its external bindings before execution.

## How the schema is bound

**Primary source (REQ-009):** on activation the extension prefers the **live
engine export** from the CLI configured by `vouchfx.cliPath`:

1. `vouchfx list --json` — verifies every step type carries bar-B metadata
   (`requiredFields`, `optionalFields`, `captureSupported`, `familyIntent`).
   A thin/old catalogue is treated as “engine too old” and the live path is
   abandoned.
2. `vouchfx schema --output <globalStorage>/engine-composed-schema.v1.json` —
   writes the composed draft-2020-12 schema; the file is version-checked
   (`x-vouchfx-schema-version: v1`) before use.

**Fallback order** when the CLI is missing, times out, or fails those checks:

1. `vouchfx.schemaPath` (optional workspace/user override).
2. The **small bundled offline schema** at
   `src/schema/composed-schema.v1.json` — still bound declaratively via
   `contributes.yamlValidation` so offline authoring works without a CLI.
   That copy is version-checked the same way (`x-vouchfx-schema-version: v1`)
   and is kept in sync with the engine by a byte-for-byte CI gate; it is **not**
   a hand-maintained parallel field catalogue.

```jsonc
"yamlValidation": [
  { "fileMatch": "*.e2e.yaml", "url": "./src/schema/composed-schema.v1.json" }
]
```

The **vouchfx** Output channel logs which source was used. If you see the
bundled fallback, install or point `vouchfx.cliPath` at an engine that provides
`vouchfx schema` and bar-B `vouchfx list --json`.

## C# in `script.csharp` blocks

A `script.csharp` step carries its author C# in a `code:` block scalar:

```yaml
- id: compute
  type: script.csharp
  code: |
    var id = Vars["greeting"]?.ToString() ?? "world";
    Vars["message"] = $"hello {id}";
```

The extension ships a TextMate **injection** grammar
(`syntaxes/csharp-in-e2eyaml.injection.json`) that embeds VSCode's built-in C#
grammar (`source.cs`) into the block scalar that follows a `code:` key, so that
body is **syntax-highlighted as C#** (keywords, strings, member access) while
the surrounding YAML stays YAML-coloured. The embedded region begins at the
block-scalar body and ends — per the YAML indentation rule — at the first line
indented at or below the `code:` key. Open
[`examples/script-csharp-highlighting.e2e.yaml`](https://github.com/tomas-rampas/vouchfx/blob/main/tools/vscode-vouchfx/examples/script-csharp-highlighting.e2e.yaml)
to eyeball it.

A step can instead reference an external `.csx` file via `file:` (resolved
relative to the `.e2e.yaml` file's own directory) — mutually exclusive with
`code:`, and read once at compile time, spliced verbatim exactly like an
inline body:

```yaml
- id: compute
  type: script.csharp
  file: scripts/compute.csx
```

`.csx` files opened directly get full C#/OmniSharp editing support from
VSCode's standard C# extension, so this is the natural place to grow a
`script.csharp` body once it outgrows a `code:` block scalar. The injection
grammar above only applies to `code:`; a `file:` value is a plain path string,
not an embedded C# region, so there is nothing to highlight inline. See
[`examples/script-csharp-external-file.e2e.yaml`](https://github.com/tomas-rampas/vouchfx/blob/main/tools/vscode-vouchfx/examples/script-csharp-external-file.e2e.yaml).

### C# IntelliSense (status)

| Capability | v1 |
| --- | --- |
| YAML schema autocomplete / hover / validation | ✅ Shipped |
| C# **syntax highlighting** inside `code:` | ✅ Shipped |
| In-block C# **IntelliSense** (completion / diagnostics / hover) | ❌ Deferred fast-follow (target: a later sprint, e.g. S10) |

Full in-block C# IntelliSense is a **documented fast-follow**, not built in v1.
The intended path — an embedded "virtual document" that declares the engine's
`Vouchfx.Engine.Abstractions.ScriptGlobalVariables` globals (`Vars`, `Services`,
`Secrets`, `Webhooks`) in a preamble and forwards completion/diagnostics to the
installed C# extension — is deferred because the modern **C# Dev Kit / Roslyn
LSP does not cleanly expose virtual-document completion forwarding** for an
in-memory `.cs` not bound to an MSBuild project, and the YAML ↔ virtual
position-mapping is non-trivial; a custom Roslyn LSP is a multi-sprint effort.
The full rationale and planned approach are in
[`docs/csharp-intellisense.md`](https://github.com/tomas-rampas/vouchfx/blob/main/tools/vscode-vouchfx/docs/csharp-intellisense.md).

## Test Explorer

**Delivered:** The extension contributes a Test Controller that discovers `*.e2e.yaml` files and renders their scenarios and steps as a tree in VSCode's Test Explorer.

### Discovery

- One parent TestItem per `.e2e.yaml` file in the workspace, labelled by `metadata.name` or the filename if metadata is absent.
- One child TestItem per id-bearing step, anchored at the YAML line on which the step begins.
- The tree updates automatically as files are created, modified, or deleted; a manual refresh button rescans the workspace.

### Running tests

A Run profile invokes the `vouchfx` CLI as:

```
<cliPath> run <file> --events <tmpEventsFile> --no-decorations --shutdown-on-stdin-eof
```

The extension runs the configured vouchfx CLI, parses its `--events` JSON Lines stream, and reports each step's verdict via the Test Explorer surface. Test Explorer requires a CLI that supports `--events`, `--no-decorations` and `--shutdown-on-stdin-eof`. Set `vouchfx.cliPath` if the CLI is not on `PATH`. A CLI that cannot start or produces no usable events fails soft with items marked `errored` and a configuration notice (see [Fail-soft behaviour](#fail-soft-behaviour) below). The extension keeps the CLI's stdin pipe open during a run; cancellation closes that pipe so `--shutdown-on-stdin-eof` requests graceful engine cleanup. If the CLI has not exited after 35 seconds, the extension forcibly terminates its child process. A cancelled run with no usable events is marked `skipped`. Each verdict maps to a VSCode TestItem state:

| vouchfx verdict | VSCode state | What it means |
| --- | --- | --- |
| PASS | passed | The step's assertions held. |
| FAIL | failed | The step's assertions did not hold; a defect in the system under test. |
| ENV_ERROR | errored | Infrastructure failure (container did not start, database unreachable, etc.); not a product defect. |
| INCONCLUSIVE | skipped | The engine could not reach a verdict (timeout, unmet dependency on earlier capture); the test is inconclusive rather than green or red. |

### Fail-soft behaviour

- When the CLI is missing, not executable, or returns a non-zero exit with no events, the affected items are marked `errored` with a clear message.
- A **one-time warning notice** mentions `vouchfx.cliPath` so the author can configure the CLI path if it is not on `PATH`.
- The run handler never throws; Test Explorer integration never regresses the schema authoring experience.

### Failed step decoration

When a step fails (FAIL verdict), the error decorates the failing YAML line in the editor, so the author sees at a glance which step needs attention. The message carries the true vouchfx verdict.

## Configuration

| Setting | Type | Default | Description |
| --- | --- | --- | --- |
| `vouchfx.cliPath` | `string` | `"vouchfx"` | Path or command to the vouchfx CLI. Used for Test Explorer runs **and** for live schema/catalogue export on activation. Set this if the CLI is not on your `PATH` or to select a specific build. |
| `vouchfx.schemaPath` | `string` | `""` | Optional override used only when the live engine schema is unavailable. Absolute, or relative to the workspace root. When empty and the CLI is unavailable, the version-checked bundled offline schema is used. |

### vouchfx.cliPath

The `vouchfx.cliPath` setting lets you point the Test Explorer at a non-standard CLI location. It must be a **single executable path or name** (not a multi-token shell command), because the extension invokes it with an argument array and `shell: false`.

**Default (installed globally):**

```json
{
  "vouchfx.cliPath": "vouchfx"
}
```

**Absolute path to a built binary:**

```json
{
  "vouchfx.cliPath": "/usr/local/bin/vouchfx"
}
```

**Development build from source:**

On Windows, set `vouchfx.cliPath` to a directly executable installed or published `vouchfx.exe`, including the executable from a .NET global tool installation. On Unix, you can run `dotnet run` from source through a small wrapper script (`run-vouchfx.sh`) at the repository root:

*run-vouchfx.sh:*
```bash
#!/bin/sh
script_dir=$(CDPATH= cd "$(dirname "$0")" && pwd) || exit 1
exec dotnet run --project "$script_dir/src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj" -- "$@"
```

Make the wrapper executable, then configure the extension with its absolute path so it works regardless of the extension host's working directory:

```bash
chmod +x /absolute/path/to/vouchfx/run-vouchfx.sh
```

```json
{
  "vouchfx.cliPath": "/absolute/path/to/vouchfx/run-vouchfx.sh"
}
```

The extension registers programmatically with the YAML language server (via its
`registerContributor` API) so the live engine schema, `vouchfx.schemaPath`, or
the bundled fallback can be served for `*.e2e.yaml` files. If the YAML
extension is unavailable, activation degrades gracefully — it never throws, and
Test Explorer registration is independent of the YAML server.

`vouchfx.schemaPath` is intended only for relocating the **same** v1 schema when
the CLI cannot supply a live export (e.g. offline or enterprise hosting of a
copy of `composed-schema.v1.json`), not for pointing at a divergent document.

## Development

```bash
npm install
npm run compile   # tsc -> dist/
npm run lint
npm test          # node:test over the compiled manifest/schema checks
npm run package   # produces vouchfx-<version>.vsix
```

The unit tests run without a full VSCode host: they assert the manifest is
valid, that the bound schema exists/parses/self-identifies as v1, and that the
C# injection grammar is structurally valid and registered. The grammar's
block-scalar boundary is additionally proven with a real tokenisation test that
drives the same engine VSCode uses (`vscode-textmate` + `vscode-oniguruma`),
confirming the embedded C# region starts at the `code:` body and stops at the
block-scalar dedent boundary.

### Packaging dependency override

`package.json` temporarily overrides `@azure/msal-node` beneath `@azure/identity`
to resolve [GHSA-xcx6-jx8p-x3h5](https://github.com/advisories/GHSA-xcx6-jx8p-x3h5).
The upstream Identity dependency range does not yet include the patched MSAL major.
The override applies to the development packaging toolchain; the packaged extension
includes only its `yaml` runtime dependency.

Remove the override when upstream Identity accepts a patched MSAL version. Verify
that change with `npm ci`, `npm audit`, lint, compilation, tests and VSIX packaging.
The compatibility review covers module/API loading and credential construction;
it does not establish live Azure authentication compatibility.

## Licence

Apache-2.0.

## Explicit v2 files

The bundled default remains the frozen v1 schema. For a v2 telemetry opt-out file,
export the matching schema with a supporting engine:

```sh
vouchfx schema --schema-version v2 --output composed-schema.v2.json
```

Add an explicit YAML language-server modeline at the top of the file, pointing to
the export relative to the YAML file:

```yaml
# yaml-language-server: $schema=./composed-schema.v2.json
metadata:
  schemaVersion: v2
  telemetry: false
```

This selects v2 completion and validation without replacing the default v1
association. The engine still requires `metadata.schemaVersion: v2`.
