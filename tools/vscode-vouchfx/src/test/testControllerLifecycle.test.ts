import assert from 'node:assert/strict';
import { spawn, type ChildProcess, type SpawnOptions } from 'node:child_process';
import { EventEmitter } from 'node:events';
import * as fs from 'node:fs';
import { createRequire } from 'node:module';
import * as os from 'node:os';
import * as path from 'node:path';
import { PassThrough } from 'node:stream';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';
import type * as vscode from 'vscode';

class Token {
  isCancellationRequested = false;
  readonly listeners = new Set<() => void>();

  onCancellationRequested(listener: () => void): vscode.Disposable {
    this.listeners.add(listener);
    return { dispose: () => { this.listeners.delete(listener); } };
  }

  cancel(): void {
    this.isCancellationRequested = true;
    for (const listener of this.listeners) {
      listener();
    }
  }
}

class Child extends EventEmitter {
  readonly stdin = new PassThrough();
  readonly stderr = new PassThrough();
  readonly signals: (string | undefined)[] = [];
  exitCode: number | null = null;
  signalCode: string | null = null;

  kill(signal?: string): boolean {
    this.signals.push(signal);
    return true;
  }

  exit(code: number): void {
    this.exitCode = code;
    this.emit('exit', code, null);
  }

  close(code: number): void {
    this.exit(code);
    this.emit('close', code, null);
  }
}

class Clock {
  private next = 0;
  readonly pending = new Map<number, { callback: () => void; delay: number }>();

  setTimeout(callback: () => void, delay: number): number {
    const id = ++this.next;
    this.pending.set(id, { callback, delay });
    return id;
  }

  clearTimeout(id: number): void {
    this.pending.delete(id);
  }

  fire(): void {
    for (const [id, timer] of this.pending) {
      this.pending.delete(id);
      timer.callback();
    }
  }
}

type Spawn = (command: string, args: string[], options: SpawnOptions) => Child | ChildProcess;
type RunHandler = (request: vscode.TestRunRequest, token: vscode.CancellationToken) => Promise<void>;

/** Load the actual compiled adapter; only the unavailable VS Code host is stubbed. */
function harness(spawnChild: Spawn, token = new Token(), clock?: Clock) {
  const controllerPath = path.resolve(__dirname, '..', 'testController.js');
  const requireActual = createRequire(controllerPath);
  const states = new Map<string, string>();
  const warnings: string[] = [];
  let handler: RunHandler | undefined;
  let ended = 0;
  const record = (state: string) => (item: vscode.TestItem) => { states.set(item.id, state); };
  const run = {
    token,
    started: record('started'), enqueued: record('enqueued'),
    passed: record('passed'), failed: record('failed'),
    errored: record('errored'), skipped: record('skipped'),
    appendOutput: () => {}, end: () => { ended++; },
  };
  const noopDisposable = () => ({ dispose: () => {} });
  const host = {
    tests: {
      createTestController: () => ({
        items: new Map(),
        createTestRun: () => run,
        createRunProfile: (_label: string, _kind: number, callback: RunHandler) => {
          handler = callback;
        },
      }),
    },
    workspace: {
      findFiles: async () => [],
      getConfiguration: () => ({ get: () => 'configured cli with spaces' }),
      createFileSystemWatcher: () => ({
        onDidCreate: noopDisposable, onDidChange: noopDisposable, onDidDelete: noopDisposable,
      }),
    },
    window: { showWarningMessage: (message: string) => { warnings.push(message); } },
    TestRunProfileKind: { Run: 1 },
    TestMessage: class { constructor(readonly message: string) {} },
  };
  const compiledModule = { exports: {} as { registerTestController(context: unknown): void } };
  runInNewContext(fs.readFileSync(controllerPath, 'utf8'), {
    module: compiledModule, exports: compiledModule.exports, Buffer,
    setTimeout: clock ? clock.setTimeout.bind(clock) : setTimeout,
    clearTimeout: clock ? clock.clearTimeout.bind(clock) : clearTimeout,
    require: (id: string) => id === 'vscode' ? host
      : id === 'node:child_process' ? { spawn: spawnChild } : requireActual(id),
  }, { filename: controllerPath });
  compiledModule.exports.registerTestController({ subscriptions: [] });
  assert.ok(handler, 'the real adapter registered its run profile');
  const registeredHandler = handler;
  const file = {
    id: 'file:///scenario with spaces.e2e.yaml',
    uri: { fsPath: path.join(os.tmpdir(), 'scenario with spaces.e2e.yaml') },
    children: new Map(),
  } as unknown as vscode.TestItem;
  return {
    token, file, states, warnings,
    get ended() { return ended; },
    run: (requestToken: Token = new Token()) => registeredHandler(
      { include: [file] } as unknown as vscode.TestRunRequest,
      requestToken as unknown as vscode.CancellationToken,
    ),
  };
}

