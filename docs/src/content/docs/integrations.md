---
title: "Integrations"
description: "A sidecar that reads a system on your own network and writes what it saw into a namespace: credentials held for one run and never stored, exact-match identity, and deletion only when the snapshot says it saw everything."
---

Most of what you want in a graph is already in something else: a spreadsheet of devices, a
network controller, an inverter on the wall. An **integration** reads one of those, describes
what it saw, and writes that description into one namespace. Then it forgets everything.

It is a **separate deployable** (`fallen-8-integrations`), its own process and container image.
It never loads the engine: it writes through the same public REST API you would use, so it can
be pointed at a scratch graph or at a shared instance on another host. That separation is not
tidiness. Jobs hand this container credentials that belong to your controllers, so it holds no host
port at all and the browser reaches it only through the API's authenticated proxy.

The shipped integrations are deliberately unalike, because each one measures whether the contract
is the right shape rather than merely a working one: the next one is meant to be written without
the people who built this in the loop.

| Integration | Reads | Needs |
| --- | --- | --- |
| `csv-device-list` | a CSV file you upload with the run: MAC, name, note, hostname | nothing but the file |
| `unifi-network` | a UniFi console's integration API, locally or through the cloud connector: sites, adopted devices, clients, and the uplink topology between them | an API key for the front door you point it at, and the two differ: a local console's key comes from the Network application under Settings then Integrations, while `api.ui.com` takes a Site Manager key from unifi.ui.com under Settings then API Keys |
| `fronius-solar` | a Fronius Solar API on your own network: inverters and the logging device in front of them | nothing. The local Solar API is unauthenticated |
| `autosar-arxml` | an AUTOSAR system extract (`.arxml`) you upload with the run: a vehicle network's FlexRay communication matrix, so its ECUs, frames, PDUs, signals and the flow between them ([below](#reading-a-vehicle-network)) | nothing but the file |

## Running one

The runtime comes up with the compose environment, on its own profile, with no published port:

```bash
npm run env:up
# f8-integrations is on the compose network; you reach it through the API.
```

`F8_INTEGRATIONS=false` skips the sidecar, and the API's four routes then refuse the capability:
a 403 on an instance with an API key configured, a 401 on an open one.

