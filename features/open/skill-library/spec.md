# Fallen-8 Skill Library — Specification

> **Status:** Draft, spec only (no implementation yet). Follow the feature workflow in the
> repository root `CLAUDE.md`. Feature branch: `feature/skill-library` (branch-only workflow —
> no GitHub issue/PR).
>
> **Interpretation note (please correct if wrong):** "skill library" is read here as a
> curated library of **Agent Skills** — folders with a `SKILL.md` in the open agent-skills
> format — that teach AI agents (Claude Code and other skill-capable runtimes) how to work
> with a running Fallen-8. The alternative reading — a *server-side* library of stored,
> reusable delegate/traversal definitions inside the engine (i.e. stored procedures) — is
> explicitly **not** this feature; that engine feature now exists as
> [stored-query-library](../../done/stored-query-library/) (skills should teach the
> register-once/invoke-by-name flow where it fits).
>
> **Companion feature:** [mcp-server](../mcp-server/spec.md) gives agents the *tools*;
> this library gives them the *procedural knowledge*. Neither blocks the other: the skills
> ship REST-first and gain an MCP-alignment pass once the MCP server lands.

## 1. Overview / problem

An agent pointed at Fallen-8 today has to reverse-engineer everything from the OpenAPI
document and the README: how to model a domain as a property graph, which index fits which
query, the exact delegate-fragment contract the dynamic endpoints compile, the transaction
idiom (`waitForCompletion`), the security posture flags, the save-game lifecycle. All of that
knowledge exists — scattered across `CLAUDE.md` (contributor-facing), feature docs, controller
XML docs, and the NL-assist prompt contract — but none of it is packaged for an *agent that
uses* Fallen-8 rather than develops it.

Agent Skills are the established packaging for exactly this: a directory with a `SKILL.md`
(YAML frontmatter: `name`, `description`; markdown body: instructions) plus optional
`references/` and `scripts/`, loaded by the agent runtime via progressive disclosure (the
description is always in context; the body loads when relevant; references load on demand).

This feature ships a **versioned, tested skill library in this repository** covering the
Fallen-8 surface, installable into Claude Code (per-project or via plugin packaging) and
usable by any runtime that honours the format.

**What makes it a feature and not "just docs":** the library is held to the repo's test bar.
Every skill is validated in CI — frontmatter contract, link/file integrity, endpoint
references checked against the pinned OpenAPI snapshot, and (uniquely) **every delegate
example compiled against the real engine** — so the skills cannot silently rot as the API
evolves.

## 2. Goals / non-goals

**Goals**

- A top-level **`skills/`** directory (product artifact, sibling of `features/`), one
  sub-directory per skill, each self-contained: `SKILL.md` + `references/` + `scripts/` as
  needed.
- **v1 catalog** (five skills, §3.2): graph modeling, REST API usage, delegate authoring,
  subgraphs, operations.
- **Format compliance** with the open agent-skills conventions: `name` (lowercase,
  hyphenated, ≤ 64 chars, matches the directory), `description` (≤ 1024 chars, written as a
  *trigger* — what the skill does + when to use it), lean body (≤ 500 lines), deep material
  pushed to `references/`.
- **CI-enforced accuracy** (§3.3): a `SkillLibraryTest` MSTest class in `fallen-8-unittest`
  that fails the build when a skill drifts from the actual system.
- **Distribution**: (1) documented copy-install into `.claude/skills` / `~/.claude/skills`;
  (2) **Claude Code plugin packaging** (`.claude-plugin/plugin.json` + `marketplace.json`) so
  `claude plugin marketplace add cosh/fallen-8-core` → install works from this repo.
- **Single-source discipline:** where authoritative contracts already exist (the NL-assist
  prompt contract for delegates, the OpenAPI snapshot for endpoints, feature READMEs for
  posture flags), skills *reference or derive from* them — never fork the facts.

**Non-goals**

- **Server-side skill storage** — no engine/API surface changes at all in this feature; the
  apiApp and engine are untouched.
- **Auto-generation of skills from OpenAPI** — the value is curation (idioms, pitfalls,
  decision guidance), which generation cannot produce; the OpenAPI snapshot is the *checker*,
  not the author.
- **Contributor skills** for developing Fallen-8 itself — that is `CLAUDE.md`'s job.
- **Shipping skills inside the Docker image / server** — skills live with the agent, not the
  database.
