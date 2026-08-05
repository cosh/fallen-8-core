# Audit defects: specification

## Why

The 2026-08-05 documentation freshness audit read the implementation behind every user-facing claim on all 31
docs pages. Checking whether a doc was true meant deciding what the code actually does, and that turned up
defects in the code. [`report.md`](report.md) is the verified list: 35 confirmed, 7 needing a maintainer
decision, 14 refuted or duplicate, plus 22 further leads found while verifying.

This feature fixes the confirmed set. It is a defect batch, not a capability: no new user-facing feature, and
every change is traceable to one numbered entry in the report.

## Scope

**In scope: the 35 confirmed defects.** Grouped by the change they need:

| Group | Defects | Nature |
| --- | --- | --- |
| Engine correctness | B10, B18, B25, B26 | Wrong results from the engine or its REST projection |
| Plugin reach | B04, B28, B49 | A registered plugin that can be registered and listed but never invoked |
| Index and type guards | B14, B19 | A silent projection desync, and an unguarded 500 where 400 is documented |
| Persistence | B39, B43 | A phantom save-game registration, a lock bypass on service deletion |
| Namespace atomicity | B31 | A rename committed before its sibling field is validated |
| Contract and OpenAPI | B05, B06, B07, B16, B17, B29, B34, B42, B52 | Published samples and statuses that cannot be produced |
| Studio and dev loop | B12, B53 | A request the UI can build but the server rejects; a stale dev proxy |
| Codegen | B09, B24 | A missing cap, and Roslyn work for a request with nothing to compile |
| Environment scripts | B01, B45, B51 | A pre-seed that misses a model, and two banners that mislead |
| Cosmetic and stale | B08, B13, B20, B33, B44, B55, B56 | Comments, messages, log lines, launch profiles |

**Out of scope, deliberately.**

- The 7 decisions in the report's own section. Each is real, and each has a fix whose shape is a product choice
  (a ceiling value, a security posture, an architecture change). They stay open until the maintainer rules.
  B54 is the one to look at first: a Test Explorer e2e run can erase a real local graph.
- The 22 unraised leads. They have one citation each and no adversarial pass. Promoting one into this batch
  means verifying it first, at which point it earns a Bnn.
- B22 (no execution budget on compiled fragments) is called out separately because it is the only entry whose
  honest answer may be "this cannot be fixed in-process". A compiled fragment runs with full trust on the
  request thread, and .NET has no safe way to abort it. Any real fix is an isolation boundary, which is a
  feature, not a patch.

## Contract

No REST route is added or removed. Three kinds of observable change are deliberate and must be called out in
the change log:

1. **B18 changes a response value.** `modificationDate` currently reads as 1970 plus the modification delta on
   every element read. After the fix it reads as the real timestamp. Any client asserting the broken value
   flips.
2. **B29 and B34 change the OpenAPI document**, so the pinned snapshot is regenerated. Additions and
   corrections only.
3. **B04, B28 and B49 make a registered plugin reachable** where it previously answered 404 or had no selector.
   That widens what a caller can invoke, which is the point, and it stays behind the existing
   dynamic-plugin capability gate.

## Definition of done

- Every confirmed defect either fixed, or moved to the decision section with a reason. No silent drops.
- A test pins each behavioural fix. Cosmetic entries (comments, log strings, launch profiles) need no test and
  must not grow one for form's sake.
- `dotnet build` clean at zero warnings, full suite green, OpenAPI snapshot regenerated where the contract
  moved, MCP coverage and contract gates green.
- `report.md`'s status column reflects reality, so the file stays useful as the record of what was decided.

## Impact on existing features

- **element-embeddings / vector-index:** B14 closes a hole that let a caller desync a bound vector index. The
  documented behaviour on [indexes](../../../docs/src/content/docs/indexes.mdx) already says the removal routes
  are not refused, so that page needs a one-line correction when the fix lands.
- **graph-namespaces:** B31 makes `PATCH /ns/{name}` all-or-nothing. No contract change, but the failure mode
  users may have seen (a rename that stuck despite a 400) disappears.
- **plugin-registration:** B04, B28 and B49 remove the two documented reach gaps. The living doc names them as
  known gaps today, so its "Reach, per contract" paragraph and the MCP page's matching sentence both need
  updating in the same PR.
- **nl-assist:** none of these changes touch the delegate-fragment surface the model drafts against, so no
  entry in `nl-assist-finetune/RETRAIN-LOG.md` is needed.
- **openapi-10:** the snapshot moves. Expect additions plus the two corrected defaults.
- **docs site:** B01 and B14 each invalidate a sentence that currently documents the defect as expected
  behaviour ([running](../../../docs/src/content/docs/running.mdx) on the offline pre-seed, indexes on the
  bound-index removals). Both are corrected as part of the fix, not afterwards.
