import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';

import { loadWASM, OnigScanner, OnigString } from 'vscode-oniguruma';
import {
  type IGrammar,
  type IOnigLib,
  type IRawGrammar,
  parseRawGrammar,
  Registry,
} from 'vscode-textmate';

// The compiled test lives in dist/test/; the extension root is two levels up.
const extensionRoot = path.resolve(__dirname, '..', '..');

const INJECTION_PATH = path.resolve(
  extensionRoot,
  'syntaxes',
  'csharp-in-e2eyaml.injection.json',
);
const INJECTION_SCOPE = 'vouchfx.injection.csharp-in-e2eyaml';
const EMBEDDED_SCOPE = 'meta.embedded.block.csharp';
const CSHARP_SCOPE = 'source.cs';
const YAML_SCOPE = 'source.yaml';

function readText(relativeFromRoot: string): string {
  return readFileSync(path.resolve(extensionRoot, relativeFromRoot), 'utf8');
}

interface InjectionGrammar {
  scopeName?: string;
  injectionSelector?: string;
  patterns?: unknown[];
  repository?: Record<string, unknown>;
}

// ---------------------------------------------------------------------------
// Structural validity — no native deps required.
// ---------------------------------------------------------------------------

test('injection grammar parses as JSON and is a well-formed TextMate injection', () => {
  const grammar = JSON.parse(readFileSync(INJECTION_PATH, 'utf8')) as InjectionGrammar;

  assert.equal(grammar.scopeName, INJECTION_SCOPE, 'scopeName must match the registered scope');
  assert.ok(
    typeof grammar.injectionSelector === 'string' && grammar.injectionSelector.length > 0,
    'an injection grammar must declare a non-empty injectionSelector',
  );
  assert.ok(
    grammar.injectionSelector?.includes(YAML_SCOPE),
    'injectionSelector must target the YAML document scope (source.yaml)',
  );
  assert.ok(Array.isArray(grammar.patterns) && grammar.patterns.length > 0, 'has patterns');
  assert.ok(grammar.repository && typeof grammar.repository === 'object', 'has a repository');

  // The body must delegate to the built-in C# grammar.
  const serialised = JSON.stringify(grammar);
  assert.ok(serialised.includes(CSHARP_SCOPE), 'the body must include source.cs');
  assert.ok(
    serialised.includes(EMBEDDED_SCOPE),
    'the body region must carry the meta.embedded.block.csharp content scope',
  );
});

interface CodeBlockRule {
  begin?: string;
  while?: string;
  whileCaptures?: Record<string, unknown>;
}

function codeBlockRule(): CodeBlockRule {
  const grammar = JSON.parse(readFileSync(INJECTION_PATH, 'utf8')) as InjectionGrammar;
  const rule = grammar.repository?.['csharp-code-block'] as CodeBlockRule | undefined;
  assert.ok(rule, 'the repository must define the csharp-code-block rule');
  return rule;
}

test('the begin indicator accepts the indentation digit and chomping in EITHER order', () => {
  const rule = codeBlockRule();
  assert.ok(typeof rule.begin === 'string', 'the rule must declare a begin pattern');
  // The begin pattern is anchored (^…$); RegExp on a single line mirrors how the
  // TextMate engine applies it line-by-line.
  const begin = new RegExp(rule.begin as string);

  // Each of these is a valid YAML block-scalar header for `code:` and MUST start
  // the injection. The indicator may be a bare `|`/`>`, carry a chomping sign, an
  // indentation digit, or both — in either order.
  const matches = [
    'code: |',
    'code: |-',
    'code: |+',
    'code: >',
    'code: >-',
    'code: >+',
    'code: |2-', // indentation-then-chomping order
    'code: >4+', // indentation-then-chomping order (folded)
    'code: |-3', // chomping-then-indentation order
    'code: >+1', // chomping-then-indentation order (folded)
    'code: |9',
    '    code: |2-', // leading indentation is captured by group 1
    'code: | # trailing comment',
  ];
  for (const line of matches) {
    assert.ok(begin.test(line), `begin must match a valid block-scalar header: "${line}"`);
  }

  // These are NOT valid single-indicator block-scalar headers and must NOT start
  // the injection: a doubled chomping sign, a two-digit indentation, a zero
  // indentation, or a plain (non-block) scalar.
  const nonMatches = [
    'code: |--', // doubled chomping indicator
    'code: |12', // two-digit indentation is illegal
    'code: |0', // zero indentation is illegal (YAML indent is 1..9)
    'code: hello', // plain scalar, not a block scalar
    'code:', // no value at all
  ];
  for (const line of nonMatches) {
    assert.ok(!begin.test(line), `begin must NOT match an invalid header: "${line}"`);
  }
});

test('the while clause declares whileCaptures so its consumed indentation is scoped', () => {
  const rule = codeBlockRule();
  assert.ok(typeof rule.while === 'string', 'the rule must declare a while pattern');
  // The while clause must match indentation plus a single space and then use a
  // lookahead for the content, so the body characters fall through to source.cs.
  assert.ok(
    (rule.while as string).includes('(?='),
    'the while pattern must use a lookahead so body characters are not consumed',
  );
  assert.ok(
    rule.whileCaptures && typeof rule.whileCaptures === 'object',
    'the rule must declare whileCaptures to scope the consumed indentation',
  );
});

