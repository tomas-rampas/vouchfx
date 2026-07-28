import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { test } from 'node:test';

import {
  BAR_B_FIELDS,
  checkCatalogueBarB,
  checkSchemaVersion,
  ENGINE_SCHEMA_FILE_NAME,
  fetchEngineSchema,
  MIN_ENGINE_SCHEMA_HINT,
  type SpawnFn,
  type SpawnResult,
} from '../engineSchemaSource';

// Compiled test lives in dist/test/; pure module has no vscode import.

const RICH_ENTRY = {
  type: 'http.rest',
  family: 'http',
  provider: 'rest',
  requiredFields: ['method', 'path', 'target'],
  optionalFields: ['body', 'expect', 'headers'],
  captureSupported: true,
  familyIntent: 'Call HTTP endpoints (REST or SOAP) on services under test.',
};

const RICH_CATALOGUE = JSON.stringify({
  schemaVersion: 1,
  engineVersion: '1.0.0-test',
  stepTypes: [
    RICH_ENTRY,
    {
      type: 'db-assert.postgres',
      family: 'db-assert',
      provider: 'postgres',
      requiredFields: ['query', 'target'],
      optionalFields: ['expect'],
      captureSupported: true,
      familyIntent: 'Query a data store and assert properties of the result set.',
    },
  ],
});

const VALID_SCHEMA = JSON.stringify({
  $schema: 'https://json-schema.org/draft/2020-12/schema',
  'x-vouchfx-schema-version': 'v1',
  $defs: {},
});

function okSpawn(stdout: string, code = 0): SpawnResult {
  return { code, stdout, stderr: '', timedOut: false };
}

function makeStorageDir(): string {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'vouchfx-schema-test-'));
}

// ---------------------------------------------------------------------------
// checkCatalogueBarB
// ---------------------------------------------------------------------------

test('checkCatalogueBarB accepts a rich bar-B catalogue', () => {
  const result = checkCatalogueBarB(RICH_CATALOGUE);
  assert.equal(result.ok, true);
  if (result.ok) {
    assert.equal(result.stepTypeCount, 2);
  }
});

test('checkCatalogueBarB rejects invalid JSON', () => {
  const result = checkCatalogueBarB('{not json');
  assert.equal(result.ok, false);
  if (!result.ok) {
    assert.match(result.reason, /not valid JSON/i);
  }
});

test('checkCatalogueBarB rejects missing stepTypes', () => {
  const result = checkCatalogueBarB(JSON.stringify({ schemaVersion: 1 }));
  assert.equal(result.ok, false);
  if (!result.ok) {
    assert.match(result.reason, /stepTypes/i);
  }
});

test('checkCatalogueBarB rejects empty stepTypes', () => {
  const result = checkCatalogueBarB(JSON.stringify({ stepTypes: [] }));
  assert.equal(result.ok, false);
  if (!result.ok) {
    assert.match(result.reason, /empty/i);
  }
});

for (const field of BAR_B_FIELDS) {
  test(`checkCatalogueBarB rejects entry missing ${field}`, () => {
    const entry = { ...RICH_ENTRY } as Record<string, unknown>;
    delete entry[field];
    const doc = JSON.stringify({ stepTypes: [entry] });
    const result = checkCatalogueBarB(doc);
    assert.equal(result.ok, false);
    if (!result.ok) {
      assert.match(result.reason, new RegExp(field));
      assert.match(result.reason, /too old|missing/i);
    }
  });
}

test('checkCatalogueBarB rejects empty familyIntent', () => {
  const entry = { ...RICH_ENTRY, familyIntent: '' };
  const result = checkCatalogueBarB(JSON.stringify({ stepTypes: [entry] }));
  assert.equal(result.ok, false);
  if (!result.ok) {
    assert.match(result.reason, /familyIntent/);
  }
});

