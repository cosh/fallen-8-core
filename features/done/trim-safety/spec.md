# Trim safety

**Status:** implemented on `feature/trim-safety`. A consumer can publish the engine FULLY TRIMMED
(`PublishTrimmed=true`, `TrimMode=full`) with no `TrimmerRootAssembly`, and construct, write, read,
traverse and extract a subgraph without a runtime failure. Measured once on a browser-wasm consumer
(a point-in-time measurement of a probe app, not a guarantee): `fallen-8-core.wasm` 412 KB -> 166 KB, all
managed assemblies 3.19 MB -> 2.02 MB, gzipped bundle 3.62 MB -> 3.14 MB.

## Why

The browser playground published trimmed and then failed at runtime:

```
System.MissingMethodException: Arg_NoDefCTor,
NoSQL.GraphDB.Core.Algorithms.Path.BidirectionalLevelSynchronousSSSP
```

The only workaround was `<TrimmerRootAssembly Include="fallen-8-core" />`, which keeps the WHOLE engine
(and, transitively, its dependencies) and cost more than a megabyte of payload. The engine also emitted
`IL2104` ("assembly produced trim warnings"), which is the trimmer telling the consumer it cannot
reason about this assembly.

## The two classes of problem, and why only ONE of them is fixable

| | What it does | Fixable? |
| --- | --- | --- |
| **A type parameter, constructed reflectively** - `TryCalculateShortestPath<T>` and `SubGraphFactory.TryCreateSubGraphFromSource<T>` do `Activator.CreateInstance(typeof(T))` | The type IS statically known at every call site | **Yes.** Annotate `T` with `[DynamicallyAccessedMembers(PublicParameterlessConstructor)]` and the trimmer keeps the constructor of whatever type a caller substitutes |
| **A type named by a STRING** - plugin discovery (`PluginFactory`: enumerate DLLs, `Assembly.Load`, `GetExportedTypes`, activate a name->type match), the payload codec's `Type.GetType`, delegate JSON | The type exists only as a string until it is read | **No.** No annotation can preserve it. It can only be DECLARED, so a trimming consumer is warned at its own call site |

That is the answer to "why was this only about shortest path": those two `Activator` sites are the only
places the engine reflectively constructs a type parameter. Everything else resolves a name.

## What was done

### 1. The IL2087 fix (the actual runtime bug)

`[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]` on the
type parameter of `TryCalculateShortestPath<T>` and `TryCreateSubGraph<T>` /
`TryCreateSubGraphFromSource<T>`. The annotation does NOT flow along an override chain, so it is
repeated on every declaration: `IFallen8Read`, `AFallen8`, `Fallen8`, `SubGraphFactory` and the apiApp's
`AddressedFallen8` forwarder. No `new()` constraint was added, so nothing breaks for an implementer or
caller whose `T` lacks a public parameterless constructor.

### 2. Honest declarations for what cannot be fixed (`[RequiresUnreferencedCode]`)

Plugin discovery (`PluginFactory`, and the string-named `TryCalculateShortestPath` / `TryRunAnalytics`
on the interface, the abstract base, the engine and the forwarder; `IndexFactory` /`ServiceFactory` /
`SubGraphFactory` name-based entry points), the payload codec (`SerializationReader`/`Writer` members
that resolve or activate a payload-named type), `DelegateJson`, `RTree.CreateConfiguredInstance`, and - at
the TYPE level, so the warning lands where a consumer constructs them - `SaveTransaction`,
`LoadTransaction`, `CreateSubGraphTransaction`.

Each family shares ONE message constant (`PluginFactory.DiscoveryIsNotTrimSafe`,
`SerializationReader.PayloadTypesAreNotTrimSafe`,
`DelegateJson.DelegateReconstructionIsNotTrimSafe`,
`LoadTransaction.RequiresReflectiveCheckpoint`), which is also where the explanation lives.

### 3. Suppressions, at boundaries where propagating would have been a lie

