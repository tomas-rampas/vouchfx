// Copies the bundled v1 JSON Schema from src/schema into dist/schema so that
// code paths resolving relative to the compiled extension (dist/extension.js)
// can read it. The packaged extension still serves the schema from
// ./src/schema/composed-schema.v1.json (the manifest yamlValidation.url),
// but the programmatic override path in extension.ts falls back to whichever
// copy sits next to the running module, so we keep both in sync at build time.
import { cp, mkdir } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '..');
const src = resolve(root, 'src', 'schema');
const dest = resolve(root, 'dist', 'schema');

await mkdir(dest, { recursive: true });
await cp(src, dest, { recursive: true });

// eslint-disable-next-line no-console
console.log(`copy-schema: ${src} -> ${dest}`);
