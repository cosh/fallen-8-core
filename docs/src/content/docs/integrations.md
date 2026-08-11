---
title: "Integrations"
description: "A sidecar that reads a system on your own network and writes what it saw into a namespace: named credentials, exact-match identity, and deletion only when the snapshot says it saw everything."
---

Most of what you want in a graph is already in something else: a spreadsheet of devices, a
network controller, an inverter on the wall. An **integration** reads one of those, describes
what it saw, and writes that description into one namespace. Then it forgets everything.

It is a **separate deployable** (`fallen-8-integrations`), its own process and container image.
It never loads the engine: it writes through the same public REST API you would use, so it can
be pointed at a scratch graph or at a shared instance on another host. That separation is not
tidiness. This container reads credentials that belong to your controllers, so it holds no host
port at all and the browser reaches it only through the API's authenticated proxy.

Three integrations ship. Three is the smallest number that proves the contract is the right
shape rather than merely a working one, because the fourth is meant to be written without the
people who built this in the loop.

| Integration | Reads | Needs |
| --- | --- | --- |
| `csv-device-list` | a CSV file in the runtime's files directory: MAC, name, note, hostname | nothing but the file |
| `unifi-network` | a UniFi console's integration API: sites, adopted devices, clients, and the uplink topology between them | an API key, created in the Network application under Settings then Integrations |
| `fronius-solar` | a Fronius Solar API on your own network: inverters and the logging device in front of them | nothing. The local Solar API is unauthenticated |

## Running one

The runtime comes up with the compose environment, on its own profile, with no published port:

```bash
npm run env:up
# f8-integrations is on the compose network; you reach it through the API.
```

`F8_INTEGRATIONS=false` skips the sidecar and the API's four routes refuse, which is also what
makes the Studio screen disappear.

A **job** is the whole configuration of one run. It names the integration, the identity it
asserts as, the namespace to write into, the provider's settings, and, for each credential
setting, **the name of a credential rather than its value**. That makes a job safe to keep, to
commit next to whatever submits it, and to read back later as a record of what was asked for:

```bash
curl -sS -X POST http://localhost:8080/integrations/job \
  -H 'content-type: application/json' \
  -d '{
        "providerId": "csv-device-list",
        "integrationInstanceId": "office-inventory",
        "namespace": "default",
        "settings": { "file": "devices.csv", "label": "device" }
      }'
```

Ask `GET /integrations/providers` what each integration's settings are; every one carries a
label, a kind and a sentence saying where to find the value in the source system. F8 Studio's
**Integrations** screen renders that form for you, and it does so from the descriptor alone,
which is why a fourth integration needs no change there.

There is deliberately **no schedule, no interval and no run history** anywhere in the runtime.
Timing belongs to whoever wants the data: run a job from cron, from a CI pipeline, from a
button. A runtime holding a schedule would own a second copy of a decision it has no way to
judge.

## The identity, which is the part to get right

`integrationInstanceId` is the identity a run asserts as, and **you own its stability**. Every
element a run creates carries a claim keyed on it, and every later run reconciles against what
that identity claimed before. Use one stable value per real integration, forever:

- a **fresh** identity each run leaves every previous run's elements claimed by an identity no
  later run knows about, so the graph quietly accumulates elements nothing will ever clean up;
- a **reused** identity inherits everything the other one claimed, and the first complete
  snapshot that does not mention those elements withdraws them and deletes them.

Nothing can detect either from inside, which is why the field is documented rather than
defaulted. Its shape is checked (letters, digits, dot, dash, underscore, at most 64 characters)
because the value is substituted into property keys.

## What lands in the graph

One vertex per thing the source described, labelled with its kind, carrying:

- the provider's own properties, **namespaced** (`unifi.model`, `fronius.status`, `csv.name`),
  because two integrations describing "the name" of one device rarely mean the same thing;
- every identifier the source reported for it, canonicalised, as `$identity:0`, `$identity:1`
  and so on;
- `$claim:<your-instance-id>`, which is this integration saying "I assert this element".

One edge per relation the source described, typed by the relation.

