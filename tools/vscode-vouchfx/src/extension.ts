import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';

import {
  checkSchemaVersion,
  fetchEngineSchema,
  MIN_ENGINE_SCHEMA_HINT,
} from './engineSchemaSource';
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

/** Configuration section that exposes CLI path and optional schema override. */
const CONFIG_SECTION = 'vouchfx';
const CONFIG_SCHEMA_PATH = 'schemaPath';
const CONFIG_CLI_PATH = 'cliPath';
const DEFAULT_CLI_PATH = 'vouchfx';

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
 * Mutable holder for the live engine schema path resolved at activate (and
 * refreshed when `vouchfx.cliPath` changes). Shared with `resolveSchemaUri`.
 */
interface EngineSchemaState {
  fsPath: string | undefined;
}

/**
 * Resolves the schema URI to bind for a vouchfx document.
 *
 * Precedence (REQ-009):
 *  1. Live engine export from `vouchfx schema` when the CLI is available and
 *     bar-B-capable (resolved once at activate into `engineState`).
 *  2. A non-empty `vouchfx.schemaPath` setting (scoped to the document).
 *  3. The small bundled offline schema shipped with the extension
 *     (version-checked `x-vouchfx-schema-version: v1`).
 *
 * @param documentUri URI of the `*.e2e.yaml` document being opened.
 * @param bundledSchemaUri `file:` URI of the schema shipped in the extension.
 * @param engineState Live engine schema path (if fetch succeeded).
 * @returns A `file:` URI pointing at the schema to apply.
 */