const events = [
  { type: 'scenario-started', runId: 'r', scenarioId: 's' },
  { type: 'step-completed', runId: 'r', stepId: 'step', verdict: 'PASS' },
  { type: 'scenario-completed', runId: 'r', scenarioId: 's', verdict: 'PASS' },
].map((event) => JSON.stringify({ v: 1, schemaVersion: 'v1', ...event })).join('\n');

test('normal runs keep stdin open, opt into EOF shutdown, and discard unused stdout', async () => {
  const child = new Child();
  let args: string[] = [];
  let options: SpawnOptions = {};
  let command = '';
  const adapter = harness((executable, argv, spawnOptions) => {
    command = executable; args = Array.from(argv); options = spawnOptions;
    return child;
  });
  const running = adapter.run();
  assert.equal(command, 'configured cli with spaces');
  assert.deepEqual(args, [
    'run', adapter.file.uri!.fsPath, '--events', args[3], '--no-decorations',
    '--shutdown-on-stdin-eof',
  ]);
  assert.deepEqual(Array.from(options.stdio as string[]), ['pipe', 'ignore', 'pipe']);
  assert.equal(options.windowsHide, true);
  assert.equal(options.shell, undefined);
  assert.equal(child.stdin.writableEnded, false);
  fs.writeFileSync(args[3], events);
  child.close(0);
  await running;
  assert.equal(adapter.states.get(adapter.file.id), 'passed');
  assert.equal(adapter.ended, 1);
  assert.equal(adapter.token.listeners.size, 0);
  assert.equal(fs.existsSync(path.dirname(args[3])), false);
});

test('cancellation closes the owned writer and gives the engine its cleanup grace', async () => {
  const child = new Child();
  const clock = new Clock();
  const adapter = harness(() => child, new Token(), clock);
  const running = adapter.run();
  adapter.token.cancel();
  adapter.token.cancel();
  assert.equal(child.stdin.writableEnded, true);
  assert.deepEqual(child.signals, []);
  assert.equal(clock.pending.size, 1);
  assert.equal([...clock.pending.values()][0].delay, 35_000);
  child.close(4);
  await running;
  assert.equal(clock.pending.size, 0);
  assert.equal(adapter.token.listeners.size, 0);
  assert.equal(adapter.states.get(adapter.file.id), 'skipped');
  assert.deepEqual(adapter.warnings, []);
});

test('a child that ignores EOF is killed only after the grace expires', async () => {
  const child = new Child();
  const clock = new Clock();
  const adapter = harness(() => child, new Token(), clock);
  const running = adapter.run();
  adapter.token.cancel();
  assert.deepEqual(child.signals, []);
  clock.fire();
  assert.deepEqual(child.signals, ['SIGKILL']);
  child.close(4);
  await running;
  assert.equal(clock.pending.size, 0);
});

test('actual exit clears cancellation before delayed stdio close and never signals again', async () => {
  const child = new Child();
  const clock = new Clock();
  const adapter = harness(() => child, new Token(), clock);
  const running = adapter.run();
  adapter.token.cancel();
  child.exit(4);
  assert.equal(clock.pending.size, 0);
  assert.equal(adapter.token.listeners.size, 0);
  adapter.token.cancel();
  clock.fire();
  assert.deepEqual(child.signals, []);
  child.emit('close', 4, null);
  await running;
});

