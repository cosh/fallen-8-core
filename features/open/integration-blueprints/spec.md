# Integration blueprints - Specification

> **Status:** Open, spec only (no implementation yet). Follow the feature workflow in the repository
> root `CLAUDE.md`. Feature branch: `feature/integration-runtime` (shared with its two siblings;
> branch-only workflow, no GitHub issue/PR).
>
> **Siblings.** [integration-runtime](../integration-runtime/spec.md) owns the sidecar, the provider
> abstraction and the conformance verifier. [integration-identity](../integration-identity/spec.md)
> owns the claim model. This document owns the provider **contract** and the three reference
> providers, and it exists to prove the other two are usable.
>
> **Phasing lives once**, in the shared [plan](../integration-runtime/plan.md), because all three
> specs are one branch with one sequence.
>
> **Revision history:**
> - *2026-08-09a* - initial design. Vendor contracts fetched and recorded in sections 3 and 4;
>   see the fetch caveat in section 3.1.

## 1. The contract, and the rule that keeps it small

**Keep the contract as small as the three blueprints allow. Anything only UniFi needs belongs in the
UniFi provider, not the contract.**

That rule needs an enforcement mechanism, because it is the rule that always erodes. The mechanism
is the ordering in section 2: the **minimal blueprint is written first**, and it is the contract's
floor test. If a later blueprint needs the contract widened, the minimal blueprint has to still
compile and still fit its budget, which makes every proposed widening visible and cheap to argue
about while the contract is still cheap to change.

A provider implements exactly what
[integration-runtime](../integration-runtime/spec.md) section 4 lists: metadata, a JSON Schema for
configuration, a credential descriptor, a connectivity test, a declared label and property
vocabulary, optional declarations (summary template, free-text fields), and a fetch returning a
snapshot. It does not resolve identity, does not see the graph, does not see a vertex id, and does
not learn whether an entity it returned was created or matched.

**The intended third axis, named now so it is not designed out.** Event-driven sources (MQTT,
Zigbee2MQTT, Shelly) must be addable later without reopening the identity model or the snapshot
schema. The single thing that makes that possible is the snapshot's **completeness** declaration: a
complete snapshot licenses reconciliation to treat absence as withdrawal, a partial one does not.
Every v1 provider declares complete. Nothing else about event-driven sources is in scope, and
poll-and-snapshot must not be allowed to become load-bearing anywhere else in the contract.

## 2. Order, and why the minimal one is first

Approved order: **minimal, then UniFi, then Fronius**, with the conformance harness before all three.

The reason the minimal blueprint moved to the front: it is roughly a hundred lines whose only job is
to show the floor, so that an agent reading the examples does not infer that every integration needs
UniFi's machinery. Written first, it reports "the contract is too heavy" while the contract is still
cheap to change. Written last, UniFi has already shaped the contract around itself and the
hundred-line test either passes trivially or fails for reasons nobody can act on.

**If the minimal blueprint cannot be written in roughly a hundred lines, the contract is too heavy,
and that is a finding to report rather than a budget to quietly raise.**

## 3. Minimal blueprint: CSV device list

The floor. A CSV file on a mounted path, one row per device, columns for a MAC, a hostname, and a
free-form note.

Declares: metadata; a two-field configuration schema (path, delimiter); **no credential descriptor**;
a connectivity test that is "the file exists and parses"; a vocabulary of one label and three
property keys; one declared free-text field (the note); no summary template.

Fetch: read, parse, emit one entity per row with a *mac* claim (strong) and a *hostname* claim
(weak). No relations. Completeness: complete.

**What it proves.** No credentials, no pagination, no rate limiting, no topology, no readings, no
auth realities. If any of those turn out to be mandatory rather than optional in the contract, this
blueprint cannot be written and the contract is wrong.

**It is also the third case the identity model needs.** Because Fronius turns out to have no strong
identifier that overlaps with UniFi (section 4), the *strong* overlap case has no natural home among
the vendor blueprints. So the CSV blueprint carries it: a row whose MAC matches a UniFi-observed
device, adding a hostname and a note, is the strong-overlap acceptance test from
[integration-identity](../integration-identity/spec.md) section 7. It is also the cheapest possible
fixture to reason about, which is what you want for the case that must be exactly right.

## 4. UniFi

The many-entity blueprint: topology edges, and the provider that produces the claims others attach
to.

### 4.1 What was fetched, and what still must be

Targeted version: **Network API v10.4.57**, recorded here and to be repeated in the user
documentation, per the standing instruction to record the version targeted.

