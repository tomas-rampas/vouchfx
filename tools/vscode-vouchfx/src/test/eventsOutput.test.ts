import assert from 'node:assert/strict';
import { test } from 'node:test';

import { parseEventsForTestRun } from '../eventsOutput';

const cleanStream = [
  '{"type":"scenario-started","scenarioId":"s1"}',
  '{"type":"step-completed","stepId":"step","verdict":"PASS"}',
  '{"type":"scenario-completed","scenarioId":"s1","verdict":"PASS"}',
].join('\n');

function readOutput(jsonl: string): string[] {
  const output: string[] = [];
  parseEventsForTestRun(jsonl, { appendOutput: (text) => output.push(text) });
  return output;
}

test('the Test Explorer output seam emits nothing for a clean stream', () => {
  assert.deepEqual(readOutput(cleanStream), []);
  assert.deepEqual(readOutput(' \r\n\n'), []);
  assert.deepEqual(readOutput('{"type":"future-event","payload":"private-canary"}'), []);
});

test('the Test Explorer output seam reports one unreadable line using constant text', () => {
  assert.deepEqual(readOutput(`${cleanStream}\nprivate-canary: not JSON`), [
    '1 event-stream line could not be read, so these results may be incomplete.\r\n',
  ]);
});

test('the Test Explorer output seam reports several unreadable lines once', () => {
  assert.deepEqual(readOutput(`${cleanStream}\nprivate-canary: not JSON\n[]\nnull`), [
    '3 event-stream lines could not be read, so these results may be incomplete.\r\n',
  ]);
});

test('the Test Explorer output seam reports malformed known fields without their data', () => {
  const jsonl = [
    cleanStream,
    '{"type":"step-completed","stepId":{"private":"private-canary"},"verdict":"PASS"}',
    '{"type":"scenario-completed","scenarioId":"private-id-canary","verdict":null}',
  ].join('\n');
  assert.deepEqual(readOutput(jsonl), [
    '2 event-stream lines could not be read, so these results may be incomplete.\r\n',
  ]);
});

test('the Test Explorer output seam reports skipped lines even with no usable scenario', () => {
  assert.deepEqual(readOutput('private-canary: not JSON\nnull'), [
    '2 event-stream lines could not be read, so these results may be incomplete.\r\n',
  ]);
});

test('the Test Explorer output seam returns valid verdicts unchanged', () => {
  const output: string[] = [];
  const parsed = parseEventsForTestRun(`${cleanStream}\n[]`, {
    appendOutput: (text) => output.push(text),
  });
  assert.equal(parsed.skippedEventLines, 1);
  assert.deepEqual(parsed.scenarios, [{
    scenarioId: 's1', file: null, verdict: 'PASS',
    steps: [{ stepId: 'step', verdict: 'PASS' }],
  }]);
  assert.equal(output.length, 1);
});
