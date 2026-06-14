// VSCode Test Explorer integration for vouchfx `.e2e.yaml` scenarios (S10-G-01).
//
// This is the THIN `vscode`-API adapter. All testable logic lives in the pure
// modules it imports — `e2eYamlOutline` (file → step line ranges),
// `eventsParse` (`--events` stream → per-scenario verdicts) and `verdictMap`
// (verdict token → editor state). The `vscode` module is unavailable under
// `node --test`, so this file deliberately holds NO logic worth unit-testing:
// it discovers files, spawns the CLI, and forwards the parsed results onto the
// Test Explorer surface.
//
// Behaviour:
//   • Discovery — one parent TestItem per `*.e2e.yaml` file in the workspace,
//     one child TestItem per id-bearing step (range from `parseE2eOutline`).
//     A FileSystemWatcher keeps this current on create/change/delete, and a
//     resolveHandler (re-)scans on demand.
//   • Run — for each requested file the CLI is spawned as
//       <cliPath> run <file> --events <tmpEventsFile> --no-decorations
//     and on exit the temp events file is parsed; each step is reported via
//     run.passed/failed/errored/skipped per `mapVerdict`. A Fail attaches a
//     TestMessage whose `location` is the step's YAML line, so the failing line
//     is decorated in the editor and the true verdict is named.
//   • Fail-soft — a missing/erroring CLI, or a run that produced no events, marks
//     the affected items `errored` with a clear message and shows a ONE-TIME
//     notice mentioning `vouchfx.cliPath`. The run handler never throws.
//
// The four §12.1 verdicts are kept distinct on the editor surface via
// `mapVerdict`: an environment error shows as `errored` (infra), an inconclusive
// result as `skipped` — never silently green, never conflated with a Fail.

import { spawn } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import * as vscode from 'vscode';

import { E2eOutlineStep, parseE2eOutline } from './e2eYamlOutline';
import { ParsedScenario, parseEvents } from './eventsParse';
import { mapVerdict } from './verdictMap';

/** Glob identifying a vouchfx test document — kept identical to the manifest. */
const E2E_GLOB = '**/*.e2e.yaml';

/** Configuration section + key exposing the CLI path. */
const CONFIG_SECTION = 'vouchfx';
const CONFIG_CLI_PATH = 'cliPath';
const DEFAULT_CLI_PATH = 'vouchfx';

/** Controller id + label shown in the Test Explorer. */
const CONTROLLER_ID = 'vouchfx';
const CONTROLLER_LABEL = 'vouchfx .e2e.yaml';

/**
 * Sets up the Test Explorer controller and registers all disposables on the
 * extension context. Fail-soft: any unexpected error is swallowed (the schema
 * authoring features must never be regressed by a Test Explorer failure).
 *
 * @param context The extension context whose `subscriptions` own every
 *   disposable created here.
 */
export function registerTestController(context: vscode.ExtensionContext): void {
  try {
    const controller = vscode.tests.createTestController(CONTROLLER_ID, CONTROLLER_LABEL);
    context.subscriptions.push(controller);

    // One-time notice latch (per activation) so a misconfigured CLI nags once.
    const notice = { shown: false };

    // ---- Discovery -------------------------------------------------------
    // A resolveHandler lets VSCode ask us to (re)populate. With no argument it
    // means "discover the roots"; we (re)scan the whole workspace.
    controller.resolveHandler = async (item) => {
      if (item === undefined) {
        await discoverAllFiles(controller);
      }
    };

    // Kick off an initial discovery so items appear without an explicit refresh.
    void discoverAllFiles(controller);

    // Manual refresh button → full re-scan.
    controller.refreshHandler = async () => {
      await discoverAllFiles(controller);
    };

    // Keep the tree in step with the filesystem.
    const watcher = vscode.workspace.createFileSystemWatcher(E2E_GLOB);
    context.subscriptions.push(watcher);
    context.subscriptions.push(
      watcher.onDidCreate((uri) => {
        void upsertFile(controller, uri);
      }),
      watcher.onDidChange((uri) => {
        void upsertFile(controller, uri);
      }),
      watcher.onDidDelete((uri) => {
        controller.items.delete(fileItemId(uri));
      }),
    );

    // ---- Run profile -----------------------------------------------------
    controller.createRunProfile(
      'Run',
      vscode.TestRunProfileKind.Run,
      (request, token) => runHandler(controller, request, token, notice),
      true,
    );
  } catch {
    // Never let Test Explorer setup take down activation.
  }
}

// ---------------------------------------------------------------------------
// Discovery
// ---------------------------------------------------------------------------

/** Stable TestItem id for a file (its URI string). */
function fileItemId(uri: vscode.Uri): string {
  return uri.toString();
}

/** Stable TestItem id for a step within a file. */
function stepItemId(uri: vscode.Uri, stepId: string): string {
  return `${uri.toString()}::${stepId}`;
}

