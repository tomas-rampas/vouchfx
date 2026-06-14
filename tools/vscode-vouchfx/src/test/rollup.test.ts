import assert from 'node:assert/strict';
import { test } from 'node:test';

import { rollupState } from '../rollup';
import { EditorState } from '../verdictMap';

// Pure helper, no `vscode` import — runs under the plain Node test runner.

// ---- Scenario verdict present: honour the aggregate verbatim --------------

test('a present scenario verdict wins over the step states (PASS)', () => {
  // Even with a failing step observed, an explicit PASS scenario-completed wins:
  // the engine already elevated the per-step verdicts into this aggregate.
  assert.equal(rollupState(['failed', 'passed'], 'PASS'), 'passed');
});

test('a present scenario verdict maps each wire token to its editor state', () => {
  assert.equal(rollupState([], 'PASS'), 'passed');
  assert.equal(rollupState([], 'FAIL'), 'failed');
  assert.equal(rollupState([], 'ENV_ERROR'), 'errored');
  assert.equal(rollupState([], 'INCONCLUSIVE'), 'skipped');
});

test('an unknown present scenario verdict maps to errored (never silently passed)', () => {
  assert.equal(rollupState(['passed'], 'NOT_A_VERDICT'), 'errored');
});

// ---- Truncated stream (null verdict): worst observed step state -----------

test('a null scenario verdict with a failing step rolls up to failed, NOT skipped', () => {
  // The regression this guards: a partial/aborted stream that already saw a
  // failure must not read as "skipped" (didn't run).
  assert.equal(rollupState(['passed', 'failed', 'skipped'], null), 'failed');
});

test('a null scenario verdict elevates errored over failed', () => {
  assert.equal(rollupState(['failed', 'errored', 'passed'], null), 'errored');
});

test('a null scenario verdict with only passed steps rolls up to passed', () => {
  assert.equal(rollupState(['passed', 'passed'], null), 'passed');
});

test('a null scenario verdict with only skipped steps rolls up to skipped', () => {
  assert.equal(rollupState(['skipped', 'skipped'], null), 'skipped');
});

test('precedence is errored > failed > skipped > passed (worst observed wins)', () => {
  // Each pair: the more-severe state dominates regardless of arrival order.
  assert.equal(rollupState(['errored', 'failed'], null), 'errored');
  assert.equal(rollupState(['failed', 'errored'], null), 'errored');
  assert.equal(rollupState(['failed', 'skipped'], null), 'failed');
  assert.equal(rollupState(['skipped', 'failed'], null), 'failed');
  assert.equal(rollupState(['skipped', 'passed'], null), 'skipped');
  assert.equal(rollupState(['passed', 'skipped'], null), 'skipped');
});

test('the worst-state precedence mirrors the engine EnvError>Fail>Inconclusive>Pass', () => {
  // All four states present → the most severe (errored) wins.
  const all: EditorState[] = ['passed', 'skipped', 'failed', 'errored'];
  assert.equal(rollupState(all, null), 'errored');
});

// ---- Empty / no-decision case ---------------------------------------------

test('a null scenario verdict with NO steps returns null (fail-soft to the caller)', () => {
  // No aggregate verdict and nothing observed: the caller applies its own
  // "no events" fail-soft behaviour rather than inventing a green/red verdict.
  assert.equal(rollupState([], null), null);
});

test('rollupState is total — a present verdict never returns null even with no steps', () => {
  for (const token of ['PASS', 'FAIL', 'ENV_ERROR', 'INCONCLUSIVE', 'weird']) {
    assert.notEqual(rollupState([], token), null, `token "${token}" must yield a state`);
  }
});
