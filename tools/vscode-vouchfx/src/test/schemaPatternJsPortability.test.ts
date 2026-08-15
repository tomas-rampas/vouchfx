// The shipped schema's every 'pattern' must be a valid JavaScript RegExp
// (client-key-password spec, REQ-001; critic MINOR-3).
//
// Why this file exists. root-language-schema.json's clientKeyPassword $comment states
// that its end anchor is '(?![\s\S])' RATHER THAN '\z' *because* this copy of the schema
// is evaluated by a JavaScript RegExp, where '\z' is not an end-of-input anchor at all
// but an identity escape — ECMA-262 reads it as a demand for a literal trailing 'z' and
// every valid reference then fails validation in the editor. That rationale was, until
// this file, ungated: the C# parity gate (SecretReferencePatternParityTests) evaluates
// through JsonSchema.Net on .NET, where '\z' works perfectly, so a future edit to '\z' —
// or to any other .NET-only construct — would keep every C# gate green while shipping a
// silently broken extension schema. Nothing but a real JavaScript RegExp can see that,
// which is why the check lives here rather than beside the pattern.
//
// Scope is deliberately wider than the one field: the hazard is a .NET-only regex
// construct anywhere in a schema that is evaluated by two different engines, so this
// compiles EVERY pattern in the shipped copy, not only the one whose comment prompted it.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';

// The compiled test lives in dist/test/, so the extension root is two levels up
// (__dirname is available because the compiled output is CommonJS) — same idiom as
// manifest.test.ts.
const extensionRoot = path.resolve(__dirname, '..', '..');
const schemaRelativePath = 'src/schema/composed-schema.v1.json';

type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };

function readSchema(): { [key: string]: JsonValue } {
  const absolute = path.resolve(extensionRoot, schemaRelativePath);
  return JSON.parse(readFileSync(absolute, 'utf8')) as { [key: string]: JsonValue };
}

/**
 * Every `"pattern": "<string>"` in the document, paired with a JSON-Pointer-ish location
 * so a failure names WHICH pattern broke rather than only that one did.
 */
function collectPatterns(node: JsonValue, location: string): Array<[string, string]> {
  if (Array.isArray(node)) {
    return node.flatMap((item, index) => collectPatterns(item, `${location}/${index}`));
  }

  if (node !== null && typeof node === 'object') {
    const found: Array<[string, string]> = [];
    for (const [key, value] of Object.entries(node)) {
      if (key === 'pattern' && typeof value === 'string') {
        found.push([`${location}/pattern`, value]);
      }
      found.push(...collectPatterns(value, `${location}/${key}`));
    }
    return found;
  }

  return [];
}

function clientKeyPasswordPattern(): string {
  const schema = readSchema();
  const defs = schema['$defs'] as { [key: string]: JsonValue };
  const security = defs['security'] as { [key: string]: JsonValue };
  const properties = security['properties'] as { [key: string]: JsonValue };
  const field = properties['clientKeyPassword'] as { [key: string]: JsonValue };
  const pattern = field['pattern'];

  assert.equal(
    typeof pattern,
    'string',
    '$defs/security/properties/clientKeyPassword/pattern must be a string in the shipped schema',
  );

  return pattern as string;
}

test('every pattern in the shipped schema compiles as a JavaScript RegExp', () => {
  const patterns = collectPatterns(readSchema(), '');

  // Non-vacuity: a walk that resolved to nothing would sweep zero patterns and pass.
  // The committed count is 24, counted off the shipped copy on 2026-08-12; the floor sits
  // just below it so a routine addition does not force an edit here, while a collapse of
  // the walk fails loudly. Recount rather than increment when raising it.
  assert.ok(
    patterns.length >= 20,
    `expected at least 20 patterns in the shipped schema, found ${patterns.length}`,
  );

  for (const [location, pattern] of patterns) {
    assert.doesNotThrow(
      () => new RegExp(pattern),
      `'${location}' is not a valid JavaScript RegExp: ${pattern}. This schema copy is ` +
        'evaluated by a JS engine in the editor, so a .NET-only construct here ships a ' +
        'silently broken extension — the C# gates cannot see it.',
    );
  }
});

test('clientKeyPassword uses no .NET-only end-of-input anchor', () => {
  const pattern = clientKeyPasswordPattern();

  // The specific regression the $comment's rationale is about. '\z' compiles fine in JS
  // (it is an identity escape for 'z'), so this cannot be a compile check — it is a
  // spelling check, backed by the behavioural verdicts below which are what actually
  // break if the anchor reverts.
  assert.ok(
    !pattern.includes('\\z'),
    `the clientKeyPassword pattern must not use '\\z': ECMA-262 parses it as a literal ` +
      `'z' rather than as an end-of-input anchor, so every valid reference would be ` +
      `rejected in the editor. Got: ${pattern}`,
  );
});

test('the clientKeyPassword pattern gives the intended verdicts under a real JS RegExp', () => {
  const expression = new RegExp(clientKeyPasswordPattern());

  const cases: Array<[string, boolean, string]> = [
    ['${secret:env/A}', true, 'the ordinary whole reference — the field is unusable if this fails'],
    [
      '${secret:env/A}\n',
      false,
      "a YAML '|' block scalar appends exactly this newline, and SecretReference.TryParse " +
        'refuses it; a bare $ anchor would accept it (in .NET) and the engine would then refuse',
    ],
    ['${secret:env/A}\r\n', false, 'the CRLF form of the same trailing-newline case'],
    ['\n${secret:env/A}', false, 'a leading newline — the start anchor is not multiline'],
    ['${secret:env/A}B', false, 'trailing literal text: TryParse requires a whole-token match'],
    ['hunter2', false, 'a bare literal passphrase — the shape this pattern exists to refuse'],
    ['', false, 'the empty string'],
    [
      '${secret:a.b/x}',
      false,
      "a dotted source: the parser's source class is [A-Za-z0-9_-], which admits no '.'",
    ],
    [
      '${secret:env/A{B}',
      true,
      "a '{' inside the path: the parser's path class is [^}], which admits it — the schema " +
        'must not be narrower',
    ],
  ];

  for (const [candidate, expected, why] of cases) {
    assert.equal(
      expression.test(candidate),
      expected,
      `${JSON.stringify(candidate)} should be ${expected ? 'ACCEPTED' : 'REJECTED'} (${why})`,
    );
  }
});