/** Finds every `*.e2e.yaml` in the workspace and (re)builds the tree. */
async function discoverAllFiles(controller: vscode.TestController): Promise<void> {
  let uris: vscode.Uri[];
  try {
    uris = await vscode.workspace.findFiles(E2E_GLOB);
  } catch {
    return;
  }

  const seen = new Set<string>();
  for (const uri of uris) {
    seen.add(fileItemId(uri));
    await upsertFile(controller, uri);
  }

  // Drop items whose backing file no longer matches (e.g. after a bulk delete).
  for (const [id] of controller.items) {
    if (!seen.has(id)) {
      controller.items.delete(id);
    }
  }
}

/**
 * Creates or refreshes the TestItem for a single file plus its step children,
 * reading the outline from disk. Fail-soft: an unreadable/garbled file yields a
 * parent item with no children rather than an error.
 */
async function upsertFile(controller: vscode.TestController, uri: vscode.Uri): Promise<void> {
  let text: string;
  try {
    const bytes = await vscode.workspace.fs.readFile(uri);
    text = Buffer.from(bytes).toString('utf8');
  } catch {
    return;
  }

  const outline = parseE2eOutline(text);

  const id = fileItemId(uri);
  const label = outline.scenarioName ?? path.basename(uri.fsPath);

  let fileItem = controller.items.get(id);
  if (fileItem === undefined) {
    fileItem = controller.createTestItem(id, label, uri);
    controller.items.add(fileItem);
  } else {
    fileItem.label = label;
  }

  // Rebuild children from the outline (the cheapest correct refresh).
  const children: vscode.TestItem[] = outline.steps.map((step) =>
    buildStepItem(controller, uri, step),
  );
  fileItem.children.replace(children);
}

/** Builds a child TestItem for one step, anchored at its YAML line range. */
function buildStepItem(
  controller: vscode.TestController,
  uri: vscode.Uri,
  step: E2eOutlineStep,
): vscode.TestItem {
  const item = controller.createTestItem(stepItemId(uri, step.id), step.id, uri);
  item.range = new vscode.Range(
    new vscode.Position(step.startLine, 0),
    new vscode.Position(step.endLine, 0),
  );
  return item;
}

// ---------------------------------------------------------------------------
// Run
// ---------------------------------------------------------------------------

/** Resolves the configured CLI path, falling back to the bare `vouchfx`. */
function resolveCliPath(scope?: vscode.Uri): string {
  const configured = vscode.workspace
    .getConfiguration(CONFIG_SECTION, scope)
    .get<string>(CONFIG_CLI_PATH, DEFAULT_CLI_PATH);
  const trimmed = (configured ?? '').trim();
  return trimmed.length > 0 ? trimmed : DEFAULT_CLI_PATH;
}

/**
 * The set of file-level TestItems a run request targets. An empty/whole-suite
 * request (`include === undefined`) maps to every known file item; a request
 * that includes step children is mapped up to their owning file (we run a file
 * at a time, then report per step from its events).
 */
function fileItemsForRequest(
  controller: vscode.TestController,
  request: vscode.TestRunRequest,
): vscode.TestItem[] {
  const result = new Map<string, vscode.TestItem>();

  const consider = (item: vscode.TestItem): void => {
    // A step child's parent is the file item; a file item has no parent.
    const fileItem = item.parent ?? item;
    result.set(fileItem.id, fileItem);
  };

  if (request.include === undefined) {
    for (const [, item] of controller.items) {
      result.set(item.id, item);
    }
  } else {
    for (const item of request.include) {
      consider(item);
    }
  }

  const excluded = new Set((request.exclude ?? []).map((i) => (i.parent ?? i).id));
  return [...result.values()].filter((i) => !excluded.has(i.id));
}

/**
 * The run handler. Spawns the CLI once per requested file, parses its
 * `--events` output, and reports per-step verdicts. Never throws.
 */
async function runHandler(
  controller: vscode.TestController,
  request: vscode.TestRunRequest,
  token: vscode.CancellationToken,
  notice: { shown: boolean },
): Promise<void> {
  const run = controller.createTestRun(request);
  try {
    const fileItems = fileItemsForRequest(controller, request);

    for (const fileItem of fileItems) {
      if (token.isCancellationRequested) {
        break;
      }
      if (fileItem.uri === undefined) {
        continue;
      }
      await runOneFile(run, fileItem, notice);
    }
  } catch {
    // Defensive: anything unexpected must not escape the handler.
  } finally {
    run.end();
  }
}

