import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { resolveSchemaPath } from './schemaPath';
import { registerTestController } from './testController';

/**
 * File-name glob that identifies a vouchfx test document. Kept identical to the
 * declarative `contributes.yamlValidation[0].fileMatch` in package.json so the
 * happy path (bundled schema) and the override path agree on what a vouchfx
 * file is.
 */
const E2E_FILE_MATCH = '*.e2e.yaml';

/** Identifier of the Red Hat YAML language server extension we delegate to. */
const YAML_EXTENSION_ID = 'redhat.vscode-yaml';

/** Configuration section that exposes the optional schema override. */
const CONFIG_SECTION = 'vouchfx';
const CONFIG_SCHEMA_PATH = 'schemaPath';

/**
 * The minimal slice of the `redhat.vscode-yaml` public API we rely on. The
 * extension exports `registerContributor`, which lets us supply a schema URI
 * for documents whose URI matches our predicate.
 *
 * @see https://github.com/redhat-developer/vscode-yaml#extensions-api
 */
interface YamlExtensionApi {
  /**
   * Registers a schema contributor.
   *
   * @param schema A stable scheme/id string identifying this contributor.
   * @param requestSchema Returns the schema URI for a given document URI, or
   *   `undefined` if this contributor does not own the document. We return a
   *   `file:` URI, which the YAML server reads directly off disk.
   * @param requestSchemaContent Returns the schema text for a URI this
   *   contributor previously claimed. The YAML server only calls this for
   *   schemes it cannot fetch itself; for `file:` URIs it reads the file. We
   *   keep a fail-soft `file:` reader as a defensive fallback.
   * @param label Optional human-readable label.
   * @returns `true` when registration succeeded.
   */
  registerContributor(
    schema: string,
    requestSchema: (resource: string) => string | undefined,
    requestSchemaContent: (uri: string) => string | undefined,
    label?: string,
  ): boolean;
}

/**
 * Stable contributor id passed to `registerContributor`. This is an identifier
 * for our contributor, NOT a URI scheme: the schema URIs we hand back are plain
 * `file:` URIs (which the YAML server resolves itself), avoiding the Windows
 * drive-letter mangling that a custom-scheme round-trip is prone to.
 */
const VOUCHFX_CONTRIBUTOR_ID = 'vouchfx';

/**
 * Resolves the schema URI to bind for a vouchfx document.
 *
 * Precedence:
 *  1. A non-empty `vouchfx.schemaPath` setting (scoped to the document), taken
 *     as absolute or resolved against the document's workspace folder.
 *  2. The schema bundled with the extension.
 *
 * @param documentUri URI of the `*.e2e.yaml` document being opened.
 * @param bundledSchemaUri `file:` URI of the schema shipped in the extension.
 * @returns A `file:` URI string pointing at the schema to apply.
 */
function resolveSchemaUri(
  documentUri: vscode.Uri,
  bundledSchemaUri: vscode.Uri,
): vscode.Uri {
  const configured = vscode.workspace
    .getConfiguration(CONFIG_SECTION, documentUri)
    .get<string>(CONFIG_SCHEMA_PATH, '');

  // Base directory for a relative override: the document's workspace folder
  // when we have one, otherwise the document's own directory. `.fsPath` gives
  // the platform-native path the pure resolver joins against.
  const folder = vscode.workspace.getWorkspaceFolder(documentUri);
  const baseDir = folder ? folder.uri.fsPath : path.dirname(documentUri.fsPath);

  const resolution = resolveSchemaPath(configured, baseDir);
  if (resolution.kind === 'bundled') {
    return bundledSchemaUri;
  }
  return vscode.Uri.file(resolution.fsPath);
}

/**
 * Tests whether a document URI is a vouchfx test file (matches `*.e2e.yaml`).
 *
 * @param resource The document URI string handed to us by the YAML server.
 */
function isE2eDocument(resource: string): boolean {
  let fileName: string;
  try {
    fileName = path.posix.basename(vscode.Uri.parse(resource).path);
  } catch {
    return false;
  }
  // E2E_FILE_MATCH is `*.e2e.yaml`; match the literal suffix case-insensitively.
  const suffix = E2E_FILE_MATCH.slice(1).toLowerCase();
  return fileName.toLowerCase().endsWith(suffix);
}

