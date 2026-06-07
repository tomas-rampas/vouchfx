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

## What was deliberately omitted
- **`.mcp.json`** — the framework's 5 `npx` MCP servers (dotnet, serena, filesystem, context7, bash).
  They require Node/npm and live-session registration; the `filesystem` path is machine-specific.
  Add them on a local machine if wanted.
- **`hooks/*.json`** — the framework's own validation/routing/pattern-capture config. These are **not**
  Claude Code native `settings.json` hooks; they are consumed by the framework's scripts. Wiring native
  `PreToolUse`/`PostToolUse` hooks is a separate, deliberate step.
- **`shared/`, `scripts/`, `claude.json`** — framework internals not needed for project-scoped use.

## How it activates
Claude Code reads agents/skills/commands/settings **at session start**. These take effect in a **new**
session on this repo (local or web) — they do not hot-load into an already-running session. Some agents
reference MCP tools (serena/context7) that are absent unless those servers are configured.

## Upstream
The full framework is designed to be installed at `~/.claude` on a developer machine
(`git clone https://github.com/tomas-rampas/claude-agentic-framework ~/.claude`). This folder is a
curated, project-scoped subset; refresh it from upstream as that repo evolves.