/** Runs a single file's scenario and reports its step verdicts. */
async function runOneFile(
  run: vscode.TestRun,
  fileItem: vscode.TestItem,
  notice: { shown: boolean },
): Promise<void> {
  const uri = fileItem.uri;
  if (uri === undefined) {
    return;
  }

  // Enqueue the file + its known step children so the UI shows progress.
  const stepItems = collectStepItems(fileItem);
  run.started(fileItem);
  for (const step of stepItems) {
    run.enqueued(step);
    run.started(step);
  }

  const cliPath = resolveCliPath(uri);
  const eventsPath = makeTempEventsPath();

  let cliResult: CliResult;
  try {
    cliResult = await spawnCli(cliPath, uri.fsPath, eventsPath, run.token);
  } catch (error) {
    failSoft(run, fileItem, stepItems, notice, cliPath, describeSpawnError(error));
    safeUnlink(eventsPath);
    return;
  }

  // Read whatever events the CLI produced (it may have written some before a
  // non-zero exit — e.g. a Fail run still emits a full stream).
  let eventsText = '';
  try {
    eventsText = fs.readFileSync(eventsPath, 'utf8');
  } catch {
    // No events file at all.
  }
  safeUnlink(eventsPath);

  const parsed = parseEvents(eventsText);
  const scenario = pickScenario(parsed.scenarios, uri);

  if (scenario === undefined || scenario.steps.length === 0) {
    // The CLI ran but yielded no usable events. If it also exited non-zero with
    // no output, that is almost certainly a CLI/config problem.
    const detail =
      cliResult.code === 0
        ? 'The run produced no events to report.'
        : `The CLI exited with code ${cliResult.code} and produced no events.` +
          (cliResult.stderr ? `\n${truncate(cliResult.stderr)}` : '');
    failSoft(run, fileItem, stepItems, notice, cliPath, detail);
    return;
  }

  reportScenario(run, fileItem, stepItems, scenario);
}

/** Reports a parsed scenario's per-step verdicts onto the Test Explorer. */
function reportScenario(
  run: vscode.TestRun,
  fileItem: vscode.TestItem,
  stepItems: vscode.TestItem[],
  scenario: ParsedScenario,
): void {
  const stepItemById = new Map(stepItems.map((s) => [stepLocalId(s), s]));

  for (const parsedStep of scenario.steps) {
    const item = stepItemById.get(parsedStep.stepId);
    if (item === undefined) {
      // A step in the events that has no TestItem (e.g. the file changed since
      // discovery). Nothing on the surface to decorate; skip it.
      continue;
    }
    applyVerdict(run, item, parsedStep.verdict);
  }

  // Any known step the events did not mention is reported as skipped so the UI
  // never leaves a stale "running" spinner.
  const reported = new Set(scenario.steps.map((s) => s.stepId));
  for (const item of stepItems) {
    if (!reported.has(stepLocalId(item))) {
      run.skipped(item);
    }
  }

  // Roll the scenario verdict up onto the file item.
  if (scenario.verdict !== null) {
    applyVerdict(run, fileItem, scenario.verdict);
  } else {
    run.skipped(fileItem);
  }
}

/**
 * Applies a verdict token to a TestItem via the mapped editor state. A Fail
 * attaches a TestMessage located at the item's YAML line, naming the true
 * verdict; the other non-pass states attach a plain message naming the verdict
 * so the four-way taxonomy is legible on the surface.
 */
function applyVerdict(run: vscode.TestRun, item: vscode.TestItem, verdictToken: string): void {
  const { state, verdict } = mapVerdict(verdictToken);

  switch (state) {
    case 'passed':
      run.passed(item);
      return;
    case 'failed':
      run.failed(item, buildMessage(item, `vouchfx verdict: ${verdict}`));
      return;
    case 'errored':
      run.errored(item, buildMessage(item, `vouchfx verdict: ${verdict}`));
      return;
    case 'skipped':
      // Inconclusive surfaces as skipped; still annotate so the reason is clear.
      run.skipped(item);
      return;
    default:
      run.errored(item, buildMessage(item, `vouchfx verdict: ${verdict}`));
      return;
  }
}

/**
 * Builds a TestMessage located at the item's YAML line so the editor decorates
 * the failing step in-line.
 */
function buildMessage(item: vscode.TestItem, text: string): vscode.TestMessage {
  const message = new vscode.TestMessage(text);
  if (item.uri !== undefined && item.range !== undefined) {
    message.location = new vscode.Location(item.uri, item.range);
  }
  return message;
}

// ---------------------------------------------------------------------------
// CLI spawning
// ---------------------------------------------------------------------------

interface CliResult {
  readonly code: number | null;
  readonly stderr: string;
}

/**
 * Spawns `<cliPath> run <file> --events <eventsPath> --no-decorations` and
 * resolves when the process exits. Rejects only when the process could not be
 * started at all (e.g. the CLI is not on PATH) — a non-zero exit resolves
 * normally so the caller can still read whatever events were written.
 */