function resolveSchemaUri(
  documentUri: vscode.Uri,
  bundledSchemaUri: vscode.Uri,
  engineState: EngineSchemaState,
): vscode.Uri {
  if (engineState.fsPath !== undefined) {
    return vscode.Uri.file(engineState.fsPath);
  }

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
 * Resolves the workspace (or default) `vouchfx.cliPath` for schema export.
 */
function resolveWorkspaceCliPath(): string {
  const configured = vscode.workspace
    .getConfiguration(CONFIG_SECTION)
    .get<string>(CONFIG_CLI_PATH, DEFAULT_CLI_PATH);
  const trimmed = (configured ?? DEFAULT_CLI_PATH).trim();
  return trimmed.length > 0 ? trimmed : DEFAULT_CLI_PATH;
}

/**
 * Attempts to load the composed schema from the live engine CLI into
 * `engineState`, logging the outcome on the vouchfx output channel.
 */
async function refreshEngineSchema(
  context: vscode.ExtensionContext,
  engineState: EngineSchemaState,
  channel: vscode.OutputChannel,
  bundledSchemaFsPath: string,
): Promise<void> {
  const cliPath = resolveWorkspaceCliPath();
  const storageDir = context.globalStorageUri.fsPath;

  channel.appendLine(`Resolving schema via engine CLI "${cliPath}"…`);

  let result: Awaited<ReturnType<typeof fetchEngineSchema>>;
  try {
    result = await fetchEngineSchema({ cliPath, storageDir });
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    result = { kind: 'error', message: `unexpected error: ${message}` };
  }

  if (result.kind === 'ok') {
    engineState.fsPath = result.fsPath;
    channel.appendLine(
      `Schema source: live engine CLI (${cliPath}) → ${result.fsPath} ` +
        `(${result.stepTypeCount} step types; x-vouchfx-schema-version: v1).`,
    );
    return;
  }

  engineState.fsPath = undefined;
  channel.appendLine(`Live engine schema unavailable: ${result.message}`);
  logFallbackSource(channel, bundledSchemaFsPath);
}

/**
 * Logs which non-engine source will be used (schemaPath override or bundled).
 */
function logFallbackSource(
  channel: vscode.OutputChannel,
  bundledSchemaFsPath: string,
): void {
  const configured = vscode.workspace
    .getConfiguration(CONFIG_SECTION)
    .get<string>(CONFIG_SCHEMA_PATH, '');
  const resolution = resolveSchemaPath(configured ?? '', process.cwd());

  if (resolution.kind === 'override') {
    channel.appendLine(
      `Schema source: vouchfx.schemaPath override → ${resolution.fsPath}`,
    );
    channel.appendLine(MIN_ENGINE_SCHEMA_HINT);
    return;
  }

  // Bundled offline fallback — version-checked against x-vouchfx-schema-version.
  let versionNote = 'version check skipped (file unreadable)';
  try {
    const text = fs.readFileSync(bundledSchemaFsPath, 'utf8');
    const check = checkSchemaVersion(text);
    versionNote = check.ok
      ? `x-vouchfx-schema-version: ${check.version}`
      : `version check failed: ${check.reason}`;
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    versionNote = `version check failed: ${message}`;
  }

  channel.appendLine(
    `Schema source: bundled offline fallback (${bundledSchemaFsPath}; ${versionNote}).`,
  );
  channel.appendLine(MIN_ENGINE_SCHEMA_HINT);
}

/**
 * Activates the extension.
 *
 * The declarative `contributes.yamlValidation` entry in package.json already
 * binds the bundled schema for the offline happy path with no code at all.
 * This `activate` additionally:
 *  - Prefers a live schema from `vouchfx schema` when the CLI is available
 *    (REQ-009), after verifying bar-B catalogue metadata via `list --json`.
 *  - Falls back to `vouchfx.schemaPath`, then the version-checked bundled copy.
 *  - Registers programmatically with the YAML language server so those sources
 *    are served for `*.e2e.yaml` files.
 *
 * Live engine schema refresh is **fire-and-forget**: `activate` must return
 * quickly and must not block on up to two CLI timeouts. `engineState` is
 * updated when the refresh settles; until then (or on failure) resolution
 * falls through to `schemaPath` / bundled. Rejections are logged so they
 * never surface as unhandled promise rejections.
 *
 * Activation is a no-op for the schema contributor (it does not throw) when
 * `redhat.vscode-yaml` is absent — the extension simply contributes nothing
 * beyond the declarative binding, which itself requires the YAML server.
 * Test Explorer is registered independently and is never gated on schema work.
 */
export async function activate(context: vscode.ExtensionContext): Promise<void> {
  // Test Explorer integration (S10-G-01). Registered FIRST and independently of
  // the schema-contributor wiring below: the schema logic has early `return`s
  // (e.g. when redhat.vscode-yaml is absent), and the Test Explorer must not be
  // gated on the YAML language server being present. `registerTestController`
  // is itself fail-soft and never throws.
  registerTestController(context);

  const channel = vscode.window.createOutputChannel('vouchfx');
  context.subscriptions.push(channel);

  // file: URI of the schema shipped inside the extension. The packaged VSIX
  // keeps src/schema/composed-schema.v1.json (see .vscodeignore), so this
  // resolves both in development and when installed.
  const bundledSchemaUri = vscode.Uri.joinPath(
    context.extensionUri,
    'src',
    'schema',
    'composed-schema.v1.json',
  );
  const bundledSchemaFsPath = bundledSchemaUri.fsPath;

  const engineState: EngineSchemaState = { fsPath: undefined };

  // Prefer live engine export in the background. Do not await: activate must
  // not block on CLI spawn/timeouts (up to 2 × DEFAULT_CLI_TIMEOUT_MS).
  // engineState is mutated when refresh completes; requestSchema reads it
  // on each validation pass. Catch guarantees no unhandled rejection.
  void refreshEngineSchema(context, engineState, channel, bundledSchemaFsPath).catch(
    (error: unknown) => {
      const message = error instanceof Error ? error.message : String(error);
      channel.appendLine(`Live engine schema refresh failed unexpectedly: ${message}`);
      engineState.fsPath = undefined;
    },
  );

  const yamlExtension = vscode.extensions.getExtension<YamlExtensionApi>(YAML_EXTENSION_ID);
  if (!yamlExtension) {
    // No YAML server: nothing to register against. The declarative binding is
    // likewise inert without it. Fail soft — do not throw.
    channel.appendLine(
      'redhat.vscode-yaml not found; programmatic schema contributor skipped ' +
        '(declarative yamlValidation still applies when the YAML server is present).',
    );
    return;
  }

  let api: YamlExtensionApi;
  try {
    api = yamlExtension.isActive ? yamlExtension.exports : await yamlExtension.activate();
  } catch {
    // The YAML extension failed to activate; degrade gracefully.
    channel.appendLine('redhat.vscode-yaml failed to activate; schema contributor skipped.');
    return;
  }

  if (!api || typeof api.registerContributor !== 'function') {
    // Unexpected/older API surface — do not throw, just skip the override hook.
    channel.appendLine(
      'redhat.vscode-yaml API missing registerContributor; schema contributor skipped.',
    );
    return;
  }

  // We resolve engine / override / bundled to a plain `file:` URI.
  // `resolveSchemaUri` already returns a `vscode.Uri` produced via
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
      const target = resolveSchemaUri(
        vscode.Uri.parse(resource),
        bundledSchemaUri,
        engineState,
      );
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
      // is small and read at most once per (file, schema) pair. Use
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
    channel.appendLine('YAML registerContributor returned false; contributor not active.');
    return;
  }

  // Re-resolve when cliPath / schemaPath change. `requestSchema` is re-invoked
  // by the YAML language server on the next validation pass (e.g. the next
  // edit/save); we deliberately do NOT mutate the user's document to force this.
  context.subscriptions.push(
    vscode.workspace.onDidChangeConfiguration((event) => {
      if (event.affectsConfiguration(`${CONFIG_SECTION}.${CONFIG_CLI_PATH}`)) {
        void (async () => {
          await refreshEngineSchema(
            context,
            engineState,
            channel,
            bundledSchemaFsPath,
          );
          void vscode.window.setStatusBarMessage(
            'vouchfx: CLI path changed — schema source refreshed; reopen or edit .e2e.yaml files to re-validate.',
            5000,
          );
        })();
      }
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
