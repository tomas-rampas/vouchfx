# Changelog

All notable changes to the vouchfx VSCode extension are documented here. The
format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - Unreleased

### Added

- Initial scaffold of the vouchfx VSCode extension.
- Bundled, offline-safe copy of the frozen **v1** composed JSON Schema, bound to
  `*.e2e.yaml` files via `contributes.yamlValidation` (delegating schema
  completion, hover and validation to the Red Hat YAML language server).
- `vouchfx.schemaPath` setting: an optional override pointing at an
  internal/enterprise schema copy, registered programmatically with the YAML
  language server.
- Node `node:test` unit tests validating the manifest wiring and the bundled
  schema (existence, parseability, `x-vouchfx-schema-version: v1`).
