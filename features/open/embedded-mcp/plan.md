# Embedded MCP host - Implementation plan

The [design note](./spec.md) is the contract, and its section 7 is a gate: phases 1 to 4 do not
start until phase 0's numbers are written into the note. Phases 1 and 2 are valuable on their own
and land through the normal gates in `fallen-8-core.sln`; phases 3 and 4 are the wasm host and
live outside the solution like the browser probe. Depends on
[mcp-plugin-gaps](../../done/mcp-plugin-gaps/plan.md) landing first: the tool layer extracted in phase 2
must be the thirteen-tool surface, not the eleven-tool one.

Same working rule as every feature here: failing test first, fix, green, mutation check, diff
against HEAD. For the refactor phases the "test" is the whole existing suite plus a byte-identical
OpenAPI snapshot, so the mutation check is "move one piece of logic back into a controller and
watch the convention test fail", not a per-line revert.

## Phase 0 - the spike (gate)

Scratchpad only; nothing is committed except the numbers.

- [ ] Copy the browser probe, add a `[JSExport]` entry point that takes a JSON-RPC string and
      returns a reply string, and a `main.mjs` that pumps `process.stdin` lines through it.
      Measure cold start to the first `tools/list` reply (a hand-written reply is enough here).
- [ ] Mount a scratch directory via NODEFS before `Main`; write a WAL and a checkpoint from the
      engine; kill the process mid-write; restart; read back. Record the behaviour of the
      engine's rename and fsync under NODEFS.
- [ ] Generate a 100k/500k graph with `fallen-8-bench`'s generator, checkpoint it, load it under
      wasm; record load time and peak memory (`process.memoryUsage()`).
- [ ] Run a property search and a 6-hop path; record against the JIT numbers; repeat with
      `RunAOTCompilation=true` and record the publish time.
- [ ] Reference `ModelContextProtocol` (core) from the spike, publish trimmed, record warnings
      and whether a server can be constructed with a custom transport.
- [ ] Write the six numbers into the design note's section 7 and decide. If no-go, change the
      note's status line to say so and stop here.

## Phase 1 - the operations library (`fallen-8-operations`)

Files: new `fallen-8-operations/` project; `fallen-8-core-apiApp/Controllers/*.cs` (thinned);
`fallen-8-core-apiApp/Namespaces/*` (moved); new convention test in `CodeQualityTest`; the
OpenAPI snapshot as the acceptance gate.

- [ ] Define `IFallen8Operations` from the thirteen tools' needs, operation by operation, with
      the REST DTO types moved alongside it where they are the natural contract (status,
      scan, path, subgraph, plugin, stored query, index, namespace, admin). Record in the note
      which types moved and which stayed REST-only.
- [ ] Move the namespaces collection, status assembly, scan handling, path and subgraph handling,
      index lifecycle, plugin and stored-query registries, savegame and admin operations into the
      library, one controller at a time; each controller becomes a thin HTTP shell. After each
      move: full suite green, OpenAPI snapshot unchanged (the script prints a diff; it must be
      empty).
- [ ] Seams for what an embedded host lacks: `IEmbeddingProvider` with a null implementation
      reporting `enabled:false`; `IStoredQueryCompiler` and `IPluginCompiler` already exist in
      the engine and have `SourceOnly` behaviour; the Docling and NLP clients stay apiApp-only
      behind an `IIngestion` seam whose null implementation reports `enabled:false`.
- [ ] Convention test: `fallen-8-operations` references neither ASP.NET packages nor the apiApp,
      in the family of the existing REST-only tests; `CodeQualityTest` project list includes it.
- [ ] Mutation check: move one operation back into a controller; the convention test that pins
      "controllers hold no logic beyond binding and status mapping" fails. That test needs
      defining in this phase (a size or a dependency rule; decide and record).
- [ ] CLAUDE.md and the architecture page updated for the new layer.

## Phase 2 - the tool-layer library (`fallen-8-mcp-tools`)

Files: new `fallen-8-mcp-tools/` project; `fallen-8-mcp/Tools/*` (moved); `fallen-8-mcp/Bridge/*`
becomes the REST adapter implementing `IFallen8Operations`; the MCP unit tests retargeted.

