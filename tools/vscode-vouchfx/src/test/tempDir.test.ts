import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { test } from 'node:test';

import { eventsPathIn, makeTempEventsDir, safeRemoveDir } from '../tempDir';

// Pure helpers, no `vscode` import — runs under the plain Node test runner.
// These exercise the REAL filesystem (mkdtemp/rm); each test removes its dir.

test('makeTempEventsDir creates a fresh, exclusive directory under tmpdir', () => {
  const dir = makeTempEventsDir();
  try {
    assert.ok(fs.existsSync(dir), 'the directory should exist after creation');
    assert.ok(fs.statSync(dir).isDirectory(), 'it should be a directory');
    assert.equal(
      path.dirname(dir),
      fs.realpathSync(os.tmpdir()),
      'it should live directly under the OS tmpdir',
    );
    assert.ok(
      path.basename(dir).startsWith('vouchfx-'),
      'it should carry the vouchfx- prefix',
    );
  } finally {
    safeRemoveDir(dir);
  }
});

test('two makeTempEventsDir calls yield distinct directories (no collision)', () => {
  const a = makeTempEventsDir();
  const b = makeTempEventsDir();
  try {
    assert.notEqual(a, b, 'each call must produce a unique directory');
  } finally {
    safeRemoveDir(a);
    safeRemoveDir(b);
  }
});

test('the created directory is private to the owner (0700) on POSIX', () => {
  const dir = makeTempEventsDir();
  try {
    if (process.platform !== 'win32') {
      const mode = fs.statSync(dir).mode & 0o777;
      assert.equal(mode, 0o700, 'mkdtemp must create a 0700 (owner-only) directory');
    }
  } finally {
    safeRemoveDir(dir);
  }
});

test('eventsPathIn places events.jsonl inside the given directory', () => {
  const dir = makeTempEventsDir();
  try {
    const p = eventsPathIn(dir);
    assert.equal(path.dirname(p), dir, 'the events file must live inside the temp dir');
    assert.equal(path.basename(p), 'events.jsonl');
  } finally {
    safeRemoveDir(dir);
  }
});

test('safeRemoveDir removes the directory AND the events file within it', () => {
  const dir = makeTempEventsDir();
  const eventsPath = eventsPathIn(dir);
  fs.writeFileSync(eventsPath, '{"type":"scenario-started","scenarioId":"x"}\n');
  assert.ok(fs.existsSync(eventsPath), 'precondition: the events file exists');

  safeRemoveDir(dir);

  assert.ok(!fs.existsSync(eventsPath), 'the events file must be gone');
  assert.ok(!fs.existsSync(dir), 'the temp directory must be gone');
});

test('safeRemoveDir is idempotent and never throws on a missing directory', () => {
  const dir = makeTempEventsDir();
  safeRemoveDir(dir);
  // Second removal of an already-deleted dir must be a no-op, not an error.
  assert.doesNotThrow(() => safeRemoveDir(dir));
  assert.doesNotThrow(() => safeRemoveDir(path.join(os.tmpdir(), 'vouchfx-does-not-exist-zzz')));
});
