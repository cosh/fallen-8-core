# Plan - trim safety

Phases as executed. See [spec.md](spec.md) for the contract and the measurements.

## Phase 0 - Work from the real warning list, not a guess

- [x] Turn the trim analyzer on temporarily
      (`dotnet build fallen-8-core/fallen-8-core.csproj -t:Rebuild -p:EnableTrimAnalyzer=true
      -p:TreatWarningsAsErrors=false`) and enumerate every diagnostic: 24 unique warnings -
      4x IL2087 (the runtime bug), the rest IL2026/2057/2065/2067/2070/2072/2075 across plugin
      discovery, the serializer, delegate JSON and the R-Tree.

## Phase 1 - Fix the two IL2087 sites (the actual bug)

- [x] `[DynamicallyAccessedMembers(PublicParameterlessConstructor)]` on the type parameter of
      `TryCalculateShortestPath<T>` in `IFallen8Read`, `AFallen8`, `Fallen8`, and of
      `TryCreateSubGraph<T>` / `TryCreateSubGraphFromSource<T>` in `SubGraphFactory`, plus the apiApp's
      `AddressedFallen8` forwarder. No `new()` constraint (that would be a breaking change on interface
      members).
- [x] Fix the false doc comment that called the typed overload reflection-free: it still calls
      `Activator.CreateInstance`; what it avoids is the NAME lookup, and the annotation is what makes it
      trim-safe.

## Phase 2 - Declare what cannot be fixed

- [x] One message constant per family, which is also where the explanation lives:
      `PluginFactory.DiscoveryIsNotTrimSafe`, `SerializationReader.PayloadTypesAreNotTrimSafe`,
      `SerializationWriter.PayloadTypesAreNotTrimSafe`, `DelegateJson.DelegateReconstructionIsNotTrimSafe`,
      `LoadTransaction.RequiresReflectiveCheckpoint`.
- [x] `[RequiresUnreferencedCode]` on the reflective members, then walked UP the call chain
      (the analyzer names each next caller) until every chain ended at a consumer-visible boundary:
      the string-named engine overloads, the name-based factory entry points, the checkpoint
      entry points, and the three transactions annotated at the TYPE.

## Phase 3 - Stop the cascade where propagating would be false

- [x] Per-MEMBER `[UnconditionalSuppressMessage]` (15 in total), each naming what degrades: the WAL
      codec's six property helpers, `ABucketIndex`/`SingleValueIndex` `Save`+`Load` (sharing one const in
      `IIndex`), `RTree.Load`, the reader's primitive/string seams, and a one-line
      `ReplaySubGraphCreateSuppressed` so the suppression covers exactly that call and nothing else in
      the replay loop. Without these, `EnqueueTransaction` and every engine constructor would have been
      marked trim-unsafe.
- [x] Never on a TYPE: the first version put one suppression on the whole `WalTransactionCodec` class,
      which would have silently swallowed the next reflective thing added there. A reviewer insisted, and
      was right.

## Phase 4 - Make it a gate

- [x] `<IsTrimmable>true</IsTrimmable>` on `fallen-8-core` (analyzer on for every build; warnings are
      errors). Engine: zero trim warnings.
- [x] `<NoWarn>IL2026</NoWarn>` on the apiApp ONLY, with the reasoning in the csproj; its
      `AddressedFallen8` forwarders annotated to match the interface (IL2046 caught this, as intended).

## Phase 5 - Prove it end to end

- [x] Fully trimmed browser-wasm consumer (`TrimMode=full`, no `TrimmerRootAssembly`): all checks pass,
      including `TryCalculateShortestPath<T>` and `TryCreateSubGraph<T>`.
- [x] Measure: engine 412 KB -> 166 KB, managed total 3.19 MB -> 2.02 MB, gzipped bundle 3.62 MB -> 3.14 MB.
- [x] No `IL2104` on publish.

## Phase 6 - Pin it

- [x] `fallen-8-unittest/TrimSafetyTest.cs` (9 tests): the annotations on every declaration, every
      `IFallen8Read` implementation in the product assemblies, the honest-declaration surfaces, the
      transactions, `IsTrimmable`, and - the other half of the contract - that the ordinary write path
      is NOT marked trim-unsafe.
- [x] Mutation-check the pins, and correct the test's own doc comment once the mutation showed the
      analyzer already catches single-site removal (IL2095).
- [x] Full suite green (1789 passed).

## Phase 6b - Review, and the repairs it forced

- [x] Three independent reviews (trim/AOT correctness, API and architecture, claims and conventions).
- [x] CRITICAL: the WAL's silent durability loss in a trimmed app, reproduced locally before fixing
      (2 committed vertices, 1 recovered). Type-level codec suppression -> six per-member ones; the
      requirement declared at the durability opt-in (`EnableWriteAheadLog` + the WAL constructor) with a
      message that states what happens; verified the trimmed consumer now gets that warning and the
      browser consumer still gets none.
- [x] `ReadOtherTypeObject` switched from `JsonSerializer.Deserialize<object>` to `JsonDocument.Parse`:
      same result, not reflective, so a trimmed app can read rich values - one requirement removed
      rather than declared.
- [x] `PluginEntry.Artifact` annotated and `PluginRegistry`'s stale suppression (justified with
      "trimming is disabled for this application", which this change falsified) deleted.
- [x] Restored the `ReplaySubGraphCreate` doc comment the new seam had displaced, and collapsed its
      four-way narration to one home.
- [x] Corrected false claims: `PluginFactory`'s "never a crash inside reflection" (discovery's guards are
      narrow and callers mostly do not catch), the reader seam's "already yields null", the docs site's
      "the call returns false", and the same claim on `IFallen8Read`.
- [x] Write-path members now carry the WRITER's message; the index/service load members carry the
      DISCOVERY message. The four duplicated index justifications became one const in `IIndex`.
      `PluginFactory.DiscoveryIsNotTrimSafe` made public so the apiApp forwarder shares the exact string
      instead of a paraphrase, pinned by a test.
- [x] apiApp: `NoWarn` -> `WarningsNotAsErrors` (diagnostics stay visible), wrong count removed.
- [x] Repaired my own scripted-edit damage: the mojibake at `SubGraphFactory.cs:146` and BOM churn:
      every changed file's encoding now matches `main` byte-for-byte (verified both directions; the first
      blanket fix had over-corrected and stripped 13 BOMs that were always there).
- [x] Annotated the two test doubles so enabling the analyzer in the test project is not a wall of errors.
- [x] Versioning record entry in `fallen-8-core.csproj` for the source-breaking-under-analyzer change.

## Phase 7 - Docs

- [x] `docs/src/content/docs/library.mdx`: a "Publishing trimmed" section (what is trim-safe, what
      warns, the payload numbers). README library entry mentions trimming.
- [x] `Algorithms/Path/Path.cs`: document that `GetEnumerator` yields one vertex PER HOP and omits the
      source (surprising, but a behaviour change would break callers).
