# Decision record: contract extraction for `vouchfx topology` (upstream ask U1)

**Status:** Proposed  
**Date:** 2026-09-22

## Context

The MCP server ([vouchfx-mcp](https://github.com/tomas-rampas/vouchfx-mcp)) reserves diagnostic `VFX-D-1210`: *a step names a topic, path or table that appears in no contract extracted from the workspace's source code*. The rule is implemented, catalogued and tested there, but deliberately left unregistered. Its only possible input is "the topics, HTTP paths and tables the code under test actually publishes, serves and writes" (`src/Vouchfx.Mcp/Validation/Semantics/TopologyCrossCheckRule.cs`), and that would come from an engine subcommand, `vouchfx topology [--sources …] [--json]`, which the MCP tracks as upstream ask **U1**. The rule checks a step's `topic` and `table`. It defers HTTP paths until U1's output defines route-pattern semantics.

The MCP attaches a second, different use to the same ask. Its Healer (`src/Vouchfx.Mcp/Diagnosis/SpecEditProposalBuilder.cs`) cannot tell whether a resource named by an `environment-error` event was declared under `environment.services` or under `environment.dependencies`. So its health-gate fragment has to target one block and name the other in a comment, and it records U1 as "a topology relay that would say which block a resource came from".

The engine does neither today. The [Planner](../planner.md) deliberately reads no source code, no OpenAPI or AsyncAPI documents and no infrastructure-as-code. U1 is therefore a new analysis capability, not the exposure of an existing one. It is also narrower than the topology-inspection tool sketched in the [AI Companion design](../04_AI_Companion_Feasibility_and_Design.md) (§4.8), which would describe services and seams worth testing: U1 reports names, deterministically. This record proposes U1's scope, output contract, command surface and security posture, grounded in a survey of how the reference samples declare their contracts and in a measurement of the parser it would use.

## Survey: how the reference samples declare contracts

The five samples in [vouchfx-samples](https://github.com/tomas-rampas/vouchfx-samples) (`samples/`, read on 2026-09-22) were read by hand. A *contract name* is a value a suite step can name: an HTTP route served, a broker destination (topic, queue, subject or stream) or a table. Paths are repository-relative, and `…/payments/` abbreviates `samples/payments-java/app/src/main/java/com/vouchfx/samples/payments/`.

| Sample and stack | Routes | Broker destinations | Tables |
|---|---|---|---|
| `orders-dotnet`: C#, ASP.NET Core minimal API, Confluent.Kafka, Npgsql | 3 literal: `samples/orders-dotnet/app/Program.cs:58`, `:63`, `:124` (`/orders/{id:guid}`) | 1 literal: `order-events`, `samples/orders-dotnet/app/Program.cs:105` | 1 in raw SQL: `orders`, `samples/orders-dotnet/app/DatabaseInitializer.cs:50`; `samples/orders-dotnet/app/Program.cs:83`, `:130` |
| `payments-java`: Spring MVC, NATS JetStream, JdbcTemplate | 3 literal annotations: `…/payments/web/PaymentController.java:57`, `:65`, `:81` | 2 `static final` constants: subject `payments.authorised`, stream `PAYMENTS_AUTHORISED`; `…/payments/messaging/NatsPublisher.java:63`, `:66`, used at `:89`–`:90`, `:125` | 1 in a constant SQL text block: `dbo.payments`, `…/payments/repository/PaymentRepository.java:32`, `:44`, `:50` |
| `inventory-python`: FastAPI, pika, PyMySQL | 3 literal decorators: `samples/inventory-python/app/main.py:112`, `:122`, `:142` | 1 module constant: queue `stock-events`, `samples/inventory-python/app/mq.py:18`, used at `:30`, `:45` | 1 in constant SQL: `items`, `samples/inventory-python/app/db.py:15`, `:24`, `:29` |
| `ledger-jsonrpc`: Node.js `node:http`, kafkajs, pg | 2 literal URL comparisons, no router: `samples/ledger-jsonrpc/app/src/server.js:99`, `:108` | 2 exported constants used from other modules: `ledger-events`, `ledger-adjustments`; `samples/ledger-jsonrpc/app/src/kafka.js:14`–`:15`, used at `samples/ledger-jsonrpc/app/src/api.js:72`, `samples/ledger-jsonrpc/app/src/server.js:146` | 2 in raw SQL: `accounts`, `adjustments`, `samples/ledger-jsonrpc/app/src/db.js:39`, `:47`, `:64`, `:153` |
| `kafka-mtls`: no application code | none | none (the suite publishes and expects its own topic `orders`: `samples/kafka-mtls/tests/kafka-mtls.e2e.yaml:266`, `:280`) | none |

What the survey found:

- **22 contract names across the four services**: 11 routes, 6 broker destinations and 5 tables. `kafka-mtls` has none, because its suite is its own producer.
- **How they are declared.** 12 (55%) are literals written where the contract is declared: all 11 routes (two of them as bare URL comparisons, with no router), plus `order-events`. 5 (23%) are named constants, two of them imported across modules. The other 5 (23%) are table names inside raw SQL text.
- **None comes from configuration.** Every sample reads connection details from the environment (for example `KAFKA_BOOTSTRAP`, `samples/orders-dotnet/app/Program.cs:31`), but never a contract name.
- **No table is mapped by an ORM.** An EF Core `ToTable`/`[Table]` recogniser would find none of the five.
- **What an extractor would catch.** A literal-only extractor covering all four languages would find at most 12 of the 22. **A literal-and-constant extractor for C# alone finds 4 of the 22 (18%).** That is four of the five names in the one C# service; the fifth is its raw-SQL table.
- **Outside these kinds**, the samples also carry an outbound webhook path built by interpolation (`samples/orders-dotnet/app/WebhookNotifier.cs:42`), a Redis key template (`samples/inventory-python/app/cache.py:31`) and four JSON-RPC method names.

What the rule can check is narrower still. Across the five suites, steps name four distinct topics and five distinct HTTP paths. No step has a `table` field. In the composed schema, `table` exists only on `db-assert.dynamodb`; the relational `db-assert.*` providers carry their table inside `query`. A C#-only extractor therefore makes one topic (`order-events`) and two paths (`/orders` and `/orders/{orderId}`) checkable, and it must stay silent on everything else.

## Decision

### 1. Scope: two separable surfaces, and U1 is only the first

**(a) Contract extraction from sources** is the input VFX-D-1210 needs, and it is U1.

**(b) Relaying the resolved environment topology** (which block each resource came from) is **not** part of U1. It is proposed as a separate, smaller ask. The two share a word, not a design:

- **Different moment.** (a) is a static property of a source tree; (b) is a fact about a run that has already happened. A `topology` command that re-read the suite from disk would describe today's file, not that run. The MCP already refuses exactly this staleness: `get_step_timeline` and `get_run_artifacts` both decline to read a suite.
- **Different channel.** The run's own record is its event stream: one stream, rendered differently for each audience (blueprint §14). Describing a run's resources a second time, in another command's output, would create the per-audience pipeline §14 rules out.
- **Different trust.** (b) comes from the engine's own resolved model; (a) parses untrusted text (§5). Bundling them would hold the Healer's fix behind the larger and riskier piece of work.
- **The gap is real and small.** `environment-error` carries only `resourceName` and `errorKind`. `resourceName` can name a service, a dependency or an engine phase (`startup` or `discovery`: `src/Engine/Vouchfx.Engine.Orchestration/SuiteTopology.cs:529`, `:626`), and nothing on the wire tells them apart. `samples/kafka-mtls` shows it happening: its broker is declared as a *service* (`samples/kafka-mtls/tests/kafka-mtls.e2e.yaml:100`, with its `healthCheck` at `:117`), so a health-gate failure there would get a fragment aimed at `environment.dependencies`.

The proposed resolution for (b) is an optional `resourceRole` field on `environment-error`, taking the value `service`, `dependency` or `engine` and omitted when unknown. It carries names and roles only, never images, `env` values or connection strings. The field is **not** a free addition, even though today's gate would not notice it. `environment-error` is a v1 wire record, but `EnvironmentErrorEvent` is declared in `Vouchfx.Engine.Orchestration`, outside the `Vouchfx.Engine.Abstractions.Events` namespace whose records `EventContractFreezeTests` freezes (§14.4.2), so adding a property to it moves no golden. That is a gap in the gate, not a licence. The change needs an explicit wire-contract decision by the maintainer, and its implementation starts by listing `EnvironmentErrorEvent` in the gate's frozen set with its current golden, so that the new property then lands as a reviewed golden change. Renderers tolerate unknown fields, so no consumer breaks, but that tolerance is what makes the change safe, not what permits it. The alternative, which changes no existing record, is a new record type (one `resource` record per declared resource, carrying its name and role), declared in `Vouchfx.Engine.Abstractions.Events` so that the gate's completeness test forces it into the frozen set. It costs a second record where one field would do, which is why this record recommends the field, subject to that decision. Either way it takes about a day in each repository and does not need to wait for U1.

### 2. The MVP

The MVP covers C# only, parsed at syntax level with Roslyn: `CSharpSyntaxTree.ParseText` from `Microsoft.CodeAnalysis.CSharp` 4.14.0. That package is already centrally pinned (`Directory.Packages.props`) and already in the CLI's dependency closure. Nothing is compiled, bound or executed.

A new library, `Vouchfx.Engine.Topology` (in the engine's reserved namespace, blueprint §5.6), exposes `TopologyExport.Extract(request, ct)`, following the `PlanExport` pattern, and the CLI command is a thin wrapper over it. The library references `Microsoft.CodeAnalysis.CSharp`, whose syntax API is all it uses, and no engine project that compiles, orchestrates or runs code. The forbidden-API test in §5 holds it to that syntax API.

Syntax has no types, so each shape below is recognised by the form of the call:

- **Routes** (`kind: route`, `technology: aspnetcore`, `direction: serve`).
  - *Minimal APIs:* `MapGet`, `MapPost`, `MapPut`, `MapDelete` and `MapPatch(pattern, handler)`; `MapMethods(pattern, methods, handler)`; and `Map(pattern, handler)`, recorded with method `*`. A `MapGroup(prefix)` prefix is applied when the group is the receiver chain itself, or a local initialised from one in the same method.
  - *Controllers:* a class-level `[Route]` combined with `[HttpGet]` … `[HttpOptions]` or an action-level `[Route]`. Tokens follow ASP.NET Core's own rules: `[controller]` becomes the class name without a trailing `Controller`, `[action]` the method name without a trailing `Async` (the framework's `SuppressAsyncSuffixInActionNames` default) or an `[ActionName]` argument where one is present, and `[area]` an `[Area]` argument. A template starting with `/` or `~/` overrides the class prefix. An application-model convention can rename controllers and actions at run time, and syntax cannot see what it does. So in a tree that declares a class implementing `IApplicationModelConvention`, `IControllerModelConvention` or `IActionModelConvention`, each token-derived segment becomes `{?}` in an `unresolved` entry with reason `convention`, rather than a name the running service may not use. No sample uses controllers. They are in scope because attribute arguments must be compile-time constants, so an attribute-routed template is always a literal or a named constant. The constant table below resolves it whenever the constant is declared in the analysed tree.
  - *Path base:* `UsePathBase` changes the path routing sees only when it runs before routing. The unprefixed route always stays, because the middleware passes a request that lacks the prefix through unchanged. The prefixed twin, with the literal (`/x`) when the argument is one and `{?}` otherwise, is emitted only when the analysed code shows the call ahead of routing: an explicit `UseRouting()` later in the same method, on the same builder. A `WebApplication` that never calls `UseRouting` runs routing first, implicitly, so its path base never reaches the route table and its routes get no twin. Where the order cannot be read from syntax, such as a call in another method or behind a condition, the twin is emitted `unresolved`, with reason `middleware-order` and the prefixed route as its `pattern`, so it can only silence the rule for paths under the prefix.
- **Kafka topics** (`kind: topic`, `technology: kafka`), from Confluent.Kafka:
  - `Produce` and `ProduceAsync`, whose first argument is a string or `new TopicPartition(…)`, give `produce`;
  - `Subscribe` (a string or a collection of strings) and `Assign(new TopicPartition(…))` give `consume`;
  - `TopicSpecification { Name = … }` gives `define`.
- **Constant folding.** The extractor folds:
  - literals: regular, verbatim and raw;
  - `const string` fields and locals, anywhere in the analysed tree. A first pass collects these declarations into a table; a name that appears twice with different values is unresolved.
  - `static readonly string` fields with a literal initialiser, but only when no assignment to that field appears anywhere else in the analysed tree. `static readonly` is not a compile-time constant: a static constructor may assign it again, and folding the initialiser would then report a name the running code never uses. That false resolved entry would suppress VFX-D-1210. Any other assignment makes the field `unresolved` with reason `non-constant`.
  - `nameof`, `+` concatenation, and interpolated strings whose holes all fold.

  Anything else is reported as `unresolved`, with its literal skeleton kept (§3).
- **Preprocessor.** A file with `#if` directives is parsed twice: once with no symbols defined, and once with every symbol its conditions mention. The union is reported, and entries from conditional regions are marked `conditional: true`. A route behind `#if DEBUG` is therefore not lost. The two parses do not reach every branch, though: a condition such as `#if DEBUG && !TRACE` is false under both, so a route in that region is in neither parse. Roslyn keeps an inactive region as `DisabledTextTrivia`, so the extractor can see exactly which regions neither parse analysed. It lists each one in `skipped` with reason `conditional-region-unanalysed` and marks the scan incomplete, so the document never claims a completeness it does not have. Enumerating every satisfying symbol assignment is deferred, because the number of assignments grows exponentially with the symbols a file mentions.

Recognition is deliberately permissive. An over-match, such as a `MapGet` or `Produce` on some unrelated type, adds a name, and for the values it matches, an extra name can only silence VFX-D-1210. It is not harmless in every respect, though. It also counts as evidence that its kind appears in the code at all (§6, condition 3), so it can let the rule fire on values the real code serves in a style the extractor does not recognise (one §2 does not list). Test projects are where most stray registrations live, since a `WebApplicationFactory` host maps its own routes, so the default excludes cover them (§4). What remains is stated rather than claimed away: a tree that mixes a recognised style with an unrecognised one can draw a false VFX-D-1210. That is advice in `semanticDiagnostics`, never a schema error or a verdict. So nothing is gated on `using` directives or project SDKs. `samples/orders-dotnet` shows why: it relies on the Web SDK's implicit usings, so a gate on `using` directives would miss all three of its routes.

**Out of the MVP, and what each would add:**

| Deferred | What it would add | Why not now |
|---|---|---|
| Tables: raw SQL in `CommandText`, command constructors and Dapper calls; EF Core `ToTable`/`[Table]` | The survey's 5 tables, all in raw SQL (EF Core would find none) | Nothing would consume them yet: the rule's only `table` field is on `db-assert.dynamodb`, so the MCP must first learn to find table names in `query` |
| Other C# clients: Azure Service Bus, RabbitMQ.Client, NATS, MongoDB, DynamoDB, S3 | `queue`, `subject`, `stream`, `collection` and `bucket` kinds | Each needs a matching rule field in the MCP, and no C# sample uses them |
| Python, Java, JavaScript and TypeScript | 17 of the survey's 22 names | Each language needs its own parser, which is a new surface for untrusted input; Roslyn has no equivalent for these languages in the dependency graph |
| OpenAPI and AsyncAPI documents | Language-neutral paths and channels, already in template form | No sample ships one, and such documents state *declared* contracts, not implemented ones |
| Configuration resolution (`appsettings*.json` keys read through `IConfiguration`) | Names whose value lives in configuration | None of the 22 names does; and an environment override, possibly from the suite's own `env:`, makes a file value only a default |

### 3. Output shape (`--json`)

The output follows `plan`'s convention. With `--json`, the command writes one indented JSON document to stdout, serialised with the CLI's shared `--json` options; without it, stdout carries a short human-readable summary instead. `--output <file>` always writes the same JSON document to that file, whether or not `--json` is passed. The file holds exactly the document's bytes, and stdout adds one trailing newline after them, as `plan` does (`File.WriteAllText` for the file, `WriteLine` for stdout). The MCP runs `topology --json`. A golden test pins it from its first release. The expected output for `samples/orders-dotnet` looks like this (abridged to two of its four entries):

```json
{
  "schemaVersion": 1,
  "engineVersion": "<engine version>",
  "sources": ["**/*.cs"],
  "exclude": ["**/bin/**", "**/obj/**", "**/.git/**", "**/node_modules/**", "**/test/**", "**/tests/**", "**/*.Tests/**"],
  "complete": true,
  "incomplete": [],
  "filesAnalysed": 6,
  "skipped": [],
  "unanalysedSourceFiles": {},
  "inputDigest": "<SHA-256 of the scan's inputs>",
  "entries": [
    { "kind": "route", "technology": "aspnetcore", "direction": "serve", "method": "GET",
      "route": "/orders/{id}", "rawRoute": "/orders/{id:guid}", "confidence": "literal",
      "provenance": { "file": "app/Program.cs", "line": 124, "column": 12 } },
    { "kind": "topic", "technology": "kafka", "direction": "produce", "name": "order-events",
      "confidence": "literal", "provenance": { "file": "app/Program.cs", "line": 105, "column": 17 } }
  ]
}
```

**Entries.**

- `kind` is `topic`, `route` or `table`; `table` is reserved for stage 2. Later kinds are additive, and a consumer must ignore any kind it does not know.
- `confidence` is `literal`, `constant` or `unresolved`. A `constant` entry also carries `declaredAt`, the location of the constant's own declaration. A consumer treats any unknown confidence value as `unresolved`.
- `provenance` is relative to the root and uses forward slashes. The document never contains an absolute path.

**A dynamic name is never dropped.** It is emitted with `confidence: "unresolved"`, its value field set to `null`, and a `reason`. The value field depends on the entry's kind: `route` for an HTTP route, `name` for a topic or table. The reason is one of `non-constant`, `configuration`, `parameter`, `ambiguous-constant`, `convention`, `middleware-order` or `too-long`. Where some of the value is known, a `pattern` keeps that skeleton; `pattern` is legal only on an `unresolved` entry, and it writes each unknown fragment as `{?}`, so `$"{env}.orders"` becomes `{?}.orders`. Dropping such a name silently would make VFX-D-1210 fire on a real contract.

**Topic and table matching.** A topic name compares case-sensitively, which is Kafka's own rule, so `Orders` and `orders` are different topics. A table name compares case-insensitively: the databases the engine targets disagree about identifier case, so a looser match here can only turn condition 5 false. It can silence VFX-D-1210 for a value it newly matches, but it never makes the rule fire. A stray resolved entry is different: through condition 3, it can make the rule fire (§2). In a topic or table `pattern`, `{?}` matches zero or more characters of any kind, the literal text between the fragments compares with the same case rule as a name of that kind, and the pattern must match the whole value, anchored at both ends. Zero, not one, because an unknown fragment can be empty: `$"{env}.orders"` with an empty `env` names `.orders`; matching that empty fragment only ever turns condition 5 false, so it too can only silence the rule, never fire it. So `{?}.orders` matches `prod.orders`, `eu.prod.orders` and `.orders`, but not `orders` or `prod.orders.dlq`. A route `pattern` uses the segment rules below instead.

**Completeness is data.** `complete` is `false` exactly when `incomplete` or `skipped` is non-empty. Their shapes are part of the contract, and so is that of `unanalysedSourceFiles`:

```json
"complete": false,
"incomplete": [ { "reason": "time-budget", "file": "src/Generated/Big.cs" } ],
"skipped": [
  { "path": "src/Legacy/Huge.cs", "reason": "too-large" },
  { "path": "src/Orders/Routes.cs", "reason": "conditional-region-unanalysed", "startLine": 40, "endLine": 58 }
],
"unanalysedSourceFiles": { ".py": 12, ".ts": 3 }
```

- `incomplete` lists causes that affect the scan as a whole. Each is an object with a `reason` and, where one file is to blame, that file as `file`. The reasons are `max-files`, `max-total-bytes` and `max-entries` for a bound that tripped; `time-budget` when the watchdog fired (§5), with the file in progress as `file`; `worker-busy` when the library refused to start a second worker while an abandoned one still ran (§5); and `parse-errors` for a file that parsed with errors, whose entries are still reported.
- `skipped` lists what was not analysed. Each is an object with a `path` and a `reason`. For a whole file or link, the reason is `too-large`, `too-deep`, `binary`, `symlink`, `not-regular` or `unreadable`. For a region inside a file, it is `conditional-region-unanalysed`, and the object also carries the region's `startLine` and `endLine`.
- `unanalysedSourceFiles` maps a lower-case extension, dot included, to the number of source files inside the scan's boundary that were not analysed. The boundary is the root minus every `--exclude` glob, the defaults included. A file is counted when it is in a source language the extractor does not read (`.py`, `.java`, `.kt`, `.js`, `.ts`, `.go`, `.fs`, `.vb` and similar), or when it is a `.cs` file that no `--sources` glob selected. So narrowing `--sources` below the tree's C# reports what it left out rather than hiding it, and `--exclude` is how code is taken out of the boundary altogether. It does not affect `complete`, because the extractor never claimed those languages; the MCP reads it separately, to stay quiet in polyglot trees.

`file` and `path` are relative to the root and use forward slashes, like `provenance`. A consumer treats a reason it does not know as it treats the known ones: the scan is incomplete.

**Deterministic.** Entries are sorted ordinally by kind, method, name (or route, or pattern), file, line and column; `incomplete` by reason and file; `skipped` by path, start line and reason; and `unanalysedSourceFiles` by extension. `inputDigest` is a SHA-256 over what the walk decides before any analysis. The walk reads whole each file that `--sources` selects, and skips a file over the per-file limit as `too-large` without reading it. The digest takes three kinds of entry, in ordinal order of relative path, and covers each one's path followed by:

- for a file the walk read whole, its bytes;
- for an entry the walk skipped (`too-large`, `symlink`, `not-regular` or `unreadable`), the reason;
- for a file counted in `unanalysedSourceFiles`, that fact alone, because adding such a file changes the MCP's condition 2 (§6).

Each field is length-prefixed, so two different walks cannot produce the same input. `binary`, `too-deep`, `conditional-region-unanalysed` and `parse-errors` are left out, because each is a function of bytes the digest already covers, under an engine version and options that the MCP's cache key covers too. §6 uses the digest to keep a cache current.

The document carries no timestamps and no host paths, so a given tree always yields the same document, whatever the operating system and whatever order the file system listed it in. Only `engineVersion` varies, between engine releases.

**The digest-only document.** `--digest-only` runs the same walk, with the same globs, containment and bounds, and stops before analysis: it neither pre-scans nor parses. It changes which document is produced, not where it goes. `--json`, `--output` and the human summary behave as they do for a full scan, and a usage error or an internal fault exits as §4 says, with no document. The document is the full one's header without the analysis fields, plus a marker that keeps the two shapes apart:

```json
{
  "schemaVersion": 1,
  "engineVersion": "<engine version>",
  "digestOnly": true,
  "sources": ["**/*.cs"],
  "exclude": ["**/bin/**", "**/obj/**", "**/.git/**", "**/node_modules/**", "**/test/**", "**/tests/**", "**/*.Tests/**"],
  "complete": true,
  "incomplete": [],
  "inputDigest": "<SHA-256 of the scan's inputs>"
}
```

Here `complete` is `false` exactly when `incomplete` is non-empty. `incomplete` can name only a walk-level cause: `max-files`, `max-total-bytes`, `time-budget` or `worker-busy`. The document lists no `skipped` entries, because the digest already covers every walk-level skip. A full document never carries `digestOnly`. A consumer that finds the marker where it expected a full document treats the output as no topology.

**Route-pattern syntax.** `route` is a normalised template:

- It has a leading `/` and no trailing `/` (except for the root route itself). Empty segments are collapsed, and literal segments are kept verbatim.
- A parameter is written `{name}`, with constraints stripped (`{id:guid}` becomes `{id}`).
- An optional parameter, or one with a default, is written `{name?}`.
- A catch-all is written `{*name}`, whether it was declared as `{*name}` or `{**name}`.
- `{?}` marks an unknown fragment, so a group prefix built in another method gives `{?}/{id}`.

`rawRoute` keeps the declared text.

**Matching** is defined here so that both repositories implement one rule. Both sides can be templates: an extracted route has parameters, and a suite path has `{placeholder}`s that are substituted at run time. So matching is symmetric. A suite path matches a route when at least one concrete request path satisfies both. First strip the suite path's query string, fragment and any trailing `/`. Then split each side into segments and read each segment as one of these tokens:

| Segment | Side | Stands for |
|---|---|---|
| literal text | either | that segment, compared case-insensitively, which is ASP.NET Core's own rule |
| `{name}`, or any segment mixing literal text and parameters, such as `{name}.{ext}` | route | exactly one non-empty segment |
| `{name?}` | route | zero or one segment, final position only |
| `{*name}` | route | zero or more segments, final position only |
| any segment containing `{?}` | route | zero or more segments, because an unknown fragment can be empty |
| any segment containing a `{placeholder}` | suite | one or more segments, because a substituted value can itself contain `/` |

Each side is then a sequence of tokens, and the check asks whether the two sequences accept a common path. That is a reachability search over pairs of positions, one in each sequence, so its cost is linear in the product of their lengths. A route segment that mixes literal text with a parameter or `{?}` is read as a wildcard, which over-matches. That only turns condition 5 false, so it can silence VFX-D-1210 for a route it newly matches but never makes the rule fire. A stray resolved entry's separate effect, through condition 3, is covered in §2. The one under-match is a placeholder whose run-time value is empty, which the rule assumes away because a path segment written as a placeholder is meant to carry a value.

For example:

- the suite path `/orders/123` matches the route `/orders/{id}`, and so does `/orders/{orderId}`;
- `/orders/{orderId}/cancel` does not match `/orders/{id}`, because the route has no third segment;
- `/files/{name}` matches `/files/{*path}`, and so does `/files/a/b.txt`;
- `/{basePath}/orders` matches `/api/v1/orders`, because `{basePath}` may stand for `api/v1`.

### 4. Command surface

```text
vouchfx topology [<root>] [--sources <glob>]... [--exclude <glob>]... [--json] [--output <file>]
                 [--max-files <n>] [--max-file-bytes <n>] [--time-budget <seconds>] [--digest-only]
```

- **Root and globs.** `<root>` defaults to the current directory and must be an existing directory. `--sources` defaults to `**/*.cs`. `--exclude` adds to the defaults `**/bin/**`, `**/obj/**`, `**/.git/**`, `**/node_modules/**`, `**/test/**`, `**/tests/**` and `**/*.Tests/**`. Globs are relative to the root and use `run --path`'s wildcard semantics (`*`, `**`, `?`, case-insensitive). They are compiled with `RegexOptions.NonBacktracking`, because they are matched against every file in the tree and the existing `GlobMatcher` sets no match timeout.
- **Bounds.** Each limit below is both the default and a ceiling. `--max-files`, `--max-file-bytes` and `--time-budget` can lower their limit but never raise it: each takes an integer from 1 up to its default, and any other value is a usage error (exit 2). The ceilings are what the hostile-input measurements in §5 cover. Raising one is therefore an engine change, made with the Roslyn pin and re-measured against the adversarial corpus, rather than a caller's choice. A tree beyond a ceiling gets an incomplete document, on which the MCP rule stays silent (§6). The fixed allowances also let a caller such as the MCP use a single timeout, set above their 70-second sum, for every call.
  - 10,000 files, 128 MiB in total, and 1 MiB per file. A larger file is skipped whole, never truncated, because a truncated file parses differently.
  - A nesting depth of 64 brackets and 4 interpolated strings, enforced by the pre-scan (§5).
  - 50,000 entries, and 512 characters for any emitted string. A longer name becomes `unresolved`, with reason `too-long`.
  - A 60-second wall-clock budget for the analysis, enforced by a watchdog that does not wait for the file in progress, and a separate 10-second allowance for writing the document (§5).
- **Containment.** The root itself must not be a symbolic link or junction: a root carrying `FileAttributes.ReparsePoint` is a usage error (exit 2), checked before enumeration starts, because following it would scan a tree the caller did not name. Links further up the root's own path are not refused, because platform paths such as macOS's `/tmp` are links. The walk resolves no path below the root. It opens the root once, as a directory and without following it, and reaches every entry through a directory it already holds open, so a directory replaced by a link while the walk is under way cannot redirect it. On Unix, it reads a directory's entries from that directory's own descriptor, checks each with `fstatat` without following links, and opens it with `openat` on the same descriptor and `O_NOFOLLOW | O_NONBLOCK`, adding `O_DIRECTORY` for a directory it descends into. The `fstatat` must already report a regular file or a directory, so a device node is not opened in the ordinary case. On Windows, it reads a directory's entries from its handle (`GetFileInformationByHandleEx` with `FileIdBothDirectoryInfo`), rules out links from the attributes that listing returns, and opens each entry by the file id it returned (`OpenFileById` with `FILE_FLAG_OPEN_REPARSE_POINT`). Every entry's type is then checked again on its handle before it is used: on Unix, `fstat` must report the regular file or directory expected; on Windows, `GetFileInformationByHandle` must report no reparse point, and a file must be a disk file (`GetFileType`). .NET exposes none of this portably, so it is the extractor's one piece of platform code, a small P/Invoke surface per platform. A link is neither descended into nor read: each link under an included path is listed in `skipped` as `symlink` and makes the scan incomplete. Anything else that fails those checks is skipped as `not-regular`, with the same effect: a FIFO, a device, a socket, or an entry swapped for a link or anything else between listing and open. Because every type is checked on what was actually opened, nothing but a regular file is ever read, and a FIFO swapped in cannot block the scan, because the non-blocking open returns at once. One residual remains: a device node swapped in during that window is opened before the check on the handle rejects it, and opening some devices has side effects. Planting one takes privileges that write access to a workspace does not give. Each read is bounded by the per-file byte cap while it streams, not after. A read that stalls, on a network file system for example, is bounded by the time budget, whose watchdog does not wait for it (§5). The scanner writes nothing. Nothing is written except `--output`, whose parent directory must exist, as for `plan --output`. That path is the caller's, not the workspace's, so the containment rules above do not govern it, and the MCP never passes one: it reads the document from stdout (§6). The file is still written without following a link at its own name: the document goes to a new temporary file in the same directory, created exclusively, which is then moved over the target, and a move replaces a link there rather than writing through it. There is no cache.
- **Exit codes.**
  - `0`: an analysis was produced, complete or not. Completeness is recorded in the document, just as gaps are data for `plan`.
  - `2`: a usage error, such as a root that is missing or not a directory, a malformed glob, a bound out of range, or a missing `--output` parent.
  - `3` and `4`: never. The command needs no infrastructure and reaches no verdict.
  - `1`: never a topology outcome, since the command asserts nothing. An unexpected internal fault falls through to System.CommandLine's default handler, which also returns `1` and writes no document. So does a document write that outlives its allowance (§5). Consumers treat any non-zero exit as "no topology".

  **A root with no matching files exits `0`**, with `filesAnalysed: 0` and a note on stderr. For a suite-only or non-C# tree such as `kafka-mtls`, that is a real answer. It differs from `plan`, where an empty suite folder is a usage error because the operator named that folder expressly.
- **Never needs Docker.** The command reads files and parses syntax. There is no topology, no Aspire or DCP, no container runtime, no network, no NuGet restore, no MSBuild evaluation and no compilation. It joins `validate`, `plan`, `schema` and `list` among the commands that run without Docker, and a test runs it with no container runtime and no network.

### 5. Security

**The input is untrusted, and it is a different trust class from suites.** The engine's containment check for files a suite references (`SecurityArtifactPath`) is explicitly lexical. That is acceptable there because the suite author is trusted: `script.csharp` already grants that author arbitrary C#. A source tree is different. It can hold vendored code, dependencies and contributors' branches, and the MCP will run this command on whatever a workspace contains. Hence the stricter symlink rule and the bounds in §4.

**No execution.** The command builds syntax trees and nothing more. It creates no `CSharpCompilation` or semantic model, and it runs no `Emit`, no analysers and no source generators. It does no MSBuild evaluation, because property functions run code at evaluation time. It makes no NuGet or network call, no `Process.Start` and no `Assembly.Load*`. Two things enforce this: the library's reference list, and an IL-level forbidden-API test over the new assembly, in the manner of the existing `ForbiddenScriptingApiScanner`.

**Roslyn's parser is not safe on hostile input without a pre-scan.** This was measured on 2026-09-22, in throwaway spikes against `Microsoft.CodeAnalysis.CSharp` 4.14.0 on .NET 8.0.31 (Linux x64, 8 MiB main-thread stack), one case per process:

| Input (one file) | Size | Result |
|---|---|---|
| 5,000 nested parentheses | 10 KB | Parsed in 3.2 s |
| 20,000 nested parentheses | 40 KB | **The process is killed** by a stack overflow in `LanguageParser.ScanType`. On a dedicated 64 MiB stack the file parses, but takes 71 s |
| 1,000 nested interpolated strings | 5 KB | Parsed in 2.1 s |
| 10,000 nested interpolated strings | 50 KB | **The process is killed**, this time in the lexer. `SyntaxFactory.ParseTokens` dies the same way |
| 1 MiB of statements nesting parentheses 64 deep (256 deep) | 1 MiB | 4.5 s (15.0 s) |
| 1 MiB of statements nesting interpolated strings 4 deep (32 deep) | 1 MiB | 2.1 s (7.7 s) |
| 1 MiB of ordinary `MapGet` and `ProduceAsync` statements | 1 MiB | 0.8 s to parse, 0.3 s to traverse |
| 10,000 to 100,000 nested `new[] {}` initialisers, lambdas or blocks | up to 300 KB | At most 0.34 s. The parser reports error CS8078 ("too complex"), so these paths are guarded |
| A hostile 1 MiB file whose cancellation token fires at 1 s | 1 MiB | Ran to completion (16.7 s): the token is not observed mid-parse |

The design responds in four ways:

1. **Pre-scan every file before parsing.** The pre-scan is hand-written and iterative (an explicit stack, no recursion, linear time). It understands comments, character literals and every string form, including interpolation holes. It measures bracket and interpolated-string nesting, and a file deeper than the caps (64 brackets, 4 interpolated strings) is skipped as `too-deep`. Roslyn's own lexer cannot do this job, because it crashes on the interpolation case. At these caps, the worst measured cost for a 1 MiB file is about 4.5 s, against 0.8 s for ordinary code.
2. **Parse on a dedicated thread with an explicit stack size.** The crash threshold follows the stack size, which differs by host: 20,000 nested parentheses kill a 1 MiB thread and the 8 MiB Linux main thread alike, but parse on a 64 MiB one. A fixed stack gives the caps the same safety margin on every host. Each file is opened, read, pre-scanned, parsed and walked on that thread, never on the thread that enforces the budget.
3. **Enforce the time budget with a watchdog.** `ParseText` ignores its token while parsing, and a read from a stalled network file system cannot be cancelled either, so the budget cannot wait for the worker to return. The main thread waits for the worker for at most the time left. When the budget runs out, the result holds the files already finished, with the budget and the file in progress named in `incomplete`. Ending the process is the command's business, never the library's. `TopologyExport.Extract` returns that result when its deadline passes and abandons the worker, a background thread (the dedicated thread of point 2), which then finishes or stays blocked in its current file on its own. An in-process caller, such as a test, therefore gets the partial result and keeps running, at the cost of that one thread, and the library bounds that cost at one. It runs at most one worker per process: a call that finds an earlier call's abandoned worker still running returns at once, with nothing analysed and `worker-busy` in `incomplete`, rather than starting a second. A worker that finishes its current file sees that it has been abandoned and exits without publishing anything more. Embedding the library is for tests and trusted trees. An untrusted tree is scanned through the command, in a process of its own, which is how the MCP does it. Only the `vouchfx topology` command ends the process: once it has written the document, it calls `Environment.Exit`, which waits for no thread, background or foreground, and so ends the abandoned worker too. The write has a watchdog of its own, because a write to a stalled sink cannot be cancelled either: a network file system under `--output`, or a stdout pipe that nobody drains. The document is serialised in memory, where the caps bound its size, and written on a worker thread, which the main thread waits on for at most 10 seconds. If the write has not finished by then, the command exits `1`, which a consumer already treats as no topology (§4). An abandoned `--output` write leaves at most its temporary file, never a partial target. The MCP runs the command as a subprocess (§6), never the library in-process. A finished file is published whole: the worker builds each file's entries into one immutable record and appends it to an immutable list by replacing the list's reference in a single atomic write, and it records the path it is working on in a volatile field. The main thread serialises the list it reads at that moment, so it sees every file whose record was published and never part of one. The caps still matter: they keep a single file's cost well inside the budget, so an ordinary scan finishes. The one wait this cannot bound is a wait inside the kernel that even process exit cannot end. The MCP stops waiting at its own process timeout, so even that case cannot hold up the rule.
4. **Keep the process boundary as the backstop.** The MCP runs `topology` as a subprocess with a wall-clock timeout, and treats a crash as "no topology". The adversarial inputs above become a regression corpus, re-measured whenever Roslyn is bumped. The caps belong to the pinned Roslyn version, the way the Aspire pin belongs to the engine release.

**Output hygiene.** Only values at recognised contract positions are emitted, never arbitrary literals, so a credential literal elsewhere in a file is never copied out. Strings are capped, and paths are relative to the root. The human-readable rendering passes names through the engine's display sanitiser. A name is text lifted from untrusted source, so consumers match it and treat it as data, never as instructions (doc 04 §7.4).

### 6. MCP integration contract

VFX-D-1210 maps step fields to kinds one to one:

- `topic` maps to `topic`;
- the `path` of `http.rest` and `http.soap` maps to `route`, using the matching rules in §3;
- `table` maps to `table`, from stage 2.

The rule **reports a value for kind K only when all of the following hold**:

1. `complete` is `true`.
2. `unanalysedSourceFiles` is empty. A Python producer is invisible to a C# extractor, so a polyglot tree gets no findings, and neither does a scan whose `--sources` left C# code unread.
3. At least one resolved entry of kind K exists. That shows the technology appears in the analysed code at all.
4. No entry of kind K is `unresolved` without a `pattern`. **An unresolved entry suppresses its kind; it never fires.**
5. The value matches no `name`, `pattern` or `route` of kind K.
6. On the MCP side, the value is not one the suite produces itself, such as an `mq-expect.kafka` topic that an earlier `mq-publish.kafka` step publishes (as in `kafka-mtls`).

Applied to the survey, the rule reports nothing. Conditions 2 and 3 suppress it for every sample except `orders-dotnet`, and in `orders-dotnet` every name matches. That is the intended behaviour.

vouchfx-mcp must make these changes:

- **Model the document.** Replace `SuiteTopology(IReadOnlySet<string> Names)` with a model of the v1 document that ignores unknown kinds and fields, and treats any `schemaVersion` other than 1 as no topology.
- **Obtain it from the pinned CLI.** Use the existing `CliPinVerifier` and subprocess plumbing, with a timeout above the command's own budget. Make the rule **CLI-optional**, like `get_schema`'s cross-check. If there is no pinned CLI, the exit is non-zero, the call times out or the document cannot be parsed, there is no topology and the rule stays silent. `validate_suite` stays usable offline.
- **Run it in the right process.** Run `topology` from the server process, not from the validate worker, and pass the result to the worker as data. Cache it per root, keyed on the pinned engine's version, the full set of options passed to `topology` (globs and bounds), and the engine's own `inputDigest`, and apply `PathSafetyGuard` to the root passed in. The engine version is in the key because recognition and bounds change between engine releases, so advancing `ENGINE_PIN` invalidates every entry rather than serving a topology the new engine would not produce. The MCP never reads a workspace file itself, because a second traversal would sit outside the extractor's bounds and containment. Instead, the document carries `inputDigest` (§3), which the same bounded, contained walk computes. To check a cached document, the MCP runs `topology --json --digest-only` with the same options, which performs that walk without the analysis and prints the digest-only document (§3). It reuses the cached document only when the digest-only document is `complete` and its `inputDigest` equals the cached document's. Otherwise it runs the full scan. A digest-only document that is not `complete` means the walk itself hit a bound, and the full scan performs the same walk under the same bounds. It would stop at the same point for `max-files` and `max-total-bytes`, and almost always sooner for `time-budget`, since it parses as well. Silence is the rule's safe direction, so the MCP treats that call as having no topology instead of spending a second budget. It caches any document except one whose `incomplete` names `time-budget` or `worker-busy`, the two causes that depend on the moment rather than on the inputs. The check is content-sensitive, so a tool that preserves modification times cannot leave a stale entry behind, and it is bounded by the same caps and budget as the scan itself.
- **Go live.** Register the rule, add the route matcher for `path`, and update `docs/errors/VFX-D-1210.md`. Advance `ENGINE_PIN` to the first engine release that carries the command.

### 7. Effort and staged plan

| Stage | Scope | Estimate |
|---|---|---|
| 1 (MVP) | **Engine** (9.5 days): enumeration, bounds, containment and globs (1.5 d); the pre-scan and parse harness (1.5 d); route recognisers (2.5 d); Kafka recognisers and constant folding (1.5 d); document, golden test, CLI and exit codes (1 d); adversarial and forbidden-API tests (1 d); docs (0.5 d). **MCP** (3.5 days): model, CLI-optional acquisition, caching and worker hand-off (1.5 d); route matcher and rule registration (1.5 d); docs and pin (0.5 d) | **about 13 engineer-days** (±30%, mostly in the route recognisers and the pre-scan) |
| 1b | Surface (b): `resourceRole` on `environment-error`, and the Healer's use of it | about 2 days; independent of U1 |
| 2 | C# tables (raw SQL and EF Core) and other C# clients; the MCP finding tables in `query`, plus new rule fields | 6–8 days |
| 3 | Java with Spring, Python with FastAPI, and JavaScript with Express and kafkajs, one language at a time: lexer-level recognisers, folding within a module, and a security review of each parser | 5–7 days each |
| 4 | OpenAPI and AsyncAPI ingest, as `technology: openapi` entries with a `declared` confidence | 3–4 days |
| 5 | Configuration resolution, only on demonstrated need | 4–6 days |

**Recommendation.**

- **Accept this record now.** It fixes the output contract and the route semantics, which are what the MCP rule is waiting on, at no build cost.
- **Ship 1b now.** It corrects a Healer fragment that `kafka-mtls` shows can target the wrong block.
- **Do not build stage 1 yet.** On the only corpus available, it yields one checkable topic and two paths, all in one sample. The cost is about thirteen days of work, a new frozen wire contract, and a new surface for untrusted input whose parser has measured crash and CPU hazards. Build it once a real C#-only workspace has been surveyed by the method above and shows a declaration profile like that of `orders-dotnet` (four of its five names literal).

## Consequences

- A `vouchfx topology` document becomes a public, schema-versioned machine contract, with a golden gate and additive-only evolution, like `plan --json`.
- The engine gains its first reader of the system under test's own source, which is less trusted than any suite. The no-execution guarantee, the bounds and the pre-scan are part of the contract, not implementation details.
- VFX-D-1210 stays deliberately quiet: in polyglot trees, on incomplete scans, and wherever a name is unresolved. Until stage 3, it reaches only workspaces written entirely in C#.
- The Planner's scope does not change. It still reads no source code; `topology` is a separate command with a separate contract.

## Open questions, with recommended answers

1. **Should a root with no matching files exit 0 or 2?** Recommended: **0**, with a note on stderr (§4). The MCP calls the command on arbitrary workspaces, and "no C# here" is an answer, not a typo.
2. **Should files in languages the extractor does not read suppress the rule, or only be reported?** Recommended: **suppress** (§6, condition 2). An operator whose tree holds code that is not part of the system under test, such as another language's tooling scripts or a C# utility that is not the service, excludes it with `--exclude`, which also removes it from `unanalysedSourceFiles`. Narrowing `--sources` instead leaves it counted. One finding that a Python producer would have refuted erodes trust in every other finding.
3. **Should route matching compare HTTP methods?** Recommended: **paths only at first.** The document already carries `method`, so a method-mismatch diagnostic can follow once the false-positive rate of path matching has been measured.
4. **Should `topology` read `.csproj` files, as XML and never evaluated, to limit the scan to web projects?** Recommended: **no, not in the MVP.** Over-matching is the safe direction (§2), and reading project files would widen the parsed surface for no measured gain.
5. **Where should the relational-table check in stage 2 live?** Recommended: **on the MCP side**, by conservatively identifying the single table after `FROM`, `INTO` or `UPDATE` in `query` and skipping anything else. That needs no change to the frozen language, whereas a new step field would.
6. **Should surface (b) be tracked separately from U1?** Recommended: **yes**, as a separately reviewed wire-contract change (§1): either the `resourceRole` property on `environment-error`, or the new `resource` record, as the maintainer decides. Tracked separately, U1 does not hold it back.
7. **Should a file over the depth caps be skipped, or parsed in a separate process?** Recommended: **skip it**, as `too-deep`, which makes the scan incomplete. Legitimate code does not nest that deeply, and a process per file would cost more than it saves.

## Related documents

- [Planner: coverage and gap analysis](../planner.md): the sibling read-only analysis command, and why it reads no source code.
- [Architecture Blueprint](../01_Technical_Architecture_and_Engineering_Blueprint.md): §12.1 and §16.4 (verdicts and exit codes), §14 (the event stream) and §5.6 (reserved namespaces).
- [AI Companion design](../04_AI_Companion_Feasibility_and_Design.md): §4.8 (topology inspection, still design-only) and §7.4 (stream content is data, not instructions).
- [Decision: dotnet tool packaging](dotnet-tool-packaging.md): the format this record follows.
- In vouchfx-mcp: `src/Vouchfx.Mcp/Validation/Semantics/TopologyCrossCheckRule.cs`, `docs/errors/VFX-D-1210.md` and `src/Vouchfx.Mcp/Diagnosis/SpecEditProposalBuilder.cs`.
