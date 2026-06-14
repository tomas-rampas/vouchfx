import assert from 'node:assert/strict';
import { test } from 'node:test';

import { mapVerdict } from '../verdictMap';

// Pure helper, no `vscode` import — runs under the plain Node test runner.

test('PASS maps to the passed editor state', () => {
  const r = mapVerdict('PASS');
  assert.equal(r.state, 'passed');
  assert.equal(r.verdict, 'Pass');
});

test('FAIL maps to the failed editor state', () => {
  const r = mapVerdict('FAIL');
  assert.equal(r.state, 'failed');
  assert.equal(r.verdict, 'Fail');
});

test('ENV_ERROR maps to the errored editor state with the human "Environment error" label', () => {
  const r = mapVerdict('ENV_ERROR');
  assert.equal(r.state, 'errored');
  assert.equal(r.verdict, 'Environment error');
});

test('INCONCLUSIVE maps to the skipped editor state', () => {
  const r = mapVerdict('INCONCLUSIVE');
  assert.equal(r.state, 'skipped');
  assert.equal(r.verdict, 'Inconclusive');
});

test('the four mappings produce four DISTINCT editor states', () => {
  const states = new Set(
    ['PASS', 'FAIL', 'ENV_ERROR', 'INCONCLUSIVE'].map((t) => mapVerdict(t).state),
  );
  assert.equal(states.size, 4, 'PASS/FAIL/ENV_ERROR/INCONCLUSIVE must not collapse onto each other');
  assert.deepEqual(
    [...states].sort(),
    ['errored', 'failed', 'passed', 'skipped'],
  );
});

test('an unknown token maps to errored/Unknown (never silently passed)', () => {
  const r = mapVerdict('NOT_A_VERDICT');
  assert.equal(r.state, 'errored');
  assert.equal(r.verdict, 'Unknown');
  assert.notEqual(r.state, 'passed');
});

test('an empty / missing token maps to errored/Unknown (never silently passed)', () => {
  for (const token of ['', '   ']) {
    const r = mapVerdict(token);
    assert.equal(r.state, 'errored');
    assert.equal(r.verdict, 'Unknown');
  }
});

test('mapVerdict is total — every input yields one of the four declared states', () => {
  const allowed = new Set(['passed', 'failed', 'errored', 'skipped']);
  for (const token of ['PASS', 'FAIL', 'ENV_ERROR', 'INCONCLUSIVE', 'weird', '', 'pass', 'Fail']) {
    const r = mapVerdict(token);
    assert.ok(allowed.has(r.state), `state ${r.state} for token "${token}" is one of the four`);
    assert.equal(typeof r.verdict, 'string');
    assert.ok(r.verdict.length > 0);
  }
});

test('the wire tokens are case-sensitive — lower-case "pass" is NOT a Pass', () => {
  // The .NET VerdictJsonConverter is case-sensitive (only "PASS" is Pass); the
  // mapper mirrors that, so a mismatched-case token is treated as unknown.
  const r = mapVerdict('pass');
  assert.equal(r.state, 'errored');
  assert.equal(r.verdict, 'Unknown');
});
