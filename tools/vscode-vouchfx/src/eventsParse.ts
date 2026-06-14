// Pure, VSCode-free parser for the vouchfx `--events` JSON Lines stream (§14.4).
//
// The engine emits ONE schema-versioned JSON Lines event stream that feeds every
// renderer; this is the Test-Explorer consumer of that stream. It reads only the
// three event types the Test Explorer needs and is deliberately TOLERANT (§14):
//   • blank lines are skipped,
//   • malformed (non-JSON) lines are skipped, never thrown on,
//   • unknown event types and unknown fields are ignored.
//
// Events consumed (flat wire shape — all fields are siblings at the root object):
//   scenario-started   → scenarioId, file?      (begins a scenario)
//   step-completed     → stepId, verdict        (a resolved step)
//   scenario-completed → scenarioId, verdict    (the aggregate scenario verdict)
//
// Step association: a `step-completed` is attached to the MOST-RECENTLY-STARTED
// scenario. The stream is line-ordered, so the active scenario is simply the
// last `scenario-started` seen. A `step-completed` before any scenario-started
// (no active scenario) is dropped. This mirrors the §14 invariant that the
// stream is self-describing and renderers tolerate aggregation: events for one
// scenario arrive between its start and the next scenario's start.

/** A single resolved step within a scenario. */
export interface ParsedStep {
  /** The step's `id`. */
  readonly stepId: string;
  /** The step's verdict wire token (`PASS`/`FAIL`/`ENV_ERROR`/`INCONCLUSIVE`). */
  readonly verdict: string;
}

/** A scenario reconstructed from the event stream. */
export interface ParsedScenario {
  /** The scenario's `id`. */
  readonly scenarioId: string;
  /** The `.e2e.yaml` file path from `scenario-started`, or `null` when absent. */
  readonly file: string | null;
  /** The aggregate verdict from `scenario-completed`, or `null` when not seen. */
  readonly verdict: string | null;
  /** The resolved steps, in arrival order. */
  readonly steps: ParsedStep[];
}

/** The parsed view of a `--events` stream. */
export interface ParsedEvents {
  /** Scenarios in the order they were started. */
  readonly scenarios: ParsedScenario[];
}

/** Mutable accumulator mirroring {@link ParsedScenario} during the single pass. */
interface ScenarioAccumulator {
  scenarioId: string;
  file: string | null;
  verdict: string | null;
  steps: ParsedStep[];
}

const EVENT_SCENARIO_STARTED = 'scenario-started';
const EVENT_STEP_COMPLETED = 'step-completed';
const EVENT_SCENARIO_COMPLETED = 'scenario-completed';

/**
 * Parses a `--events` JSON Lines stream into a per-scenario view.
 *
 * Tolerant by contract (§14): blank, malformed, and unknown-type lines are
 * skipped; unknown fields on a known event are ignored. Never throws.
 *
 * @param jsonl The raw contents of the `--events` file.
 * @returns The scenarios (with their steps and verdicts) in start order.
 */
export function parseEvents(jsonl: string): ParsedEvents {
  const scenarios: ScenarioAccumulator[] = [];
  const byId = new Map<string, ScenarioAccumulator>();
  let active: ScenarioAccumulator | null = null;

  // Split on both \n and \r\n; a trailing newline yields a final empty line that
  // the blank-line guard skips.
  const lines = jsonl.split(/\r?\n/);
  for (const line of lines) {
    const trimmed = line.trim();
    if (trimmed.length === 0) {
      continue;
    }

    const event = tryParseObject(trimmed);
    if (event === null) {
      continue;
    }

    const type = typeof event.type === 'string' ? event.type : undefined;
    switch (type) {
      case EVENT_SCENARIO_STARTED: {
        const scenarioId = readString(event.scenarioId);
        if (scenarioId === null) {
          break;
        }
        const acc: ScenarioAccumulator = {
          scenarioId,
          file: readString(event.file),
          verdict: null,
          steps: [],
        };
        scenarios.push(acc);
        byId.set(scenarioId, acc);
        active = acc;
        break;
      }

      case EVENT_STEP_COMPLETED: {
        // Attach to the most-recently-started scenario; drop if none is active.
        if (active === null) {
          break;
        }
        const stepId = readString(event.stepId);
        const verdict = readString(event.verdict);
        if (stepId === null || verdict === null) {
          break;
        }
        active.steps.push({ stepId, verdict });
        break;
      }

      case EVENT_SCENARIO_COMPLETED: {
        const scenarioId = readString(event.scenarioId);
        if (scenarioId === null) {
          break;
        }
        // Match by id so an out-of-order completion lands on the right scenario;
        // an unknown scenarioId is ignored rather than inventing a scenario.
        const target = byId.get(scenarioId);
        if (target) {
          target.verdict = readString(event.verdict);
        }
        break;
      }

      default:
        // Unknown / future event type (§14 forward-compat): ignore.
        break;
    }
  }

  return {
    scenarios: scenarios.map((s) => ({
      scenarioId: s.scenarioId,
      file: s.file,
      verdict: s.verdict,
      steps: s.steps,
    })),
  };
}

/**
 * Parses one line as a JSON object, returning `null` for anything that is not a
 * plain object (including non-JSON text, arrays, and primitives).
 */
function tryParseObject(line: string): Record<string, unknown> | null {
  let value: unknown;
  try {
    value = JSON.parse(line);
  } catch {
    return null;
  }
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    return null;
  }
  return value as Record<string, unknown>;
}

/** Returns a value as a string when it is one, else `null`. */
function readString(value: unknown): string | null {
  return typeof value === 'string' ? value : null;
}
