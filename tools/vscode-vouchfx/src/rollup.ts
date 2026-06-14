// Pure, VSCode-free roll-up of a scenario's observed step states onto the
// file-level Test Explorer item.
//
// The Test Explorer reports each step individually, then needs ONE state for the
// owning file item. When the stream carried a `scenario-completed` we use that
// aggregate verdict directly (mapped via `mapVerdict`). But on a TRUNCATED or
// ABORTED stream there is no `scenario-completed` — `scenario.verdict` is null —
// and we must not present a partial run that already saw a failing step as
// `skipped` ("didn't run"). Instead we roll up to the WORST observed step state.
//
// The precedence mirrors the engine's §12.1 verdict elevation
// (EnvError > Fail > Inconclusive > Pass): on the editor surface that is
//   errored > failed > skipped > passed
// — `errored` (an environment/infra problem) dominates a `failed` (a defect),
// which dominates a `skipped` (inconclusive / not mentioned), which dominates a
// `passed`. So a partial stream containing a failing step surfaces as failed (or
// errored), never as a misleading `skipped`.
//
// Keeping this pure (no `vscode` import) makes the worst-state precedence and the
// null-scenario-verdict branches directly unit-testable under `node --test`.

import { EditorState, mapVerdict } from './verdictMap';

/**
 * The editor-state precedence used to elevate a set of step states to one
 * file-level state, mirroring the engine's EnvError > Fail > Inconclusive > Pass
 * elevation. A higher number dominates.
 */
const STATE_PRECEDENCE: Readonly<Record<EditorState, number>> = {
  passed: 0,
  skipped: 1,
  failed: 2,
  errored: 3,
};

/**
 * Rolls a scenario's observed step states up to a single file-level
 * {@link EditorState}.
 *
 * - When a scenario verdict is present (`scenarioVerdict` is a wire token, i.e. a
 *   `scenario-completed` arrived), that aggregate verdict wins: it is mapped via
 *   {@link mapVerdict} and returned directly. The engine has already elevated the
 *   per-step verdicts into this one, so we honour it verbatim.
 * - When the scenario verdict is `null` (no `scenario-completed` — a truncated or
 *   aborted stream) and there is at least one observed step, the file rolls up to
 *   the WORST observed step state by {@link STATE_PRECEDENCE}
 *   (errored > failed > skipped > passed). This keeps a partial stream whose
 *   steps include a failure from masquerading as `skipped`.
 * - When the scenario verdict is `null` and there are NO observed steps, this
 *   returns `null` so the caller can apply its own fail-soft "no events"
 *   behaviour (an `errored` item naming the missing stream).
 *
 * @param stepStates The editor states observed for the scenario's steps (a
 *   step the events never mentioned is reported as `skipped` upstream and may be
 *   included here, or omitted — either way it cannot lower the result below
 *   `skipped`).
 * @param scenarioVerdict The `scenario-completed` wire token, or `null` when no
 *   completion event arrived.
 * @returns The file-level editor state, or `null` when there is neither a
 *   scenario verdict nor any observed step.
 */
export function rollupState(
  stepStates: readonly EditorState[],
  scenarioVerdict: string | null,
): EditorState | null {
  if (scenarioVerdict !== null) {
    return mapVerdict(scenarioVerdict).state;
  }

  if (stepStates.length === 0) {
    return null;
  }

  // Worst-observed-state: take the maximum by precedence.
  return stepStates.reduce<EditorState>((worst, state) => {
    return STATE_PRECEDENCE[state] > STATE_PRECEDENCE[worst] ? state : worst;
  }, 'passed');
}