test('checkCatalogueBarB rejects non-boolean captureSupported', () => {
  const entry = { ...RICH_ENTRY, captureSupported: 'yes' };
  const result = checkCatalogueBarB(JSON.stringify({ stepTypes: [entry] }));
  assert.equal(result.ok, false);
  if (!result.ok) {
    assert.match(result.reason, /captureSupported/);
  }
});

// ---------------------------------------------------------------------------
// checkSchemaVersion
// ---------------------------------------------------------------------------

test('checkSchemaVersion accepts x-vouchfx-schema-version v1', () => {
  const result = checkSchemaVersion(VALID_SCHEMA);
  assert.deepEqual(result, { ok: true, version: 'v1' });
});

test('checkSchemaVersion rejects wrong version', () => {
  const result = checkSchemaVersion(
    JSON.stringify({ 'x-vouchfx-schema-version': 'v2' }),
  );
  assert.equal(result.ok, false);
  if (!result.ok) {
    assert.match(result.reason, /v1/);
  }
});

test('checkSchemaVersion rejects missing marker', () => {
  const result = checkSchemaVersion(JSON.stringify({ $schema: 'x' }));
  assert.equal(result.ok, false);
});

// ---------------------------------------------------------------------------
// fetchEngineSchema (mocked spawn)
// ---------------------------------------------------------------------------

test('fetchEngineSchema writes schema when list is bar-B and schema succeeds', async () => {
  const storageDir = makeStorageDir();
  try {
    const schemaPath = path.join(storageDir, ENGINE_SCHEMA_FILE_NAME);
    const spawnFn: SpawnFn = async (_cli, args) => {
      if (args[0] === 'list') {
        return okSpawn(RICH_CATALOGUE);
      }
      if (args[0] === 'schema') {
        // Mimic CLI --output: write the file the CLI would create.
        const outIdx = args.indexOf('--output');
        assert.ok(outIdx >= 0 && args[outIdx + 1] === schemaPath);
        fs.writeFileSync(schemaPath, VALID_SCHEMA, 'utf8');
        return okSpawn('');
      }
      return { code: 1, stdout: '', stderr: `unexpected args ${args.join(' ')}`, timedOut: false };
    };

    const result = await fetchEngineSchema({
      cliPath: 'vouchfx',
      storageDir,
      spawnFn,
    });

    assert.equal(result.kind, 'ok');
    if (result.kind === 'ok') {
      assert.equal(result.fsPath, schemaPath);
      assert.equal(result.stepTypeCount, 2);
      assert.equal(fs.readFileSync(result.fsPath, 'utf8'), VALID_SCHEMA);
    }
  } finally {
    fs.rmSync(storageDir, { recursive: true, force: true });
  }
});

test('fetchEngineSchema fails when list catalogue is thin (pre-bar-B)', async () => {
  const storageDir = makeStorageDir();
  try {
    const thin = JSON.stringify({
      schemaVersion: 1,
      stepTypes: [{ type: 'http.rest', family: 'http', provider: 'rest' }],
    });
    const spawnFn: SpawnFn = async (_cli, args) => {
      if (args[0] === 'list') {
        return okSpawn(thin);
      }
      assert.fail('schema must not be invoked when catalogue is thin');
      return okSpawn('');
    };

    const result = await fetchEngineSchema({
      cliPath: 'vouchfx',
      storageDir,
      spawnFn,
    });

    assert.equal(result.kind, 'error');
    if (result.kind === 'error') {
      assert.match(result.message, /requiredFields|too old/i);
    }
  } finally {
    fs.rmSync(storageDir, { recursive: true, force: true });
  }
});

test('fetchEngineSchema fails when list --json exits non-zero', async () => {
  const storageDir = makeStorageDir();
  try {
    const spawnFn: SpawnFn = async () => ({
      code: 3,
      stdout: '',
      stderr: 'catalogue incomplete',
      timedOut: false,
    });

    const result = await fetchEngineSchema({
      cliPath: 'vouchfx',
      storageDir,
      spawnFn,
    });

    assert.equal(result.kind, 'error');
    if (result.kind === 'error') {
      assert.match(result.message, /list --json/);
      assert.match(result.message, /3/);
    }
  } finally {
    fs.rmSync(storageDir, { recursive: true, force: true });
  }
});

