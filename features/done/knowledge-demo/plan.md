# Knowledge demo: implementation plan

Phases are ordered so the risky, externally-dependent work lands first and each phase leaves the
tree green. See [spec.md](spec.md) for the contract and the verified constraints.

## Phase 0: de-risking (DONE before speccing)

Proven live against the compose environment, recorded in the spec under "Verified up front":
the binding creates all three roles, markdown / PDF-with-picture / XLSX all reach `indexed`,
identifier extraction is exact, one chunk's `mentions` edges reach both domain vertices and NER
entities, fused search hits on both the dense and lexical sides, and a `DictionaryIndex` does not
backfill. Nothing below rests on an unverified assumption about the pipeline.

## Phase 1: the documents

Author the three assets under `samples/documents/` plus `generate-documents.py` (provenance, run
by hand) and a short `README.md` explaining why they are committed rather than built.

- Sections sized above the 800-character merge floor so each document yields a real chunk chain.
- The RCA's figure meaning duplicated in body prose, because captions are dropped.
- Asset tags spelled exactly as the domain graph's `assetTag` values, ordinal-identical.
- Shared prose entities present in all three documents so the entity network deduplicates
  across the corpus.

Exit: each document ingests into a scratch namespace and reaches `indexed`; chunk counts and
extracted identifiers recorded.

## Phase 2: the dataset generator

`fallen-8-web-ui/scripts/samples/windFarm.ts` emitting the domain graph, registered in
`build-samples.ts`'s `REGISTRY` (build order is gallery card order). Deterministic: fixed seed,
fixed `creationDate`, no `Math.random`.

- The failing batch has seven members; the register document lists only a few of them.
- `assetTag` on every asset vertex, `icon` per label, `styleConfig` colouring by label.
- A `DictionaryIndex` recipe for the asset tags, plus `indexSeeds` and `linkIndexIds` pointing at
  it.

Exit: `npm run build:samples -- --only wind-farm` writes the jsonl and manifest entry; building
twice is byte-identical; counts in the entry match the file.

## Phase 3: the manifest contract

Extend `fallen-8-web-ui/src/lib/samples.ts`: `SampleDocument`, `SampleIndexSeed`, the three new
optional `SampleManifestEntry` fields, and the `knowledge` badge. Types only, so the five
existing samples keep type-checking untouched.

Exit: `tsc` clean.

## Phase 4: the loader

Extend `fallen-8-web-ui/src/lib/sampleLoader.ts` with three steps after index creation, and
`LoadStep` with `seeding`, `binding` and `ingesting`. Add `ingestionGate` beside `embeddingGate`.
Add `link` to `ingestFile` in `endpoints.ts` (the `linkJson` form field).

Every new step is conditional on its manifest field, so a sample without `documents` follows the
current code path exactly.

Exit: vitest covers seeding, binding, both ingest routes, polling outcomes, and the
zero-`/document`-calls pin for document-free samples.

## Phase 5: the Studio surface

Badge rendering, the ingestion gate on the card (blocking states disable the load button and name
the fixing environment variable; `nlp-off` warns and still permits), and the new progress steps
in the load line.

Exit: vitest on the card's gate states; `tsc` and lint clean.

## Phase 6: live end-to-end run

Load the sample into a dedicated namespace on the compose environment and walk the payoff:
search, inspect the top chunk, confirm its `mentions` edges reach both graphs, expand to the
batch, count the at-risk siblings. Record real numbers in the run ledger below.

Exit: the ledger filled with observed counts, not predictions.

## Phase 7: docs, README, screenshots

A docs-site page for the walkthrough (frontmatter, sidebar registration, base-aware root-relative
links), the samples page entry, a root README key-features line, and recaptured screenshots.
Mark the deferral in `features/done/unstructured-ingestion/spec.md` resolved with a pointer.

Exit: `npm --prefix docs run build` green (link-checked).

## Phase 8: gates and merge

`dotnet build`, `dotnet test`, vitest, tsc, docs build, and an assertion that the OpenAPI
snapshot did NOT move (a diff would mean accidental REST surface). Council gate, then
`git merge --no-ff` and move `features/open/knowledge-demo/` to `features/done/`.

## Run ledger

Observed on 2026-08-04 against the compose environment (bge-m3 on CPU Ollama, docling and NLP
sidecars up), by replaying the loader's exact sequence into a clean `knowledge-demo` namespace.

| What | Observed |
| --- | --- |
| Domain graph imported | 94 vertices / 164 edges (259 jsonl lines) |
| Asset tags seeded | 89 of 94 vertices (the 5 technicians carry no `assetTag`, by design) |
| Binding roles ready | `documents` (vector), `documents-text` (fulltext), `documents-entities` (entity) |
| Documents ingested / chunks | 3 / 13 (PDF 6, XLSX 2, markdown 5) |
| Entities discovered | 89, cross-document: Halvard Drivetrain, Priya Raman 3, Northwind Energy 3 |
| Chunk to domain `mentions` edges | top hit reaches 3 assets (`GBX_A17_02`, `WTG_A17`, `NW_STD_0417`) plus 4 entities; the register table chunk reaches 16 assets (the `MaxLinksPerChunk` cap, from 40 extracted tags) |
| At-risk siblings the documents never name | 5 of 7 batch members: `WTG_A05`, `WTG_A11`, `WTG_A13`, `WTG_B03`, `WTG_B07` |
| Wall-clock load time | 49s (dominated by docling PDF conversion and CPU embedding) |

Payoff confirmed in order, re-verified after the council fixes by loading the sample through the
REAL Studio UI (not by replaying REST calls): `"why did the bearing fail"` retrieves the mechanism
section; that chunk bridges both graphs; `"why is a single vibration number not enough"` lands on
the WRONG section in `lexical` mode and the right one in `fused`, which demonstrates the dense
side rather than asserting it; searching `WTG_A05` returns three hits that name it nowhere; and
expanding the batch yields five at-risk turbines that appear in no document.

Two content defects were found by running the thing rather than by reading it:

1. The mechanism section originally named no asset, so the top hit for the obvious query linked to
   zero domain vertices and the headline claim was false. It now names the unit it explains.
2. **Both original fusion claims were false**, caught during the council pass. "No keyword overlap,
   so the dense side finds it" was wrong: the section is titled "Why the bearing failed" and the
   lexical side scores it 39. "Dense embeddings are weak at exact identifiers, so
   `GBX_BATCH_2023_11` arrives lexically" was also unsupported HERE: bge-m3 retrieves that
   identifier perfectly well, and no identifier query in this corpus shows the lexical side
   rescuing the dense one. That does not disprove the general motivation for fusion, which is
   why the semantic-layer page still states it; it means THIS demo may not claim to show it. The replacement claim was found by measurement and
   is reproducible.
