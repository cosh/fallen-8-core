---
title: "Sample gallery"
description: "One-click curated demo graphs, each a guided tour of a different feature, with screenshots and example queries."
---

[F8 Studio](/fallen-8-core/studio/)'s **Samples** screen ships a gallery of curated graphs that load in one
click. Each comes styled for the canvas, indexed where it helps, and paired with example
queries, so every card is a short guided tour of a different Fallen-8 capability — analytics,
weighted paths, semantic search, visualization. A tag bar at the top filters the gallery by
capability. This doc walks through each one, with screenshots and queries you can run yourself.

![The F8 Studio Samples screen with its capability tag filter.](../../assets/images/screen-samples.png)

## How loading works

Clicking **Load** fetches the dataset, imports it, builds the sample's indices, and re-reads the
elements onto the canvas with the sample's style. For the baked datasets no embedding work
happens at load time, because the vectors are already in the file. The datasets ship with the app
and are served same-origin from `/samples` by default, so the gallery shows exactly the samples
the app was built with and works offline; set `VITE_F8_SAMPLES_BASE` to fetch them from a remote
mirror or a fork instead.

One sample is different: **Wind Farm Fleet Integrity** additionally ingests three synthetic documents
through the live [semantic layer](/fallen-8-core/unstructured-ingestion/) after the graph is
imported, so it adds three steps (seeding the asset index, binding the layer, ingesting) and it
does compute embeddings at load time. It therefore needs more of the environment than the others:
when something it needs is missing, its card blocks the load and names what to fix before you
click.

- **Import needs an empty graph** (ids must not clash). Loading into a non-empty instance is
  gated behind a typed-name confirm that erases first — save a checkpoint
  ([save games](/fallen-8-core/save-games/)) if you need the current data, or switch to a fresh
  [namespace](/fallen-8-core/namespaces/).
- **The datasets are `fallen8-jsonl`** — the same format [bulk import/export](/fallen-8-core/bulk-import-export/)
  uses, fetched and streamed through `POST /bulk/import`.
- **Bring-your-own-vector always works.** The embedded samples carry their vectors in the
  file, so vector scans work even with no embedding provider. The **text-in** features
  (semantic search by typed text) additionally need a provider whose model identity matches
  the baked vectors — each card tells you whether that works on the current instance. See
  [semantic traversal](/fallen-8-core/semantic-traversal/).

## The samples

### 🛡️ Asymmetric Cyber Warfare — 6 vertices, 5 edges

![The Asymmetric Cyber Warfare graph in the 3D canvas with a radial-DAG layout, emoji nodes with directed edges.](../../assets/images/sample-cyber-warfare.png)

A tiny, story-driven graph: a nation-state actor weaponizes a compromised software supply-chain
tool to deliver a payload to two targets (critical infrastructure and a government agency), while
a SOC and its analyst investigate. Six entities, five directed relationships, emoji nodes, and
labelled edges. It is the same graph the [first-run walkthrough](/fallen-8-core/studio/) animates. The point:
the full blast radius of a compromise is a single native traversal here, versus the brittle
multi-table joins a relational store forces.

The same graph in the 3D renderer with a force layout:

![The Asymmetric Cyber Warfare graph in the 3D canvas, force layout.](../../assets/images/sample-cyber-warfare-3d.png)

Try it:

- **[Path](/fallen-8-core/path-finding/)** from the Nation State Actor to the Critical Infrastructure to see
  the blast radius of a supply-chain compromise (look ids up on the Browser screen).
- **[Subgraph](/fallen-8-core/subgraphs/)** capturing the Software Supply Chain Tool and everything it
  delivers to, then recalculate it as the graph changes.
- **[Analytics](/fallen-8-core/graph-analytics/) → `PAGERANK`**, then color the canvas by the score: the
  compromised tool and the critical target rank highest.

### 🥋 Zachary's Karate Club — 34 vertices, 78 edges

![Karate Club on the canvas, colored by faction, sized by degree.](../../assets/images/sample-karate-club.png)

