import * as path from 'node:path';

/**
 * Pure, VSCode-free schema-path resolution.
 *
 * This module deliberately imports nothing from `vscode` so it can be unit
 * tested under a plain Node test runner (the VSCode API is only available
 * inside the extension host). `extension.ts` adapts the VSCode `Uri` world onto
 * these string-in / string-out helpers.
 */

/**
 * The outcome of resolving a `vouchfx.schemaPath` setting value.
 *
 * - `kind: 'bundled'` — the setting is empty (or whitespace only); the caller
 *   should fall back to the schema bundled with the extension.
 * - `kind: 'override'` — the setting points at an external schema; `fsPath` is
 *   the resolved, platform-native filesystem path to bind.
 */
export type SchemaPathResolution =
  | { readonly kind: 'bundled' }
  | { readonly kind: 'override'; readonly fsPath: string };

/**
 * Resolves a raw `vouchfx.schemaPath` setting value into either a "use the
 * bundled schema" signal or a concrete filesystem path.
 *
 * Precedence mirrors the documented contract:
 *  1. Empty / whitespace-only value → bundled schema.
 *  2. Absolute value → used verbatim (normalised).
 *  3. Relative value → resolved against `baseDir` (the document's workspace
 *     folder, or the document's own directory when there is no folder).
 *
 * The function is platform-aware via `node:path`: on Windows a value such as
 * `C:\schemas\v1.json` is recognised as absolute and a relative value is joined
 * with backslash semantics; on POSIX the same logic applies with `/`.
 *
 * @param configured The raw setting value (may be untrimmed or empty).
 * @param baseDir The platform-native directory a relative value resolves
 *   against. Ignored for absolute and empty values.
 * @returns A discriminated resolution describing what schema to bind.
 */
export function resolveSchemaPath(
  configured: string,
  baseDir: string,
): SchemaPathResolution {
  const trimmed = configured.trim();

  if (trimmed.length === 0) {
    return { kind: 'bundled' };
  }

  if (path.isAbsolute(trimmed)) {
    return { kind: 'override', fsPath: path.normalize(trimmed) };
  }

  return { kind: 'override', fsPath: path.resolve(baseDir, trimmed) };
}