test('injection grammar is registered in package.json contributes.grammars', () => {
  interface GrammarContribution {
    scopeName?: string;
    path?: string;
    injectTo?: string[];
    embeddedLanguages?: Record<string, string>;
  }
  interface Manifest {
    contributes?: { grammars?: GrammarContribution[] };
  }
  const manifest = JSON.parse(readText('package.json')) as Manifest;
  const grammars = manifest.contributes?.grammars ?? [];
  const entry = grammars.find((g) => g.scopeName === INJECTION_SCOPE);

  assert.ok(entry, 'contributes.grammars must register the injection scope');
  assert.equal(entry?.path, './syntaxes/csharp-in-e2eyaml.injection.json');
  assert.ok(entry?.injectTo?.includes(YAML_SCOPE), 'must inject into source.yaml');
  assert.ok(
    entry?.embeddedLanguages?.[EMBEDDED_SCOPE] === 'csharp',
    'must map the embedded block scope to the csharp language',
  );
});

// ---------------------------------------------------------------------------
// Tokenisation — the real boundary proof, using the same engine VSCode uses.
// vscode-oniguruma (WASM) + vscode-textmate. The YAML/C# host grammars here are
// minimal stand-ins whose only job is to provide the source.yaml host scope and
// a source.cs target so we can observe where the injection starts and stops.
// ---------------------------------------------------------------------------

const wasmPath = path.resolve(
  extensionRoot,
  'node_modules',
  'vscode-oniguruma',
  'release',
  'onig.wasm',
);

async function makeOnigLib(): Promise<IOnigLib> {
  const wasmBin = readFileSync(wasmPath).buffer;
  await loadWASM(wasmBin);
  return {
    createOnigScanner(sources: string[]) {
      return new OnigScanner(sources);
    },
    createOnigString(str: string) {
      return new OnigString(str);
    },
  };
}

function loadRawGrammar(absPath: string): IRawGrammar {
  return parseRawGrammar(readFileSync(absPath, 'utf8'), absPath);
}

async function buildGrammar(): Promise<IGrammar> {
  const yamlPath = path.resolve(
    extensionRoot,
    'src',
    'test',
    'fixtures',
    'minimal-yaml.tmLanguage.json',
  );
  const csharpPath = path.resolve(
    extensionRoot,
    'src',
    'test',
    'fixtures',
    'minimal-csharp.tmLanguage.json',
  );

  const sources: Record<string, string> = {
    [YAML_SCOPE]: yamlPath,
    [CSHARP_SCOPE]: csharpPath,
    [INJECTION_SCOPE]: INJECTION_PATH,
  };

  const registry = new Registry({
    onigLib: makeOnigLib(),
    loadGrammar: async (scopeName) => {
      const file = sources[scopeName];
      return file ? loadRawGrammar(file) : null;
    },
    getInjections: (scopeName) => (scopeName === YAML_SCOPE ? [INJECTION_SCOPE] : undefined),
  });

  const grammar = await registry.loadGrammar(YAML_SCOPE);
  assert.ok(grammar, 'the YAML grammar (with the C# injection) must load');
  return grammar;
}

interface ScopedLine {
  readonly text: string;
  /**
   * True when every non-whitespace token on the line sits inside the embedded
   * C# region (carries the meta.embedded.block.csharp content scope). This is
   * the boundary proof: a fully-embedded line is "C# territory".
   */
  readonly isCsharp: boolean;
  /** True when any token on the line carries the embedded block scope. */
  readonly hasEmbeddedScope: boolean;
  /**
   * True when at least one token was tokenised by the included C# grammar
   * (a `.cs` rule scope). Proves the body is delegated to source.cs, not merely
   * wrapped in the embedded content scope. (With the real VSCode C# grammar
   * every token also carries the `source.cs` root scope; the minimal stand-in
   * used here only tags the constructs it recognises.)
   */
  readonly hasCsharpToken: boolean;
  /**
   * True when the FIRST non-whitespace token of the line is tokenised by the
   * included C# grammar (carries `source.cs` or a `.cs` rule scope). This is the
   * precise proof that the `while` clause consumes only indentation: if it ate
   * the first content character, that character would carry the embedded content
   * scope but NOT a `.cs` scope.
   */
  readonly firstContentIsCsharp: boolean;
  readonly scopesByToken: ReadonlyArray<readonly string[]>;
}

