# Embedded MCP host (WebAssembly stdio server) - Design note and specification

> **Status:** Design note, not yet a committed design. Item 9 of the plugin review of 2026-10-06
> (see [features/open/mcp-plugin-gaps/](../mcp-plugin-gaps/spec.md) for items 1 to 8 and the
> review's provenance). The review asked for an evaluation first and an implementation after;
> this document is the evaluation, and it ends in a recommendation with a go/no-go gate (section
> 7) rather than in a promise. The implementation plan ([plan.md](./plan.md)) is phased so that
> nothing after the spike is started until the spike's numbers are in this file.
>
> Branch-only workflow when it starts: `feature/embedded-mcp`. No issue or PR unless asked.

## 1. What is being asked

The Claude Code plugin's default tier runs `node bin/embedded/fallen8-mcp.js` and expects a stdio
MCP server with the same tools and schemas as `fallen-8-mcp`, backed by the engine itself running
under the .NET WebAssembly runtime in Node, persisting to `FALLEN8_DATA_DIR`. No server to
install, no .NET SDK on the machine, one artifact for every platform Node runs on. Today only
[tools/browser-probe](../../../tools/browser-probe/BrowserProbe.csproj) exists: the engine as a
trimmed browser-wasm app, headless under node, as the trim and single-thread gate, with no MCP
and no persistence to a real disk.

## 2. What the code says today (verified at `4517ee75`)

- **The tool layer is bound to REST.** Every `IMcpTool` takes `Fallen8RestClient` (a concrete
  class) and calls REST paths directly, e.g. `_bridge.RequestVoidAsync(HttpMethod.Put, ns,
  "vertex?waitForCompletion=true", body)` in
  [MutateTool.cs](../../../fallen-8-mcp/Tools/MutateTool.cs). Argument parsing, REST call and
  result shaping are interleaved in each tool. There is no operation-level seam to implement
  twice.
- **The logic behind the REST routes lives in the apiApp's controllers,** not in the engine.
  Status assembly (`StatusREST`, including provider, ingestion and NLP probes), scan parsing,
  path-specification compilation and the one-owner-per-slot rules, subgraph registration, plugin
  registration, stored-query compilation (`StoredQueryCompiler` is in
  [apiApp/Helper](../../../fallen-8-core-apiApp/Helper/StoredQueryCompiler.cs), Roslyn-backed),
  bulk import, statistics. An engine-side implementation of "the eleven operations" that does not
  reuse this code re-implements it, which the repository's no-duplication rule forbids.
- **Three concepts the tools expose do not exist in the engine at all:**
  - **Namespaces.** The collection is `Fallen8Namespaces` in the apiApp
    ([Namespaces/Fallen8Namespaces.cs](../../../fallen-8-core-apiApp/Namespaces/Fallen8Namespaces.cs));
    the engine is one graph. `f8_overview` with no namespace, `f8_namespace`, `f8_admin
    op:activate` are apiApp behaviour.
  - **The embedding provider and chat gateway.** `Fallen8EmbeddingProvider` is apiApp-only by
    design (CLAUDE.md: "never in the engine"). `semantic.queryText`, `f8_documents` ingestion
    (chunking, embedding, Docling) and `f8_search mode:semantic` with text are unavailable
    without it; `queryVector` forms work.
  - **Dynamic code.** Roslyn is apiApp-side; the engine already has the compiler-less states
    `StoredQueryCompileState.SourceOnly` and `PluginCompileState.SourceOnly`
    ([Fallen8.Persistence.cs:539](../../../fallen-8-core/Fallen8.Persistence.cs#L539),
    [658](../../../fallen-8-core/Fallen8.Persistence.cs#L658)). Inline fragments, `register_*`
    and stored-query `register` cannot work; stored queries load but cannot run.
- **The single-threaded arms exist and are gated** by `HostCapabilities.SupportsBackgroundWork`
  ([HostCapabilities.cs:61](../../../fallen-8-core/HostCapabilities.cs#L61)), and the probe is
  the only thing that runs them. The engine on wasm is proven to construct, write, index (after
  `RegisterPluginType`, because assembly discovery finds nothing in a browser bundle), traverse,
  checkpoint and tear down.
- **Persistence on wasm writes to the emscripten in-memory file system.** The probe's
  [main.mjs](../../../tools/browser-probe/main.mjs) mounts nothing. Real-disk persistence needs
  a NODEFS mount of `FALLEN8_DATA_DIR` into the runtime's virtual file system before `Main`
  runs. The .NET runtime exposes the emscripten module for exactly this kind of hook; whether the
  engine's `FileStream` and `Directory` use survives a NODEFS mount (fsync semantics, rename
  atomicity the WAL may rely on) is a spike question, not a reading question.
- **The MCP C# SDK on browser-wasm is untested here.** `ModelContextProtocol` 1.4.1 is pinned for
  the server; its stdio transport reads `Console.In`, which the node host does not wire. Whether
  the core package trims and runs under browser-wasm at all is unknown.

## 3. Options evaluated

### 3a. Browser-wasm under Node, as the review proposes

One artifact, every platform Node supports, no .NET on the machine. Single-threaded (the engine
has that path and a gate for it). Interpreter by default, slower than JIT by an order of
magnitude on hot loops; `RunAOTCompilation` recovers most of it at the cost of a long publish
and the emscripten toolchain (the `wasm-tools` workload has it). Memory ceiling is wasm32's 4 GB
(`EmccMaximumHeapSize`), with a 2 GB default; for a developer's embedded graph that is ample,
for a production graph it is not, which is the point of the two other tiers. Stdin/stdout
pumping is JavaScript's job (`readline` over `process.stdin`, `[JSExport]` entry point taking a
JSON-RPC message and returning the reply, or an async queue for notifications). Persistence
through NODEFS. Startup time is the number to measure: the runtime boots in well under a second
when cached; the engine's load of a checkpoint adds whatever the graph costs.

### 3b. NativeAOT native binaries per platform

Threads, real file system, fast startup, JIT-class throughput. Five release assets (win-x64,
linux-x64, linux-arm64, osx-x64, osx-arm64) of 30 MB or more each; the plugin must pick one and
mark it executable; macOS quarantine and code signing are the plugin's problem. Roslyn is still
off (NativeAOT cannot host it in a useful way). Node is not needed, but the plugin's embedded
tier is defined as a Node entry point, so the plugin would have to change for this option.

### 3c. The .NET tool from item 3 as the embedded tier

`dnx fallen-8-mcp --stdio` already gives a stdio MCP server, but it bridges to a REST target;
it does not embed the engine. A `--embedded` mode of that tool, hosting the apiApp in-process on
a loopback port and bridging to itself, would reuse every controller unchanged, keep Roslyn,
namespaces, the provider and threads, and ship as one package. Its cost is the .NET SDK on the
machine (the plugin's "native" tier already assumes that) and an apiApp that today is a
deployable, not a library. This is the cheapest path to "the same thirteen tools with the same
behaviour", but it does not answer the plugin's default tier as defined, which assumes Node and
nothing else.

### 3d. What the extraction would have to be, under any option that embeds the engine

To avoid a second implementation of the controllers, the operations behind the tools must be
pulled out of the apiApp into a library both the apiApp and an embedded host reference, with
the controllers becoming thin. That library would own namespaces, status assembly, scan and path
and subgraph handling, plugin and stored-query registries, and the seams for things an embedded
host lacks (a `NullEmbeddingProvider` that reports `enabled:false`, a `NullCompiler` that yields
`SourceOnly`). That is a refactor of most of the apiApp's controllers and is by far the largest
piece of work in the review. The tool layer extraction the review names (ToolCatalog,
SchemaBuilder, the tools, ToolResults into a transport-free project over an operations interface)
is the smaller half and is only useful once the larger half exists.

## 4. Recommendation

**Do 3a, but in the order that keeps a wrong bet cheap.** The plugin's tier is defined as Node,
the review's reasoning (no install, one artifact) holds, and the engine's wasm readiness is the
one hard part already paid for. Three decisions bound it:

1. **Honest capability, not the same tool list.** The embedded host advertises the tools whose
   operations it can perform and omits the rest; `f8_overview` on the embedded host reports
   `host: "embedded"`, `codeExecution: "unavailable"`, `embeddingEnabled: false` (already a
   field), `namespaces: 1`, and the stored queries it loaded as `SourceOnly`. The schemas of the
   tools it does advertise are byte-identical to `fallen-8-mcp`'s because they come from the same
   library (section 5). A tool that is advertised and then answers "not available here" would be
   the dishonest shape; absence plus a stated reason in the overview is the honest one. The
   plugin author's "same eleven tools" is therefore not met literally, and section 8 says so.
2. **The extraction comes first and is its own value.** The operations library (3d) makes the
   apiApp's controllers thin, which the repository has wanted for other reasons (the
   consolidation audit's findings about logic in controllers), and the tool-layer library makes
   `fallen-8-mcp` testable without HTTP. Both land in `fallen-8-core.sln` under the normal gates
   and are useful even if the wasm host is later abandoned.
3. **The wasm host is gated by a spike with numbers.** Section 7 lists what must be measured
   and the thresholds; the host is not started until they are in this file. If the spike fails,
   option 3c is the fallback and the plugin author is told so with the numbers.

## 5. Target shape, if the spike passes

```
fallen-8-operations/          IFallen8Operations + the implementation over the engine
                              (moved out of apiApp controllers); namespaces collection;
                              Null provider / Null compiler seams. References fallen-8-core only.
fallen-8-mcp-tools/           ToolCatalog, SchemaBuilder, the IMcpTool classes, ToolResults,
                              error normalisation, over IFallen8Operations. References the MCP
                              SDK's protocol types only (no transport, no ASP.NET, no engine).
fallen-8-mcp/                 unchanged role: the REST deployable. Implements IFallen8Operations
                              over REST (the current Fallen8RestClient + DTOs become that adapter).
fallen-8-core-apiApp/         controllers call fallen-8-operations; the REST contract is unchanged
                              (OpenAPI snapshot identical, which is the gate for the refactor).
tools/embedded-mcp/           browser-wasm host: fallen-8-operations over the engine in-process,
                              fallen-8-mcp-tools for the surface, main.mjs pumps JSON-RPC over
                              stdio and mounts FALLEN8_DATA_DIR; PublishTrimmed, TrimMode=full.
                              NOT in fallen-8-core.sln (wasm-tools workload), own CI job.
```

- **Protocol handling in the wasm host:** first try the SDK's `McpServer` with a custom
  `ITransport` fed by the JavaScript pump; if the SDK does not survive trimming on
  browser-wasm, a minimal JSON-RPC handler for `initialize`, `ping`, `notifications/initialized`,
  `tools/list`, `tools/call` at protocol revision `2025-06-18`, kept in `tools/embedded-mcp`
  and pinned by a test that drives both hosts with the same transcript and diffs the replies.
- **Persistence:** `FALLEN8_DATA_DIR` mounted via NODEFS at `/data`; WAL and checkpoints under
  it; a missing variable means in-memory only, said once on stderr at start. The engine's
  durability lifecycle (recovery on open, checkpoint on a cadence and at shutdown) runs as it
  does in the apiApp, through the operations library's lifecycle hooks, so there is one
  lifecycle, not two.
- **Stored queries and plugins** load as `SourceOnly`; `f8_overview` reports the count and the
  state; `f8_paths storedQuery:<name>` answers the engine's own refusal for a non-compiled entry.
- **Release asset:** `fallen8-mcp-embedded.zip` with `fallen8-mcp.js` (the pump plus the
  runtime bootstrap) and `_framework/`; its layout documented on the docs page; the plugin
  downloads and unpacks it. Published by `release.yml` as a GitHub release asset, not to npm (the
  plugin owns its npm surface).
- **Gates:** the operations refactor is pinned by the unchanged OpenAPI snapshot and the full
  unit suite; the tool-layer extraction by `McpToolSurfaceTest` and friends running against a
  fake `IFallen8Operations`; the wasm host by a node-driven transcript test in its own CI job
  (initialize, list, create, search, path, checkpoint, restart, read back), and by the existing
  browser probe continuing to pass (the host must not weaken what the probe guards). A
  convention test pins that `fallen-8-operations` and `fallen-8-mcp-tools` reference neither
  ASP.NET nor the apiApp, in the family of the existing REST-only tests.

## 6. Impact on existing features

- **Architecture diagrams (both):** a new deployable and a new channel (agent, through the
  plugin, to an embedded engine with no REST). Both diagrams change in the same PR as the host;
  the operations and tool-layer libraries change the layer picture in `architecture.md` too.
- **CLAUDE.md:** the "three projects are the database itself" sentence and the deployables list
  change; the REST-only rule gains the operations library as the one thing an embedded host may
  reference.
- **fallen-8-mcp:** behaviour unchanged, structure changed (adapter over the operations
  interface). Every existing MCP test must pass unchanged; that is the refactor's acceptance.
- **apiApp:** controllers become thin; the OpenAPI snapshot must not change by a byte.
- **browser-probe:** stays as the trim gate; the embedded host adds a second wasm consumer of
  the engine, so a trim warning in either fails CI.
- **Studio, NL-assist, integrations, agents:** none.
- **The plugin (external):** the tool set differs by host; the overview says which host it is.

## 7. Go/no-go gate (the spike)

A throwaway in the scratchpad, never committed, that measures on the current engine and
runtime, with the numbers recorded in this section before any phase after the spike starts:

| Measurement | Threshold to proceed |
|---|---|
| Cold start of the runtime plus an empty engine under node, to first `tools/list` reply | under 3 s on a developer laptop |
| Load of a checkpoint with 100k vertices and 500k edges from NODEFS | under 20 s; memory under 1.5 GB |
| `f8_search mode:property` and a 6-hop BLS path on that graph | within 10x of the JIT numbers from `fallen-8-bench` without AOT, within 3x with AOT |
| WAL write, checkpoint, process kill, restart, read back | identical graph; no partial checkpoint accepted |
| MCP SDK core on browser-wasm, trimmed | runs, or the minimal handler fallback is chosen with the reason written here |
| Trim warnings from the engine plus SDK | zero, as the probe requires today |

Failing the first, second or fourth row means option 3c instead, and this note's status line
changes to say so with the numbers. Failing the third means AOT is mandatory and the publish
time is recorded. Failing the fifth means the minimal handler.

## 8. Handoff to the plugin author

- The embedded tier will not advertise the same tool list as the REST tiers; `f8_overview`
  reports `host` and the reasons. The plugin should read the list from `tools/list` rather than
  hard-coding it, which also absorbs the thirteen-tool change from items 4 and 6.
- The release asset name and layout will be fixed in section 5 once the spike passes; until then
  the plugin's embedded tier has nothing to download, and the honest default is the native tier
  (`dnx fallen-8-mcp --stdio`) once that package ships.
