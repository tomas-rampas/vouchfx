// Pure, VSCode-free temp-directory helpers for one Test Explorer run's events
// file (N1 security hardening).
//
// Why a DIRECTORY and not a predictable file name: the events file is created by
// the spawned CLI, so handing it a guessable path under `os.tmpdir()` (pid +
// timestamp + Math.random()) leaves a window on a shared tmpdir where another
// local user could pre-create that path or point a symlink at it. `mkdtempSync`
// instead atomically creates a fresh directory we own (mode 0700 on POSIX); the
// events file then lives inside it, out of reach of that race.
//
// Kept free of any `vscode` import so the mkdtemp/rm logic is directly unit-
// testable under `node --test`.

import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';

/** The prefix `mkdtempSync` appends its random suffix to. */
const TEMP_PREFIX = 'vouchfx-';

/** The fixed events-file name placed inside each run's exclusive temp dir. */
const EVENTS_FILE_NAME = 'events.jsonl';

/**
 * Creates an exclusive temp directory for one run and returns its path.
 *
 * `fs.mkdtempSync` atomically creates a brand-new directory (mode 0700 on POSIX)
 * that this process owns — there is no predictable name an attacker on a shared
 * tmpdir could pre-create or aim a symlink at, unlike a guessable file name
 * directly under `os.tmpdir()`.
 */
export function makeTempEventsDir(): string {
  return fs.mkdtempSync(path.join(os.tmpdir(), TEMP_PREFIX));
}

/** The events-file path INSIDE a temp directory from {@link makeTempEventsDir}. */
export function eventsPathIn(dir: string): string {
  return path.join(dir, EVENTS_FILE_NAME);
}

/**
 * Recursively removes a temp directory (and the events file within), ignoring
 * any error (it may already be gone, or never have been fully populated). Call
 * this on EVERY exit path so the "always cleaned up" guarantee holds.
 */
export function safeRemoveDir(dir: string): void {
  try {
    fs.rmSync(dir, { recursive: true, force: true });
  } catch {
    // Ignore.
  }
}
