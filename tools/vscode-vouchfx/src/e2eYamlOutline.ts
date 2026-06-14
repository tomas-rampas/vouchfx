// Pure, VSCode-free outline extraction for a vouchfx `.e2e.yaml` document.
//
// Parses the document with the `yaml` package's `parseDocument` (which retains
// byte-offset `.range`s on every node) and produces a flat outline: the
// scenario name (`metadata.name`, or null) and one entry per `steps:` item that
// declares an `id:`, each with the 0-based line range it occupies.
//
// This module imports nothing from `vscode` so it can be unit-tested under a
// plain Node test runner; `testController.ts` turns each entry into a child
// `TestItem` anchored at the step's start line.
//
// Line-range semantics (the part that matters for decorating the right line):
//   • Each `steps:` sequence item's start line comes from its node `.range[0]`
//     byte offset, converted to a 0-based line via the `yaml` `LineCounter`.
//   • Its end line is the line BEFORE the next sequence item starts (or the last
//     line of the document for the final item). Crucially this is computed over
//     ALL items — including id-less ones — BEFORE filtering, so a `code: |`
//     block scalar's interior lines stay inside their owning step's range and an
//     id-less step's lines are never absorbed into a neighbour.
//
// Robustness: a `code: |` (literal) or `>-` (folded) block scalar's interior
// lines are children of the step node, so they fall inside the step's `.range`
// and are never mistaken for new sequence items. Comments between items are
// likewise absorbed by the gap between one item's end line and the next item's
// start line. Malformed YAML never throws out of here — a parse failure yields
// an empty outline.

import { Document, isMap, isScalar, isSeq, LineCounter, parseDocument } from 'yaml';

/** A single step entry in a `.e2e.yaml` outline. */
export interface E2eOutlineStep {
  /** The step's `id:` value (steps without an id are skipped entirely). */
  readonly id: string;
  /** 0-based line on which the `steps:` item begins (`- id: ...`). */
  readonly startLine: number;
  /**
   * 0-based, inclusive line on which the step's region ends — the line just
   * before the next sequence item, or the document's last line for the final
   * item. Always `>= startLine`.
   */
  readonly endLine: number;
}

/** The outline of a `.e2e.yaml` document. */
export interface E2eOutline {
  /** `metadata.name`, or `null` when absent / not a scalar. */
  readonly scenarioName: string | null;
  /** One entry per `steps:` item that declares an `id:`, in document order. */
  readonly steps: readonly E2eOutlineStep[];
}

const EMPTY_OUTLINE: E2eOutline = { scenarioName: null, steps: [] };

/**
 * Parses a `.e2e.yaml` document into a flat outline (scenario name + per-step
 * line ranges). Never throws: a malformed document yields an empty outline.
 *
 * @param text The full text of a `.e2e.yaml` document.
 * @returns The scenario name and the id-bearing steps with their line ranges.
 */
export function parseE2eOutline(text: string): E2eOutline {
  let doc: Document.Parsed;
  let lineCounter: LineCounter;
  try {
    lineCounter = new LineCounter();
    doc = parseDocument(text, { lineCounter });
  } catch {
    // parseDocument is itself tolerant (it collects errors rather than throwing
    // in most cases), but guard regardless so a pathological input can never
    // propagate out of a pure helper.
    return EMPTY_OUTLINE;
  }

  const root = doc.contents;
  if (!isMap(root)) {
    return EMPTY_OUTLINE;
  }

  const scenarioName = readScenarioName(root);
  const steps = readSteps(root, lineCounter, text);
  return { scenarioName, steps };
}

/**
 * Reads `metadata.name` as a string, or `null` when the path is absent or the
 * value is not a scalar string.
 */
function readScenarioName(root: unknown): string | null {
  const metadata = getMapValue(root, 'metadata');
  const name = getMapValue(metadata, 'name');
  if (isScalar(name) && typeof name.value === 'string') {
    return name.value;
  }
  return null;
}

/**
 * Reads the `steps:` sequence into outline entries, dropping id-less items but
 * computing every item's line range over the FULL sequence first so ranges
 * never overlap and a `code:` block stays inside its owner.
 */
function readSteps(
  root: unknown,
  lineCounter: LineCounter,
  text: string,
): readonly E2eOutlineStep[] {
  const stepsNode = getMapValue(root, 'steps');
  if (!isSeq(stepsNode)) {
    return [];
  }

  const items = stepsNode.items;
  const lastLine = lastLineIndex(text);

  // Start line of every item (0-based), computed once over the whole sequence.
  const starts: number[] = items.map((item) => offsetToLine(itemStartOffset(item), lineCounter));

  const out: E2eOutlineStep[] = [];
  for (let i = 0; i < items.length; i++) {
    const item = items[i];
    const id = isMap(item) ? readId(item) : null;
    if (id === null) {
      // Skip — but its line span was still accounted for via `starts`, so the
      // preceding kept step ends before THIS item, not before the next kept one.
      continue;
    }

    const startLine = starts[i];
    const nextStart = i + 1 < items.length ? starts[i + 1] : lastLine + 1;
    // End just before the next item; clamp so a degenerate ordering can never
    // produce endLine < startLine.
    const endLine = Math.max(startLine, nextStart - 1);
    out.push({ id, startLine, endLine });
  }
  return out;
}

/** Returns the byte offset at which a sequence item begins. */
function itemStartOffset(item: unknown): number {
  // Every yaml Node carries a `range: [start, valueEnd, nodeEnd]` tuple of byte
  // offsets. A map item (the common `- id: ...` case) and a bare scalar item
  // both expose it.
  const range = (item as { range?: readonly [number, number, number] | null }).range;
  if (range && typeof range[0] === 'number') {
    return range[0];
  }
  return 0;
}

/** Reads a map item's `id:` value as a string, or `null` when absent/non-string. */
function readId(item: unknown): string | null {
  const value = getMapValue(item, 'id');
  if (isScalar(value) && typeof value.value === 'string' && value.value.length > 0) {
    return value.value;
  }
  return null;
}

/**
 * Looks up a key in a yaml map node and returns its value node, or `undefined`
 * when the key is absent or the node is not a map. Works on any node that
 * exposes `.items` of pairs.
 */
function getMapValue(node: unknown, key: string): unknown {
  if (!isMap(node)) {
    return undefined;
  }
  for (const pair of node.items) {
    const k = pair.key;
    if (isScalar(k) && k.value === key) {
      return pair.value;
    }
  }
  return undefined;
}

/**
 * Converts a byte offset into a 0-based line index using the `yaml`
 * `LineCounter`, which records newline positions during parsing (handling both
 * `\n` and `\r\n`). `linePos` returns a 1-based line; we subtract 1.
 */
function offsetToLine(offset: number, lineCounter: LineCounter): number {
  const pos = lineCounter.linePos(offset);
  return Math.max(0, pos.line - 1);
}

/** 0-based index of the last line of `text`. */
function lastLineIndex(text: string): number {
  if (text.length === 0) {
    return 0;
  }
  // Count newlines; a trailing newline still leaves the last content line as the
  // highest index we care about for a step range.
  let lines = 0;
  for (let i = 0; i < text.length; i++) {
    if (text.charCodeAt(i) === 10 /* \n */) {
      lines++;
    }
  }
  // `lines` is the number of newline characters. The highest 0-based line index
  // is `lines` when there is content after the final newline, else `lines - 1`.
  const endsWithNewline = text.charCodeAt(text.length - 1) === 10;
  return endsWithNewline ? Math.max(0, lines - 1) : lines;
}