- **A general skill marketplace** — one repo, one plugin, Fallen-8 skills only.

## 3. Design sketch

### 3.1 Layout

```
skills/
  fallen8-graph-modeling/SKILL.md
  fallen8-rest-api/SKILL.md
    references/endpoints.md          machine-checkable endpoint inventory
  fallen8-delegates/SKILL.md
    references/delegate-contract.md  derived from the NL-assist prompt contract
    references/examples.md           every fragment compile-verified by CI
  fallen8-subgraphs/SKILL.md
  fallen8-operations/SKILL.md
.claude-plugin/
  plugin.json                        plugin: name, version, skills path
  marketplace.json                   makes the repo installable as a marketplace
```

### 3.2 v1 catalog

| Skill | Teaches | Grounded in |
|-------|---------|-------------|
| `fallen8-graph-modeling` | Modeling a domain as an F8 property graph: labels, properties, when to index (index types and their scan endpoints), edge direction conventions, supernode cautions, ID semantics. | engine model + index feature docs |
| `fallen8-rest-api` | Operating the REST surface: auth header, versioning, the transaction idiom (`waitForCompletion` — never act on an unapplied write), scan vs. get, the problem+json error contract, rate-limit/413 behaviour. | OpenAPI snapshot + `api-error-contract` + `api-security-boundary` READMEs |
| `fallen8-delegates` | Authoring the C# filter/cost fragments: the "return a lambda" shape, `TryGetProperty` idiom, the exact `VertexModel`/`EdgeModel`/`AGraphElementModel` members, the validate-before-use loop (`/delegates/validate`), the security posture (`EnableDynamicCodeExecution`, trusted-as-the-process honesty note). | NL-assist prompt contract (`features/done/web-ui/nl-assist/spec.md` §5) + `DelegateValidationHelper` |
| `fallen8-subgraphs` | Subgraph recipes/patterns: defining, registering, recalculation semantics, quotas, code-free vs. code-bearing recipes. | `features/done/subgraph/` + `subgraph-quotas` |
| `fallen8-operations` | Running F8: durability modes, save-games, WAL recovery, the security flags, docker/compose deployment, TLS via a fronting proxy (deployment recipe — no in-app TLS by project decision). | `hosted-durability-lifecycle`, `save-games`, `api-security-boundary` docs |

Each skill body leads with *when to reach for what* (decision guidance), shows 2–4 canonical
request/response examples (curl + PowerShell), and links its references. Access-path phrasing
is REST-first in v1; the MCP-alignment pass (§3.4) adds "via MCP use tool `f8_…`" mappings.

> Follow-up: the landed [vector-index](../../done/vector-index/) feature's GraphRAG recipe
> (kNN scan → traverse from the hits) belongs in `fallen8-graph-modeling` or a sixth
> `fallen8-graphrag` skill when this catalog is authored.

### 3.3 CI enforcement (`SkillLibraryTest`, fallen-8-unittest)

1. **Frontmatter contract:** every `skills/*/SKILL.md` parses (the frontmatter is a flat
   `key: value` block — a small test-side parser, no new runtime dependency); `name` matches
   the directory, is lowercase-hyphenated, ≤ 64 chars; `description` non-empty, ≤ 1024 chars,
   contains both a "what" and a "when/use when" clause.
2. **Integrity:** every relative link/file reference in a skill resolves inside the repo; no
   skill exceeds the 500-line body budget; no absolute local paths; no secrets/keys.
3. **Endpoint drift-guard:** every `METHOD /path` token in fenced code blocks (and the
   `fallen8-rest-api` endpoint inventory) exists in the pinned OpenAPI snapshot
   (`features/done/web-ui/openapi-v0.1.json`) with that method. An API change that invalidates
   a skill fails the suite — the same drift-guard philosophy as the web UI's contract test.
