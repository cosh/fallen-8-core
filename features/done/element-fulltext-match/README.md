# Element fulltext match

Search across all properties of the element a delegate fragment is holding - and make the
per-field idiom crash-proof. Two things shipped (contract record: [spec.md](spec.md)):

- **`AnyPropertyValueMatches(Func<string, bool> valuePredicate)`** on `AGraphElementModel`:
  an allocation-free test over the element's string-typed property VALUES (names never
  reach the predicate; reserved embedding entries are skipped). Match semantics stay in
  the BCL - there is deliberately no match-kind enum.
- **`TryGetProperty<T>` is-pattern**: a missing property, a wrong-typed value, or a stored
  `null` reads as `false` - never an `InvalidCastException` out of a compiled filter. The
  engine's undo/conflict paths keep null-presence via the internal `TryGetPropertyRaw`.

```csharp
return (v) => v.Label == "company"
           && v.TryGetProperty(out string name, "name") && name.Contains("Tech")
           && v.TryGetProperty(out string ind, "industry") && ind.EndsWith("Solutions");

return (v) => v.AnyPropertyValueMatches(s => s.Contains("Tech", StringComparison.OrdinalIgnoreCase));
```

The user-facing story lives on the docs site's
[Delegates page](https://docs.fallen-8.com/delegates/) (accessor table +
troubleshooting). The NL-assist impact (new member on the fragment type surface, new
scenario classes, prompt rule allowing predicate-argument lambdas) is logged as the
2026-07-29 PENDING entry in [`nl-assist-finetune/RETRAIN-LOG.md`](../../../nl-assist-finetune/RETRAIN-LOG.md);
dataset/eval tooling is wired, the fine-tune run drains it. Tests:
`fallen-8-unittest/ElementFulltextMatchTest.cs`.

Conscious test deferral: of the six `TryGetPropertyRaw` call sites, the single-set undo,
remove undo, and batch conflict check carry per-site mutation pins; the batch apply-phase
undo and the two embedding prior-value captures are correct by the same mechanism but
reachable only through residual post-validation throws, so pinning them would need fault
injection - deferred until one of those paths changes shape.
