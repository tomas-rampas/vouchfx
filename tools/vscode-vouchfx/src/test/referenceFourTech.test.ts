import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';

import { parseE2eOutline } from '../e2eYamlOutline';
import { mapVerdict } from '../verdictMap';

// Tests that prove the S11-D-01 reference four-technology scenario
// (examples/reference/reference.e2e.yaml, mirrored as the fixture below) is
// correctly handled by the editor surface:
//   1. parseE2eOutline discovers all seven id-bearing steps as TestItems, in
//      document order, with non-overlapping line ranges.
//   2. mapVerdict correctly maps a PASS event for the reference scenario.
//
// Fixture resolution: yaml files are NOT compiled by tsc, so read them from the
// SOURCE tree — same pattern as e2eYamlOutline.test.ts.
const extensionRoot = path.resolve(__dirname, '..', '..');
const fixturesDir = path.resolve(extensionRoot, 'src', 'test', 'fixtures');

function readFixture(name: string): string {
  return readFileSync(path.resolve(fixturesDir, name), 'utf8');
}

// ---------------------------------------------------------------------------
// Outline / Test-Explorer discovery tests
// ---------------------------------------------------------------------------

test('reference fixture: parseE2eOutline returns the scenario name', () => {
  const outline = parseE2eOutline(readFixture('reference-four-tech.e2e.yaml'));
  assert.equal(outline.scenarioName, 'reference-four-tech');
});

test('reference fixture: parseE2eOutline discovers exactly the seven id-bearing steps', () => {
  const outline = parseE2eOutline(readFixture('reference-four-tech.e2e.yaml'));
  assert.deepEqual(
    outline.steps.map((s) => s.id),
    [
      'rest-get-whoami',
      'db-insert-row',
      'db-assert-rows',
      'kafka-publish-order',
      'kafka-expect-order',
      'webhook-trigger',
      'webhook-await',
    ],
  );
});

test('reference fixture: step count is exactly seven', () => {
  const outline = parseE2eOutline(readFixture('reference-four-tech.e2e.yaml'));
  assert.equal(outline.steps.length, 7);
});

test('reference fixture: each step has the correct 0-based start line', () => {
  const outline = parseE2eOutline(readFixture('reference-four-tech.e2e.yaml'));
  const byId = new Map(outline.steps.map((s) => [s.id, s]));

  // Lines are 0-based; editor line numbers (shown in the fixture by Read tool)
  // are 1-based. 0-based = (editor 1-based) - 1.
  //
  // rest-get-whoami    → editor line 34  → 0-based 33
  // db-insert-row      → editor line 46  → 0-based 45
  // db-assert-rows     → editor line 66  → 0-based 65
  // kafka-publish-order→ editor line 79  → 0-based 78
  // kafka-expect-order → editor line 86  → 0-based 85
  // webhook-trigger    → editor line 96  → 0-based 95
  // webhook-await      → editor line 111 → 0-based 110
  assert.equal(byId.get('rest-get-whoami')?.startLine, 33);
  assert.equal(byId.get('db-insert-row')?.startLine, 45);
  assert.equal(byId.get('db-assert-rows')?.startLine, 65);
  assert.equal(byId.get('kafka-publish-order')?.startLine, 78);
  assert.equal(byId.get('kafka-expect-order')?.startLine, 85);
  assert.equal(byId.get('webhook-trigger')?.startLine, 95);
  assert.equal(byId.get('webhook-await')?.startLine, 110);
});

test('reference fixture: no step range overlaps the next step range', () => {
  const outline = parseE2eOutline(readFixture('reference-four-tech.e2e.yaml'));
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

test('reference fixture: every step has endLine >= startLine', () => {
  const outline = parseE2eOutline(readFixture('reference-four-tech.e2e.yaml'));
  for (const step of outline.steps) {
    assert.ok(
      step.endLine >= step.startLine,
      `step ${step.id}: endLine (${step.endLine}) must not be before startLine (${step.startLine})`,
    );
  }
});

test('reference fixture: the code: | block scalar does not leak its interior lines into the db-insert-row range boundary', () => {
  // db-insert-row owns a multi-line `code: |` block. Its endLine must extend to
  // the line just before db-assert-rows, not collapse onto the `code: |` keyword
  // line — proving the block scalar interior stays inside its owning step.
  const outline = parseE2eOutline(readFixture('reference-four-tech.e2e.yaml'));
  const byId = new Map(outline.steps.map((s) => [s.id, s]));
  const insertRow = byId.get('db-insert-row');
  const assertRows = byId.get('db-assert-rows');
  assert.ok(insertRow, 'db-insert-row must be present');
  assert.ok(assertRows, 'db-assert-rows must be present');
  // The block scalar interior (the C# lines) must be inside insertRow's range.
  assert.ok(
    insertRow.endLine > insertRow.startLine + 2,
    'db-insert-row range must span the code: | block (more than 2 lines)',
  );
  // insertRow must end before assertRows begins.
  assert.ok(insertRow.endLine < assertRows.startLine);
});

test('reference fixture: the RETRY step (kafka-expect-order) is discovered as a normal TestItem', () => {
  // verifyMode: RETRY is a runtime attribute — the outline parser must discover
  // the step regardless; it is the engine's concern, not the editor surface's.
  const outline = parseE2eOutline(readFixture('reference-four-tech.e2e.yaml'));
  const ids = outline.steps.map((s) => s.id);
  assert.ok(ids.includes('kafka-expect-order'), 'kafka-expect-order (RETRY) must be a TestItem');
  assert.ok(ids.includes('webhook-await'), 'webhook-await (RETRY) must be a TestItem');
});

test('reference fixture: the capture: block on rest-get-whoami does not displace the step range', () => {
  // A step with a capture: block must still have a contiguous range ending just
  // before the next step — the capture key is just another YAML map entry.
  const outline = parseE2eOutline(readFixture('reference-four-tech.e2e.yaml'));
  const byId = new Map(outline.steps.map((s) => [s.id, s]));
  const whoami = byId.get('rest-get-whoami');
  const insertRow = byId.get('db-insert-row');
  assert.ok(whoami, 'rest-get-whoami must be present');
  assert.ok(insertRow, 'db-insert-row must be present');
  assert.ok(whoami.endLine < insertRow.startLine);
});

// ---------------------------------------------------------------------------
// Verdict-mapping test for the reference scenario
// ---------------------------------------------------------------------------

test('reference scenario: a PASS step-completed event maps to the passed editor state', () => {
  // Confirms that when the reference scenario emits a PASS verdict for any of
  // its steps, the editor surface shows the step as passed (not errored/skipped).
  // Uses the same mapVerdict helper as verdictMap.test.ts; no new harness needed.
  const r = mapVerdict('PASS');
  assert.equal(r.state, 'passed');
  assert.equal(r.verdict, 'Pass');
});
