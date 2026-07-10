// Pure, VSCode-free mapping from a vouchfx verdict wire token to a VSCode Test
// Explorer editor state plus a human-readable verdict label.
//
// SOURCE OF TRUTH — the wire tokens (PASS/FAIL/ENV_ERROR/INCONCLUSIVE) mirror the
// .NET `VerdictJsonConverter` (Token* constants), which exists on `main`. The
// editor-state mapping mirrors the .NET `EditorStateMapping` pinned in
//   tests/Vouchfx.Engine.Reporting.Tests/RendererParityTests.cs
// — the renderer-parity guard that pins how the four §12.1 verdicts surface on
// the editor / Test Explorer surface — delivered in the companion Sprint-10 .NET
// PR #148 (so that symbol is not yet on `main`). The four-verdict taxonomy is non-
// negotiable and is kept separate everywhere — taxonomy, reporting, exit codes,
// and now the editor surface:
//
//   wire token     editor state   human label
//   ------------   ------------   --------------------
//   PASS        →  passed         "Pass"
//   FAIL        →  failed         "Fail"
//   ENV_ERROR   →  errored        "Environment error"
//   INCONCLUSIVE→  skipped        "Inconclusive"
//
// An ENVIRONMENT error MUST surface as `errored` (an infra problem), never as
// `failed` (a product defect): conflating the two destroys trust in the tool
// (§12.1). An INCONCLUSIVE result MUST surface as `skipped`, not a pass or a
// fail — the engine could not decide. This mirrors the JUnit mapping the engine
// already ships (Fail→failure, EnvError→error, Inconclusive→skipped).
//
// CROSS-LANGUAGE SYNC: there is no automated gate yet that fails CI when this
// TypeScript table and the .NET EditorStateMapping diverge — that gate is a
// tracked follow-up. Until it exists, any change here must be mirrored there
// (and vice versa) by hand.
//
// TOTALITY: every input — including the empty string, whitespace, an unknown
// token, or a wrong-case token — yields exactly one of the four declared
// states. An unknown/missing/mismatched token maps to `errored` / "Unknown",
// NEVER silently to `passed`: a verdict we cannot interpret must be loud, not
// optimistically green.

/**
 * The four VSCode Test Explorer outcome states this extension drives, mirroring
 * the four §12.1 verdicts. These are exactly the names of the
 * `vscode.TestRun` outcome methods (`run.passed`, `run.failed`, `run.errored`,
 * `run.skipped`), so a caller can dispatch on `state` directly.
 */
export type EditorState = 'passed' | 'failed' | 'errored' | 'skipped';

/**
 * The result of mapping a verdict wire token: the editor state to drive, plus a
 * human-readable verdict label for a {@link TestMessage} / status line.
 */
export interface VerdictMapping {
  /** The VSCode Test Explorer state to apply. */
  readonly state: EditorState;
  /**
   * The human-readable verdict label, e.g. "Pass", "Environment error",
   * "Inconclusive", or "Unknown" for an unrecognised token.
   */
  readonly verdict: string;
}

/**
 * The canonical, case-sensitive wire tokens emitted by the .NET
 * `VerdictJsonConverter`. Mirrored here so an exact-case match is required (the
 * converter rejects anything else), and a `pass`/`Fail` mismatch falls through
 * to the unknown branch rather than being silently accepted.
 */
const MAPPINGS: Readonly<Record<string, VerdictMapping>> = {
  PASS: { state: 'passed', verdict: 'Pass' },
  FAIL: { state: 'failed', verdict: 'Fail' },
  ENV_ERROR: { state: 'errored', verdict: 'Environment error' },
  INCONCLUSIVE: { state: 'skipped', verdict: 'Inconclusive' },
};

/** The fallback for an unknown / missing / wrong-case token. */
const UNKNOWN: VerdictMapping = { state: 'errored', verdict: 'Unknown' };

/**
 * Maps a verdict wire token (`"PASS"`, `"FAIL"`, `"ENV_ERROR"`,
 * `"INCONCLUSIVE"`) to its editor state and human label.
 *
 * The match is case-sensitive, mirroring the .NET converter. Any unrecognised,
 * empty, or wrong-case token maps to `errored` / `"Unknown"` — never silently
 * to `passed`.
 *
 * @param token The verdict token read from a `step-completed` /
 *   `scenario-completed` event.
 * @returns The editor state to drive and the human verdict label.
 */
export function mapVerdict(token: string): VerdictMapping {
  // No trimming/upper-casing: the wire contract is exact-case, so a token with
  // surrounding whitespace or the wrong case is genuinely not one we recognise.
  return MAPPINGS[token] ?? UNKNOWN;
}
