// VSCode-free seam used by the Test Explorer adapter after reading --events.
import { ParsedEvents, parseEvents } from './eventsParse';

/** Parses results and appends a count-only warning when any lines were skipped. */
export function parseEventsForTestRun(
  jsonl: string,
  output: { appendOutput(text: string): void },
): ParsedEvents {
  const parsed = parseEvents(jsonl);
  const count = parsed.skippedEventLines;
  if (count > 0) {
    const lines = count === 1 ? 'event-stream line' : 'event-stream lines';
    // Only the count and constant text can reach this warning, never event data.
    output.appendOutput(
      `${count} ${lines} could not be read, so these results may be incomplete.\r\n`,
    );
  }
  return parsed;
}
