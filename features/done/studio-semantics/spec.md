# Studio semantics — Specification

> **Status:** Implemented and council-gated (branch `feature/studio-semantics`); see
> [plan.md](./plan.md) for the phase record and review outcome. A few contract points were
> refined during implementation and are noted inline below.
>
> **Builds on:** [element-embeddings](../../done/element-embeddings/) and
> [embedding-provider](../../done/embedding-provider/) (server side, complete). The
> traversal rules this UI surfaces are documented once, in the element-embeddings README
> ("Semantic traversal") — this spec only decides how F8 Studio exposes them.

## Problem

The server now has a complete semantics story — embeddings as element state, bound vector
indices, the `semantic` traversal block, and an optional text-in embedding provider — but
F8 Studio exposes none of it:

- **Embeddings are invisible.** An element's embedding is a reserved `$embedding:<name>`
  property; the Browser shows it as an opaque float array with no way to set, replace, or
  remove it through the typed endpoints (`PUT/GET/DELETE /graphelement/{id}/embedding/{name}`),
  and no hint that it feeds bound indices and semantic traversal.
- **Bound indices are indistinguishable from raw ones.** The Query screen's index
  management (feature studio-index-discovery) creates `VectorIndex` with dimension +
  metric only — no `embeddingName`/`model` options — and the inventory shows no binding.
  Worse, the existing "add vector to index" form fails with a 400 against a bound index
  and the UI cannot say why.
- **Semantic traversal is unreachable.** The Path and Subgraph screens build
  filter/cost fragments (Monaco + NL assist) but have no way to send a `semantic` block —
  the ONE part of the traversal surface that works with dynamic code execution *off*, which
  is exactly the posture most Studio users run.
- **The provider is dark.** With `Fallen8:Embedding:Enabled=true` the server offers
  embed-to-element, semantic search, and `queryText` — but Studio has no affordance for
  any of them, and no place that shows whether the provider is on, which model it runs,
  or whether it has loaded.

## Contract

### 1. Provider awareness (one query, one store)

`GET /statistics` already returns `embedding: { enabled, backend, modelName, modelVersion,
dimension, intendedMetric, loaded }`. The UI reads it from the existing graph-shape query
(`embeddingProvider(shape)`); `null` on older servers **and until a Graph shape is
computed** — `/statistics` is a budgeted on-demand pass, not a poll (refined from the
draft's "polled"). Every affordance below keys off it, treating "unknown" like "not
confirmed enabled": text-in controls stay disabled with a "Compute the Graph shape" hint,
consistent with how every other shape-derived datalist behaves.

- provider **absent/disabled** → text-in controls render disabled with an honest hint
  ("embedding provider off — Fallen8:Embedding:Enabled"), vector-paste controls stay
  fully functional (bring-your-own-vector is first-class, never gated);
- provider **enabled** → text inputs light up; the Dashboard shows a provider card
  (backend, model stamp, dimension, metric, loaded state).

### 2. Element embeddings in the Browser

The element inspector gains an **Embeddings** tab (sibling of Properties):

- lists the element's named embeddings (name, dimension, a truncated vector preview, the
  model stamp when present — stamp from `GET /graphelement/{id}/embedding/{name}`);
- **Set/replace**: name input (validated `[A-Za-z0-9_-]{1,64}`) + either a pasted JSON
  float array or — provider enabled — a text field that calls
  `POST /embedding/element`; **Remove** calls the DELETE route. All three surface the
  server's 400 reasons verbatim (dimension conflict with a bound index, zero-norm, etc.);
- reserved `$embedding:*` / `$embeddingModel:*` properties are folded out of the plain
  Properties list into this tab (they remain visible raw via a "show reserved" toggle —
  honesty over magic).

### 3. Bound vector indices in Query → Indices