function spawnCli(
  cliPath: string,
  filePath: string,
  eventsPath: string,
  token: vscode.CancellationToken,
): Promise<CliResult> {
  return new Promise<CliResult>((resolve, reject) => {
    const args = ['run', filePath, '--events', eventsPath, '--no-decorations'];

    // `shell: false` (the default) — arguments are passed as an array, so no
    // shell-quoting/injection concern even if a path contains spaces.
    const child = spawn(cliPath, args, { windowsHide: true });

    let stderr = '';
    let settled = false;

    const onCancel = token.onCancellationRequested(() => {
      try {
        child.kill();
      } catch {
        // Best effort.
      }
    });

    child.stderr?.on('data', (chunk: Buffer) => {
      stderr += chunk.toString('utf8');
    });

    child.on('error', (error) => {
      if (settled) {
        return;
      }
      settled = true;
      onCancel.dispose();
      reject(error);
    });

    child.on('close', (code) => {
      if (settled) {
        return;
      }
      settled = true;
      onCancel.dispose();
      resolve({ code, stderr });
    });
  });
}

/** A unique temp path for one run's events file. */
function makeTempEventsPath(): string {
  const name = `vouchfx-events-${process.pid}-${Date.now()}-${Math.random().toString(36).slice(2)}.jsonl`;
  return path.join(os.tmpdir(), name);
}

/** Deletes a file, ignoring any error (it may never have been created). */
function safeUnlink(p: string): void {
  try {
    fs.unlinkSync(p);
  } catch {
    // Ignore.
  }
}

// ---------------------------------------------------------------------------
// Fail-soft + helpers
// ---------------------------------------------------------------------------

/**
 * Marks the file and its steps as errored with a clear message, and shows a
 * one-time notice mentioning `vouchfx.cliPath` so the user can point the
 * extension at their CLI.
 */
function failSoft(
  run: vscode.TestRun,
  fileItem: vscode.TestItem,
  stepItems: vscode.TestItem[],
  notice: { shown: boolean },
  cliPath: string,
  detail: string,
): void {
  const text =
    `vouchfx could not produce a verdict for this scenario.\n${detail}\n\n` +
    `CLI invoked: "${cliPath}". Set "vouchfx.cliPath" if the vouchfx CLI is ` +
    `not on PATH.`;

  run.errored(fileItem, buildMessage(fileItem, text));
  for (const step of stepItems) {
    run.errored(step, buildMessage(step, text));
  }

  if (!notice.shown) {
    notice.shown = true;
    void vscode.window.showWarningMessage(
      `vouchfx: could not run the suite via "${cliPath}". ` +
        `Check the "vouchfx.cliPath" setting points at your vouchfx CLI.`,
    );
  }
}

/** A human description of a spawn error (CLI missing, not executable, …). */
function describeSpawnError(error: unknown): string {
  if (error instanceof Error) {
    const code = (error as NodeJS.ErrnoException).code;
    if (code === 'ENOENT') {
      return 'The vouchfx CLI was not found (ENOENT).';
    }
    return `Failed to start the vouchfx CLI: ${error.message}`;
  }
  return 'Failed to start the vouchfx CLI.';
}

/** Collects the step children of a file item. */
function collectStepItems(fileItem: vscode.TestItem): vscode.TestItem[] {
  const out: vscode.TestItem[] = [];
  for (const [, child] of fileItem.children) {
    out.push(child);
  }
  return out;
}

/** Recovers a step's local id (the part after `::`) from its TestItem id. */
function stepLocalId(item: vscode.TestItem): string {
  const marker = item.id.lastIndexOf('::');
  return marker >= 0 ? item.id.slice(marker + 2) : item.id;
}

/**
 * Chooses the scenario from the parsed events that corresponds to this file.
 * The CLI runs exactly one file here, so prefer a scenario whose `file` matches
 * the run target; fall back to the sole scenario when there is exactly one.
 */
function pickScenario(
  scenarios: readonly ParsedScenario[],
  uri: vscode.Uri,
): ParsedScenario | undefined {
  if (scenarios.length === 0) {
    return undefined;
  }
  const targetBase = path.basename(uri.fsPath).toLowerCase();
  const byFile = scenarios.find((s) => {
    if (s.file === null) {
      return false;
    }
    return path.basename(s.file).toLowerCase() === targetBase;
  });
  if (byFile !== undefined) {
    return byFile;
  }
  // No file-name match (e.g. the CLI omitted `file`): a single-file run produces
  // exactly one scenario, so fall back to the first. With multiple unmatched
  // scenarios we still take the first deterministically rather than guess.
  return scenarios[0];
}

/** Truncates a long stderr blob for an inline message. */
function truncate(text: string, max = 800): string {
  const trimmed = text.trim();
  return trimmed.length > max ? `${trimmed.slice(0, max)}…` : trimmed;
}