test('spawn failure clears fallback, disposes cancellation, and removes owned events directory', async () => {
  const child = new Child();
  const clock = new Clock();
  let eventsPath = '';
  const adapter = harness((_command, args) => { eventsPath = args[3]; return child; }, new Token(), clock);
  const running = adapter.run();
  adapter.token.cancel();
  child.emit('error', Object.assign(new Error('missing CLI'), { code: 'ENOENT' }));
  child.emit('close', null, null);
  await running;
  assert.equal(clock.pending.size, 0);
  assert.equal(adapter.token.listeners.size, 0);
  assert.equal(adapter.ended, 1);
  assert.equal(fs.existsSync(path.dirname(eventsPath)), false);
  clock.fire();
  assert.deepEqual(child.signals, []);
});

test('already cancelled run tokens launch no child', async () => {
  const token = new Token();
  token.cancel();
  let spawned = 0;
  const adapter = harness(() => { spawned++; return new Child(); }, token);
  // The run token can already be cancelled before the request token catches up.
  const running = adapter.run();
  assert.equal(spawned, 0);
  await running;
});

test('cancellation during spawn is observed even when its notification was missed', async () => {
  const token = new Token();
  const child = new Child();
  const clock = new Clock();
  const adapter = harness(() => { token.cancel(); return child; }, token, clock);
  const running = adapter.run();
  assert.equal(child.stdin.writableEnded, true);
  assert.deepEqual(child.signals, []);
  child.close(4);
  await running;
  assert.equal(clock.pending.size, 0);
});

test('writer errors during an exit race are handled and cannot kill an exited child', async () => {
  const child = new Child();
  const clock = new Clock();
  const adapter = harness(() => child, new Token(), clock);
  const running = adapter.run();
  adapter.token.cancel();
  child.stdin.emit('error', Object.assign(new Error('closed pipe'), { code: 'EPIPE' }));
  child.close(4);
  await running;
  clock.fire();
  assert.deepEqual(child.signals, []);
});

test('real child sees cancellation EOF and completes its cleanup before exit', { timeout: 30_000 }, async (t) => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'vouchfx-lifecycle-'));
  const marker = path.join(directory, 'cleanup.txt');
  let child: ChildProcess | undefined;
  let ready: () => void = () => {};
  const started = new Promise<void>((resolve) => { ready = resolve; });
  const adapter = harness((_command, args, options) => {
    const script = `
      const fs = require('node:fs');
      process.stdin.resume();
      process.stdin.on('end', () => {
        fs.writeFileSync(${JSON.stringify(marker)}, 'cleanup completed');
        setTimeout(() => process.exit(4), 20);
      });
      process.stderr.write('READY');
      setInterval(() => {}, 1000);
    `;
    child = spawn(process.execPath, ['-e', script, '--', ...args], options);
    child.stderr!.once('data', ready);
    return child;
  });
  t.after(() => {
    if (child && child.exitCode === null && child.signalCode === null) {
      child.kill('SIGKILL');
    }
    fs.rmSync(directory, { recursive: true, force: true });
  });
  const running = adapter.run();
  await started;
  assert.equal(fs.existsSync(marker), false, 'stdin stayed open before cancellation');
  adapter.token.cancel();
  await running;
  assert.equal(fs.readFileSync(marker, 'utf8'), 'cleanup completed');
  assert.equal(child!.exitCode, 4);
  assert.equal(adapter.states.get(adapter.file.id), 'skipped');
});

test('real child can write 8 MiB stdout and finish without pipe backpressure', { timeout: 30_000 }, async (t) => {
  let child: ChildProcess | undefined;
  const adapter = harness((_command, args, options) => {
    const script = `
      const fs = require('node:fs');
      const { once } = require('node:events');
      (async () => {
        const block = Buffer.alloc(65536, 'x');
        for (let i = 0; i < 128; i++) {
          if (!process.stdout.write(block)) await once(process.stdout, 'drain');
        }
        fs.writeFileSync(process.argv[4], ${JSON.stringify(events)});
      })().catch(() => process.exit(1));
    `;
    child = spawn(process.execPath, ['-e', script, '--', ...args], options);
    return child;
  });
  const watchdog = setTimeout(() => adapter.token.cancel(), 20_000);
  t.after(() => {
    clearTimeout(watchdog);
    if (child && child.exitCode === null && child.signalCode === null) {
      child.kill('SIGKILL');
    }
  });
  await adapter.run();
  assert.equal(child!.exitCode, 0);
  assert.equal(adapter.states.get(adapter.file.id), 'passed');
});