A **job** is the whole configuration of one run: the integration, the identity it asserts as, the
namespace to write into, the provider's settings and its credentials (see
[Credentials](#credentials)). Nothing about a job is stored anywhere, so this is also the only
place a run is described:

```bash
curl -sS -X POST http://localhost:8080/integrations/job \
  -H 'content-type: application/json' \
  -d "$(jq -n --arg csv "$(base64 -w0 devices.csv)" '{
        providerId: "csv-device-list",
        integrationInstanceId: "office-inventory",
        namespace: "default",
        settings: { label: "device" },
        files: { file: { name: "devices.csv", contentBase64: $csv } }
      }')"
```

Ask `GET /integrations/providers` what each integration's settings are; every one carries a
label, a kind and a sentence saying where to find the value in the source system. That is
deliberately enough to render a form from, so a new integration needs no new UI code when a
screen for it arrives.

## Files

An integration that reads a file gets it **from you, with the run**. In [F8 Studio](/studio/)
that is a dropzone and a file picker on the Integrations screen, the same gesture the
[Knowledge](/unstructured-ingestion/) screen uses for documents; over the API it is the `files`
map above, one entry per file setting, carrying the file's own name and its **bytes as base64**.

Bytes rather than text, because the encoding is not yours to guess: an AUTOSAR extract a vendor
tool wrote as UTF-16 decodes correctly, where a transport carrying "the text" would have handed
the integration mojibake and written that into your graph without a word on the report.

The runtime **mounts nothing and opens nothing on disk**. There is no directory to prepare, no
staged upload to clean up and no file name that could point somewhere it should not: a file lives
exactly as long as the run that needed it, which is the same rule
[credentials](#credentials) follow. Two consequences worth knowing:

- A file's **name** is a label. It is what every message about the run calls it, so a diagnostic
  still reads `devices.csv row 7`, and nothing resolves, opens or joins it to a path.
- An **empty** file is refused rather than read as an empty source, because a complete snapshot
  describing nothing withdraws every element the integration ever claimed.

`Integrations:MaxFileBytes` (default **128 MiB**, measured on the decoded bytes) is the ceiling, and
it belongs to the **runtime's** configuration rather than the instance you submit through. A file over
it is refused with both numbers named. That default is sized for the real thing: an AUTOSAR system
extract for one vehicle platform runs to tens of megabytes, and a 100 MiB device list or extract goes
through in one run.

Above it sits a fixed 192 MiB bound on the request body itself, at the API's proxy - base64 costs a
third, so a maximal legal job arrives at about 171 MiB and never meets it. It is deliberately not
configurable, which has one consequence worth stating: raising `Integrations:MaxFileBytes` past about
144 MiB has no effect, because the proxy is the only way in (the runtime publishes no port).

A file that size is not free. It arrives base64, is decoded to bytes, and is decoded again to text for
the integration - two bytes per character for XML - so a run over a maximal extract peaks in the high
hundreds of megabytes before anything is parsed, and the elements it produces are written to the graph
in batches rather than one enormous transaction. Budget memory for the runtime container accordingly;
the ceiling exists precisely because the caller, not the operator, picks the size.

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

**A credential arrives with the job that needs it, and nowhere else.** The runtime holds it for
that run, keeps it out of every log line and every report, and drops it when the run ends. There
is no mount of any kind - no credential mount and no files mount - no store, no cache and no
keyring: it has nothing to rotate because it remembers nothing, and whoever submits a job is
whoever already holds the credential. [Files](#files) arrive the same way, for the same reason.

```bash
curl -sS -X POST http://localhost:8080/integrations/job \
  -H 'content-type: application/json' \
  -d '{
        "providerId": "unifi-network",
        "integrationInstanceId": "home-unifi",
        "settings": { "baseUrl": "https://10.0.0.1/proxy/network/integration" },
        "credentialValues": { "apiKey": "the-api-key" }
      }'
```

The value travels in that request, so **serve the API over TLS** for anything but your own
machine, and remember that whatever composed the request holds a secret for as long as it keeps
the body. A job carrying one is not a job to save: no shell history, no committed file, no
pipeline variable that outlives the run.

The value is taken verbatim except for a single trailing newline, so a key pasted out of a console
survives the line break that came with it, and spaces inside or around it survive too, because
they can be part of a real password. An **empty credential is a failure**, never "no credential":
a form submitted before the paste would otherwise produce a run that reads whatever the source
shows the public, declares that complete, and withdraws everything the integration ever claimed.

A credential may never arrive as a **setting**. A setting is neither leased nor redacted, so a
value there would be logged and reported like any other; the runtime refuses a job that puts one
in `settings` rather than quietly accepting it.

A report from a credentialed run carries a `credentialFingerprint`, a keyed hash under a key random
to each process (a run needing no credential has none). It answers one question and carries no
secret: *did this run use the value I just changed?* Compare it with an earlier run **from the same
runtime process**: the same fingerprint twice means your new key never reached the runtime, which is
a different problem from a key the source rejects.

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

**`errorKind` names which system failed** (`configuration`, `credential`, `source`, `graph`, plus
`conflict` on the 409 a second run under one identity gets),
because "the job is wrong", "the key is wrong", "the console will not answer" and "the graph will
not answer" send you to four different places. A source that answers `401` or `403` is a
`credential` failure, not a `source` one: the front door answered, and what it said was no. A run that failed **withdrew
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
declares a summary template, and the job asks for it. Embedding every client on a busy network
by default would be cost and noise in equal measure.

In **F8 Studio**, tick *embed entity summaries* on the run form. The form shows the template
that will be embedded, so you can see what lands before the run rather than infer it after, and
the checkbox is offered only when the selected integration declares a template and the instance
has an embedding provider. Over REST it is two job fields:

```json
{
  "providerId": "autosar-arxml",
  "integrationInstanceId": "vehicle-fleet",
  "embedSummaries": true,
  "embeddingName": "default"
}
```

`embeddingName` is optional and defaults to `default`, which is also the name the document
layer binds its index to, so out of the box integration summaries and document chunks share one
bound index and answer the same searches. Give it a name of its own if you want them separate.

The dimension and the metric are read from the instance's own embedding configuration, so
nothing here pins a model. If the instance has no embedding provider, or it is switched off, the
run still succeeds and the summaries are simply **absent**, with a diagnostic saying so.

Two things worth knowing before you run it:

- **Only entities the run creates or changes are embedded.** A summary is a pure function of an
  entity's kind and properties, so an unchanged entity has no new summary and re-running over the
  same source embeds nothing. To embed a graph that was already imported without the opt-in,
  clear that namespace (`HEAD /ns/<name>/tabularasa`, or the Connect screen's reset) and run
  again. Note that clearing also drops index **definitions**, so recreate the bound vector index
  afterwards.
- **The summaries are written in batches**, so a large extract is many requests rather than one.
  If the provider stops answering half way, the vectors that already landed stay: the run reports
  the count that was written and a diagnostic naming the shortfall.

## Reading a vehicle network

`autosar-arxml` reads an **AUTOSAR system extract**, the XML file the automotive industry uses to
exchange the communication matrix of a vehicle network, and describes the FlexRay bus it carries.
One file per run, [uploaded with the job](#files) like the CSV integration's.

What lands in the graph is the network itself, its ECUs, the frames on the bus, the PDUs inside
those frames (including the container and secured layers), the signals inside the PDUs, the
system signals they implement and the scaling methods that give them a unit. The edges are the
questions people actually ask: `sends` and `deliversTo` point **with** the data flow, so a path
from a sending ECU to a receiving one never traverses an edge backwards, while `contains`,
`carries` and `secures` walk down the protocol stack from a frame to a single signal.

Identity is the element's **AUTOSAR reference path**, which the standard already makes both its
identity and the way every cross-reference in the file addresses it. Nothing is matched by name
or similarity. Because an extract is by construction the complete description of its network,
running the next release into the same namespace withdraws exactly what the release removed, so
the [change feed](/change-feed/) becomes the release diff without anything extra.

Two limits worth knowing up front. This version reads **FlexRay** clusters, and a readable
extract carrying none fails the run rather than reporting an empty network, because an empty
complete snapshot would delete the network a previous run described. And the software-component
level (the data mappings between components) is deliberately not read: this is the network view.

### Finding a signal you cannot name

Signal names in a real matrix are unguessable codes, which is exactly what the optional summary
embedding above is for. The template covers a signal's name, both language descriptions and its
**unit**, and the unit is the point: an odometer's description says "accumulated distance" and
never "kilometer", so its unit is the only thing connecting it to somebody searching for one.

With the embed opt-in on the run and an embedding provider configured, create a
[vector index](/vector-search/) bound to that embedding name. In Studio that is the Indexes
screen: type an index id, pick `VectorIndex`, and set *bind embedding* to the name the run used.
The dimension and metric are prefilled from the instance's provider, and accepting them is what
you want -- an index whose dimension disagrees with the model writing into it is refused on
every later embed and every search. Over REST:

```bash
curl -sf -X POST http://localhost:8080/ns/vehicle/index \
     -H "Content-Type: application/json" \
     -d '{"uniqueId":"arxml-summary","pluginType":"VectorIndex","pluginOptions":{
           "dimension":{"propertyId":"dimension","propertyValue":"1024","fullQualifiedTypeName":"System.Int32"},
           "metric":{"propertyId":"metric","propertyValue":"Cosine","fullQualifiedTypeName":"System.String"},
           "embeddingName":{"propertyId":"embeddingName","propertyValue":"default","fullQualifiedTypeName":"System.String"}}}'
```

Take the `dimension` from `GET /status` (the `embedding` block), not from this example. Order
does not matter: a bound index created after the vectors exist materialises itself from them.

Then ask in words:

```bash
curl -sf -X POST http://localhost:8080/ns/vehicle/embedding/search \
     -H "Content-Type: application/json" \
     -d '{"indexId":"arxml-summary","text":"kilometer","k":10,"label":"signal"}'
```

Note the `label`. It is not decoration. Only three of the seven ARXML entity kinds get a
description read out of the extract at all -- signals, system signals and PDUs -- so a network, an
ECU or a frame embeds as little more than its own name, and those vectors cluster by identifier
shape rather than by meaning. An unconstrained similarity search therefore ranks that noise
against real matches. Constrain every similarity query to the kind of thing you are looking for.

Once a signal is on your screen you can also search **from it** instead of describing it: the
[Studio](/studio/) detail panel and the Browser's Embeddings tab both offer *Find similar*,
which searches the bound index with that element's own vector and drops the element itself from
the hits.

The hits are element ids, so they feed straight into the traversal surface: "who receives the
kilometer signal" is that query followed by one `deliversTo` hop. A multilingual embedding model
matters here, since the prose in these files is routinely German and English in the same
element; the compose environment's default model is multilingual, and a single-language one
degrades the German half.

## Writing the next one

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
or the graph, that every file it read was one the job carried for a setting it declares, that it
did not over-declare completeness, and that an unreadable source failed the run. Each check is
named, and each has a deliberately broken integration in the test suite that fails exactly that
one.

## See also

- [Architecture](/architecture/) for where the runtime sits among the deployables.
- [Running Fallen-8](/running/) for the compose variables.
- [Semantic layer](/unstructured-ingestion/) for the other way data arrives:
  documents in, entities out.
