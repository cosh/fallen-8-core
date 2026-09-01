# CAN clusters in the AUTOSAR ARXML integration: implementation plan

> Companion to [spec.md](./spec.md). Phases are ordered so that each one leaves the tree green and
> the riskiest change is landed first, under test, before anything depends on it.

## Ordering, and why

The union rule (FR-2) goes first even though CAN is the feature. It changes the reader's core
contract, it changes FlexRay behaviour, and every later phase's measurements are wrong if it lands
afterwards. The two shipping defects (FR-2, FR-4) are therefore fixed before the capability is added,
which also means each is verifiable on its own against the FlexRay-only reader that exists today.

The job ceiling (FR-5) goes last of the runtime work, because until CAN is read there is nothing that
needs a raised ceiling in one job, and a raised ceiling with nothing to carry is an untested number.

## P0 - the fixture problem, before any code

The measured export is a third party's confidential data and cannot enter the repo: it names a
manufacturer and a real person. Every existing ARXML test uses hand-written fixtures, and this phase
extends them rather than importing anything.

Build the minimum synthetic set that reproduces each measured structure:

- a CAN extract: one `CAN-CLUSTER`, one `CAN-PHYSICAL-CHANNEL`, baudrate and protocol fields, two
  `CAN-FRAME-TRIGGERING`s with `IDENTIFIER` and `CAN-ADDRESSING-MODE`, their frames, PDUs and
  signals, and an ECU with a CAN connector;
- a second CAN extract that **re-declares the first's cluster path** with different channel content,
  which is the collision that FR-2 and FR-3 exist for;
- a third that shares a signal path with the first, which is the ordinary catalogue collision;
- a FlexRay extract and a CAN extract that share one ECU, each carrying only its own connector,
  which is the gateway case;
- an extract carrying an `ETHERNET-CLUSTER` and nothing else readable, for FR-4.

Gate: the fixtures are asserted to reproduce the measured cardinalities (1:1 frames to triggerings
to identifiers, definitions directly under the channel), so a later reader can trust them as a stand
in for the real export. No repo file names the manufacturer, and `CodeQualityTest` conventions hold.

## P1 - FR-2, the cross-file union

`Collected.Add` changes from owning a subtree to owning an element; every collector's early return
(`ArxmlReader.cs:372-376, :438-440`) changes to continue into children. The docstring at
`:1210-1226` is rewritten in the same commit, because it currently argues for the behaviour being
removed.

Tests: the two-extract cluster collision unions its channels; the ordinary catalogue collision still
first-wins per child; the gateway ECU ends up attached to both buses; emission order is asserted
stable across two runs over the same set in a different file order, which is what the conformance
suite's `Deterministic` check demands and what a dictionary-backed union would break.

Gates: `dotnet test`, and the conformance suite in full. FlexRay import counts are re-measured and
recorded here, because this phase can change them and a later phase must not be blamed for it.

## P2 - FR-4, the half-read snapshot

The bus gate at `AutosarArxmlProvider.cs:202-229` gains: a diagnostic per unread cluster element kind
present in the set, with the count of files it appeared in; and a refusal narrowed to "no bus of any
kind this version reads", with the FlexRay-specific wording removed.

Tests: a set carrying an Ethernet cluster and a FlexRay cluster imports, and names `ETHERNET-CLUSTER`
as unread; a set carrying only an Ethernet cluster still fails; the failed run withdraws nothing,
which the conformance suite's `UnreadableSourceFails` already requires and which is asserted here
directly. `IntegrationsBlueprintTest.cs:1591` and `:1875` are rewritten rather than deleted.

## P3 - FR-1 and FR-6, CAN itself

The protocol table, its two `Collect` arms, the three parameterised literals in `CollectCluster`, the
CAN triggering payload, and the new properties. `slotId`, `baseCycle` and `cycleRepetition` become
documented-optional.

Tests, in the order they earn their keep: the CAN fixture imports to the expected topology; a CAN
frame carries its identifier and addressing mode and no slot; a FlexRay frame is unchanged, asserted
against the counts recorded in P1; both protocols in one set produce one graph with two networks; the
protocol-neutral properties appear on both; a property key containing any of the six banned
substrings fails the conformance suite, asserted so the rule is visible rather than remembered.