4. **Delegate examples compile:** every C# fragment in `fallen8-delegates` (marked by fence
   info-string, e.g. ` ```csharp delegate:path-filter `) is run through the real validation
   path (`CodeGenerationHelper`/`DelegateValidationHelper` in-process — the engine is right
   there in the solution). A contract change that breaks published examples fails the suite.
   This is the library's strongest honesty guarantee and is unique to having the skills live
   in the engine's own repo.
5. **Plugin manifests:** `plugin.json`/`marketplace.json` parse and point at existing paths
   (v1: JSON well-formedness + path existence; `claude plugin validate` documented as the
   authoritative manual check).

### 3.4 Distribution & lifecycle

- **Copy-install (phase 1):** README section — clone/copy `skills/fallen8-*` into a project's
  `.claude/skills/` (or `~/.claude/skills/`); works for any skill-capable runtime.
- **Plugin (phase 2):** `.claude-plugin/marketplace.json` at the repo root lists a single
  `fallen-8` plugin whose `skills` point at `skills/`; users run
  `claude plugin marketplace add cosh/fallen-8-core` then install. Version field tracks the
  library (bumped on catalog/contract changes; per-skill changelog in the plugin README).
- **MCP alignment (phase 3, after `mcp-server` lands):** each skill's access-path sections
  gain the MCP tool names (`f8_scan_index`, …) alongside REST, plus a mapping table
  (endpoint ↔ tool ↔ tier) validated against the MCP server's tool inventory by test.

### 3.5 Voice & content rules (kept in a `skills/AUTHORING.md`)

- Skills are for **agents using** F8: imperative, decision-first, no marketing.
- Honesty notes carry over verbatim where they matter (dynamic code = trusted as the process;
  TLS first-request caveat; destructive admin endpoints).
- Every claim is either checkable by the CI gates or cites the governing feature doc.
- One skill = one job; overlap resolved by cross-linking, not duplication.

## 4. Acceptance criteria

- **Catalog present:** the five v1 skills exist under `skills/`, each format-compliant
  (frontmatter contract, body budget, references resolve).
- **CI gates live:** `SkillLibraryTest` enforces §3.3 items 1–5 and is green; deliberately
  breaking a skill (bad endpoint, non-compiling delegate example) fails the suite (verified
  once during development, then reverted).
- **Delegate examples proven:** every published fragment in `fallen8-delegates` compiles
  through the real engine validation path in CI.
- **Installable:** following the README copy-install instructions makes the skills visible to
  Claude Code in a fresh project; the plugin manifests parse and `claude plugin validate`
  passes (documented manual step).
- **Single-source respected:** the delegate skill's contract material derives from the
  NL-assist prompt contract (cross-referenced, not forked); endpoint facts trace to the
  OpenAPI snapshot.
- **No runtime coupling:** engine, apiApp, web UI, Docker image all byte-identical in
  behaviour; the only solution-level change is the new test class (and test-project file
  globs if needed).
- **Suite green**, build clean.

## 5. Risks

- **Interpretation risk** (§ header note): if "skill library" meant stored server-side
  procedures, this spec is the wrong shape — flagged prominently; the companion-feature
  framing (MCP tools + agent knowledge) is the best-fit reading of the request.
- **Doc rot** is the failure mode of all curated docs. Mitigation: the CI gates are the
  feature (§3.3) — endpoint drift and delegate-contract drift fail the build in this repo,
  the moment the drift happens, not when a user hits it.
- **Format evolution:** the agent-skills conventions are young. Mitigation: v1 uses only the
  stable core (name/description/body/references); plugin manifests are isolated in phase 2 so
  a format change touches packaging, not content.
- **Context bloat** (skills too big to be useful): enforced body budget + progressive
  disclosure via references.
- **Divergence from the NL-assist prompt** (two sources describing the delegate contract):
  the single-source rule + the compile gate keep both honest; the NL-assist spec remains the
  governing document for how the *model* is prompted.
- **Marketplace/plugin mechanics change faster than the repo:** copy-install (phase 1) is the
  guaranteed path; the plugin is additive convenience.

## 6. Keep (do not regress)

- **`CLAUDE.md` stays the contributor guide** — nothing moves out of it; the skill library
  addresses a different audience (agents *using* a running F8).
- **The OpenAPI snapshot's role** (`features/done/web-ui/openapi-v0.1.json`) as the pinned
  REST contract, already code-referenced by the web UI build — the drift-guard reads it
  in place; its path/consumers are unchanged.
- **The NL-assist prompt contract** (`features/done/web-ui/nl-assist/spec.md` §5) as the
  authority on delegate-fragment grounding for the *model*; the skill derives from it.
- **The security honesty notes** from `api-security-boundary` — reproduced, never softened,
  wherever a skill touches the dynamic-code surface.
- **Repo test discipline:** skills enter the same `dotnet test` gate as everything else.
