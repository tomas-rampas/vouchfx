import assert from 'node:assert/strict';
import path from 'node:path';
import { test } from 'node:test';

import { resolveSchemaPath } from '../schemaPath';

// The compiled test lives in dist/test/, importing the compiled helper from
// dist/. The helper is pure (no `vscode` import), so it loads under the plain
// Node test runner with no extension host.

const isWindows = process.platform === 'win32';

test('empty value resolves to the bundled schema', () => {
  const result = resolveSchemaPath('', '/any/base');
  assert.deepEqual(result, { kind: 'bundled' });
});

test('whitespace-only value resolves to the bundled schema', () => {
  const result = resolveSchemaPath('   \t  ', '/any/base');
  assert.deepEqual(result, { kind: 'bundled' });
});

test('a surrounding-whitespace value is trimmed before resolution', () => {
  const baseDir = path.resolve('base', 'dir');
  const result = resolveSchemaPath('  schema.json  ', baseDir);
  assert.equal(result.kind, 'override');
  assert.ok(result.kind === 'override');
  assert.equal(result.fsPath, path.resolve(baseDir, 'schema.json'));
});

test('a relative value resolves against the base directory', () => {
  const baseDir = path.resolve('workspace', 'root');
  const result = resolveSchemaPath(path.join('schemas', 'v1.json'), baseDir);
  assert.ok(result.kind === 'override');
  assert.equal(result.fsPath, path.resolve(baseDir, 'schemas', 'v1.json'));
});

test('an absolute value is used verbatim (ignoring the base directory)', () => {
  const absolute = isWindows
    ? 'C:\\enterprise\\schema\\composed.json'
    : '/enterprise/schema/composed.json';
  const result = resolveSchemaPath(absolute, path.resolve('unused', 'base'));
  assert.ok(result.kind === 'override');
  assert.equal(result.fsPath, path.normalize(absolute));
});

if (isWindows) {
  test('Windows: a drive-letter absolute path survives intact (drive letter preserved)', () => {
    const absolute = 'D:\\src\\github\\vouchfx\\schema.v1.json';
    const result = resolveSchemaPath(absolute, 'C:\\some\\other\\base');
    assert.ok(result.kind === 'override');
    assert.equal(result.fsPath, absolute);
    // The drive letter must not be lost or doubled — the round-trip hazard this
    // fix addresses.
    assert.ok(/^D:\\/.test(result.fsPath), 'drive letter D:\\ must be preserved');
  });

  test('Windows: a relative override joins with backslash semantics', () => {
    const result = resolveSchemaPath('sub\\schema.json', 'C:\\ws\\root');
    assert.ok(result.kind === 'override');
    assert.equal(result.fsPath, 'C:\\ws\\root\\sub\\schema.json');
  });
} else {
  test('POSIX: an absolute path survives intact', () => {
    const absolute = '/srv/vouchfx/schema.v1.json';
    const result = resolveSchemaPath(absolute, '/some/other/base');
    assert.ok(result.kind === 'override');
    assert.equal(result.fsPath, absolute);
  });

  test('POSIX: a relative override joins with forward-slash semantics', () => {
    const result = resolveSchemaPath('sub/schema.json', '/ws/root');
    assert.ok(result.kind === 'override');
    assert.equal(result.fsPath, '/ws/root/sub/schema.json');
  });
}
