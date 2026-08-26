# First-run walkthrough - living notes

The behaviour and the auto-show rules are specified in [spec.md](spec.md) and [plan.md](plan.md),
which are the historical record of how this landed. This file carries only what is still LIVE.

## The show is MODAL, and that is load-bearing for every test that follows it

`FirstRunOverlay` is a Radix `Dialog`, so while it is open two things are true that surprise a
test author, and both have cost CI real time:

- Every sibling carries `aria-hidden`, so content behind it is unreachable BY ROLE. A Playwright
  `getByRole` query reports "element(s) not found", NOT a wrong-text mismatch, which sends the
  reader looking for a missing element instead of a covering dialog.
- Its scrim is viewport-wide, so it owns every pointer event until dismissed.

Any suite that reaches a graph screen of an empty namespace must dismiss the show before asserting
or clicking anything behind it. Both e2e suites got this wrong once and were fixed on 2026-08-26;
`e2e/studio.spec.ts` states the rule on its own `dismissFirstRunIfPresent` helper, which is the
place to look.

## Known issue: a scope-key flip can un-dismiss the show

The dismissal is remembered per `useNamespaceSignals().key`, which is `useBoundInstance()?.id`.
That id is `<instanceId>/<namespace>` until `/ns` is known to be unsupported, and the bare
`<instanceId>` afterwards. A dismissal recorded under one key does not cover the other, so if that
flip lands AFTER a dismissal, the show re-opens on a namespace the operator already dismissed it
for.

Not reachable in the test fixtures, where the flip resolves within a second of load. **Revisit
trigger:** a pre-namespace or slow server, where the flip can land late enough for an operator to
have dismissed the show first.

Related, on the embed's side rather than the walkthrough's:
[features/done/studio-embeddable/README.md](../studio-embeddable/README.md).