The most famous graph in community detection: club members, friendships, and the real 1977
split. Nodes are colored by `faction` and sized by degree, so the two camps and their leaders
(member #0 and #33) are obvious at a glance.

Try it:

- **[Analytics](/fallen-8-core/graph-analytics/) → `LABELPROPAGATION`** with write-back, then color the
  canvas by the computed community — it reproduces the club's real split (compare with color
  by `faction`).
- **`TRIANGLECOUNT`** and **`WCC`** on the textbook graph.
- **[Path](/fallen-8-core/path-finding/)** from Mr. Hi to the Officer (look their ids up on the Browser
  screen).

### 🛡️ AD Attack Surface — 117 vertices, 142 edges

![The AD Attack Surface graph on the canvas.](../../assets/images/sample-attack-surface.png)

A synthetic Active-Directory estate: users, workstations, servers, and groups. The scenario
is a red-team classic — phish an intern, then find the cheapest path to Domain Admins. Ships
with a bound [vector index](/fallen-8-core/vector-search/) for semantic search.

Try it:

- **[Path](/fallen-8-core/path-finding/) → Dijkstra** from the phished `finance.intern` workstation to the
  `DOMAIN ADMINS` group, using cost property `exploitCost` — the result is the cheapest attack
  chain.
- **[Semantic search](/fallen-8-core/semantic-traversal/):** "where do the financial documents live"
  surfaces the Finance file server.
- **[Analytics](/fallen-8-core/graph-analytics/) → `DEGREE` / `PAGERANK`** to spot lateral-movement
  choke points.

### 🎬 Movie Night — 191 vertices, 1,697 edges

![Movie Night on the canvas with poster-image nodes.](../../assets/images/sample-movie-night.png)

Films, genres, and viewers with real taste communities — poster-image nodes, plot embeddings,
and rating-weighted edges. The richest sample for semantic and recommendation work.

Try it:

- **[Semantic search](/fallen-8-core/semantic-traversal/):** "mind-bending sci-fi about dreams" surfaces
  Inception; "a haunted hotel" finds The Shining (see the [worked example](#semantic-search)
  below).
- **[Path](/fallen-8-core/path-finding/):** a 2-hop viewer → movie → viewer → movie chain is a
  recommendation.
- **[Analytics](/fallen-8-core/graph-analytics/) → `PAGERANK`** ranks the canon; **`LABELPROPAGATION`**
  recovers the taste communities.

### ✈️ World Air Routes — 250 vertices, 5,702 edges

![World Air Routes on the canvas — airports colored by country, sized by degree.](../../assets/images/sample-air-routes.png)

The 250 busiest airports and the flights between them (OpenFlights), colored by country and
sized by degree so the mega-hubs (US, GB, DE, FR…) pop. Each node carries its country flag as
its `icon`; where the browser has no flag-emoji font it falls back to the two-letter country
code, as in the shot above.

Try it:

- **[Path](/fallen-8-core/path-finding/) → Dijkstra** on cost property `km` between two airports — a real
  minimum-distance itinerary.
- **[Semantic search](/fallen-8-core/semantic-traversal/):** "major airports in Japan" or "busiest hubs in
  the Middle East".
- **[Analytics](/fallen-8-core/graph-analytics/) → `PAGERANK` / `DEGREE`** to rank the global hubs.

### 📦 Fallen-8 Dependencies — 392 vertices, 517 edges

![The Fallen-8 dependency graph on the canvas, colored by ecosystem.](../../assets/images/sample-fallen8-deps.png)

Fallen-8's own supply chain across every ecosystem (npm, NuGet, PyPI, GitHub Actions), colored
by ecosystem and sized by in-degree. The static twin of the live GitHub card.

Try it:

- **[Analytics](/fallen-8-core/graph-analytics/) → `PAGERANK`** for the most-depended-on packages;
  **`WCC`** to see each ecosystem fall out as its own component.
- **Canvas** → color by `license` or `ecosystem`.

### 🌬️ Wind Farm Fleet Integrity: 94 vertices, 164 edges, plus 3 ingested documents

![The Wind Farm Fleet Integrity card in the gallery, badged knowledge, with its guided steps.](../../assets/images/sample-wind-farm.png)

An offshore wind operator's asset graph (turbines, gearboxes, casting batches, substations, work
orders, technicians) with **three synthetic documents ingested into it at load time**: a PDF
root-cause analysis carrying a vibration figure, an XLSX maintenance register, and a markdown
engineering standard. Nothing about the knowledge graph is baked; docling conversion, embedding,
spaCy enrichment and exact-match linking all actually run, which is why this card needs the
sidecars.

