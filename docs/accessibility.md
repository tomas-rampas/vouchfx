# Accessibility Conformance Record for vouchfx Report Renderers

**WCAG 2.1 Level AA conformance audit and remediation plan for the terminal and HTML report renderers.**

## 1. Scope and Method

This document records the accessibility audit of the two primary report-rendering surfaces in vouchfx:

- **Terminal renderer** (`src/Engine/Platform.Engine.Reporting/TerminalRenderer.cs`, S10-G-03a) — structured JSON Lines event stream rendered to stdout or a file as plain text, with optional accessibility decorations (shape glyphs and ANSI colour).
- **HTML renderer** (`src/Engine/Platform.Engine.Reporting/HtmlRenderer.cs`, S10-G-03b) — the same event stream rendered as a self-contained HTML5 document with inline stylesheet and no external resource fetches.

### Audit method

This record is based on:

1. **Internal expert code review** — direct examination of the rendering source code and the CSS/HTML structure emitted.
2. **Deterministic automated CI gates** — two comprehensive test suites that verify conformance properties and regression-prevent any future drift:
   - `tests/Platform.Engine.Reporting.Tests/TerminalRendererAccessibilityTests.cs` — verifies that all four §12.1 verdicts are distinguishable by text token alone (colour and glyphs stripped).
   - `tests/Platform.Engine.Reporting.Tests/HtmlRendererWcagTests.cs` — recomputes WCAG 2.1 relative-luminance contrast ratios from the emitted stylesheet and asserts structural invariants.

The gates do not use assistive technology or live user testing; they are deterministic unit tests that can run in any CI environment without specialised hardware.

---

## 2. Terminal Renderer (WCAG 1.4.1 — Use of Colour)

### Design

The terminal renderer outputs one line per step with its verdict. The four §12.1 verdicts are:

| Verdict | Text token |
|---------|-----------|
| Pass | `PASS` |
| Fail | `FAIL` |
| Environment error | `ENV_ERROR` |
| Inconclusive | `INCONCLUSIVE` |

Each verdict is **always** rendered with its distinct text token, unconditionally in both plain and decorated output modes. This is the core WCAG 1.4.1 guarantee: verdicts are **never distinguished by colour alone**.

### Decoration modes

The renderer accepts a `decorate` boolean parameter:

- **Plain mode** (`decorate: false`, the default) — emits only the text token, no ANSI colour or glyphs. Output is byte-identical to the pre-S10-G-03a renderer, suitable for piped/redirected/CI environments.
- **Decorated mode** (`decorate: true`) — prefixes each verdict line with a per-verdict ASCII shape glyph and wraps the verdict token in ANSI colour:
  - Pass `[+]` green (SGR 32)
  - Fail `[x]` red (SGR 31)
  - Environment error `[!]` yellow (SGR 33)
  - Inconclusive `[?]` blue (SGR 34)

The glyph is a **shape cue independent of colour** — even a colour-blind user with decorations enabled sees a distinct shape-and-token pair per verdict. The colour is a redundant, sighted-only convenience layered on top.

### Accessibility guarantees

1. **Text token is unconditional.** The verdict word (`PASS`, `FAIL`, `ENV_ERROR`, `INCONCLUSIVE`) is emitted in every mode, so a screen reader or plain terminal always reads a distinct verdict label.
2. **Glyph is shape-distinct.** The four shape glyphs (`[+]`, `[x]`, `[!]`, `[?]`) are mutually distinct ASCII characters, distinguishable even at low resolution, on monochrome displays, and to colour-blind users.
3. **Colour is optional and reset.** When decorations are enabled, colour wraps the verdict token but is always followed by an ANSI reset (`\x1b[0m`), preventing colour bleed onto subsequent lines or the shell prompt.
4. **Plain mode is invariant.** The renderer is a pure function of its `decorate` boolean input — it does **not** probe the environment (no `NO_COLOR` checks, no output-redirection detection). The caller (the CLI) decides whether to decorate and passes that decision in; the renderer obeys it. This keeps rendering deterministic and testable.

### CI gate

**`TerminalRendererAccessibilityTests.cs`** verifies:

- Plain mode contains all four distinct text tokens and no ANSI colour or glyphs.
- Decorated mode contains the correct glyph per verdict, attached to the correct line.
- Decorated mode wraps each verdict token in its ANSI colour with a trailing reset.
- **Core test**: verdicts remain distinguishable by text token alone when colour and glyphs are stripped.

---

## 3. HTML Renderer (WCAG 2.1 Level AA)

### Structure and self-containment

The HTML renderer emits a single, complete HTML5 document:

- `<!DOCTYPE html>` + `<html lang="en">` — declares English language.
- `<meta charset="utf-8">` + `<meta name="viewport" content="width=device-width, initial-scale=1">` — UTF-8 encoding and reflow support (WCAG 1.4.10).
- Single `<h1>` "vouchfx report" — landmark title.
- `<main>` landmark — programmatic content region (WCAG 1.3.1).
- Inline `<style>` only — no external `@import`, no CDN fetches, no web fonts, no JavaScript. The document is a single portable file.

### Verdict rendering — four independent cues

Every step verdict is rendered with **four distinct non-colour cues**, ensuring it remains intelligible even if colour is lost:

1. **Text label** — the §12.1 verdict word (`PASS`, `FAIL`, `ENV_ERROR`, `INCONCLUSIVE`), always present.
2. **CSS class** — each verdict wrapped in a unique class (`verdict-pass`, `verdict-fail`, `verdict-env-error`, `verdict-inconclusive`, or `verdict-unknown` for incomplete steps).
3. **Left border style** — solid, dashed, dotted, or double, one per verdict, to visually distinguish the verdict box by shape even on monochrome.
4. **Left border colour** — a distinct hue per verdict, complemented by a matching background tint on the verdict token itself.

The run-summary table also renders each verdict as a data row in a real semantic `<table>` with `<th scope="col">` headers and `<th scope="row">` row labels, making the verdict-to-count relationship programmatically determinable (WCAG 1.3.1).

### Contrast compliance (WCAG 1.4.3 and 1.4.11)

All text-and-background colour pairs meet the AA floor of **4.5:1** contrast ratio:

| Class | Foreground | Background | Ratio | Level |
|-------|-----------|------------|-------|-------|
| `.verdict-pass` | `#0f5d29` (dark green) | `#e6f4ea` (light green) | 7.06:1 | AA |
| `.verdict-fail` | `#8a0017` (dark red) | `#fce8e6` (light red) | 8.52:1 | AA |
| `.verdict-env-error` | `#6b4600` (dark amber) | `#fff4e5` (light amber) | 7.73:1 | AA |
| `.verdict-inconclusive` | `#333366` (dark blue) | `#ececf7` (light blue) | 9.91:1 | AA |
| `.verdict-unknown` | `#4a4a4a` (dark grey) | `#eeeeee` (light grey) | 7.64:1 | AA |
| Body text | `#1a1a1a` | `#ffffff` | 17.40:1 | AAA |
| Redacted marker (§17) | `#6b4600` | `#ffffff` | 8.40:1 | AA |
| Diff panel text | `#1a1a1a` | `#f5f5f5` | 15.96:1 | AA |
| Base `.verdict` fallback (R-4) | `#1a1a1a` | `#f0f0f0` | 15.27:1 | AA |

Non-text graphical elements (the left border cues) meet the **3.0:1** floor per WCAG 1.4.11:

| Border colour | Background | Ratio | Level |
|---|---|---|---|
| `#1b7f3b` (PASS solid) | `#ffffff` | 5.07:1 | AA |
| `#b00020` (FAIL solid) | `#ffffff` | 7.33:1 | AA |
| `#8a5a00` (ENV_ERROR dashed) | `#ffffff` | 5.93:1 | AA |
| `#4a4a8a` (INCONCLUSIVE dotted) | `#ffffff` | 7.96:1 | AA |
| `#888888` (UNKNOWN double) | `#ffffff` | 3.54:1 | AA |
| `#767676` (summary-table grid) | `#ffffff` | 4.54:1 | AA |

The summary-table border uses `#767676` (an informative structure cue, not decorative) rather than `#cccccc` to maintain the 3.0:1 non-text floor.

### Structural invariants (WCAG 1.3.1, 1.4.7)

The document is verified to have:

- **Monotone heading hierarchy** — no heading jumps more than one level deeper than its predecessor (e.g., h1 → h2 → h2 is ok; h1 → h2 → h4 is not).
- **Per-verdict class ↔ label co-emission** — wherever a `.verdict-pass` / `.verdict-fail` / etc. class is used, the matching text label is also present, so verdicts are never conveyed by colour alone.
- **Self-containment** — no `http://`, `https://`, `src=`, or `href=` attributes; no external resource fetches.
- **Table semantics** — the run-summary is a real `<table>` with `<caption>`, `<th scope="col">`, and `<th scope="row">` to make verdict-and-count relationships navigable.
- **Well-formedness** — the document closes with `</main>`, `</body>`, `</html>` in order, so truncation or mis-nesting is caught.

### Forward-looking focus-visible indicator

A `:focus-visible` CSS rule is defined for any future interactive element (`<a>`, `<button>`, `<summary>`):

```css
a:focus-visible, button:focus-visible, summary:focus-visible { 
  outline: 3px solid #1b4fd6; 
  outline-offset: 2px; 
}
```

