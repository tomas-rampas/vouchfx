---
date: 2026-07-24
authors:
  - tomas-rampas
categories:
  - Announcements
tags:
  - MCP
  - AI
  - Tooling
---

# Why vouchfx has an MCP server now

The documentation site for vouchfx-mcp is live, at [vouchfx-mcp.vouchfx.io](https://vouchfx-mcp.vouchfx.io/). It is the fifth site in the family and probably the one that needs the most explaining, because "MCP server for an integration testing engine" does not tell you much on its own.

<!-- more -->

## The thing that annoyed me

I use AI coding agents for real work, and at some point I noticed the same thing happening again and again. I would ask an agent to fix a failing `.e2e.yaml` suite. The agent would run `vouchfx` in a terminal, read whatever text came back, and then tell me with great confidence that my test was broken.

Sometimes it was right. Often it was not, because what actually happened was that a container did not come up.

This distinction is not a detail for vouchfx. The engine has four outcomes, not two: a test can **pass**, it can **fail**, it can end in an **environment error**, or it can be **inconclusive**. A fail means the system under test is wrong and somebody should look at the code. An environment error means the test infrastructure is wrong, which is a completely different conversation and, importantly, does not break CI by default. Inconclusive means we ran out of time or the evidence never arrived, so we honestly do not know.

Keeping those apart is one of the things I care most about in this project. And then the agent reads the console output, flattens all four into "it failed", and starts editing my test to fix a bug that does not exist. That is worse than no help at all.

The console output is written for humans. It has colours, it gets reworded whenever I improve an error message. Asking a language model to parse it is asking for trouble. So the server returns structured results instead, and the four verdicts stay four verdicts all the way through.

## What is actually in it

Six tools and two resources. It is easiest to split them by whether they need a real test run or not.

Four of them do not. `validate_suite` checks a suite against the engine's JSON Schema and reports the structural errors it finds, without executing anything. `list_step_types` enumerates the step types in the dotted `family.provider` form, grouped by family. `describe_step_type` returns the full contract for one type, with the required and optional fields and their schema types. `search_docs` does free-text search over two vendored documents, the generated language reference and the recipes library, which are also exposed directly as MCP resources under `vouchfx-docs:///language-reference` and `vouchfx-docs:///recipes`.

None of those four need the vouchfx CLI to be installed at all. The schema and the documents are vendored into the server. An agent can draft a suite, check it, look up which fields `db-assert.postgres` takes, and find a recipe for RETRY polling, on a machine with no engine on it.

The other two touch a real run. `run_suite` executes a suite through the packaged CLI and reports the verdict together with each step's outcome. `explain_run` then diagnoses a run in plain language, purely by reading and parsing its JSON Lines event stream. It never re-runs anything, which matters more than it sounds: the run you are asking about is the run that happened, not a second attempt that might behave differently.

## Three decisions I would defend

**It refuses to run on a version mismatch.** The server pins an engine release in an `ENGINE_PIN` file, currently v1.0.0-rc.1, and if the installed CLI is not that version it stops and says so. A mismatched CLI does not fail loudly. It just quietly disagrees with the vendored schema about what a step type is, and then you lose an afternoon to it. I would rather refuse.

**The suite runs out of process.** The CLI is spawned as a child process. A suite that hangs, or spins, or is simply hostile, cannot take the server down with it. The engine has caps on script bodies and document sizes, but those are guard rails for honest mistakes, not a defence. Real isolation needs a separate process, so that is what this does.

**It never touches secrets.** The server does not resolve `${secret:...}` references and does not read environment variables looking for them. The engine already redacts, and the server passes the redacted stream through unchanged. There was a temptation to be clever here, and I am glad I did not take it.

## Where it stands, honestly

The six tools work, and the site covers installation and registration, the tool and resource reference, an overview, and troubleshooting.

What it does not have yet is a NuGet package. `Vouchfx.Mcp` is packaged and the publishing pipeline is wired up, but no tagged release has been pushed to NuGet.org, so `dotnet tool install Vouchfx.Mcp` will not find anything today. If you want to try it now, the install page documents a clone, `dotnet pack`, then install-from-local-source path, and that works. It is more commands than I would like, and it is temporary.

If you use it and something is unclear or wrong, please tell me. The shape of these tools comes from guessing what an agent needs, and guesses benefit from contact with reality.

## Links

- [vouchfx-mcp documentation](https://vouchfx-mcp.vouchfx.io/)
- [Install & register](https://vouchfx-mcp.vouchfx.io/docs/install.html)
- [Tool & resource reference](https://vouchfx-mcp.vouchfx.io/docs/tools-and-resources.html)
- [Troubleshooting](https://vouchfx-mcp.vouchfx.io/docs/troubleshooting.html)
- [GitHub repository](https://github.com/tomas-rampas/vouchfx-mcp)