It is the sample that shows the [semantic layer](/fallen-8-core/unstructured-ingestion/)'s real
thesis: a Chunk is an ordinary vertex, so the text you searched and the assets you operate are
**one graph**. Each document reaches the graph a different way, and one chunk ends up bridging
both worlds:

- **Structural linking reaches assets.** Identifier-shaped tokens in the text (`WTG_A17`,
  `GBX_A17_02`, `GBX_BATCH_2023_11`) are matched exactly against an `asset-tags` index, so a
  chunk gets `mentions` edges straight to the real equipment. The register's table chunk extracts
  40 such tags and links to the first 16 of them, that being the `MaxLinksPerChunk` cap rather
  than the end of the list.
- **NER reaches people, organisations and places.** The technicians, the two gearbox
  manufacturers, `Esbjerg`, the `North Sea`: those arrive as deduplicated Entity vertices,
  because prose names with spaces are not identifier-shaped and never link structurally. The
  reliability engineer who signs all three documents is one vertex with three mentions.

The documents are **synthetic**, and so are the two manufacturers named in them: the narrative
attributes a premature failure to a supplier's casting batch, which is not a thing to say about a
real company.

Try it, in order (the last two steps are the point):

1. **Knowledge → Search:** `why did the bearing fail`. You get the section that explains the
   mechanism without having to guess the vocabulary it uses (*rolling contact fatigue*,
   *spalling*, *Hertzian contact stress*). You asked a question; you did not construct a keyword query.
2. **Search `why is a single vibration number not enough`, then switch mode to `lexical`.**
   Keyword matching alone lands on the wrong section (the bearing-failure narrative, which is
   full of those words). The default **fused** mode gets it right, because the dense side
   recognises the paraphrase of "an overall broadband level is a single number summarising all
   the vibration energy". That is what fusion buys you, demonstrated rather than asserted.
3. **Send the top hit to the canvas** and expand it. Its `mentions` edges reach both worlds at
   once: `WTG_A17`, `GBX_A17_02` and `NW_STD_0417` on the asset side by exact tag match, plus
   whichever entities the NLP sidecar found in that paragraph. Open the report's opening section
   for the richest entity fan-out.
4. **Search `WTG_A05`.** You get three confident-looking hits and **not one of them names that
   turbine**, because no document covers it. This is the honest limit of retrieval, and it is the
   moment the graph earns its place.
5. **Expand `GBX_A17_02` → `GBX_BATCH_2023_11`, then expand the batch.** Seven gearboxes came
   from that casting run. The documents name only two of the turbines carrying them. **The other
   five, `WTG_A05` included, are in no document at all**, and their readings all sit under
   the warning level, which is exactly what the root-cause analysis warns about. The corpus
   explains the mechanism; the graph gives you the blast radius.
6. **Knowledge → Entities:** note that the signing engineer exists twice, once as an Entity the
   NLP sidecar derived from text and once as a `Technician` the asset import created. Resolving
   those two is the next graph problem, and Fallen-8 hands you both sides of it.

