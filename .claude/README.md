# vouchfx `.claude/` — project Claude Code configuration

These agents, skills, commands, and settings are vendored from the
[**claude-agentic-framework**](https://github.com/tomas-rampas/claude-agentic-framework)
(source commit `2af6e0e`) and committed here as **project-scoped** Claude Code config so the team
shares the same agents and permissions when working on vouchfx.

## What was included
- `agents/` — 19 specialised subagent definitions (csharp-expert, system-architect,
  code-review-gatekeeper, security-specialist, database-specialist, devops-orchestrator,
  technical-docs-writer, product-owner, and language/role experts).
- `skills/` — 14 framework skills.
- `commands/` — 6 slash commands.
- `settings.json` — the permission allowlist (dotnet/git/npm/etc.) + `alwaysThinkingEnabled`.
- `hooks/` — 44 framework hook configs + `PHASE3_SUMMARY.md`. **Note:** these use the framework's own
  v3.0 schema (`agents`/`triggers`/`actions`/`enforcement`), **not** Claude Code's native `settings.json`
  hook format (`PreToolUse`/`PostToolUse` → command). They therefore do **not** auto-execute as native
  Claude Code hooks — they are configuration consumed by the framework's own routing/agents. Included for
  parity with upstream.

## What was deliberately omitted
- **`.mcp.json`** — the framework's 5 `npx` MCP servers (dotnet, serena, filesystem, context7, bash).
  They require Node/npm and live-session registration; the `filesystem` path is machine-specific.
  Add them on a local machine if wanted.
- **`shared/`, `scripts/`, `claude.json`** — framework internals not needed for project-scoped use.

> Wiring **native** Claude Code hooks (a `settings.json` `hooks` block with `PreToolUse`/`PostToolUse`
> commands — e.g. a SessionStart `.NET 8` installer) is a separate, deliberate step and is not created by
> copying the framework's `hooks/*.json`.

## How it activates
Claude Code reads agents/skills/commands/settings **at session start**. These take effect in a **new**
session on this repo (local or web) — they do not hot-load into an already-running session. Some agents
reference MCP tools (serena/context7) that are absent unless those servers are configured.

## Upstream
The full framework is designed to be installed at `~/.claude` on a developer machine
(`git clone https://github.com/tomas-rampas/claude-agentic-framework ~/.claude`). This folder is a
curated, project-scoped subset; refresh it from upstream as that repo evolves.