Verified from [developer.ui.com/llms.txt](https://developer.ui.com/llms.txt):

- every Ubiquiti service authenticates with an **X-API-KEY header**. There is **no OAuth and no
  third-party app consent flow anywhere**, so none is scaffolded. Keys are generated at unifi.ui.com.
- "Network and Protect APIs run locally on each UniFi host and are accessed through the Site Manager
  Cloud Connector or directly on-site."
- machine-readable specs live at `https://developer.ui.com/{service}/{version}/openapi.json`.

Verified from the v10.4.57 OpenAPI document:

- **pagination**: *limit* (default 25), *offset*, with *count* and *totalCount* on each page. Every
  list endpoint is paged and every one must be paged by the provider.
- **device**: *id* (uuid), *name*, *model*, *macAddress*, *ipAddress*, *state* (ONLINE, OFFLINE,
  PENDING_ADOPTION and others), *supported*, *firmwareVersion*.
- **device uplink**: *deviceId* (uuid), documented as "connection to the parent device in network
  topology".
- **client**: *id* (uuid), *name*, a *type* discriminator (WIRELESS, WIRED, VPN, TELEPORT),
  *ipAddress*, *connectedAt*, and a per-type *access* object. Clients are filterable by
  `macAddress.eq(...)`, and MAC values are conventionally lowercase without separators.

**Correction to the brief:** topology is **`uplink.deviceId`**, a nested object, not a flat
*uplinkDeviceId*. The brief's warning about the pre-2025 shape is right in substance: the *uplink*
object survived and the *mac* inside it did not, so nothing may rely on it.

**Fetch caveat, stated honestly.** My retrieval of the OpenAPI document was **truncated** and
returned schema definitions without the full path inventory. So the entity list below is grounded in
the verified schemas plus the documented resource set, and **the complete path and method inventory
must be re-fetched and types generated from the contract before any DTO is written**. Nothing in this
section is invented, but the path list is not yet complete and must not be treated as such.

### 4.2 Auth: three realities to support and document

All three are X-API-KEY or a legacy credential; none is OAuth.

1. **A local API key** generated in the Network application under Integrations. The v1 path.
2. **A Site Manager cloud key** from unifi.ui.com.
3. **Legacy username and password** for self-hosted Network Application installs, which do not
   support API keys.

**Ship the official local Network Integration API in v1, behind a source seam**, so the Site Manager
cloud API and the Connector Proxy at
`api.ui.com/v1/connector/consoles/{consoleId}/proxy/network/integration/v1` can be added later
without touching the provider's entity mapping. The seam is a fetch-transport interface; the mapping
above it is shared.

### 4.3 Behaviour

- **GET requests only.** Stated in the documentation **and enforced in code**: the transport rejects
  any other method, and a test asserts it.
- **429 handled with Retry-After.** A sync that is rate-limited backs off and reports a partial
  failure; it never emits a snapshot it knows is incomplete while declaring it complete.
- **Every list endpoint paged.** A provider that reads only the first page silently loses devices,
  which reconciliation would then interpret as **withdrawals and delete them**. This is the single
  most dangerous provider bug in the whole feature, so the conformance suite includes a fixture whose
  data spans three pages and asserts the full set is emitted.

### 4.4 Entities, claims, relations

Entities: *site*, *device*, *port*, *client*, *network*.

Claims: *mac* (strong) for devices and clients; the provider-native uuid ids (*unifi-site-id*,
*unifi-device-id*, *unifi-client-id*, strong, provider scope, since they are UUIDs); IPs as **weak**
claims.

Relations, all by claim: *contains*, *hasPort*, *uplinksTo*, *connectedVia*, *onNetwork*, *hasIp*.

Ships stored queries: the per-integration view, the topology walk from a site to its leaves, and the
GraphRAG entry-point query.

## 5. Fronius

The single-device blueprint: unauthenticated local API, time-varying readings, and it **must attach
to an entity it did not create**. This blueprint exists to prove the identity model.

### 5.1 The identity situation, verified, and it is worse than the brief expected

The brief asked me to read the identity situation and report what I found. I did, and the finding
changes the vocabulary design.

Verified against the Fronius Solar API V1 documentation and independent captures
([Victron's dbus-fronius samples](https://github.com/victronenergy/dbus-fronius/blob/master/documents/solar_api_samples.txt),
the [Home Assistant integration](https://www.home-assistant.io/integrations/fronius/)):

- `GetInverterInfo.cgi` returns per inverter: **UniqueID**, *DT* (device type), *PVPower*,
  *CustomName*, *Show*, *ErrorCode*, *StatusCode*, *InverterState*.
- `GetLoggerInfo.cgi` returns the datamanager's **UniqueID**, plus *HWVersion* and *SWVersion*.
- **There is no MAC address and no manufacturer serial number anywhere in the local Solar API.**
- **UniqueID is a short numeric string**, not a serial. Real captures show `476` and `3113`.
- The local API is **unauthenticated** HTTP under `/solar_api/v1/`. Both v0 and v1 exist in the field.

**So the brief's expectation is confirmed: the only overlap with the UniFi view of the same box is
the IP address, which is a weak claim.** This is the weak-only case, and this blueprint demonstrates
the merge-candidate path end to end, exactly as the brief specified for that outcome. Because Fronius
therefore cannot carry the *strong* overlap case, that case moves to the CSV blueprint (section 3),
so both paths still ship with a working example.

**And one consequence the brief did not anticipate.** A short numeric *UniqueID* cannot be "strong
but scoped to their provider": two Fronius installations both reporting `1` would merge into one
device. *fronius-unique-id* must be scoped to the **integration instance**, which is why the
identifier vocabulary carries a three-valued *scope* field rather than a provider flag. That is the
highest-consequence single entry in the vocabulary, and it was only visible after fetching the real
contract.

### 5.2 GEN24, which is a first-run failure mode and not a footnote

On GEN24 devices with firmware 1.14.1 and later, the **Solar API is disabled by default** and must be
enabled in the inverter's own web interface. A user following the documentation on a modern inverter
will otherwise get a connection failure with no indication why. This belongs in the credential
descriptor text, in the connectivity test's failure message, and in the user documentation, so it is
discovered at setup rather than in a support thread.

### 5.3 Readings: what lands in the graph, and what does not

**Fallen-8 does not become a metrics store.** The fleet observability stack (Prometheus, Tempo, Loki,
Grafana) already exists in the default environment and is where time series belong.

What lands: the coarse, slow-moving facts that make traversal meaningful. Model, rated PV peak power,
device type, current state, and a small number of coarse current readings that a question about the
network would actually use.

What does not land: anything that changes on every poll at high resolution, and any history at all.
The graph holds the present shape of the network, not its past.

**Erring toward fewer, coarser properties is correct, and there is now a second reason beyond
tidiness.** Every property the provider re-asserts on a poll that has *changed* is a write, and until
the audit's W2 lands there is no atomic property-update path at all. So a chatty reading set does not
merely bloat the graph; it converts every poll into a large non-atomic write burst, and it makes the
zero-mutation invariant unobservable in practice. The reading set is therefore a **design decision
with a stated justification in the provider**, not a matter of taste.

### 5.4 Entities, claims, relations

Entities: *inverter*, and the datamanager where it is distinguishable.

Claims: *fronius-unique-id* (strong, **instance** scope); the IP as a **weak** claim, which is the
only overlap with UniFi and therefore the whole point.

Relations, by claim: *hasIp*, and the attachment to a device the UniFi provider created, resolved
purely by claim so this provider never knows whether that device exists.

Ships stored queries: the inverter view, and the GraphRAG question that walks from the inverter to
the network path it hangs off.

## 6. Conformance

All three blueprints pass the suite in [integration-runtime](../integration-runtime/spec.md)
section 9 as **regression** tests, with recorded, scrubbed fixtures and no network access.

Per-blueprint fixtures beyond the shared suite:

- **CSV**: a malformed row (diagnostic, not a crash); a row whose MAC matches a UniFi device (the
  strong-overlap case).
- **UniFi**: a three-page list (the paging trap); a 429 with Retry-After; a device whose
  *uplink.deviceId* points at a device not present in the same snapshot (dropped-relation diagnostic,
  retried next sync); a non-GET attempt rejected in code.
- **Fronius**: v0 and v1 shapes; an inverter whose IP matches a UniFi device (the weak-only case, and
  the merge-candidate path end to end); two instances with identical *UniqueID* values asserting they
  do **not** merge; a GEN24-disabled response producing the specific setup message.

**Anonymization**, with a test asserting no original identifier survives anywhere, since these
fixtures also feed the public demo graph, where a real network graph is the whole point of the
screenshot. The test compares against the pre-transform fixture rather than pattern-matching, so a
missed field cannot pass.

## 7. Out of scope

- Event-driven providers (section 1 names the one field that keeps them addable).
- The Site Manager cloud API and the Connector Proxy: the seam is built, the transports are not.
- UniFi Protect, Mobility, InnerSpace, Carrier Fabric.
- Fronius Solar.web, the cloud API. Local only.
- Modbus TCP for either vendor.
- Writing anything back to either source. Both providers are read-only, permanently.