Identity is **exact match, never similarity**. A run finds its own elements by looking up a
canonical identifier key, and only identifiers that are actually identifying take part: a MAC
address or a serial resolves, an IP address or a hostname never does, because an address is a
lease and a hostname is user-editable. Both are still recorded and indexed, which is the point:

**Nothing ever merges two elements.** If two integrations both report the MAC
`44:d2:44:aa:bb:cc`, you get two elements that carry one identical claim key. That is not a
failure to deduplicate, it is the mechanism: the overlap is now a thing you can query, and what
to do about it is a decision for a person or an agent, not for a job runner that keeps no
memory.

## Finding what an integration wrote

An integration registers no stored queries, because the useful query cannot be expressed as
one: it is "the elements this integration wrote", which is a per-instance property. Scoping by
label instead returns the wrong rows (two integrations legitimately write the label `device`).
So the queries are here, to copy:

**Everything one integration claims**, by its claim property:

```bash
curl -sS -X POST http://localhost:8080/scan/index/all \
  -H 'content-type: application/json' \
  -d '{
        "indexId": "f8i-claims",
        "operator": 0,
        "literal": { "value": "office-inventory", "fullQualifiedTypeName": "System.String" },
        "resultType": "Both"
      }'
```

**Everything that carries one identifier**, whoever claimed it, which is how you find an
overlap between two integrations:

```bash
curl -sS -X POST http://localhost:8080/scan/index/all \
  -H 'content-type: application/json' \
  -d '{
        "indexId": "f8i-identity",
        "operator": 0,
        "literal": { "value": "mac:44d244aabbcc", "fullQualifiedTypeName": "System.String" },
        "resultType": "Both"
      }'
```

Two ids back from the second query and one from each of two `f8i-claims` queries is exactly the
picture that says "two integrations see the same device". The identifier types, how each value
is canonicalised into a key, and which ones can resolve are served by
`GET /integrations/vocabulary`.

The two indices (`f8i-identity`, `f8i-claims`) are created and repaired by the runtime before
every job. They are derived projections of what the elements themselves say, so if one is ever
dropped, by a reset, by loading a save game, or by a checkpoint that could not persist it, the
next run rebuilds it from element state before trusting a lookup.

## Credentials

A credential is **named, never stored**. You put the value in a file; the runtime reads it when
a job starts, uses it, and drops it when the job ends.

```bash
# one file per credential, in the mounted directory (./secrets by default)
printf 'the-api-key' > secrets/unifi-console
```

```bash
curl -sS -X POST http://localhost:8080/integrations/job \
  -H 'content-type: application/json' \
  -d '{
        "providerId": "unifi-network",
        "integrationInstanceId": "home-unifi",
        "settings": { "baseUrl": "https://10.0.0.1/proxy/network/integration" },
        "credentials": { "apiKey": "unifi-console" }
      }'
```

**Rotating one is overwriting the file** in place: no restart, nothing re-entered, and no stored
copy to go stale. Overwrite in place rather than moving a new file over it, because a bind mount
keeps reading the file it opened and the job would keep succeeding with the credential you think
you revoked. Every report carries a `credentialFingerprint` for exactly that reason: if it does
not change after you rotate, the runtime is still reading the old value.

The file's content is taken verbatim except for a single trailing newline, so `printf 'pw'` and
`echo pw` give the same credential, and spaces inside it survive. An **empty file is a failure**,
never "no credential": a truncated file would otherwise produce a run that reads whatever the
source shows the public, declares that complete, and withdraws everything the integration ever
claimed.

Two lists bound where a credential can go, and both are configuration only, never job settings:

| Setting | What it does |
| --- | --- |
| `F8_INTEGRATIONS_ALLOWED_HOSTS` | the hosts a run **holding a credential** may contact, enforced as the request leaves. Set it: a source address arrives in a job from whoever can reach the API, so without this list a caller can aim your admin password at a host of their choosing. Empty means no restriction, and the runtime says so at startup |
| `F8_INTEGRATIONS_SELF_SIGNED_HOSTS` | hosts whose TLS certificate is not validated. A UniFi console and a Fronius inverter serve HTTPS with a self-signed certificate for a private address no authority will sign, so without this you cannot reach them at all. It is the one place this feature reduces trust, and it is not pinning: a named host is trusted for whatever certificate it presents |

