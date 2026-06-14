import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';

import { parseE2eOutline } from '../e2eYamlOutline';

// The compiled test lives in dist/test/, importing the compiled helper from
// dist/. The helper is pure (no `vscode` import) — it only uses the `yaml`
// package — so it loads under the plain Node test runner with no extension
// host.
//
// Fixtures live in src/test/fixtures/ and are copied verbatim into
// dist/test/fixtures/ by `tsc` only for *.ts; *.yaml fixtures are NOT compiled,
// so resolve them against the SOURCE tree (two levels up from dist/test plus
// src/test/fixtures). Reading from src keeps the fixture single-sourced.
const extensionRoot = path.resolve(__dirname, '..', '..');
const fixturesDir = path.resolve(extensionRoot, 'src', 'test', 'fixtures');

function readFixture(name: string): string {
  return readFileSync(path.resolve(fixturesDir, name), 'utf8');
}

test('parseE2eOutline returns metadata.name', () => {
  const outline = parseE2eOutline(readFixture('multi-step.e2e.yaml'));
  assert.equal(outline.scenarioName, 'outline-fixture-demo');
});

test('parseE2eOutline keeps every step that declares an id, in document order', () => {
  const outline = parseE2eOutline(readFixture('multi-step.e2e.yaml'));
  assert.deepEqual(
    outline.steps.map((s) => s.id),
    ['compute', 'assert-shape', 'compute-folded'],
  );
});

test('parseE2eOutline skips a step that has no id (no anchor for a TestItem)', () => {
  const outline = parseE2eOutline(readFixture('multi-step.e2e.yaml'));
  assert.ok(
    !outline.steps.some((s) => s.id === undefined || s.id === null),
    'no step with a missing id survives',
  );
  // The fixture has 4 sequence items, one without an id → 3 kept.
  assert.equal(outline.steps.length, 3);
});

test('parseE2eOutline maps each kept step to its 0-based start line', () => {
  const outline = parseE2eOutline(readFixture('multi-step.e2e.yaml'));
  const byId = new Map(outline.steps.map((s) => [s.id, s]));
  // Line numbers are 0-based and verified against the fixture layout:
  //   `- id: compute`        is line 15 in 1-based editor terms → 14 0-based.
  //   `- id: assert-shape`   → 27 0-based (line 28 1-based; the literal block +
  //                            trailing blank + comment push it down).
  //   `- id: compute-folded` → 39 0-based (line 40 1-based).
  assert.equal(byId.get('compute')?.startLine, 14);
  assert.equal(byId.get('assert-shape')?.startLine, 27);
  assert.equal(byId.get('compute-folded')?.startLine, 39);
});

test('a `code: |` block scalar does not leak its interior lines into the step range', () => {
  const outline = parseE2eOutline(readFixture('multi-step.e2e.yaml'));
  const byId = new Map(outline.steps.map((s) => [s.id, s]));
  // `compute` owns its whole literal block (the C# lines) up to the line before
  // the next sequence item. endLine is inclusive and 0-based.
  const compute = byId.get('compute');
  assert.ok(compute);
  assert.equal(compute.startLine, 14);
  // The literal block (the C# lines) is fully inside compute's range: its
  // end-line is the line just before the NEXT sequence item (`- id: assert-shape`
  // at 27), i.e. 26 — NOT line 18/19 where the `code: |` keyword sits. If the
  // block scalar had leaked, endLine would have collapsed onto the keyword line.
  assert.equal(compute.endLine, 26);
  // assert-shape begins after compute's end-line boundary.
  assert.ok((byId.get('assert-shape')?.startLine ?? -1) > compute.endLine);
});

test('a step range never overlaps the next step range', () => {
  const outline = parseE2eOutline(readFixture('multi-step.e2e.yaml'));
  for (let i = 0; i < outline.steps.length - 1; i++) {
    const here = outline.steps[i];
    const next = outline.steps[i + 1];
    assert.ok(
      here.endLine < next.startLine,
      `step ${here.id} (lines ${here.startLine}-${here.endLine}) must end before ` +
        `step ${next.id} starts at ${next.startLine}`,
    );
  }
});

test('the last kept step extends to (at least) its own start line', () => {
  const outline = parseE2eOutline(readFixture('multi-step.e2e.yaml'));
  const last = outline.steps[outline.steps.length - 1];
  assert.ok(last.endLine >= last.startLine, 'endLine is never before startLine');
});

test('parseE2eOutline returns scenarioName null when metadata.name is absent', () => {
  const text = 'steps:\n  - id: only\n    type: http.rest\n';
  const outline = parseE2eOutline(text);
  assert.equal(outline.scenarioName, null);
  assert.deepEqual(
    outline.steps.map((s) => s.id),
    ['only'],
  );
});

test('parseE2eOutline tolerates a document with no steps block', () => {
  const text = 'metadata:\n  name: empty\n';
  const outline = parseE2eOutline(text);
  assert.equal(outline.scenarioName, 'empty');
  assert.deepEqual(outline.steps, []);
});

test('parseE2eOutline never throws on malformed YAML — returns an empty outline', () => {
  const text = 'steps: [this is : not : valid\n  - broken';
  const outline = parseE2eOutline(text);
  // We do not assert exact contents (the parser may salvage a partial tree);
  // the contract is only that it returns a well-formed object and does not throw.
  assert.ok(Array.isArray(outline.steps));
  assert.ok(outline.scenarioName === null || typeof outline.scenarioName === 'string');
});