- **Create** for `VectorIndex` gains two optional fields: `embeddingName` (datalist of
  names seen on the graph-shape pass, free-form allowed) and `model` (free-form identity
  string; prefilled with the provider's stamp when enabled). Helper text states the
  binding contract in one line: "bound = auto-maintained projection of that embedding;
  explicit adds are rejected".
- **Inventory** rows show a `bound:<name>` badge; the "add vector" form is disabled for
  bound indices with the same one-liner.
- **Semantic search box** (provider enabled): a text input + k next to the existing kNN
  scan form, calling `POST /embedding/search`; results reuse the existing scored-element
  table. 409 identity conflicts render verbatim (they are the model-drift signal).

### 4. The `semantic` block on Path and Subgraph screens

One shared component (`SemanticBlockEditor`) on both screens, mirroring the server's
one-owner-per-slot rules **in the UI before the request is sent**:

- **Query source**: radio — *vector* (JSON float array textarea, reusing `lib/vector.ts`
  parsing) or *text* (input; disabled with hint when the provider is off — the server
  would 403).
- **Options**: embeddingName (default `default`), metric (`Cosine`/`DotProduct`/`L2`),
  `minScore` (number, direction hint flips for L2: "≤ = closer"), and on the Path screen
  only, `costBySimilarity` (checkbox; disabled with reason when metric is DotProduct or
  algorithm is BLS — BLS ignores costs).
- **Conflict prevention, not conflict errors**: enabling `minScore` renders the
  vertex-filter fragment slot inert with a one-line reason **and omits that fragment from
  the built request** (it stays in the draft, so toggling `minScore` off restores it) —
  the semantic block owns the slot, so the fragment is never sent alongside it; same for
  `costBySimilarity` vs the vertex-cost slot. On the Subgraph screen the whole block is
  disabled when a stored template is selected, and is then never built or validated. The
  server's 400s remain the backstop and render verbatim when they occur anyway. (The
  omission was added after review caught that a disabled slot still sent its stale
  fragment.)
- **Dynamic-code independence, stated**: the block carries a hint that it works with
  dynamic code off — on servers where the fragment editors are disabled (403 posture),
  the semantic block stays enabled. This is the main reason the feature exists.
- Path results: when `costBySimilarity` was sent, the result table's `totalWeight` column
  gets a tooltip explaining the cost mapping (Cosine: `1 − score` per vertex).

### 5. NL assist stays out (for now)

The NL assist drafts C# fragments; teaching it to also emit `semantic` blocks is a prompt
change with its own eval loop — deferred to a follow-up, noted in the NL feature docs as a
one-line pointer. (Non-goal below.)

## Non-goals

- **NL assist emitting semantic blocks** — separate prompt/eval work; revisit after this
  ships and real usage shows fragments being hand-written for things `minScore` covers.
- **Client-side embedding in the browser** (transformers.js etc.) — the provider is the
  server-side answer; revisit only if operators demand fully provider-less text input.
- **Embedding visualisation** (projections, similarity heatmaps on the canvas) — canvas
  styling by score is a natural studio-canvas-viz follow-up, not this feature.
- **Batch embed UI** (`POST /embedding/elements`) — bulk ingestion is a pipeline concern;
  the REST endpoint exists for scripts. Revisit on demand.
- **Editing `$embedding:*` via the raw property editor** — remains possible (it is a
  property) but undocumented in the UI beyond the "show reserved" toggle.

## Acceptance sketch

- With a model-free server (provider off): set/inspect/remove a vector by paste, create a
  bound index, run a `minScore` path — all without touching dynamic code or the provider.
- With the provider on: type text on an element → embedding + stamp appear; type text in
  semantic search → hits; type text in the Path semantic block → filtered path; Dashboard
  shows the provider card.
- Every server rejection (400 slot conflicts, 403 gates, 409 identity) renders its reason
  verbatim; no affordance lets the user build a request the UI already knows is invalid.
- Vitest coverage in the existing patterns (api-contract routes, screen tests with mocked
  client) for every new control and gating state.
