// Pure, VSCode-free engine schema source (REQ-009 / engine-schema-and-catalogue-export).
//
// The extension MUST prefer the live engine export (`vouchfx schema` + bar-B
// `vouchfx list --json`) over a hard-coded field catalogue so validation and
// completion stay aligned with the installed CLI. This module:
//
//   1. Spawns `<cliPath> list --json` and verifies every step entry carries
//      requiredFields / optionalFields / captureSupported / familyIntent (bar B).
//   2. Spawns `<cliPath> schema --output <storageDir/…>` with a timeout.
//   3. Version-checks the written document (`x-vouchfx-schema-version: v1`).
//
// When the CLI is missing, too old (thin catalogue), or times out, callers fall
// back to `vouchfx.schemaPath` or the small bundled offline schema — both of
// which must be documented and version-checked. This file imports nothing from
// `vscode` so the catalogue parser and fetch orchestration are unit-testable
// under a plain Node test runner (injectable `spawnFn`).

import { spawn } from 'node:child_process';
import * as fs from 'node:fs';
import * as path from 'node:path';

/** File name written under the caller's storage directory for the live schema. */
export const ENGINE_SCHEMA_FILE_NAME = 'engine-composed-schema.v1.json';

/** Default timeout for each CLI invocation (ms). */
export const DEFAULT_CLI_TIMEOUT_MS = 10_000;

/**
 * Bar-B catalogue field names required on every step-type entry (REQ-002).
 * An engine that omits any of these is treated as too old for live export.
 */
export const BAR_B_FIELDS = [
  'requiredFields',
  'optionalFields',
  'captureSupported',
  'familyIntent',
] as const;

/**
 * Hint shown when falling back from the live engine. The minimum engine is any
 * build that ships `vouchfx schema` and bar-B `vouchfx list --json`.
 */
export const MIN_ENGINE_SCHEMA_HINT =
  'Minimum engine: a vouchfx CLI that provides `vouchfx schema` and bar-B ' +
  '`vouchfx list --json` (requiredFields, optionalFields, captureSupported, familyIntent).';

/** Expected value of the composed schema's `x-vouchfx-schema-version` marker. */
export const SCHEMA_LANGUAGE_VERSION = 'v1';

// ---------------------------------------------------------------------------
// Spawn abstraction (injectable for tests)
// ---------------------------------------------------------------------------

export interface SpawnResult {
  readonly code: number | null;
  readonly stdout: string;
  readonly stderr: string;
  readonly timedOut: boolean;
}

/**
 * Spawns a CLI with an argument array (`shell: false`) and a hard timeout.
 * Resolves on process exit, spawn error, or timeout — never rejects.
 */
export type SpawnFn = (
  cliPath: string,
  args: readonly string[],
  timeoutMs: number,
) => Promise<SpawnResult>;

/**
 * Grace period after the first timeout kill before a second kill attempt.
 * Cross-platform: Node maps `child.kill()` / `child.kill('SIGKILL')` to
 * `TerminateProcess` on Windows, so a second call is a safe fallback when the
 * first kill fails to reap the child (or the `close` event never fires).
 */
const KILL_FALLBACK_MS = 250;

/**
 * Default `child_process.spawn` implementation used in production.
 *
 * Arguments are passed as an array with `shell: false` (no shell quoting /
 * injection). On timeout the child is killed (with a short second-kill
 * fallback) and the promise is **always** settled with `timedOut: true` —
 * even when kill fails or the process never emits `close`. Never rejects.
 */
export function defaultSpawn(
  cliPath: string,
  args: readonly string[],
  timeoutMs: number,
): Promise<SpawnResult> {
  return new Promise<SpawnResult>((resolve) => {
    let settled = false;
    let stdout = '';
    let stderr = '';
    let timedOut = false;
    let killFallbackTimer: ReturnType<typeof setTimeout> | undefined;

    let child: ReturnType<typeof spawn>;
    try {
      child = spawn(cliPath, [...args], { windowsHide: true, shell: false });
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      resolve({ code: null, stdout: '', stderr: message, timedOut: false });
      return;
    }

    const finish = (code: number | null, errText: string): void => {
      if (settled) {
        return;
      }
      settled = true;
      clearTimeout(timer);
      if (killFallbackTimer !== undefined) {
        clearTimeout(killFallbackTimer);
      }
      resolve({
        code,
        stdout,
        stderr: errText.length > 0 ? errText : stderr,
        timedOut,
      });
    };

    const timer = setTimeout(() => {
      timedOut = true;
      try {
        child.kill();
      } catch {
        // Best effort — still settle below.
      }
      // Second kill if the first did not reap the process (Windows-compatible:
      // Node maps SIGKILL to TerminateProcess). Independent of whether kill
      // throws, always settle so callers never hang waiting on `close`.
      killFallbackTimer = setTimeout(() => {
        if (settled) {
          return;
        }
        try {
          child.kill('SIGKILL');
        } catch {
          // Best effort.
        }
        finish(null, stderr);
      }, KILL_FALLBACK_MS);
    }, timeoutMs);

    child.stdout?.on('data', (chunk: Buffer | string) => {
      stdout += chunk.toString();
    });
    child.stderr?.on('data', (chunk: Buffer | string) => {
      stderr += chunk.toString();
    });

    child.on('error', (error) => {
      finish(null, error.message);
    });

    child.on('close', (code) => {
      finish(code, stderr);
    });
  });
}