This satisfies WCAG 2.4.7 (Focus Visible) in advance, ensuring that if future releases add interactive controls, they will not require a separate CSS patch.

### Secret safety (§17)

Secret-derived substitutions and observations are never rendered verbatim. The report shows only:

- For secrets: the non-sensitive **reference label** (e.g., `env/API_TOKEN`) plus a `(redacted)` marker.
- For observations: the structure shape (`{keys}`, `[n item(s)]`, `<string>`, `<number>`, `<bool>`) rather than literal values.

Every dynamic string (step ids, scenario names, diffs, hashes) is HTML-escaped before emission, preventing both markup injection and accidental value exposure. This invariant is maintained throughout the HTML renderer's output.

### CI gate

**`HtmlRendererWcagTests.cs`** verifies:

1. **Contrast floor.** The relative-luminance helper recomputes WCAG contrast ratios from the parsed `<style>` block and asserts:
   - All text pairs meet the 4.5:1 AA floor.
   - All non-text border cues meet the 3.0:1 floor.
   - The summary-table border (`#767676`) is present and meets the 3.0:1 floor, proving decorative `#cccccc` was not used.

2. **Structural invariants.** The rendered output is parsed for:
   - `<html lang="en">` declaration.
   - Exactly one `<h1>` element.
   - Presence of `<main>` opening and closing tags.
   - Monotone heading hierarchy (no jumps > 1 level).
   - Per-verdict class ↔ label co-emission.
   - No external resource references.
   - Summary table `<th scope="col">` and `<th scope="row">` scopes.
   - Well-formed close sequence: `</main>` → `</body>` → `</html>`.

---

## 4. Remediation Record

The following remediations were applied in S10-G-03 to achieve the conformance stated above:

| Remediation | Issue | Solution | WCAG criterion |
|---|---|---|---|
| **R-1** | No main content landmark | Added `<main>` wrapper around body content (run summary, scenarios, errors, envelope) | 1.3.1 Info-and-relationships |
| **R-2** | Fixed-width rendering on narrow screens | Added `<meta name="viewport" content="width=device-width, initial-scale=1">` | 1.4.10 Reflow |
| **R-3** | Run-summary layout not semantically table | Replaced `<div>` layout with real `<table>` with `<caption>`, `<th scope="col">`, and `<th scope="row">` | 1.3.1 Info-and-relationships; 1.4.11 Non-text contrast |
| **R-4** | Orphan verdict spans at risk of invisible text | Added base `.verdict { color: #1a1a1a; background: #f0f0f0; }` fallback rule (15.27:1 contrast) | 1.4.3 Minimum contrast |

All remediations are additive and maintain backward compatibility with the event stream contract (§14.4).

---

## 5. Residual Items and Forward Obligations

### Current limitations (by design)

The report is currently **static and non-interactive**. WCAG criteria concerning keyboard navigation (2.1.1), focus management (2.4.7), and dynamic disclosure patterns (2.5.4) are therefore not yet applicable. These criteria will be addressed if and when the report is extended with interactive features (e.g., collapsible step details, filter controls).

### Future-readiness

The stylesheet already includes a `:focus-visible` rule for any future `<a>`, `<button>`, or `<summary>` element, ensuring that when interactivity is added, focus indicators will be present and visible without requiring a separate accessibility update.

---

## 6. External Verification Outstanding

This conformance record represents an **internal expert review plus deterministic automated CI gates** — sufficient for engineering remediation and regression prevention within the vouchfx team.

The remaining open step is a **formally-commissioned external accessibility audit** (independent verification using assistive technology — NVDA, JAWS, and/or VoiceOver — plus keyboard-only traversal and zoom/reflow testing) for a third-party WCAG 2.1 AA **conformance attestation**. This audit is necessary if the report is intended for use in regulated environments or public-facing deployment where an external audit trail is required. For internal development and testing, the internal gates provide sufficient confidence.

---

## 7. References

- **WCAG 2.1** — W3C Web Content Accessibility Guidelines 2.1, [https://www.w3.org/WAI/WCAG21/quickref/](https://www.w3.org/WAI/WCAG21/quickref/)
- **vouchfx blueprint §12.1** — The verdict taxonomy (docs/01).
- **vouchfx blueprint §14** — Reporting architecture (docs/01).
- **vouchfx blueprint §14.2a** — Terminal renderer accessibility (docs/01).
- **vouchfx blueprint §17** — Secrets and redaction (docs/01).
- **TerminalRendererAccessibilityTests.cs** — CI gate for terminal renderer text-distinctness.
- **HtmlRendererWcagTests.cs** — CI gate for HTML contrast and structure.
