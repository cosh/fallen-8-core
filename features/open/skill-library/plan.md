# Fallen-8 Skill Library — Plan

Companion to [spec.md](./spec.md). Curated, CI-enforced Agent Skills for *using* Fallen-8.
Feature branch: `feature/skill-library` (branch-only workflow — no GitHub issue/PR).

Ordering principle: the enforcement harness comes first (it defines what a valid skill *is*),
then content in two waves (core surface, then ops/subgraphs), then packaging, then the
MCP-alignment pass which depends on the `mcp-server` feature landing.

## Phase 0 — Format contract + enforcement harness

Intent: `SkillLibraryTest` exists and fails on an invalid skill before any real skill does.

- [ ] `skills/AUTHORING.md`: the format contract (frontmatter fields + limits, 500-line body
  budget, references layout, voice rules, the fence info-string convention for checkable
  snippets, e.g. ` ```csharp delegate:path-filter ` and ` ```http endpoint `).
- [ ] `SkillLibraryTest` in `fallen-8-unittest` with a small test-side frontmatter parser
  (flat `key: value` block — no new runtime dependency):
  - frontmatter contract (name = directory, lowercase-hyphen, ≤ 64 chars; description
    non-empty, ≤ 1024 chars, contains "what" + "use when" clauses),
  - integrity (relative links/files resolve, body ≤ 500 lines, no absolute paths/secrets),
  - endpoint drift-guard: `METHOD /path` tokens in fenced blocks exist with that method in
    `features/done/web-ui/openapi-v0.1.json`,
  - delegate compile gate: fragments in marked fences run through the real
    `CodeGenerationHelper`/`DelegateValidationHelper` path in-process.
- [ ] A throwaway fixture skill proves each gate fails on violation (bad name, dead link,
  unknown endpoint, non-compiling fragment) — then the fixture is deleted; the gates stay.

## Phase 1 — Core skills (modeling, REST, delegates)

Intent: the three skills any agent needs on day one, fully gate-checked.

- [ ] `fallen8-graph-modeling`: labels/properties/indices decision guidance, edge-direction
  conventions, supernode cautions, ID semantics, the transaction idiom.
- [ ] `fallen8-rest-api`: auth header + posture flags, versioning, `waitForCompletion`
  ("never act on an unapplied write"), scan-vs-get decision table, problem+json error
  contract, rate-limit/413 behaviour; `references/endpoints.md` as the machine-checked
  inventory.
- [ ] `fallen8-delegates`: the "return a lambda" shape, `TryGetProperty` idiom, exact model
  members, the validate-before-use loop (`/delegates/validate`), the security honesty note;
  `references/delegate-contract.md` **derived from** the NL-assist prompt contract
  (`features/done/web-ui/nl-assist/spec.md` §5, cross-referenced, not forked);
  `references/examples.md` with compile-gated fragments.
- [ ] All Phase 0 gates green over the three skills; curl + PowerShell example parity.

## Phase 2 — Ops + subgraph skills

Intent: complete the v1 catalog.

- [ ] `fallen8-subgraphs`: recipes/patterns, registration, recalculation semantics, quotas,
  code-free vs. code-bearing recipes (and the double-gate posture for the latter).
- [ ] `fallen8-operations`: durability modes, save-games, WAL recovery, security flags,
  docker/compose deployment; TLS section referencing `transport-encryption` (kept honest —
  written against whatever state that feature is in when this phase runs).
- [ ] Cross-links between all five skills resolved per the one-job/no-duplication rule.

## Phase 3 — Packaging & install paths

Intent: installable by agents, not just readable in the repo.

- [ ] Copy-install docs (README section "Use Fallen-8 with AI agents", shared with the
  mcp-server feature): `skills/fallen8-*` → project `.claude/skills/` or `~/.claude/skills/`.
- [ ] `.claude-plugin/plugin.json` + `.claude-plugin/marketplace.json` (repo as a one-plugin
  marketplace: `claude plugin marketplace add cosh/fallen-8-core`); manifest JSON validity +
  path existence covered by `SkillLibraryTest`; `claude plugin validate` documented as the
  manual authoritative check.
- [ ] Library version field + per-skill changelog stub in the plugin README.
- [ ] Fresh-project manual verify (documented): skills discovered and triggerable in Claude
  Code after copy-install and after plugin-install.

## Phase 4 — MCP alignment (depends on `mcp-server` landing)

Intent: skills teach the MCP path as the preferred agent access, REST as the fallback.

- [ ] Each skill's access-path sections gain the `f8_*` tool names alongside REST calls.
- [ ] `references/mcp-mapping.md` in `fallen8-rest-api`: endpoint ↔ tool ↔ tier table,
  validated by test against the MCP server's actual tool inventory (extends the drift-guard).
- [ ] Tier/auth guidance mirrored from the mcp-server README (least-privilege defaults,
  destructive-tool cautions).
- [ ] If `mcp-server` has not landed when Phases 0–3 are done, this phase is a documented
  DEFER on the feature branch merge — the library ships REST-first (valid outcome per the
  repo workflow), and this phase becomes a follow-up on the same feature directory.

## Phase 5 — Gate

- [ ] Full `dotnet test` green (including all `SkillLibraryTest` gates); build clean; no
  behavioural change anywhere in engine/apiApp/UI (docs + tests + manifests only).
- [ ] Council review per the repo merge gate (scaled to docs+tests risk); fix findings on the
  branch; `git merge --no-ff` to `main`; move `features/open/skill-library/` →
  `features/done/`.

## Progress

- [ ] Phase 0 — AUTHORING.md + `SkillLibraryTest` gates (frontmatter, integrity, endpoint
  drift, delegate compile)
- [ ] Phase 1 — `fallen8-graph-modeling`, `fallen8-rest-api`, `fallen8-delegates`
- [ ] Phase 2 — `fallen8-subgraphs`, `fallen8-operations`
- [ ] Phase 3 — copy-install docs + plugin/marketplace packaging
- [ ] Phase 4 — MCP alignment (or documented DEFER if mcp-server hasn't landed)
- [ ] Phase 5 — council gate, merge + move to done/

## Decision / revisit conditions

- **Agent Skills, not stored procedures** (spec interpretation note): revisit immediately if
  the user meant a server-side reusable-delegate library — that would be an engine/API
  feature with a very different shape.
- **Hand-curated, generation-checked:** the OpenAPI snapshot validates skills but never
  writes them. Revisit only if the catalog grows past maintainability (> ~10 skills).
- **In-repo library:** skills live with the engine so the compile/drift gates can exist.
  A separately-versioned skills repo becomes worth it only if release cadences diverge.