## P4 - FR-3, the cluster-collision diagnostic

Separate from P1 on purpose: P1 makes the union correct, this makes it visible. A new
`ArxmlDiagnosticKind` plus its `CodeOf` arm, which is where the dossier found a trap worth fixing in
passing: a new kind without an arm **compiles** and throws `ArgumentOutOfRangeException` mid
`ObserveAsync` (`AutosarArxmlProvider.cs:275-297`), despite an XML doc claiming the hole is
compile-time. Either the switch becomes exhaustive or the doc stops claiming it.

Tests: the cluster collision emits the new code and the catalogue collision does not; both texts are
asserted, since the existing one calls a collision "the expected case rather than a fault" and that
is now true of only one of the two.

## P5 - FR-7, the PDU flow path

`PDU-TRIGGERING/I-PDU-PORT-REF -> sends`/`deliversTo`. Additive, and it changes FlexRay edge counts,
so this phase updates the counts recorded in P1 and the figures quoted in
`features/done/autosar-arxml/spec.md`.

Tests: a PDU triggering with an out port produces `sends`, with an in port produces `deliversTo`; the
FlexRay fixture's new edges are asserted by count, not by absence.

## P6 - FR-8 and FR-5, volume and the ceiling

Diagnostics aggregate per kind per file, sized against a volume re-measured after P1 and P5 rather
than against the figure the spec quotes, which is the pre-fix one.
`Integrations:MaxJobFileBytes` default rises to **560 MiB** (587,202,560), which touches the options
type, the setting catalogue if it is listed there, the startup banner's number, and the three
documented ceilings.

The number is a shared constraint with `integration-file-transport`, not a free choice: over the JSON
arm base64 expands by a third, so the largest job that arm can deliver inside the fixed 767 MiB
effective budget is 575.25 MiB decoded. Anything above that has the runtime accept jobs the proxy
refuses with a bare 413. Assert it: a test that the default is at most 575 MiB, with the arithmetic
in its message, so the next person to raise it is stopped by a test rather than by a support case.

Gates: `SettingCatalogTest`, the banner tests added by `integration-file-transport`, and a check that
`GET /integrations/limits` reports 560 MiB with no code change to the limits route.

## P7 - the record and the sweep

The descriptor's protocol-neutral `description`, its regenerated snapshot, and the recaptured
`screen-integrations.png`. The docs pass: the AUTOSAR section, the three ceilings, and the FlexRay
only claims at `integrations.md:25, :561, :577, :585`. The corrections to
`features/done/autosar-arxml/spec.md` (its struck trigger, its scope, its 99% figure, and its
entity-model assessment marked confirmed for CAN and refuted for Ethernet). The cross-feature sweep
recorded in the spec, including recipes and stored queries. Then move this directory to
`features/done/`.

## Verification that is not a unit test

Three things the suite cannot establish, to be done before the council gate:

1. **A live run over the real export**, one job, one identity, all 11 CAN and FlexRay files, against
   two real processes. The suite's fixtures are synthetic by necessity, so the only evidence that
   this works on the data it was built for is running it. Record element and edge counts, wall clock,
   and peak resident memory in `findings.md`. The expected network count is **9**, not 11; if it
   comes back lower, the bug is in the union rule, and if the response is ever to make a cluster's
   identity file-specific, stop, because that is the claim-key change measured as destructive.
2. **A second identical run**, asserting zero withdrawals. This is the reconciliation claim, and it
   is the one whose failure destroys data.
3. **A re-run over a deliberate subset**, asserting the difference *is* withdrawn. The contract cuts
   both ways and the destructive direction deserves a measurement too, not just a sentence.

## Gates, every phase

`dotnet build` clean under warnings-as-errors; `dotnet test`; the conformance suite's twelve checks;
`ProviderDescriptorSnapshotTest` with its regenerated snapshot and recaptured screenshot when the
descriptor moves; `CodeQualityTest`; the link-checked docs build. No OpenAPI or MCP work is expected,
since no REST operation is added, and the browser probe is unaffected because this deployable
references neither the engine nor the apiApp. Each of those two expectations is confirmed in P7
rather than assumed.