`[RequiresUnreferencedCode]` propagates to callers, and several paths funnel through code that is
genuinely trim-safe. Propagating would have annotated `EnqueueTransaction` and every engine
constructor - telling a browser consumer that creating a vertex is unsafe, which is false. Each
suppression is per-MEMBER (never on a type, so the next reflective thing added to the same class still
fails the build) and names what actually degrades:

| Site | Why suppressed, and what degrades if trimmed |
| --- | --- |
| `WalTransactionCodec`, six property helpers | WAL frames store property VALUES through the object codec, and the codec is called by the commit path and by replay during construction. The requirement is REAL and is declared one level out, on `Fallen8.EnableWriteAheadLog` and the WAL constructor - see "The defect the review caught" below |
| `ABucketIndex.Save`/`Load`, `SingleValueIndex.Save`/`Load` | An index KEY is an arbitrary property value; same codec. The ENGINE reaches these only through the annotated checkpoint path (they are public members of a public contract, so a consumer calling `index.Save(writer)` directly is not warned) |
| `RTree.Load` | The metric/dimension types are named in the checkpoint. Any failure to rebuild them - `InvalidDataException` when the type is gone, `MissingMethodException` when only its constructor is - is caught per index by `PersistencyFactory`, which skips that one index: a degraded load, not a crash |
| `Fallen8.ReplaySubGraphCreateSuppressed` | A subgraph WAL entry whose algorithm cannot be resolved is warned and skipped (the documented D4 skip-and-continue), and a subgraph is rebuildable derived state |
| `SerializationReader.ReadNullablePrimitive`, `ReadStringArray`, `ReadOptimizedStringArray` | The value read back is one this writer encoded directly (a primitive, a framework struct, a string), a path that resolves no type. Another encoding here is corruption, which already throws on an untrimmed build |

### 4. The defect the review caught - the write-ahead log

Three independent reviews were run on this change. One reproduced a defect in the first version of it
that is worth recording, because the fix is the most interesting part of the feature.