![Both graphs on one canvas after the load: the asset graph (turbines, gearboxes, work orders) and the knowledge graph (documents, chunks, 89 entities) joined, with the register's table chunk a visible hub.](../../assets/images/sample-wind-farm-canvas.png)

The legend is the point: `Turbine`, `Gearbox`, `WorkOrder`, `CastingBatch` and `Substation` came
from the imported dataset, while `Document`, `Chunk` and the `Entity` vertices were produced by
the ingest a moment earlier. They are one graph, in one canvas, joined by `mentions` edges. The
bright hub in the middle is the register's table chunk reaching its 16 linked assets. (The entity
count shown, around 90, depends on the spaCy model and tier, so expect it to differ a little on
your machine; the asset links do not vary, because exact matching is deterministic.)

The pipeline itself (chunking, the binding, fused retrieval, linking) is documented in
[semantic layer](/fallen-8-core/unstructured-ingestion/).

### 📈 Scale: 100k × 1M and 🐙 Any GitHub repo

Two more cards round out the gallery:

- **Scale: 100k × 1M** — a 100,000-vertex, ~1M-edge graph generated server-side on the
  **Benchmark** tab (not fetched); use it to feel ingest speed, memory footprint, and
  analytics at scale. See [Studio → Benchmark](/fallen-8-core/studio/).
- **Any GitHub repo** — paste `owner/repo` to fetch any public repository's dependency graph
  from GitHub just-in-time and ingest it — the dynamic twin of the Fallen-8 Dependencies
  sample.

## Worked examples

### Semantic search

Load **Movie Night**, open **Query**, pick the `embeddings` index, switch to **text
(provider)**, and search a *concept* rather than keywords. "mind-bending sci-fi about dreams"
ranks Inception top by cosine similarity — the query text is embedded once server-side, then
run as exact kNN.

![Semantic search on Movie Night returning Inception as the top hit.](../../assets/images/query-semantic-search.png)

The mechanics — element embeddings, bound indices, the model-identity contract — are in
[semantic traversal](/fallen-8-core/semantic-traversal/); the kNN scan itself in [vector search](/fallen-8-core/vector-search/).

### An interesting path

The **Path** screen finds routes between two elements. On a weighted sample (air routes by
`km`, the attack surface by `exploitCost`) a Dijkstra run returns the genuinely cheapest
route; the default BLS finds fewest-hop paths.

![A path result on the Path screen.](../../assets/images/path-result.png)

Filters and cost functions are C# [delegates](/fallen-8-core/delegates/); the full path contract is in
[path finding](/fallen-8-core/path-finding/).

### A subgraph

The **Subgraph** screen builds an alternating vertex–edge pattern and extracts everything on a
matching path into a new standalone graph.

![The subgraph pattern builder.](../../assets/images/screen-subgraph-builder.png)

![A created subgraph.](../../assets/images/subgraph-result.png)

The pattern model and REST lifecycle are in [subgraphs](/fallen-8-core/subgraphs/).

## Rebuilding and adding samples

The datasets live in the repo's top-level [samples/](https://github.com/cosh/fallen-8-core/tree/main/samples/) and are served from a public
raw URL; the gallery is driven entirely by `samples/index.json`, so adding a sample is a data
change, not a UI change. The embedded samples' vectors are produced at build time (never at
load) against an instance with the embedding provider on.

A sample can also declare `documents`, which the loader ingests live after the import, plus
`indexSeeds` to fill an equality index from an imported property and `linkIndexIds` to name the
linking allowlist. The seeding step exists because creating an index does not backfill it: a
dictionary index created after an import is empty, so linking against it would find nothing. The
document files themselves live in
[samples/documents/](https://github.com/cosh/fallen-8-core/tree/main/samples/documents/) and are
committed rather than built.

## See also

- [Studio](/fallen-8-core/studio/) — the UI that hosts the gallery
- [Bulk import/export](/fallen-8-core/bulk-import-export/) — the `fallen8-jsonl` format the samples use
- [Semantic traversal](/fallen-8-core/semantic-traversal/) / [Vector search](/fallen-8-core/vector-search/) — the embedding features the samples exercise
- [Graph analytics](/fallen-8-core/graph-analytics/) · [Path finding](/fallen-8-core/path-finding/) · [Subgraphs](/fallen-8-core/subgraphs/) — the algorithms the "try it" steps drive