test('fetchEngineSchema fails when CLI cannot be started', async () => {
  const storageDir = makeStorageDir();
  try {
    const spawnFn: SpawnFn = async () => ({
      code: null,
      stdout: '',
      stderr: 'ENOENT',
      timedOut: false,
    });

    const result = await fetchEngineSchema({
      cliPath: 'missing-vouchfx',
      storageDir,
      spawnFn,
    });

    assert.equal(result.kind, 'error');
    if (result.kind === 'error') {
      assert.match(result.message, /could not start|ENOENT/i);
    }
  } finally {
    fs.rmSync(storageDir, { recursive: true, force: true });
  }
});

test('fetchEngineSchema fails when list times out', async () => {
  const storageDir = makeStorageDir();
  try {
    const spawnFn: SpawnFn = async () => ({
      code: null,
      stdout: '',
      stderr: '',
      timedOut: true,
    });

    const result = await fetchEngineSchema({
      cliPath: 'vouchfx',
      storageDir,
      spawnFn,
      timeoutMs: 50,
    });

    assert.equal(result.kind, 'error');
    if (result.kind === 'error') {
      assert.match(result.message, /timed out/i);
    }
  } finally {
    fs.rmSync(storageDir, { recursive: true, force: true });
  }
});

test('fetchEngineSchema fails when schema --output exits non-zero', async () => {
  const storageDir = makeStorageDir();
  try {
    const spawnFn: SpawnFn = async (_cli, args) => {
      if (args[0] === 'list') {
        return okSpawn(RICH_CATALOGUE);
      }
      return { code: 2, stdout: '', stderr: 'parent directory missing', timedOut: false };
    };

    const result = await fetchEngineSchema({
      cliPath: 'vouchfx',
      storageDir,
      spawnFn,
    });

    assert.equal(result.kind, 'error');
    if (result.kind === 'error') {
      assert.match(result.message, /schema --output/);
    }
  } finally {
    fs.rmSync(storageDir, { recursive: true, force: true });
  }
});

test('fetchEngineSchema fails when written schema has wrong version', async () => {
  const storageDir = makeStorageDir();
  try {
    const schemaPath = path.join(storageDir, ENGINE_SCHEMA_FILE_NAME);
    const spawnFn: SpawnFn = async (_cli, args) => {
      if (args[0] === 'list') {
        return okSpawn(RICH_CATALOGUE);
      }
      fs.writeFileSync(
        schemaPath,
        JSON.stringify({ 'x-vouchfx-schema-version': 'v0' }),
        'utf8',
      );
      return okSpawn('');
    };

    const result = await fetchEngineSchema({
      cliPath: 'vouchfx',
      storageDir,
      spawnFn,
    });

    assert.equal(result.kind, 'error');
    if (result.kind === 'error') {
      assert.match(result.message, /version check/i);
    }
  } finally {
    fs.rmSync(storageDir, { recursive: true, force: true });
  }
});

test('fetchEngineSchema rejects empty cliPath', async () => {
  const result = await fetchEngineSchema({
    cliPath: '   ',
    storageDir: os.tmpdir(),
    spawnFn: async () => {
      assert.fail('spawn must not run for empty cliPath');
      return okSpawn('');
    },
  });
  assert.equal(result.kind, 'error');
  if (result.kind === 'error') {
    assert.match(result.message, /empty/i);
  }
});

test('MIN_ENGINE_SCHEMA_HINT mentions vouchfx schema', () => {
  assert.match(MIN_ENGINE_SCHEMA_HINT, /vouchfx schema/);
  assert.match(MIN_ENGINE_SCHEMA_HINT, /list --json/);
});
