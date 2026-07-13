# CSR Adjacency — Assessment (do / skip)

> **Status:** Assessment complete. **Recommendation: SKIP** (do not build a CSR adjacency), with the
> reasoning below and a concrete revisit condition. This is the residual item after the
> `adjacency-flattening` feature; it was always scoped as *assess-and-recommend*, not *implement*.

## What CSR is, and the idea

Compressed Sparse Row (CSR) stores a graph's adjacency as a few large, contiguous **primitive**
arrays: a `rowOffsets[V+1]` array and a single `columnIndices[E]` array of neighbour vertex ids (plus
optional parallel edge-payload arrays). Iterating a vertex's neighbours is then a tight scan of a
contiguous slice `columnIndices[rowOffsets[v] .. rowOffsets[v+1]]` — no per-edge object, no pointer
chasing, excellent cache locality. It is the standard representation for **static, read-heavy graph
analytics** (PageRank, BFS/SSSP sweeps over an immutable graph).

## Current representation (post adjacency-flattening)

Each `VertexModel` holds two `volatile EdgeAdjacency` fields (out / in). `EdgeAdjacency` is immutable
and grouped by edge-property-id:

- **Single-group (the common case): inline** — just the group key + one contiguous `EdgeModel[]`, no
  dictionary, minimal overhead.
- **Multi-group: a `Dictionary<string, EdgeModel[]>`**, each group a contiguous array.

Mutations build a new `EdgeAdjacency` and publish it with **one volatile reference assignment**
(copy-on-write); readers are lock-free. The arrays hold **references to `EdgeModel` objects**.

## Why CSR does not fit here — the three blockers

1. **Edges are first-class objects, not reducible to a neighbour-id column.** `EdgeModel : AGraphElementModel`
   has its own id, its own property store, participates in indices, and is removable by id. CSR's core
   win is collapsing an edge to a primitive `columnIndices[]` entry — but here every edge must remain
   an addressable object regardless. So CSR could only ever be an **additional** neighbour-id/edge-id
   overlay layered on top of the existing `EdgeModel` objects, not a replacement. That adds memory and
   a second structure to maintain; it does not remove the per-edge objects that dominate the footprint.

2. **The graph is continuously mutated; CSR is static.** This is a live transactional store — edges
   are added and removed by transactions at any time. CSR's contiguity is precisely what makes
   incremental mutation hostile: inserting/removing one edge shifts `columnIndices` and every
   subsequent `rowOffsets` entry (O(E) per change), or forces a periodic global rebuild, or a
   delta-overlay (CSR base + per-vertex deltas) — and the current per-vertex copy-on-write adjacency
   **already is** an efficient incremental structure that touches only the changed vertex.

3. **A global CSR array is at odds with the concurrency model.** Lock-free reads + single-writer work
   today because each vertex publishes its *own* adjacency by one volatile swap; a reader captures one
   reference and sees a self-consistent slice. A shared global `columnIndices`/`rowOffsets` cannot be
   swapped per-vertex — a writer would either need to publish a whole new global array on every edge
   change (absurd) or introduce locking/epoch reclamation over the shared arrays, a substantial
   redesign of the invariant the rest of the engine relies on.

## What was already captured (so the residual is small)

The `adjacency-flattening` feature already took the realistic wins: the dominant single-group vertex
is inline with a contiguous array (no dictionary, no pointer-heavy `ImmutableDictionary`), and even
multi-group vertices store contiguous per-group arrays. Within a group, neighbour iteration is already
a contiguous array walk. The only "residual" CSR could target is **cross-vertex** locality (one big
array vs many small ones) and the `Dictionary` overhead of genuinely multi-group vertices — and the
latter only exists for vertices with edges under several distinct edge-property-ids.

## Cost vs. benefit

- **Benefit:** better cross-vertex cache locality for full-graph traversal sweeps, and removal of the
  multi-group `Dictionary` overhead — but ONLY as an overlay (blocker 1), ONLY for read-mostly phases
  (blocker 2), and the per-edge `EdgeModel` objects (the real footprint) remain.
- **Cost:** a new global structure; O(E) maintenance or a delta/rebuild scheme under continuous
  mutation; a concurrency-model redesign for shared arrays; and the ongoing complexity of keeping the
  overlay consistent with the authoritative `EdgeModel` objects and the master store. High effort,
  high risk, and directly against the DB's live-transactional grain.

This is the same shape of decision as the P6 (parent-pointer BLS) and P3 (non-blocking save)
deferrals: a real idea whose measured/structural payoff does not justify its risk here.

## Recommendation — SKIP

Do not build a CSR adjacency. Keep the immutable per-vertex `EdgeAdjacency` (inline single-group +
contiguous per-group arrays), which fits the live-mutation + lock-free-read + single-writer model and
already has good within-group locality.

**Revisit only if** a concrete, measured workload emerges that is (a) overwhelmingly read-only after
an initial load, (b) dominated by full-graph neighbour-iteration sweeps, and (c) large enough that
cross-vertex locality is shown (by benchmark) to be the bottleneck. In that case the right shape is a
**derived, read-only CSR snapshot built on demand** (e.g. for an analytics session) from the live
graph and discarded after — an *additive* accelerator that never sits on the mutation path — rather
than replacing the authoritative adjacency. That would be its own feature, justified by numbers.

## Non-goals of this assessment

- No engine change is proposed. This document records the do/skip decision (skip) and the condition
  under which to reopen it, consistent with the feature workflow's "definitive, documented decision"
  practice.
