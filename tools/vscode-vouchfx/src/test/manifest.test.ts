import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';

// Resolve paths relative to the extension root regardless of where the test is
// run from. The compiled test lives in dist/test/, so the root is two levels
// up from __dirname. (__dirname is available because the compiled output is
// CommonJS — VSCode requires a CommonJS extension entry point.)
const extensionRoot = path.resolve(__dirname, '..', '..');

function readJson(relativePath: string): unknown {
  const absolute = path.resolve(extensionRoot, relativePath);
  return JSON.parse(readFileSync(absolute, 'utf8'));
}

interface YamlValidationEntry {
  fileMatch?: string;
  url?: string;
}

interface PackageManifest {
  name?: string;
  publisher?: string;
  extensionDependencies?: string[];
  activationEvents?: string[];
  contributes?: {
    yamlValidation?: YamlValidationEntry[];
    configuration?: {
      properties?: Record<string, { type?: string; default?: unknown }>;
    };
  };
}

test('package.json parses and identifies the vouchfx extension', () => {
  const manifest = readJson('package.json') as PackageManifest;
  assert.equal(manifest.name, 'vouchfx');
  assert.equal(manifest.publisher, 'vouchfx');
});

test('manifest depends on the Red Hat YAML language server', () => {
  const manifest = readJson('package.json') as PackageManifest;
  assert.ok(
    manifest.extensionDependencies?.includes('redhat.vscode-yaml'),
    'extensionDependencies must include redhat.vscode-yaml',
  );
});

test('yamlValidation binds *.e2e.yaml to the bundled schema', () => {
  const manifest = readJson('package.json') as PackageManifest;
  const entries = manifest.contributes?.yamlValidation;
  assert.ok(Array.isArray(entries) && entries.length === 1, 'exactly one yamlValidation entry');

  const [entry] = entries;
  assert.equal(entry.fileMatch, '*.e2e.yaml', 'fileMatch must be *.e2e.yaml');
  assert.equal(
    entry.url,
    './src/schema/composed-schema.v1.json',
    'url must point at the bundled schema',
  );
});

test('the vouchfx.schemaPath override setting is declared', () => {
  const manifest = readJson('package.json') as PackageManifest;
  const property = manifest.contributes?.configuration?.properties?.['vouchfx.schemaPath'];
  assert.ok(property, 'vouchfx.schemaPath must be declared');
  assert.equal(property.type, 'string');
  assert.equal(property.default, '');
});

test('activationEvents register against YAML / *.e2e.yaml', () => {
  const manifest = readJson('package.json') as PackageManifest;
  const events = manifest.activationEvents ?? [];
  assert.ok(events.includes('onLanguage:yaml'), 'should activate on the yaml language');
  assert.ok(
    events.some((event) => event.includes('*.e2e.yaml')),
    'should activate on workspaces containing *.e2e.yaml',
  );
});

test('the schema URL resolves to a file that exists, parses, and is v1', () => {
  const manifest = readJson('package.json') as PackageManifest;
  const url = manifest.contributes?.yamlValidation?.[0]?.url;
  assert.ok(url, 'yamlValidation url must be present');

  // The url is a workspace-relative path ('./src/...'); resolve it and load it.
  const schema = readJson(url) as Record<string, unknown>;
  assert.equal(
    schema['x-vouchfx-schema-version'],
    'v1',
    'the bundled schema must self-identify as the frozen v1 schema',
  );
  assert.equal(schema['$schema'], 'https://json-schema.org/draft/2020-12/schema');
  assert.ok(typeof schema['$defs'] === 'object' && schema['$defs'] !== null, 'schema has $defs');
});
