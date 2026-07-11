# Subgraph Quotas — Specification

> **Status:** Planned. Originates from limitations in
> [../subgraph/spec.md](../subgraph/spec.md) (§9) and the REST/API + security review.
> Tracked by its GitHub feature issue.

## 1. Problem

Creating a subgraph clones matching vertices/edges into a new in-memory `Fallen8` that is
retained in the factory until deregistered. There is no ceiling on:

- the **number** of registered subgraphs, or
- the **materialized size** (vertices + edges) of each subgraph or of all subgraphs
  combined.

A caller can therefore create many subgraphs — even with empty filters, which clone the
entire graph and require no compiled code at all — and exhaust memory. Because creation is
authenticated the same as the rest of the API (no stricter gate), an authenticated client
can drive this. This is a resource-exhaustion / denial-of-service risk, not a correctness
bug.

## 2. Goals / non-goals

**Goals**
- Configurable ceilings, enforced at creation time:
  - maximum number of registered subgraphs,
  - maximum materialized element count per subgraph,
  - (optional) maximum total materialized elements across all subgraphs.
- Exceeding a quota fails cleanly (REST `409 Conflict` or `413`-style error with a clear
  message), leaves the source graph untouched, and does not register a partial subgraph.
- Sensible defaults that don't break existing usage/tests; quotas configurable and
  effectively unlimited when unset for trusted/embedded use.

**Non-goals**
- Per-caller quotas / authentication changes (the API's auth model is out of scope here).
- Time-based eviction or LRU of subgraphs.

## 3. Design sketch

- Add a `SubGraphQuota` (or options) with `MaxSubGraphCount`, `MaxElementsPerSubGraph`,
  `MaxTotalElements`, defaulting to unlimited. Surface it on the `SubGraphFactory` (settable
  by the host; the REST app sets conservative defaults).
- Enforce count before creation; enforce size during/after materialization. Because size is
  only known after the algorithm runs, either (a) check the post-materialization count and
  discard+fail if over, or (b) pass the cap into the algorithm to abort early. Prefer (b)
  for the per-subgraph element cap to avoid building an oversized graph; fall back to (a)
  where early abort isn't feasible.
- The REST layer maps a quota breach to a clear 4xx with the offending limit.

## 4. Acceptance criteria

- With a configured `MaxSubGraphCount`, the N+1th create returns a clear 4xx and registers
  nothing.
- With a configured per-subgraph element cap, a create whose result would exceed it fails
  cleanly and leaves the source graph and registry unchanged.
- With quotas unset, behaviour matches today; all existing tests pass.

## 5. Testing

- Count-limit breach, per-subgraph size breach, and (if implemented) total-size breach.
- Source-graph-unchanged and nothing-registered on breach.
- Defaults-unset regression.
