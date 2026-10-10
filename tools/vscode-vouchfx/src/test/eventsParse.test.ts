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
  // The fixture deliberately includes a blank line, a non-JSON prose line, a
  // truncated JSON object, and an unknown future event type. None of these may
  // throw, and none may leak into the scenario set.
  const { scenarios } = parseEvents(readFixture('four-verdict-events.jsonl'));
  assert.equal(scenarios.length, 4);
  for (const s of scenarios) {
    assert.ok(typeof s.scenarioId === 'string' && s.scenarioId.length > 0);
  }
});

test('parseEvents on an empty string returns an empty scenario set', () => {
  assert.deepEqual(parseEvents('').scenarios, []);
  assert.deepEqual(parseEvents('   \n\n  \n').scenarios, []);
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

function skippedEventLines(jsonl: string): number {
  return parseEvents(jsonl).skippedEventLines;
}

test('a clean stream ignores blank lines, future events and unknown fields without skips', () => {
  const jsonl = [
    '',
    '   ',
    '{"type":"scenario-started","scenarioId":"s1","future":{"secret":"opaque"}}',
    '{"type":"future-event","scenarioId":false,"verdict":null}',
    '{"type":"step-completed","stepId":"step","verdict":"PASS","future":[1,2]}',
    '{"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}',
  ].join('\r\n');
  assert.equal(skippedEventLines(jsonl), 0);
});

for (const [label, line] of [
  ['non-JSON text', 'private-canary: not JSON'],
  ['truncated object', '{"type":"scenario-started"'],
  ['array', '[{"type":"scenario-started"}]'],
  ['string', '"private-canary"'],
  ['number', '17'],
  ['boolean', 'true'],
  ['null', 'null'],
  ['missing type', '{}'],
  ['null type', '{"type":null}'],
  ['numeric type', '{"type":17}'],
] as const) {
  test(`one unreadable ${label} line is counted`, () => {
    assert.equal(skippedEventLines(line), 1);
  });
}

test('several unreadable lines are counted without dropping later valid verdicts', () => {
  const jsonl = [
    '{"type":"scenario-started","scenarioId":"s1"}',
    'private-canary: not JSON',
    '[]',
    '{"type":"step-completed","stepId":"step","verdict":"FAIL"}',
    '{"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}',
    '{"type":"scenario-started"',
  ].join('\n');
  const parsed = parseEvents(jsonl);
  assert.equal(skippedEventLines(jsonl), 3);
  assert.equal(parsed.scenarios[0].verdict, 'FAIL');
  assert.deepEqual(parsed.scenarios[0].steps, [{ stepId: 'step', verdict: 'FAIL' }]);
});

const malformedKnownEvents: ReadonlyArray<readonly [string, Record<string, unknown>]> = [
  ['scenario-started missing scenarioId', { type: 'scenario-started' }],
  ['scenario-started null scenarioId', { type: 'scenario-started', scenarioId: null }],
  ['scenario-started numeric scenarioId', { type: 'scenario-started', scenarioId: 1 }],
  ['step-completed missing stepId', { type: 'step-completed', verdict: 'PASS' }],
  ['step-completed null stepId', { type: 'step-completed', stepId: null, verdict: 'PASS' }],
  ['step-completed object stepId', { type: 'step-completed', stepId: {}, verdict: 'PASS' }],
  ['step-completed missing verdict', { type: 'step-completed', stepId: 'step' }],
  ['step-completed null verdict', { type: 'step-completed', stepId: 'step', verdict: null }],
  ['step-completed numeric verdict', { type: 'step-completed', stepId: 'step', verdict: 1 }],
  ['scenario-completed missing scenarioId', { type: 'scenario-completed', verdict: 'PASS' }],
  ['scenario-completed null scenarioId', { type: 'scenario-completed', scenarioId: null, verdict: 'PASS' }],
  ['scenario-completed array scenarioId', { type: 'scenario-completed', scenarioId: [], verdict: 'PASS' }],
  ['scenario-completed missing verdict', { type: 'scenario-completed', scenarioId: 's1' }],
  ['scenario-completed null verdict', { type: 'scenario-completed', scenarioId: 's1', verdict: null }],
  ['scenario-completed boolean verdict', { type: 'scenario-completed', scenarioId: 's1', verdict: false }],
];

for (const [label, event] of malformedKnownEvents) {
  test(`a malformed known event is counted once: ${label}`, () => {
    const jsonl = [
      '{"type":"scenario-started","scenarioId":"s1"}',
      '{"type":"scenario-completed","scenarioId":"s1","verdict":"FAIL"}',
      JSON.stringify(event),
    ].join('\n');
    assert.equal(skippedEventLines(jsonl), 1);
    assert.equal(parseEvents(jsonl).scenarios[0].verdict, 'FAIL');
    assert.deepEqual(parseEvents(jsonl).scenarios[0].steps, []);
  });
}

test('known events without a corresponding scenario are counted once each', () => {
  const jsonl = [
    '{"type":"step-completed","stepId":"orphan","verdict":"PASS"}',
    '{"type":"step-completed"}',
    '{"type":"scenario-completed","scenarioId":"unknown","verdict":"PASS"}',
    '{"type":"scenario-started","scenarioId":"s1"}',
    '{"type":"step-completed","stepId":"real","verdict":"PASS"}',
  ].join('\n');
  assert.equal(skippedEventLines(jsonl), 3);
  assert.deepEqual(parseEvents(jsonl).scenarios[0].steps, [{ stepId: 'real', verdict: 'PASS' }]);
});

test('unknown verdict tokens and optional non-string files remain usable without skips', () => {
  const jsonl = [
    '{"type":"scenario-started","scenarioId":"","file":17}',
    '{"type":"step-completed","stepId":"","verdict":"FUTURE_VERDICT"}',
    '{"type":"scenario-completed","scenarioId":"","verdict":"FUTURE_VERDICT"}',
  ].join('\n');
  assert.equal(skippedEventLines(jsonl), 0);
  assert.deepEqual(parseEvents(jsonl).scenarios, [{
    scenarioId: '', file: null, verdict: 'FUTURE_VERDICT',
    steps: [{ stepId: '', verdict: 'FUTURE_VERDICT' }],
  }]);
});

test('the four-verdict fixture reports its two unreadable lines', () => {
  assert.equal(skippedEventLines(readFixture('four-verdict-events.jsonl')), 2);
});
