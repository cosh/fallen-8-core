# AUTOSAR ARXML integration: implementation plan

The spec is [spec.md](spec.md) and owns every rule. This owns the order the work lands in and
what makes each phase done. The ordering principle is the one that served the integrations
feature: **each phase is verifiable when it lands**, and the piece other code depends on
(the vocabulary) lands before anything depends on it.

Branch: `feature/autosar-arxml`. Feature code never lands on `main` directly. Commit
messages are honest and concise and reference no assistant.

The parsing design is already validated: a prototype with exactly this path-stack and
two-stage resolution processed a production 82 MB extract with zero unresolved references
(spec section 8). The implementation work is therefore mostly contract work: the vocabulary
entry, the provider mapping, the diagnostics, and the fixtures that make every rule fail.

## Phases

| Phase | What lands | Done when |
| --- | --- | --- |
| **0. Vocabulary** | the `trim` canonicaliser in the closed set; the `arxml-path` entry in `identifier-vocabulary.v1.json` exactly as spec section 5 states it | the new entry has the same per-entry tests every existing entry has (canonicaliser behaviour, accept pattern, anchoring), plus one asserting case is PRESERVED (the reason `trim` exists); any test pinning the entry count updates; a provider declaring `arxml-path` passes catalog construction |
| **1. The parser** | `Providers/AutosarArxml/ArxmlReader.cs`: the hardened `XmlReader` settings, the namespace gate, the short-name path stack, the interest set of spec section 4, subtree reads, reference collection, the two-stage resolution including the `PDU-TRIGGERING` indirection, and the unit denormalisation onto signals (`implements` then `scaledBy`); an internal model type, no contract types yet; the synthetic fixture of spec section 11 | every parsing rule has a test that fails when the rule is inverted: path reconstruction through nested and unnamed elements, each PDU kind mapped, `carries`/`secures` resolved through triggerings, port direction deciding `sends` versus `deliversTo`, frame timing landing on the frame, DE/EN description selection, the unit landing on the signal and staying ABSENT when any link of its chain is missing, `duplicatePath` and `unresolvedReference` each produced by a negative fixture, a DTD refused, a foreign namespace refused naming what was found |
| **2. The provider** | `AutosarArxmlProvider`: descriptor exactly as spec section 3, `ObserveAsync` mapping the model to `SnapshotDocument` (complete, claims, prefixed properties, deduplicated relations), `IObservableProvider`; catalog registration; the regenerated descriptor snapshot | `TheShippedArxmlProviderConforms` passes offline; two runs over one unchanged fixture report `issuedMutations` false on the second; a file with no FlexRay cluster fails the run and withdraws nothing; a missing `file` setting is a configuration failure; an empty-but-valid cluster still yields a complete snapshot (its emptiness is real); the rendered summary of the fixture's odometer signal carries its name, BOTH descriptions and the unit, and removing any hole from the template turns that test red (spec section 9's offline half); `ProviderDescriptorSnapshotTest` green after `scripts/update-provider-descriptor-snapshot.ps1` |
| **3. Docs and captures** | the provider section on `docs/src/content/docs/integrations.md`, including the worked semantic-search recipe of spec section 9 (opt-in, vector index, `POST /embedding/search`, the multilingual-model caveat); the stale-count sweep (root `CLAUDE.md` quality-gates bullet, the docs page, anything else a grep for "three" near "provider/descriptor/blueprint" finds); recaptured `screen-integrations.png` per the standing capture pipeline | `npm --prefix docs ci && npm --prefix docs run build` green (link check); the screenshot shows the four-provider list; no count of shipped providers survives anywhere it can go stale |
| **4. The merge gate** | the full suite; the impact sweep of spec section 12 re-checked against the tree as it now is; the live semantic acceptance; the adversarial pass; the hygiene greps | branch green (`dotnet build` + `dotnet test`, warnings as errors); the fixture network run into a compose environment with the embedding sidecar and the "kilometer" query ranking the odometer signal above the near-miss speed signal, recorded in the PR (spec section 9's live half); the adversarial pass names each invariant it examined and, for each, the mutation that turns a named test red; a grep over the whole diff finds no OEM-derived string (spec section 11), no em dash and no en dash |

## Rules the work is held to

Carried over from the integrations plan, because they were paid for there:

1. **Before writing a check, name the fixture that makes it red**, and write that fixture in
   the same commit. A check that cannot fail is worse than no check.
2. **Mutation-test the checks that matter, once, by hand**: the case-preservation test
   (fold case in `trim` and watch it fail), the direction test (swap `IN`/`OUT` and watch
   sends/deliversTo invert), the completeness rule (return an empty snapshot for a
   cluster-free file and watch the withdrawal test fail), and the semantic payload (drop
   `{arxml.descDe}` or `{arxml.unit}` from the template and watch the summary test fail).
3. **Read the platform, do not remember it.** The descriptor, context and snapshot shapes
   used here were read out of the contract types; anything further the implementation needs
   (catalog registration, conformance seams) is read the same way before code depends on it.
4. **Correct prose and code in the same pass**: when a rule here changes, grep the tree for
   its wording before the commit closes.
5. **Fixtures are synthetic, always** (spec section 11). The production extract that
   validated the design never enters the repository, in any form, at any size.

## Progress

- [ ] Phase 0: vocabulary
- [ ] Phase 1: the parser
- [ ] Phase 2: the provider
- [ ] Phase 3: docs and captures
- [ ] Phase 4: the merge gate
