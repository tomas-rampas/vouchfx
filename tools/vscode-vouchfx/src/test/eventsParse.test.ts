import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';

import { parseEvents } from '../eventsParse';

// Pure helper, no `vscode` import — runs under the plain Node test runner.

const extensionRoot = path.resolve(__dirname, '..', '..');
const fixturesDir = path.resolve(extensionRoot, 'src', 'test', 'fixtures');

function readFixture(name: string): string {
  return readFileSync(path.resolve(fixturesDir, name), 'utf8');
}

test('parseEvents extracts every scenario in the 4-verdict stream', () => {
  const { scenarios } = parseEvents(readFixture('four-verdict-events.jsonl'));
  assert.deepEqual(
    scenarios.map((s) => s.scenarioId),
    [
      'checkout-happy-path',
      'checkout-rejects-bad-card',
      'checkout-db-unreachable',
      'checkout-webhook-timeout',
    ],
  );
});

test('parseEvents reads the file from scenario-started', () => {
  const { scenarios } = parseEvents(readFixture('four-verdict-events.jsonl'));
  const byId = new Map(scenarios.map((s) => [s.scenarioId, s]));
  assert.equal(byId.get('checkout-happy-path')?.file, 'checkout-happy-path.e2e.yaml');
  assert.equal(byId.get('checkout-db-unreachable')?.file, 'checkout-db-unreachable.e2e.yaml');
});

test('parseEvents carries each scenario verdict from scenario-completed', () => {
  const { scenarios } = parseEvents(readFixture('four-verdict-events.jsonl'));
  const byId = new Map(scenarios.map((s) => [s.scenarioId, s.verdict]));
  assert.equal(byId.get('checkout-happy-path'), 'PASS');
  assert.equal(byId.get('checkout-rejects-bad-card'), 'FAIL');
  assert.equal(byId.get('checkout-db-unreachable'), 'ENV_ERROR');
  assert.equal(byId.get('checkout-webhook-timeout'), 'INCONCLUSIVE');
});

test('parseEvents associates a step with the most-recently-started scenario', () => {
  const { scenarios } = parseEvents(readFixture('four-verdict-events.jsonl'));
  const byId = new Map(scenarios.map((s) => [s.scenarioId, s]));

  assert.deepEqual(byId.get('checkout-happy-path')?.steps, [
    { stepId: 'place-order', verdict: 'PASS' },
  ]);
  assert.deepEqual(byId.get('checkout-rejects-bad-card')?.steps, [
    { stepId: 'assert-status', verdict: 'FAIL' },
  ]);
  assert.deepEqual(byId.get('checkout-db-unreachable')?.steps, [
    { stepId: 'query-orders', verdict: 'ENV_ERROR' },
  ]);
  assert.deepEqual(byId.get('checkout-webhook-timeout')?.steps, [
    { stepId: 'await-webhook', verdict: 'INCONCLUSIVE' },
  ]);
});

test('parseEvents tolerates blank, malformed and unknown-type lines (§14 tolerance)', () => {
  // The fixture deliberately includes a blank line, two non-JSON prose lines, a
  // truncated JSON object, and an unknown future event type. None of these may
  // throw, and none may leak into the scenario set.
  const { scenarios } = parseEvents(readFixture('four-verdict-events.jsonl'));
  assert.equal(scenarios.length, 4);
  for (const s of scenarios) {
    assert.ok(typeof s.scenarioId === 'string' && s.scenarioId.length > 0);
  }
});

test('parseEvents on an empty string returns an empty scenario set', () => {
  assert.deepEqual(parseEvents(''), { scenarios: [] });
  assert.deepEqual(parseEvents('   \n\n  \n'), { scenarios: [] });
});

test('a step-completed before any scenario-started is dropped (no scenario to attach to)', () => {
  const jsonl = [
    '{"type":"step-completed","stepId":"orphan","verdict":"PASS"}',
    '{"type":"scenario-started","scenarioId":"s1","file":"s1.e2e.yaml"}',
    '{"type":"step-completed","stepId":"real","verdict":"FAIL"}',
    '{"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}',
  ].join('\n');
  const { scenarios } = parseEvents(jsonl);
  assert.equal(scenarios.length, 1);
  assert.deepEqual(scenarios[0].steps, [{ stepId: 'real', verdict: 'FAIL' }]);
});

test('scenario-completed for an unknown scenarioId is ignored, not invented', () => {
  const jsonl = [
    '{"type":"scenario-started","scenarioId":"s1","file":"s1.e2e.yaml"}',
    '{"type":"scenario-completed","scenarioId":"ghost","verdict":"PASS"}',
    '{"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}',
  ].join('\n');
  const { scenarios } = parseEvents(jsonl);
  assert.deepEqual(
    scenarios.map((s) => s.scenarioId),
    ['s1'],
  );
  assert.equal(scenarios[0].verdict, 'PASS');
});