// ---------------------------------------------------------------------------
// Pure catalogue / schema checks
// ---------------------------------------------------------------------------

export type CatalogueBarBResult =
  | { readonly ok: true; readonly stepTypeCount: number }
  | { readonly ok: false; readonly reason: string };

/**
 * Parses `vouchfx list --json` stdout and verifies every step-type entry
 * carries the bar-B shape-level fields (REQ-002 / REQ-009).
 *
 * A thin (pre-bar-B) catalogue is reported as failure so the caller can fall
 * back rather than silently using an incomplete engine.
 */
export function checkCatalogueBarB(listJsonText: string): CatalogueBarBResult {
  let parsed: unknown;
  try {
    parsed = JSON.parse(listJsonText);
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    return { ok: false, reason: `list --json is not valid JSON: ${message}` };
  }

  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
    return { ok: false, reason: 'list --json root must be a JSON object' };
  }

  const root = parsed as Record<string, unknown>;
  const stepTypes = root['stepTypes'];
  if (!Array.isArray(stepTypes)) {
    return {
      ok: false,
      reason: 'list --json missing stepTypes array (engine too old or wrong document)',
    };
  }

  if (stepTypes.length === 0) {
    return { ok: false, reason: 'list --json stepTypes is empty' };
  }

  for (let i = 0; i < stepTypes.length; i++) {
    const entry = stepTypes[i];
    if (entry === null || typeof entry !== 'object' || Array.isArray(entry)) {
      return { ok: false, reason: `stepTypes[${i}] is not an object` };
    }
    const record = entry as Record<string, unknown>;
    const typeLabel =
      typeof record['type'] === 'string' && record['type'].length > 0
        ? record['type']
        : `stepTypes[${i}]`;

    for (const field of BAR_B_FIELDS) {
      if (!(field in record)) {
        return {
          ok: false,
          reason:
            `Catalogue entry "${typeLabel}" is missing bar-B field "${field}" ` +
            `(engine too old for live schema export)`,
        };
      }
    }

    if (!Array.isArray(record['requiredFields'])) {
      return {
        ok: false,
        reason: `Catalogue entry "${typeLabel}": requiredFields must be an array`,
      };
    }
    if (!Array.isArray(record['optionalFields'])) {
      return {
        ok: false,
        reason: `Catalogue entry "${typeLabel}": optionalFields must be an array`,
      };
    }
    if (typeof record['captureSupported'] !== 'boolean') {
      return {
        ok: false,
        reason: `Catalogue entry "${typeLabel}": captureSupported must be a boolean`,
      };
    }
    if (typeof record['familyIntent'] !== 'string' || record['familyIntent'].length === 0) {
      return {
        ok: false,
        reason: `Catalogue entry "${typeLabel}": familyIntent must be a non-empty string`,
      };
    }
  }

  return { ok: true, stepTypeCount: stepTypes.length };
}

export type SchemaVersionResult =
  | { readonly ok: true; readonly version: string }
  | { readonly ok: false; readonly reason: string };

/**
 * Verifies a composed schema document self-identifies as the frozen language
 * version (`x-vouchfx-schema-version: v1`). Used for both the live engine export
 * and the small bundled offline fallback.
 */
export function checkSchemaVersion(schemaJsonText: string): SchemaVersionResult {
  let parsed: unknown;
  try {
    parsed = JSON.parse(schemaJsonText);
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    return { ok: false, reason: `schema is not valid JSON: ${message}` };
  }

  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
    return { ok: false, reason: 'schema root must be a JSON object' };
  }

  const version = (parsed as Record<string, unknown>)['x-vouchfx-schema-version'];
  if (version !== SCHEMA_LANGUAGE_VERSION) {
    return {
      ok: false,
      reason:
        `expected x-vouchfx-schema-version "${SCHEMA_LANGUAGE_VERSION}", ` +
        `got ${JSON.stringify(version)}`,
    };
  }

  return { ok: true, version: SCHEMA_LANGUAGE_VERSION };
}

// ---------------------------------------------------------------------------
// Live engine fetch
// ---------------------------------------------------------------------------