function tokeniseLines(grammar: IGrammar, lines: string[]): ScopedLine[] {
  let ruleStack = null as Parameters<IGrammar['tokenizeLine']>[1];
  const out: ScopedLine[] = [];
  for (const text of lines) {
    const result = grammar.tokenizeLine(text, ruleStack);
    ruleStack = result.ruleStack;

    const scopesByToken = result.tokens.map((tok) => tok.scopes);
    const nonBlank = result.tokens.filter(
      (tok) => text.slice(tok.startIndex, tok.endIndex).trim().length > 0,
    );
    const isCsharp =
      nonBlank.length > 0 && nonBlank.every((tok) => tok.scopes.includes(EMBEDDED_SCOPE));
    const hasEmbeddedScope = result.tokens.some((tok) => tok.scopes.includes(EMBEDDED_SCOPE));
    const hasCsharpToken = result.tokens.some((tok) =>
      tok.scopes.some((s) => s === CSHARP_SCOPE || s.endsWith('.cs')),
    );
    const firstContentToken = nonBlank[0];
    const firstContentIsCsharp =
      firstContentToken !== undefined &&
      firstContentToken.scopes.some((s) => s === CSHARP_SCOPE || s.endsWith('.cs'));

    out.push({
      text,
      isCsharp,
      hasEmbeddedScope,
      hasCsharpToken,
      firstContentIsCsharp,
      scopesByToken,
    });
  }
  return out;
}

test('literal block scalar after `code:` is scoped as embedded C#; the boundary holds', async () => {
  const grammar = await buildGrammar();

  // A `code: |` block followed by a sibling key at the same indentation.
  const lines = [
    'steps:',
    '  - id: compute',
    '    type: script.csharp',
    '    code: |',
    '      var id = Vars["greeting"];',
    '      Vars["out"] = id;',
    '',
    '      return true;',
    '  - id: next-step',
    '    type: db-assert.postgres',
  ];

  const t = tokeniseLines(grammar, lines);

  // The `code:` key line itself stays YAML (not C#).
  const codeKeyLine = t[3];
  assert.ok(!codeKeyLine.isCsharp, 'the `code:` key line must not be C#-scoped');
  assert.ok(
    codeKeyLine.scopesByToken.flat().some((s) => s.startsWith('entity.name.tag')),
    'the `code` key keeps its YAML key scope',
  );

  // The block-scalar body lines (indices 4, 5, 7) are embedded C#, and their
  // recognised constructs are tokenised by the included C# grammar.
  for (const idx of [4, 5, 7]) {
    assert.ok(t[idx].hasEmbeddedScope, `body line ${idx} must carry ${EMBEDDED_SCOPE}`);
    assert.ok(t[idx].isCsharp, `body line ${idx} must be C#-scoped: "${t[idx].text}"`);
    assert.ok(
      t[idx].hasCsharpToken,
      `body line ${idx} must be delegated to ${CSHARP_SCOPE}: "${t[idx].text}"`,
    );
    // The precise boundary: the FIRST non-whitespace character of the body line
    // must itself be tokenised by source.cs. With the old `while` pattern
    // (`^(?:\1\s+\S|\s*$)`), the `\S` consumed that first character, leaving it
    // in the embedded region but NOT delegated to the C# grammar.
    assert.ok(
      t[idx].firstContentIsCsharp,
      `the first content character of body line ${idx} must be C#, not consumed by the while clause: "${t[idx].text}"`,
    );
  }

  // The blank line inside the block (index 6) stays part of the block (does not
  // terminate the scalar) — the `while` clause allows blank lines.
  assert.ok(
    t[6].hasEmbeddedScope || t[6].text.trim().length === 0,
    'a blank line inside the block does not end the embedded region',
  );

  // The boundary: the next sibling `- id: next-step` (index 8), indented at the
  // same level as the list item that owns `code:`, must be YAML again — NOT C#.
  const nextStep = t[8];
  assert.ok(!nextStep.isCsharp, 'the next list item must return to YAML, not stay C#');
  assert.ok(
    !nextStep.hasEmbeddedScope,
    'the next list item must NOT carry the embedded C# scope (boundary closed)',
  );
  assert.ok(
    !nextStep.hasCsharpToken,
    'the next list item must NOT be tokenised by the C# grammar (boundary closed)',
  );
  assert.ok(
    nextStep.scopesByToken.flat().every((s) => s.startsWith('source.yaml') || s.includes('.yaml')),
    'the next list item is tokenised entirely by the host YAML grammar',
  );

  // And the `type:` line that follows it is plain YAML too — no leak past the
  // sibling boundary.
  assert.ok(!t[9].isCsharp, 'the trailing type: line is YAML');
  assert.ok(!t[9].hasEmbeddedScope, 'the trailing type: line is not embedded C#');
});

test('a non-`code:` block scalar is NOT treated as C#', async () => {
  const grammar = await buildGrammar();

  const lines = [
    'steps:',
    '  - id: seed',
    '    description: |',
    '      this is prose, not C#',
    '      var looksLikeCode = 1;',
    '  - id: after',
  ];

  const t = tokeniseLines(grammar, lines);

  // The `description:` block scalar must not be embedded C# — only `code:` is.
  assert.ok(!t[3].hasEmbeddedScope, 'description block must not be embedded C#');
  assert.ok(!t[4].hasEmbeddedScope, 'description block second line must not be embedded C#');
});
