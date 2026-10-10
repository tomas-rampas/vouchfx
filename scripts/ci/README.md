# Workflow and Docker CI checks

The blocking `workflow-contract` job lints every workflow and executes the literal
pre-pull script from `.github/workflows/build.yml`. Tests substitute only its image
list, with fixture Docker, timeout and sleep commands; no containers or images are
changed. Source extraction must agree byte-for-byte with the parsed YAML block.
Retry, classification, registry backoff, tally and workflow-command fencing checks
fail when behaviour changes. Negative mutation controls confirm those checks detect
a lost fatal exit and a changed YAML representation.

Run from the repository root with Python 3.12, Bash, yq 4.53.2 and ShellCheck 0.11.0:

```sh
python -B -m unittest discover -s scripts/ci -p 'test_*.py' -v
python -B scripts/ci/lint_workflows.py
```

On Windows, use `py -3.12` and Git Bash. The harness selects the standard Git Bash
installation; `VOUCHFX_CI_BASH` can name another Git Bash executable. Fixtures,
extracted scripts, command logs and integer exit files remain under TEMP.

The lint wrapper runs actionlint's structure checks and every inline Bash/sh block
through Bash syntax checks and ShellCheck. It uses actionlint's existing exclusions
for expressions and workflow environment variables; the standalone pre-pull check
uses no exclusions. This separate file transport avoids [actionlint 1.7.12's
pre-start pipe write](https://github.com/rhysd/actionlint/blob/914e7df21a07ef503a81201c76d2b11c789d3fca/process.go),
which can deadlock when a script exceeds the OS pipe capacity. Tools have bounded
waits, and any non-zero lint exit fails the gate. Python syntax is checked too.

`install-workflow-tools.sh` installs into the runner's temporary directory and
checks exact official release-asset SHA-256 values: [actionlint 1.7.12](https://github.com/rhysd/actionlint/releases/tag/v1.7.12)
(source commit `914e7df21a07ef503a81201c76d2b11c789d3fca`),
[ShellCheck 0.11.0](https://github.com/koalaman/shellcheck/releases/tag/v0.11.0) and
[yq 4.53.2](https://github.com/mikefarah/yq/releases/tag/v4.53.2).

The Docker test command uses `-m:1` to serialise test projects across assemblies.
Per-assembly xUnit settings alone do not prevent cross-assembly overlap. This
scheduling bound preserves `requires=docker`, mini crash dumps, coverage and
project-specific TRX files. It does not change intentional concurrency inside the
engine's `ParallelSuiteRunner` or its parallel capstone.

To measure that scheduling boundary with two real .NET 8 test assemblies:

```sh
python -B scripts/ci/probe_docker_serialization.py
```

This uses the actual workflow arguments and exact repository test-package versions
in TEMP, with feeds disabled; those packages must already be cached. One run omits
only `-m:1` and must overlap; the other must not. Two selected facts, distinct host
PIDs, monotonic intervals and per-project TRX counters are required for each run.
A non-overlapping control returns non-zero rather than claiming a reproduction.
The probe establishes local scheduling behaviour; hosted Docker execution and
branch protection require separate evidence. The default-root capture guard
remains intact.

## Integration capture artefacts

The Docker/Aspire job keeps the recorder enabled at its normal per-user root,
`${XDG_DATA_HOME:-$HOME/.local/share}/vouchfx`. It snapshots that directory before
the test step and collects newly appearing DCP captures afterwards, including when
the test fails. No job-wide capture-directory override is set: the READY-path
Docker test still checks the real default root.

The `integration-dcp-captures` artefact contains matching capture files and a
manifest with run, attempt, job, test outcome, filenames, sizes and SHA-256 values.
It expires after seven days. A run with no captures uploads only a zero-count
manifest. Snapshot state and resolved source paths are excluded from the artefact;
existing captures are not copied or deleted. Changed old files, links/reparse
points, invalid headers and unsafe paths fail collection. Upload requires successful
collection, so a refused destination cannot publish unrelated existing files.

Collection is bounded to 4,096 directory entries, 64 inventory captures, 256 KiB
per file, 12 new captures and 3 MiB of payload. It rejects excess rather than
truncating. The engine may already have pruned older captures before collection;
this copies the retained evidence, not a complete history of every topology start.

Capture bodies are verbatim, **not redacted**. They can contain generated dependency
passwords, fixed local test credentials, host values routed via `${env:NAME}` and
absolute host paths. Public Actions
artefact contents are not secret-masked; this integration job must continue mapping
no secrets or credential-bearing host values into its suites. Temporal inventory
attribution identifies files appearing during this job's test window, not individual
topology ownership. The native fixture tests prove collection and workflow contracts;
hosted upload and real Docker execution need separate evidence.