/**
 * Activates the extension.
 *
 * The declarative `contributes.yamlValidation` entry in package.json already
 * binds the bundled schema for the happy path with no code at all. This
 * `activate` adds ONE thing on top: programmatic registration with the YAML
 * language server so a workspace-level `vouchfx.schemaPath` setting can point
 * `*.e2e.yaml` files at an internal/enterprise schema copy instead.
 *
 * Activation is a no-op (it does not throw) when `redhat.vscode-yaml` is
 * absent — the extension simply contributes nothing beyond the declarative
 * binding, which itself requires the YAML server to take effect.
 */
export async function activate(context: vscode.ExtensionContext): Promise<void> {
  // Test Explorer integration (S10-G-01). Registered FIRST and independently of
  // the schema-contributor wiring below: the schema logic has early `return`s
  // (e.g. when redhat.vscode-yaml is absent), and the Test Explorer must not be
  // gated on the YAML language server being present. `registerTestController`
  // is itself fail-soft and never throws.
  registerTestController(context);

  // file: URI of the schema shipped inside the extension. The packaged VSIX
  // keeps src/schema/composed-schema.v1.json (see .vscodeignore), so this
  // resolves both in development and when installed.
  const bundledSchemaUri = vscode.Uri.joinPath(
    context.extensionUri,
    'src',
    'schema',
    'composed-schema.v1.json',
  );

  const yamlExtension = vscode.extensions.getExtension<YamlExtensionApi>(YAML_EXTENSION_ID);
  if (!yamlExtension) {
    // No YAML server: nothing to register against. The declarative binding is
    // likewise inert without it. Fail soft — do not throw.
    return;
  }

  let api: YamlExtensionApi;
  try {
    api = yamlExtension.isActive ? yamlExtension.exports : await yamlExtension.activate();
  } catch {
    // The YAML extension failed to activate; degrade gracefully.
    return;
  }

  if (!api || typeof api.registerContributor !== 'function') {
    // Unexpected/older API surface — do not throw, just skip the override hook.
    return;
  }

  // We resolve both the bundled-default and the override to a plain `file:`
  // URI. `resolveSchemaUri` already returns a `vscode.Uri` produced via
  // `vscode.Uri.file(...)` / `vscode.Uri.joinPath(...)`, so `.toString()`
  // yields a well-formed `file:` URI that the YAML server reads directly off
  // disk — no custom scheme, no drive-letter round-trip. `requestSchemaContent`
  // remains as a fail-soft `file:` reader in case the server ever asks us for
  // the bytes.
  const registered = api.registerContributor(
    VOUCHFX_CONTRIBUTOR_ID,
    (resource: string): string | undefined => {
      if (!isE2eDocument(resource)) {
        return undefined;
      }
      const target = resolveSchemaUri(vscode.Uri.parse(resource), bundledSchemaUri);
      // A plain file: URI. The redhat.vscode-yaml server fetches file: URIs
      // itself, so the path survives intact on Windows (drive letter and all).
      return target.toString();
    },
    (uri: string): string | undefined => {
      let parsed: vscode.Uri;
      try {
        parsed = vscode.Uri.parse(uri);
      } catch {
        return undefined;
      }
      if (parsed.scheme !== 'file') {
        // The server only delegates schemes it cannot fetch; we only ever hand
        // back file: URIs, so anything else is not ours.
        return undefined;
      }
      // Synchronous read: the API contract requires a string return. The schema
      // is small (~19 KB) and read at most once per (file, schema) pair. Use
      // `parsed.fsPath` so the platform-native path (with the Windows drive
      // letter) is reconstructed from the file: URI without manual parsing.
      try {
        return fs.readFileSync(parsed.fsPath, 'utf8');
      } catch {
        return undefined;
      }
    },
    'vouchfx .e2e.yaml schema',
  );

  if (!registered) {
    return;
  }

  // Surface override-setting changes. `requestSchema` is re-invoked by the YAML
  // language server on the next validation pass (e.g. the next edit/save), at
  // which point the new `vouchfx.schemaPath` value is picked up automatically;
  // we deliberately do NOT mutate the user's document to force this.
  context.subscriptions.push(
    vscode.workspace.onDidChangeConfiguration((event) => {
      if (event.affectsConfiguration(`${CONFIG_SECTION}.${CONFIG_SCHEMA_PATH}`)) {
        void vscode.window.setStatusBarMessage(
          'vouchfx: schema override changed — reopen or edit .e2e.yaml files to re-validate.',
          5000,
        );
      }
    }),
  );
}

/** Deactivates the extension. No resources require explicit teardown. */
export function deactivate(): void {
  // Intentionally empty: all disposables are owned by `context.subscriptions`.
}
