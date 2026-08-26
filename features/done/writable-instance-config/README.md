# Writable instance configuration - living notes

The behaviour, the tier model and the two-act write gate are specified in [spec.md](spec.md)
and [plan.md](plan.md), which are the historical record of how this landed. The user-facing
documentation is <https://docs.fallen-8.com/configuration/>. This file carries only what is
still LIVE about the feature: things a later change has to know.

## Where the contract actually lives

- `fallen-8-core-apiApp/Configuration/Fallen8SettingCatalog.cs` owns the catalog. Its
  invariants are structural rather than merely tested: `Tier` is derived from `ApplyMode`
  through an exhaustive switch with a throwing default, and only three factory methods
  (`NotWritable`, `Restart`, `Live`/`LiveForNewWork`) can build an entry, so a contradictory
  tier and apply-mode pair cannot be constructed.
- `Fallen8ConfigOverridesSource.IsAuthority` is the single predicate for "this provider's
  value can never be overridden by a stored one". `Fallen8ConfigOverrides.Classify` delegates
  its default branch to it, so a new authority provider type reports environment-grade
  instead of the writable-looking `host`.

## Known risks, with the trigger that makes each one real

Recorded by the 2026-08 code-health review. None is reachable today; each names the change
that would make it reachable, so whoever makes that change finds this.

1. **Two bindings of `Fallen8:Security:EnableConfigurationWrite` at different capture
   times.** The authorization policy closes over a POCO bound before `builder.Build()`, while
   the value `AdminController` publishes as `configWriteEnabled` comes from
   `IOptions<Fallen8SecurityOptions>`, resolved on first use. The write gate itself stays
   correct either way, because the policy is authoritative; what could disagree is the
   Studio editor's decision to show a working Save button.
   **Trigger: relaxing or re-ordering the policy, or making the setting live-tier.**
2. **The reload-token subscription happens after the namespace collection and the overrides
   layer are constructed.** A configuration reload firing inside that boot-time gap is not
   applied until a later reload.
   **Trigger: making startup slower or asynchronous between those two points.**
3. **`IsLoaded` means "an engine is attached", not "its checkpoint has been restored".** The
   namespace data-loss guard is safe today only because the boot loop is sequential and
   single-threaded, so nothing observes a partially booted namespace. `StartupState` states
   that invariant; the guard does not assert it.
   **Trigger: parallel boot loading, which the spec already names as a revisit item.** If it
   lands, `IsLoaded` must start meaning "restored" or the guard must check separately.