- [ ] Move `ToolCatalog`, `SchemaBuilder`, `ToolResults`, the thirteen tools and the error
      normalisation into the library; replace every `Fallen8RestClient` call with an
      `IFallen8Operations` call. The library references the MCP SDK's protocol types only.
- [ ] `fallen-8-mcp`: `Fallen8RestClient` plus the DTOs become `RestOperations :
      IFallen8Operations`; the three-rule error mapping stays there (it is REST-specific).
- [ ] Every existing MCP test passes unchanged against the REST deployable (acceptance). A new
      `McpToolsOverFakeOperationsTest` drives the catalog against an in-memory fake, which is the
      test the review's "transport-free" asks for.
- [ ] `McpRestCoverageTest` keeps governing `fallen-8-mcp` (REST to MCP); it is unaffected by the
      embedded host, which has no REST. Record that in the test's header.
- [ ] Convention test: `fallen-8-mcp-tools` references neither ASP.NET, the engine, nor the
      apiApp.

## Phase 3 - the wasm host (`tools/embedded-mcp`)

Files: new `tools/embedded-mcp/EmbeddedMcp.csproj`, `Program.cs`, `main.mjs`; a node-driven
transcript test under `tools/embedded-mcp/test/`; `.github/workflows/buildAndTest.yml` (a job
next to `browser`).

- [ ] Project: browser-wasm, `PublishTrimmed`, `TrimMode=full`, `AllowUnsafeBlocks`, references
      `fallen-8-core`, `fallen-8-operations`, `fallen-8-mcp-tools`. `RegisterPluginType` for the
      shipped index types at start (discovery finds nothing in a bundle; the probe documents why).
- [ ] `main.mjs`: NODEFS mount of `FALLEN8_DATA_DIR` (or in-memory with one stderr line),
      stdin line pump into the exported entry point, replies and notifications to stdout, nothing
      else on stdout ever (a stray `console.log` breaks the protocol; the probe's convention that
      the host may write to the console does NOT carry over, and a test greps the bundle for it).
- [ ] Protocol: the SDK with a custom transport if phase 0 said so, else the minimal handler.
- [ ] `f8_overview` on this host reports `host:"embedded"`, `codeExecution:"unavailable"`,
      `embeddingEnabled:false`, the `SourceOnly` stored-query and plugin counts.
- [ ] Transcript test: initialize, tools/list (exact set), create vertices, index create and
      search, path, checkpoint, process restart, read back, stored query listed as `SourceOnly`;
      the same transcript against `fallen-8-mcp` over REST diffs only in the fields the note
      says differ (`host`, the tool set).
- [ ] CI job `embedded`: `dotnet workload install wasm-tools`, publish, run the transcript test;
      the existing `browser` job stays.
- [ ] Mutation check: remove the NODEFS mount; the restart step of the transcript test fails.

## Phase 4 - release asset and docs

Files: `.github/workflows/release.yml`, `docs/src/content/docs/mcp-server.md` (or a new
`embedded-mcp.md` page registered in the sidebar), `README.md` key features, both architecture
diagrams, `features/done/mcp-server/README.md`.

- [ ] `release.yml`: publish `fallen8-mcp-embedded.zip` (`fallen8-mcp.js` + `_framework/`) as a
      release asset; layout documented.
- [ ] Docs page: what the embedded host is, what it cannot do and why (one home for that
      explanation; the overview's fields point here), `FALLEN8_DATA_DIR`, the asset layout, the
      plugin's install lines.
- [ ] Both architecture diagrams gain the embedded host and the two libraries, in the fixed
      style (dark surfaces, brand red accent).
- [ ] Hard-rule sweep (Python): no em or en dash, none of the forbidden external names, MIT
      headers, no `Console.Write*` in product code (the wasm host's `main.mjs` is JavaScript and
      its stdout rule is the protocol's, pinned by its own test).
- [ ] Handoff list for the plugin author written into the note's section 8 with the final asset
      name and the tool set.

## When it lands

Move to `features/done/embedded-mcp/`; status line updated with the spike numbers kept in
section 7 as the record of why the design is what it is.