A credentialed run also refuses plain `http` to anything but loopback, and never follows a
redirect, because a source answering `302` to another host would walk your credential off the
list.

## What a run tells you

The report is the only account of a job, because the runtime keeps none:

```json
{
  "providerId": "csv-device-list",
  "integrationInstanceId": "office-inventory",
  "startedUtc": "2026-08-11T09:12:44.1180000+00:00",
  "durationMilliseconds": 148,
  "elementsCreated": 0,
  "elementsMatched": 42,
  "edgesCreated": 0,
  "claimsWithdrawn": 1,
  "elementsDeleted": 1,
  "deletionsDeferred": 0,
  "issuedMutations": true,
  "summariesEmbedded": 0,
  "error": null,
  "errorKind": null,
  "credentialFingerprint": null,
  "diagnostics": [
    { "code": "rowWithoutMac", "message": "...", "subject": "devices.csv row 7" }
  ]
}
```

Three things on it are worth knowing:

**`issuedMutations` is false on a re-run over an unchanged source.** Every write is conditional
on an actual difference, so running a job on a timer costs nothing when nothing changed: no
change-feed noise, no write-ahead log growth.

**`errorKind` names which system failed** (`configuration`, `credential`, `source`, `graph`),
because "the mount is broken", "the password is wrong", "the console will not answer" and "the
graph will not answer" send you to four different places. A run that failed **withdrew
nothing**: the next run starts from the same graph.

**`diagnostics` are never dropped**, and each has a stable `code` you can grep for and alert on.
They cover both what the source could not tell the run (a CSV row with no MAC) and what the
graph could not be told (a claim an index refused).

## Deletion, and what licenses it

Every snapshot declares whether it describes the **whole** source. Only that declaration lets a
run withdraw a claim, and an element is deleted only when the **last** claim on it is gone, so
an element another integration still asserts is never removed.

Two safeguards sit in front of that. A source that cannot be read **fails the run** rather than
producing an empty snapshot, because "I could not look" must never become "there is nothing
there". And deletion is **deferred** whenever the target's own durability says it is not safe:
if writes are not reaching disk, if the last recovery was truncated, or if the last checkpoint
dropped an index, deletions are counted in `deletionsDeferred` and reported rather than
performed. Deferring is recoverable; deleting wrongly is not.

## Semantic search over what landed

An integration can optionally have its entities embedded, so they turn up in semantic search
alongside your documents. It is **off unless both halves are asked for**: the integration
declares a summary template, and the job sets `"embedSummaries": true`. Embedding every client
on a busy network by default would be cost and noise in equal measure.

The dimension and the metric are read from the instance's own embedding configuration, so
nothing here pins a model. If the instance has no embedding provider, or it is switched off, the
run still succeeds and the summaries are simply **absent**, with a diagnostic saying so.

## Writing the fourth one

An integration is a data descriptor plus one method that observes its source and returns what it
saw. It never resolves identity, never sees the graph or an element id, never opens a file, and
never declares how strong its own identifiers are: all of that is on the runtime's side of the
line, so the worst a wrong integration can do is describe its source wrongly, which is visible
in its own output.

Two things make that claim safe rather than aspirational. `POST /integrations/snapshot/validate`
judges a snapshot document on its own, before any source is wired to it. And a conformance suite
runs a candidate twice through the real runner against an in-memory graph, with no network and
no live graph, and checks twelve named properties: that its claims are well formed, that it does
not promote its own weak identifier, that two runs describe one source identically, that the
second issues no write, that it writes only to what it claims, that it offers no similarity
score, that it reached nothing the suite did not stand in for, that no credential reached a log
or the graph, that it read no file it was not offered, that it did not over-declare
completeness, and that an unreadable source failed the run. Each check is named, and each has a
deliberately broken integration in the test suite that fails exactly that one.

## See also

- [Architecture](/fallen-8-core/architecture/) for where the runtime sits among the deployables.
- [Running Fallen-8](/fallen-8-core/running/) for the compose variables.
- [Semantic layer](/fallen-8-core/unstructured-ingestion/) for the other way data arrives:
  documents in, entities out.
