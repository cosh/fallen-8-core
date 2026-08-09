# Integration runtime - Plan

**The shared plan for all three integration specs.** They are one branch
(`feature/integration-runtime`) with one phasing, so the phasing has one home rather than three
copies: [integration-runtime](./spec.md), [integration-identity](../integration-identity/spec.md)
and [integration-blueprints](../integration-blueprints/spec.md) are all sequenced here.

**Ordering principles.**

1. The platform's silent-failure defects come first, because the identity model's correctness rests
   on them and because a silent failure cannot be deferred.
2. The contract's floor is tested before the contract's hardest consumer, which is why the minimal
   blueprint precedes UniFi.
3. The conformance harness precedes every blueprint, so each blueprint lands already verified rather
   than verified later.
4. The skills are written after the contract ships, so they are checked against reality rather than
   intent.

## Phase 0 - the platform floor

Executes the P0 items of [platform-integrity-audit](../platform-integrity-audit/spec.md) on this
branch, per your decision that they land as part of this feature. That document owns their design,
evidence and rejected alternatives; this is the sequencing only.

- [x] W1 registry and catalog write durability, plus a loud corrupt pointer. The boot path is
  unchanged: save-games FR-8 specifies the empty-registry behaviour (see the audit's W1 correction).
- [ ] W2 property replace and remove: one WAL ordinal, both batch routes, equal-value as a true
  no-op, the two wrong doc comments fixed.
- [ ] W3 claim-index integrity: idempotent add, loud missing index, loud manifest drop, literal-first
  route shape, resync contract documented.
- [ ] W4 rebuild from element state, plus the prefix-bound index mode.
- [ ] W5 durability and recovery-integrity signal.
- [ ] W6 batch element read, DateTime round-trip, and the claim-set shape written down.
- [ ] W7 the typed facade (the channel decision, made before any route table exists).
- [ ] Gate: full suite green, snapshot regenerated with a reviewed diff, coverage and contract tests
  moved.

**Stop-and-review trigger.** If W4's prefix-bound index mode proves larger than the bound vector
index suggests, fall back to the rebuild primitive alone with explicit index writes. Do **not** fall
back to WAL-logging index writes; that is rejected in the audit with a trigger.

## Phase 1 - specs

- [x] The three specs plus the skills revision (this phase's deliverable).
- [ ] Your review, with [integration-identity](../integration-identity/spec.md) reviewed
  independently and hardest.

## Phase 2 - runtime skeleton, identity, compose, pipeline

Intent: the thing actually runs end to end against a live Fallen-8 with one trivial provider.

- [ ] Project, Dockerfile, *CodeQualityTest* project lists, *Integrations__* and *Fallen8Target__*
  options, posture log at startup.
- [ ] The identifier vocabulary as a **validated data file**, with its three-valued *scope*, plus the
  canonicalization site and one test per entry.
- [ ] The snapshot schema, including *completeness*, and the snapshot validator endpoint.
- [ ] Claim and claim-set representation as reserved-prefix properties; the two prefix-bound indices;
  the ensure-and-rebuild path.
- [ ] Resolution, merge rules, merge candidates as graph state keyed on claim keys, reconciliation as
  a set difference, deletion with its durability precondition.
- [ ] The ephemeral cache and the **mandatory** change-feed subscription, with resync treated as
  ensure-index-then-rebuild and withdrawals refused until it completes.
- [ ] Provider abstraction, catalog endpoint, instances, scheduler (conservative default interval, no
  auto-save), per-instance status.
- [ ] Secrets: Data Protection with the key ring on the volume, required
  *F8_INTEGRATIONS_SECRET_KEY*, fail-with-instructions when secrets exist and the key does not, never
  return a secret, redaction filter applied before the log line is formed.
- [ ] The typed facade's controllers on the apiApp; MCP decision recorded (ops on existing tools or a
  written deferral).
- [ ] Compose service, *Integrations__* block on *fallen8*, named volume, profile wired at **all**
  *env:up* / *env:down* / *env:logs* / *env:status* call sites including the hard-coded invocations in
  *package.json*, plus *env-info.js*.
- [ ] *release.yml* matrix leg; *buildAndTest.yml* builds the project and runs its tests.
- [ ] Fleet observability with the same tenant and instance identity, bounded tag sets.
- [ ] Both two-integration cases from the identity spec, using two throwaway fixture providers.

## Phase 3 - conformance harness

Intent: a candidate integration can be judged without a human reading it.

- [ ] The suite: schema, claim well-formedness, determinism, idempotence (zero **write calls**),
  claim scoping, no weak-only merge, no similarity-influenced merge, declared free-text fields are the
  only ones enriched, the disabled-capability matrix, no secret in any log or response.
- [ ] Fully offline: recorded fixtures plus a fake Fallen-8 target, so an agent iterates with no live
  source and no live instance.
- [ ] **Negative fixtures**: deliberately broken sample integrations that must fail specific **named**
  checks, so the verifier itself is tested.
- [ ] Wired into *buildAndTest.yml*.

## Phase 4 - minimal blueprint (CSV)

Intent: measure the contract's floor while the contract is still cheap to change.

- [ ] Roughly a hundred lines. No credentials, no pagination, no rate limiting, no topology.
- [ ] Carries the **strong-overlap** acceptance case (a row whose MAC matches a UniFi-observed
  device), because Fronius cannot.
- [ ] Passes the suite.
- [ ] **If it does not fit the budget, stop and report the contract is too heavy.** Do not raise the
  budget.

## Phase 5 - UniFi

- [ ] Re-fetch the v10.4.57 OpenAPI document in full (my fetch was truncated) and **generate types
  from the contract**. Record the targeted version in the docs.
- [ ] Local Network Integration API behind a transport seam; Site Manager and Connector Proxy not
  built.
- [ ] Three auth realities supported and documented; no OAuth scaffolding.
- [ ] GET-only enforced in code with a test; 429 with Retry-After; every list endpoint paged.
- [ ] Entities, claims and relations per the blueprint spec; topology from *uplink.deviceId*.
- [ ] Stored queries registered at instance enable.
- [ ] Fixtures including the three-page paging trap, the 429, and an unresolvable uplink.

## Phase 6 - Fronius

- [ ] Verify the current Solar API against Fronius's published documentation once more before writing
  DTOs; v0 and v1 shapes.
- [ ] The weak-only merge-candidate path **end to end**, including surviving a restart and a re-sync.
- [ ] Two instances with identical *UniqueID* values asserting they do **not** merge.
- [ ] The GEN24 disabled-API case producing the specific setup message.
- [ ] The reading set, with its justification recorded in the provider.
- [ ] Stored queries.

## Phase 7 - degradation matrix

- [ ] All sixteen combinations of *F8_EMBEDDINGS*, *F8_CHAT*, *F8_NLP*, *F8_INGESTION*, asserted as a
  matrix over observable behaviour. Every AI-dependent behaviour degrades to **absent**, never to
  broken.
- [ ] Embedding opt-in per provider **and** per instance, default off; dimension and metric read from
  `/status`.

## Phase 8 - skills revision

- [ ] The revision to [skill-library](../skill-library/spec.md) and its plan (already drafted in
  phase 1); author the three skills' content against the shipped contract.
- [ ] The vocabulary drift-guard gate in *SkillLibraryTest*.

## Phase 9 - Studio screen

- [ ] Route `/integrations` (plural; the REST resource is singular so it is not shadowed).
- [ ] Reachability, catalog, instances with status, pending candidates with a confirm action, forms
  rendered from the declared JSON Schema, secrets write-only.
- [ ] Discovery from `/status`, never by probing a route (the two topologies answer differently for a
  nonexistent route).
- [ ] **No provider-specific code anywhere.** A provider-specific component is a contract failure.
- [ ] vitest and e2e coverage; screenshots recaptured per the standing rule.

## Phase 10 - docs, sweep, diagrams

- [ ] Four pages: runtime (config surface, secret handling, how the channel works, security posture,
  the degradation matrix), authoring (pointing at the skills and the conformance suite as the
  authority), UniFi (the three auth realities step by step, what is read, that it is read-only, the
  resulting graph, example queries), Fronius (same shape plus the identity story and what the
  merge-candidate flow looks like for a user).
- [ ] Sidebar registration; README key-feature line linking each published page.
- [ ] **Both** architecture diagrams: this adds a deployable and a channel, so the root README diagram
  and the docs-site architecture page change in the same PR, in the fixed dark and brand-red style.
- [ ] The cross-feature impact sweep recorded in each spec's own impact section.

## Phase 11 - gate

- [ ] Full `dotnet test` green; UI suites green; build clean.
- [ ] OpenAPI snapshot regenerated with a reviewed diff; coverage and contract tests green.
- [ ] Docs-site build green and link-checked.
- [ ] Council review; fix findings on the branch; merge with `--no-ff`; move all four feature
  directories to `features/done/`.

## Progress

- [ ] Phase 0 - platform floor (audit P0)
- [x] Phase 1 - specs
- [ ] Phase 2 - runtime, identity, compose, pipeline
- [ ] Phase 3 - conformance harness
- [ ] Phase 4 - minimal blueprint
- [ ] Phase 5 - UniFi
- [ ] Phase 6 - Fronius
- [ ] Phase 7 - degradation matrix
- [ ] Phase 8 - skills revision
- [ ] Phase 9 - Studio screen
- [ ] Phase 10 - docs, sweep, diagrams
- [ ] Phase 11 - gate, merge, move to done

## Decision / revisit conditions

- **Phase 0 is not optional and not deferrable.** Without W2 there is no property-update path at all;
  without W4 the claim index has no repair path and an out-of-process owner cannot rebuild it; without
  W5 deletion is unsafe. The identity model is not implementable on today's platform.
- **The minimal blueprint's budget is a finding, not a target.** Raising it silently defeats its only
  purpose.
- **The vocabulary's *scope* field is load-bearing.** Getting it wrong produces a false merge between
  two users' devices on first sync. It is data with a validator for that reason.
- **No similarity signal ever influences a merge.** Fixed, no trigger.
- **No durable sidecar state.** Fixed by your decision; the trigger for revisiting is a second
  Fallen-8 target or a second writer to the same namespace.