`PublishTrimmed=true` sets `System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault=false`
(confirmed in the publish's `runtimeconfig.json`). The object codec's fallback for a value it does not
encode directly is `JsonSerializer.Serialize(value)` with no options - so in a trimmed application it
throws **deterministically, for every such value**, not rarely and not only for exotic types. The first
version of this change suppressed that warning at the WAL codec with a justification claiming the app's
own code would keep the type alive. That was wrong twice over: reflection-based JSON is off entirely, so
preserving the type does not help, and the suppression removed the only signal a consumer would ever get.

Measured on a trimmed self-contained app (`TrimMode=full`), two committed vertices, one carrying a nested
value:

```
plain    : state=Finished durable=True
complex  : state=Finished durable=False error=none
in-memory vertices=2   ->  restart  ->  WAL-recovered vertices=1
```

The engine behaves per its documented degraded-durability contract (`Finished` + `Durable=false`), but a
caller that only checks `WaitUntilFinished()` sees success, and **nothing warned at build time**.

The fix keeps the commit path unannotated - that judgement was right - and declares the requirement at
the durability OPT-IN instead: `Fallen8.EnableWriteAheadLog` and the public WAL constructor carry
`[RequiresUnreferencedCode(Fallen8.WriteAheadLogNeedsReflectionForRichValues)]`, whose message states
exactly what happens and to which values. Verified: a trimmed consumer now gets IL2026 at every
`new Fallen8(loggerFactory, new WriteAheadLogOptions(path))`, while the browser consumer - which opens no
log - still publishes with zero warnings.

The read side was also fixed rather than declared: `ReadOtherTypeObject` now uses
`JsonDocument.Parse(json).RootElement.Clone()` instead of `JsonSerializer.Deserialize<object>(json)`.
Same result (that overload returns a boxed `JsonElement` for this input), but not reflective, so a
trimmed application can still READ rich values out of a checkpoint or log - and one trim requirement
disappeared instead of being annotated.

### 5. `PluginRegistry`, which the same review flagged as a prerequisite

`PluginRegistry.TryActivate` reflectively activates `PluginEntry.Artifact` and carried a pre-existing
suppression justified with "trimming is disabled for this application" - a sentence this change makes
false by declaring `IsTrimmable`. `PluginEntry.Artifact` and its constructor parameter are now annotated
`[DynamicallyAccessedMembers(PublicParameterlessConstructor)]` and the suppression is DELETED, so the
requirement flows from whoever supplies the type. For a runtime-compiled artifact this costs nothing; it
is also the seam the follow-up below would build on.

### 6. The gate

`<IsTrimmable>true</IsTrimmable>` on `fallen-8-core`: it declares the assembly trim-ready to consumers
AND keeps the trim analyzer on for every build. With warnings-as-errors, new reflection must now either
be statically analyzable or declare itself. The engine builds with **zero** trim warnings, and a
trimmed publish emits no `IL2104`.

`fallen-8-core-apiApp` keeps its analyzer but stops treating IL2026 as an error, with the reasoning in
the csproj: the service exists to expose discovery, string-named plugins and checkpoints over REST and is
published untrimmed, so those warnings carry no information there - while every other trim diagnostic (an
annotation mismatch, an unannotated reflective construction) stays an error. It is
`WarningsNotAsErrors`, not `NoWarn`, so the diagnostics stay VISIBLE in the build output instead of being
erased. No count is quoted: two reviewers measured it independently and got different numbers (27 distinct
call sites, 54 warnings), which is exactly why a number in a comment is a liability.

## Size of the change

114 `[RequiresUnreferencedCode]` sites, 15 per-member suppressions (one pre-existing suppression
DELETED, in `PluginRegistry`), 10 `[DynamicallyAccessedMembers]` annotations, across 33 files. Most of
the `RequiresUnreferencedCode` count is the payload codec's own internals: `SerializationReader`/`Writer`
members call each other, so the requirement has to be repeated along those chains. A reviewer proposed
collapsing over half of them by splitting a trim-safe primitive core out of `ProcessObject` and having
the typed readers route through it. That is a real improvement and a real refactor of a vendored
serializer, so it is recorded as a follow-up rather than smuggled into this change.

## Verified

- Fully trimmed browser-wasm consumer, `TrimMode=full`, **no** `TrimmerRootAssembly`: construct ->
  write vertices/edges -> read back (counts, properties, adjacency) -> `TryCalculateShortestPath<T>` finds
  the 3-hop path -> `TryCreateSubGraph<T>` extracts a subgraph -> clean rollback reports `NotFound`. No
  `MissingMethodException`. (Throwaway `wasmconsole` probe, scratchpad, not committed.)
- A browser-wasm publish emits no trim warnings at all (previously `IL2104`). A trimmed DESKTOP consumer
  that opens a write-ahead log now gets exactly one, at the constructor where it opted in.
- `dotnet test fallen-8-core.sln`: **1801 passed, 0 failed**, 30 skipped (the opt-in benchmarks).
- `fallen-8-unittest/TrimSafetyTest.cs` pins the annotations. Mutation-checked: removing the annotation
  from one declaration fails the build as IL2095, from all of them as IL2087 - so the tests deliberately
  cover only what the build cannot see (a suppression used instead of an annotation, the gate switched
  off, the write path wrongly marked, a forwarder's message drifting from the engine's).
- `PathTest` now pins the `GetEnumerator` behaviour the new doc comment describes (one vertex per hop,
  source omitted) - it had no test at all before.
- `CodeQualityTest` pins the apiApp's IL2026 suppression to `PublishTrimmed=false`, so flipping the
  service to a trimmed publish cannot silently lose every diagnostic.
- Docs site builds, all internal links valid.

## Review

Three independent reviews were run before merge (trim/AOT correctness, API and architecture, claims and
conventions). They found: the critical WAL defect above (with a reproduction), the `PluginRegistry`
prerequisite, a destroyed doc comment, an encoding corruption plus BOM churn from a scripted edit pass,
several false claims in comments and justifications, two wrong counts, and the type-level suppression
that has since been split into six per-member ones. Everything in this document that reads as a
correction is one of theirs. What they explicitly endorsed and asked NOT to change: `IsTrimmable` as the
gate, the DAM annotations on the typed overloads, type-level `RequiresUnreferencedCode` on the three
transactions, the decision to keep `EnqueueTransaction` unannotated, and scoping the apiApp's IL2026
relaxation to the untrimmed service only.

## Known gap (NOT fixed here) - the name-based surface on a trimmed host

The trim-SAFE way in is a typed overload, and one exists only for path finding and subgraphs. Analytics
(`TryRunAnalytics`), index creation (`IndexFactory.TryCreateIndex`), services and graph functions are
name-only, so on a trimmed host they now warn at the call site and degrade at runtime.

Worth knowing before anyone "fixes" this with more typed overloads: in browser-wasm the name-based
families are dead **regardless of trimming**, because discovery enumerates `*.dll` under
`AppContext.BaseDirectory` and a browser has none there. And since `IndexFactory.TryCreateIndex` is the
ONLY way to create an index, a browser consumer cannot create an index at all today, trimmed or not. This
change documents that hole; it neither created nor closed it.

Recommended shape for closing it, from the architecture review, and it is NOT typed overloads: typed
overloads fit only the *stateless algorithm selected per call* shape (path, subgraph, analytics).
Indexes and services are NAMED, long-lived instances that are referenced by string afterwards and
rehydrated from a checkpoint by plugin name, so a `TryCreateIndex<T>` would add API without closing the
hole - and it would spread the `IL2091` virality of annotated generic parameters across three more
families. Instead, let the HOST register plugin TYPES:

`Fallen8.Plugins` (`PluginRegistry`) is already consulted BEFORE `PluginFactory` in
`Fallen8.ResolveCachedPlugin`, but today it holds only runtime-COMPILED plugins, which needs Roslyn and so
cannot work in a browser. Adding a compile-free registration that carries a `Type` - now that
`PluginEntry.Artifact` is annotated (see section 5), the requirement flows from the host's `typeof(MyAlgo)`
straight to the `Activator`, with no suppression anywhere - makes name-based lookup work in a trimmed app
for exactly what the host registered, across ALL families at once, with no scanning. It needs one
deliberate decision first: `PluginDefinition.SourceCode` is the persisted identity today, so a
host-provided entry needs a "not persisted" state of its own. That is its own feature.

## Impact on existing features

| Area | Impact |
| --- | --- |
| Public API | Binary-compatible everywhere (attributes only; a DAM annotation on a generic parameter does not change a signature). SOURCE-breaking for a consumer who runs the trim analyzer, which is why the versioning record in `fallen-8-core.csproj` gains an entry - the same class of change as `0.2.0 -> 0.3.0`, which was a minor bump |
| Third-party `IFallen8Read` implementers | Must repeat the annotations on the two string-named members and the typed overload IF they build with the trim analyzer on (measured: IL2046 on the implementation, IL2095 on a mismatched generic parameter); otherwise unaffected |
| Consumers with their own generic wrappers | A wrapper that forwards `T` into the typed overload must annotate its own type parameter (IL2091). This is the largest practical source-compat cost and it propagates up their call chain |
| `TrimMode=partial` consumers | Behaviour DOES change: `IsTrimmable` means the engine now gets member-trimmed in partial mode too, where before it was left whole. String-named plugin calls that worked can start returning `false` - warned at publish, not silent |
| Reading rich property values in a trimmed app | Now WORKS (the read side stopped using reflective JSON). Writing them to a log still cannot, and is declared |
| REST contract, OpenAPI snapshot, MCP | None - no controller, route or XML doc changed |
| apiApp | `AddressedFallen8` forwarders annotated (required to match the interface); `NoWarn IL2026` added with reasoning |
| Persistence, WAL, change feed, indices | No behaviour change; the reflective paths are declared or suppressed, never altered |
| Studio UI, NL-assist | None |
| Docs | `library.mdx` gains a "Publishing trimmed" section; the README library entry mentions it |

## Out of scope

Roslyn in the browser, browser persistence, AOT (`PublishAot` / IL3050-class diagnostics), and the
inline transaction execution mode.
