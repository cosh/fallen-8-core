# F8 Studio: Phased Implementation Plan

> **Status:** Satisfies the `plan.md` deliverable (spec §11.2). Split out of
> [design.md](./design.md) §9; design.md is the source of truth for implementation
> approach, and unprefixed section references below (§2.2, §2.3, §3.3, …) resolve
> against it. Requirements and the API contract remain governed by
> [spec.md](./spec.md) and [openapi-v0.1.json](./openapi-v0.1.json). The NL-assist
> model backend (phase 9) is specified in [nl-assist/spec.md](./nl-assist/spec.md).

Order follows spec §11.2. Each phase ships its tests.

1. **Foundation.** Scaffold (Vite + TS + Tailwind/Radix + TanStack Router/Query +
   Zustand). OpenAPI types + instance-bound client (§2.3). Instance registry, switching,
   per-instance store factory (§2.2). App shell and nav. Backend G-1 (static serving +
   CORS). *Tests:* API client route/serialization correctness against the OpenAPI file;
   per-instance state scoping (two instances never share/leak context).
2. **Dashboard + admin.** Counts, memory, plugin lists; save/load/trim/tabularasa with
   typed confirmation and rollback-as-failure; demo-data playground. *Tests:* destructive
   confirmation gating; 500 surfaced as failure.
3. **Element browser.** Element fetch, adjacency, degrees, bulk graph with truncation.
   *Tests:* truncation detection.
4. **Query workspace.** Five scans, typed-literal editor, batched hydration, to-table and
   to-canvas. *Tests:* typed-literal conversions.
5. **Graph canvas.** Sigma + graphology, force + circular layouts, label styling,
   selection to detail panel, expand-on-demand, degradation. *Tests:* component render;
   expand merges without duplicating elements.
6. **Delegate editor, static IntelliSense.** Component, type-model.json + providers,
   per-kind snippets, snippet library. *Tests:* completions from the static model per
   parameter type; marker rendering.
7. **G-2 validation.** Backend endpoint + position mapping (MSTest), client wiring
   (debounced validate, markers, submit-blocking). *Tests:* backend cases per spec §10;
   client diagnostic-position mapping (no double-map).
8. **Path finder.** Algorithm options + explainer, defaults, five slots (advanced tier),
   validate-before-submit, results + overlay. *Tests:* scenarios 5 and 6.
9. **NL assist (FR-26/G-6).** Per [nl-assist/spec.md](./nl-assist/spec.md): global model
   config, prompt assembly from §6.1/§6.2, insert-as-editable, validation loop,
   disabled-without-backend. *Tests:* generation prompt includes the required context per
   kind; insertion; disabled state (model calls mocked); nl-assist spec §13.
10. **Subgraph studio.** Lifecycle, pattern builder with alternation/direction/length
    rules, status-code mapping, empty-as-valid, diagnostics back into the right slot.
    *Tests:* scenario 7; pattern-sequence validation.
11. **Mutations + polish.** Create/update/remove with `waitForCompletion=true`;
    accessibility (keyboard nav for tables/forms, non-pointer canvas equivalents where
    feasible, legible light/dark); error/empty/disconnected states hardened.
    *Tests:* scenarios 8 and 9.

Delivery (spec §11.3): branch `feature/web-ui`, delivered via PR. Commit messages and PR
text are honest and concise and do not reference an AI assistant. The full testing
summary is in [design.md](./design.md) §10.