export type FetchEngineSchemaResult =
  | { readonly kind: 'ok'; readonly fsPath: string; readonly stepTypeCount: number }
  | { readonly kind: 'error'; readonly message: string };

export interface FetchEngineSchemaOptions {
  /** Path or command name for the vouchfx CLI (`vouchfx.cliPath`). */
  readonly cliPath: string;
  /**
   * Directory that already exists (or will be created by the caller). The
   * composed schema is written here as {@link ENGINE_SCHEMA_FILE_NAME}.
   * Prefer the extension's `globalStorageUri` so the file persists across
   * activations; a temp directory is also acceptable.
   */
  readonly storageDir: string;
  /** Per-invocation timeout in ms (default {@link DEFAULT_CLI_TIMEOUT_MS}). */
  readonly timeoutMs?: number;
  /** Injectable spawn (tests); defaults to {@link defaultSpawn}. */
  readonly spawnFn?: SpawnFn;
}

/**
 * Obtains the composed schema from the live vouchfx CLI.
 *
 * Order of work:
 *  1. `list --json` → bar-B catalogue gate (fail closed if thin / missing).
 *  2. `schema --output <storageDir/engine-composed-schema.v1.json>`.
 *  3. Read the file and version-check `x-vouchfx-schema-version: v1`.
 *
 * @returns `{ kind: 'ok', fsPath }` on success, or `{ kind: 'error', message }`
 *   describing why the caller should fall back.
 */
export async function fetchEngineSchema(
  options: FetchEngineSchemaOptions,
): Promise<FetchEngineSchemaResult> {
  const {
    cliPath,
    storageDir,
    timeoutMs = DEFAULT_CLI_TIMEOUT_MS,
    spawnFn = defaultSpawn,
  } = options;

  const trimmedCli = cliPath.trim();
  if (trimmedCli.length === 0) {
    return { kind: 'error', message: 'cliPath is empty' };
  }

  // --- 1. Catalogue bar-B gate ------------------------------------------------
  const listResult = await spawnFn(trimmedCli, ['list', '--json'], timeoutMs);
  if (listResult.timedOut) {
    return {
      kind: 'error',
      message: `\`list --json\` timed out after ${timeoutMs} ms`,
    };
  }
  if (listResult.code === null) {
    return {
      kind: 'error',
      message:
        `could not start CLI for \`list --json\`: ${listResult.stderr || 'unknown error'}`,
    };
  }
  if (listResult.code !== 0) {
    const detail = summariseCliFailure(listResult);
    return {
      kind: 'error',
      message: `\`list --json\` exited ${listResult.code}${detail}`,
    };
  }

  const catalogue = checkCatalogueBarB(listResult.stdout);
  if (!catalogue.ok) {
    return { kind: 'error', message: catalogue.reason };
  }

  // --- 2. Schema export to storage --------------------------------------------
  try {
    fs.mkdirSync(storageDir, { recursive: true });
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    return { kind: 'error', message: `cannot create storage directory: ${message}` };
  }

  const schemaPath = path.join(storageDir, ENGINE_SCHEMA_FILE_NAME);
  const schemaResult = await spawnFn(
    trimmedCli,
    ['schema', '--output', schemaPath],
    timeoutMs,
  );

  if (schemaResult.timedOut) {
    return {
      kind: 'error',
      message: `\`schema --output\` timed out after ${timeoutMs} ms`,
    };
  }
  if (schemaResult.code === null) {
    return {
      kind: 'error',
      message:
        `could not start CLI for \`schema\`: ${schemaResult.stderr || 'unknown error'}`,
    };
  }
  if (schemaResult.code !== 0) {
    const detail = summariseCliFailure(schemaResult);
    return {
      kind: 'error',
      message: `\`schema --output\` exited ${schemaResult.code}${detail}`,
    };
  }

  // --- 3. Read + version-check the written document ---------------------------
  let schemaText: string;
  try {
    schemaText = fs.readFileSync(schemaPath, 'utf8');
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    return {
      kind: 'error',
      message: `schema command succeeded but file is unreadable at "${schemaPath}": ${message}`,
    };
  }

  const version = checkSchemaVersion(schemaText);
  if (!version.ok) {
    return {
      kind: 'error',
      message: `engine schema failed version check: ${version.reason}`,
    };
  }

  return {
    kind: 'ok',
    fsPath: schemaPath,
    stepTypeCount: catalogue.stepTypeCount,
  };
}

/** Short stderr / timed-out note for error messages (single line). */
function summariseCliFailure(result: SpawnResult): string {
  const stderr = result.stderr.trim().replace(/\s+/g, ' ');
  if (stderr.length === 0) {
    return '';
  }
  const clipped = stderr.length > 240 ? `${stderr.slice(0, 240)}…` : stderr;
  return `: ${clipped}`;
}
