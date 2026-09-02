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
| `autosar-arxml` | an AUTOSAR system extract (`.arxml`) you upload with the run: a vehicle network's communication matrix over its CAN, FlexRay and Ethernet buses, so its channels, ECUs, frames, PDUs, signals and the flow between them ([below](#reading-a-vehicle-network)) | nothing but the file |

## Running one

The runtime comes up with the compose environment, on its own profile, with no published port:

```bash
npm run env:up
# f8-integrations is on the compose network; you reach it through the API.
```

`F8_INTEGRATIONS=false` skips the sidecar, and the API's `/integrations` routes then refuse the capability:
a 403 on an instance with an API key configured, a 401 on an open one.

A **job** is the whole configuration of one run: the integration, the identity it asserts as, the
namespace to write into, the provider's settings and its credentials (see
[Credentials](#credentials)). Nothing about a job is stored anywhere, so this is also the only
place a run is described:

```bash
curl -sS -X POST http://localhost:8080/integrations/job \
  -F 'job={"providerId":"csv-device-list","integrationInstanceId":"office-inventory","namespace":"default","settings":{"label":"device"}};type=application/json' \
  -F 'files[file]=@devices.csv'
```

That call **accepts** the run rather than waiting for it. It answers `202` with a run id, because
a source worth importing takes longer than any connection in front of it will stay open, and the
run is deliberately built to outlive its caller. Everything that can *reject* the job is still
judged before the answer, so a `202` means the run really started and a `400` or `409` means it
never did.

Watch it, or read its outcome afterwards:

```bash
curl -sS http://localhost:8080/integrations/run/office-inventory
```

While a run is in flight that carries the phase it is in and how far through it is; once it ends it
carries the report itself, or the error if it produced none. The phases are `observe`, `validate`,
`resolve`, `write-elements`, `write-edges`, `embed-summaries` and `reconcile`. Two of them matter
most, because both can run for a long time while the graph shows no change at all and used to be
indistinguishable from a hang: a large extract **parses** for minutes, and summary **embedding** is
model inference for hours.

In [F8 Studio](/studio/) this is the run panel on the Integrations screen. It survives a page
reload, because the identity is enough to re-find the run.

Add `?wait=true` to the job call for the old synchronous shape, which returns the report itself. It
is for a small source and a script: the API's proxy holds a connection for a bounded time, so a real
import will outlast it.

### Stopping one

```bash
curl -sS -X POST http://localhost:8080/integrations/run/office-inventory/cancel
```

That answers `202` when the signal reached a run in flight, and `404` when there is nothing to stop.
It is a **request**, not an event: the run stops at its next safe point, which during the embedding
phase is after the chunk already in the model. So `cancelRequested` turns true at once and
`cancelled` only when the run has really stopped. Cancelling twice is not an error, and a run that
had already finished is not cancellable.

A cancelled run **keeps what it had already written** and deliberately does not reconcile, so it
withdraws nothing and deletes nothing. That is the whole reason it is safe to press: reconciliation
withdraws by set difference over what the run claimed, and a run stopped half way has not claimed
the entities it never reached, so reconciling would delete healthy elements the source still
describes. The leftovers carry the integration's own claims, so the next completed run under that
identity matches them rather than duplicating them, and that run's reconciliation removes whatever
the source really has stopped describing.

This is also the way out of the `409` a second run under one identity gets: cancel the one in
flight, and the identity is free.

### Restarts

A run that is in flight when the runtime stops is **picked up on the next start**, under the same run
id, and continues where it stopped. In the Studio the panel says so; over the API the run reports
`resumed`.

This exists because of one asymmetry. A run's graph writes are recomputable: re-run it and every
element it created is matched instead of created again. Its **embedding** is not, because only
entities whose data changed are embedded, so once the writes have landed a re-run finds everything
equal and embeds nothing at all. Before this, a restart after twenty of twelve thousand summaries
lost the remaining twelve thousand permanently, and the only cure was clearing the namespace and
importing from scratch. Now it costs seconds.

It needs somewhere to write: set `Integrations:SpoolDirectory` and give the container a volume for
it. The compose environment does both. **Unset is the default and writes nothing**, which is what a
bare `dotnet run` gets, and then an interrupted run is simply lost as it always was.

What may live there is a short list: the job's envelope, the snapshot the provider produced, and the
embedding journal. **Never a credential and never a file's bytes** - a credential is needed only to
read the source, and a file only to produce the snapshot, so past that point neither can affect the
run and neither is written down. A run interrupted *before* its source had been read therefore
cannot be resumed at all, and says so rather than being retried: submit it again.

An entry is deleted on every ending a run has, succeeded, failed and cancelled alike, so a healthy
runtime's spool is **empty**. It is not a run history.

One exception, because the runtime restarts alongside the graph it writes into and may come up first:
a resumed run that failed **because the graph did not answer** keeps its entry and is tried again on
the next start, up to three times. Any other failure is about the job or the source, which re-running
unchanged will not mend, so the entry goes.

What is remembered in MEMORY is deliberately narrow: **the current and the last run of each
identity**, lost on a restart of the runtime, and bounded in number. There is no run history, no
schedule and no list of past runs.

Ask `GET /integrations/providers` what each integration's settings are; every one carries a
label, a kind and a sentence saying where to find the value in the source system. That is
deliberately enough to render a form from, so a new integration needs no new UI code when a
screen for it arrives.

## Files

An integration that reads a file gets it **from you, with the run**. In [F8 Studio](/studio/)
that is a dropzone and a file picker on the Integrations screen, the same gesture the
[Knowledge](/unstructured-ingestion/) screen uses for documents; over the API it is the `files`
map above, one entry per file setting, carrying the file's own name and its bytes.

A file's bytes travel as `multipart/form-data`, one part per file, and nowhere else. See
[Sending the bytes](#sending-the-bytes).

### Sending the bytes

**One part per file.** Nothing expands, nothing is held twice, and the sender streams straight from
the file. The first part is named `job` and carries the document with `files` **absent**; each file
follows as its own part with its bytes verbatim:

```
POST /integrations/job
Content-Type: multipart/form-data; boundary=X

--X
Content-Disposition: form-data; name="job"

{"providerId":"autosar-arxml","integrationInstanceId":"vehicle-7","settings":{}}
--X
Content-Disposition: form-data; name="files[file][0]"; filename="chassis.arxml"

<AUTOSAR>...
--X
Content-Disposition: form-data; name="files[file][1]"; filename="body.arxml"

<AUTOSAR>...
--X--
```

With `curl` that is one `-F` per file:

```bash
curl -sS -X POST http://localhost:8080/integrations/job \
  -F 'job={"providerId":"autosar-arxml","integrationInstanceId":"vehicle-7","settings":{}};type=application/json' \
  -F 'files[file][0]=@chassis.arxml' \
  -F 'files[file][1]=@body.arxml'
```

**A `contentBase64` field is refused, by name.** Earlier versions took the bytes as base64 inside
the document. It is the shape a shell script writes most easily, so the refusal says what to send
instead rather than only saying no. Two things retired it. Whatever composes such a request holds
the file's bytes, its base64 string and the serialised request at once, so the peak is several times
the file, and in a browser the encoder itself fails at roughly 384 MiB of input because a JavaScript
string caps at 512 MiB. More decisively, base64 costs a third: while a job could arrive that way,
the job ceiling had to stay under three quarters of the transport bound, or the runtime would accept
jobs the API in front of it refuses. Every job paid that for a shape no client used.

The rules, each of which is a `400` naming the part it is about:

- The `job` part is **first**, appears once, and is a value part with no `filename`. The files are
  read as they stream past, so the document that says which setting each belongs to cannot arrive
  after them.
- `files[<settingKey>]` carries one file. `files[<settingKey>][<n>]` is the **list** form, numbered
  from 0, ascending, with no gaps and no repeats. The two forms are not mixed for one setting, and
  the numbering is explicit rather than implied by part order because a list of one is a different
  statement from one file - a setting that takes exactly one file refuses the list form, and that
  refusal is load-bearing.
- Every file part declares a `filename`, which becomes the file's name. `filename*` is honoured.
- **An unknown part name is refused, never ignored.** A misspelled file part that was quietly
  dropped would submit a snapshot that does not mention whatever that file described, and a complete
  snapshot withdraws what it does not mention.
- The document in the `job` part may not carry a `files` map of its own: on this transport the parts
  *are* the files.

Anything other than these two content types is a `415`. One job sent either way produces an
identical run and an identical report.

### One setting, several files

A file setting the integration declares `multiple` takes an **ordered set** instead of one file,
sent as one numbered part each:

```
Content-Disposition: form-data; name="files[file][0]"; filename="chassis.arxml"
Content-Disposition: form-data; name="files[file][1]"; filename="body.arxml"
```

An unnumbered `files[file]` part stays valid everywhere and is one file rather than a set of one,
and a setting that takes one file **refuses** a numbered set rather than reading the first entry of
it. That refusal is the important half: these integrations
declare complete snapshots, so files that went unread would be reported as parts of the source that
no longer exist, and reconciliation would delete everything they describe.

**The set of files is the source.** That is the sharp edge of it, and worth reading twice. A vehicle
network is handed over as one AUTOSAR extract per domain or per bus, and those extracts reference
each other by path, so no single file is a complete description of anything. One run over all of
them resolves references across their union, exactly as it would inside one file. But it also means
a later run given **fewer** files withdraws whatever only the missing file described, because the
snapshot is complete over what it was given. Run the whole set every time.

Two more consequences of the union:

- **Order is precedence.** Where two files declare the same thing, the one listed **first** wins.
  That is not a quirk to work around: every extract carries the standard platform packages, so a
  four-extract job re-declares hundreds of paths, and the runtime reports one aggregate diagnostic
  per re-declaring file rather than hundreds of individual ones.
- Two files with the **same name** are refused. Every diagnostic about a file names it, so two files
  with one name make each of those messages ambiguous, and the commonest cause is the same file
  picked twice by mistake.

Bytes rather than text, all the way to the integration, because the encoding is not yours to guess: an
AUTOSAR extract a tool wrote as UTF-16 reads correctly, where a transport carrying "the text" would
have handed the integration mojibake and written that into your graph without a word on the report.
An XML extract goes further and is read by its own **encoding declaration**, so one written in a
single-byte encoding with no byte-order mark reads correctly too.

The runtime **mounts no files and opens none on disk**. There is no directory to prepare, no
staged upload to clean up and no file name that could point somewhere it should not: a file lives
exactly as long as the run that needed it, which is the same rule
[credentials](#credentials) follow. (The one thing the container may write is the
[run spool](#restarts), and a file's bytes are deliberately not in it.) Two consequences worth
knowing:

- A file's **name** is a label. It is what every message about the run calls it, so a diagnostic
  still reads `devices.csv row 7`, and nothing resolves, opens or joins it to a path.
- An **empty** file is refused rather than read as an empty source, because a complete snapshot
  describing nothing withdraws every element the integration ever claimed.

### The ceilings, and how to read them before you send

There are **three** ceilings, all belonging to the **runtime's** configuration rather than to the
instance you submit through. A refusal names what it saw and the ceiling it broke.

- `Integrations:MaxFileBytes`, default **128 MiB**, per file, on the decoded bytes. Sized for the
  real thing: an AUTOSAR system extract for one vehicle platform runs to tens of megabytes.
- `Integrations:MaxJobFileBytes`, default **760 MiB**, for the job **total** across every file
  setting on it. Not a restatement of the first: several extracts can each be legal while their sum
  is what this process has to hold at once, and one request carries a whole run. It is as high as the
  transport allows, because a job declaring no [scope](#scope-when-one-source-needs-more-than-one-job)
  has to carry its whole source: 760 is the 768 MiB bound less the envelope and the parts' own
  framing. A higher ceiling would have the runtime accept jobs the API refuses.
- `Integrations:MaxJobFiles`, default **256**, for **how many** files one job carries, counted the
  same way. The byte ceilings cannot bound this on their own, because a one-byte file is legal: a
  set can satisfy both of them and still ask the runtime for an unreasonable number of entries.

Above them sits a fixed 768 MiB bound on the request body itself, at the API's proxy, which is
deliberately not configurable and is the only way in. It is what caps the job ceiling: raising
`Integrations:MaxJobFileBytes` past what the bound leaves for files has no effect beyond turning a
named refusal into a bare 413 from the proxy.

**Ask the instance rather than assuming.** `GET /integrations/limits` answers the three numbers as
they actually bind for you - the proxy reconciles its own bound with the runtime's configuration, so
there is one number per question and nothing to combine:

```bash
curl -sS http://localhost:8080/integrations/limits
# {"maxFileBytes":134217728,"maxJobFileBytes":796917760,"maxJobFiles":256}
```

Zero or less means that ceiling is off. Studio reads this before you stage anything, which is why an
oversized set is refused in the form: refusing at the far end works, but it costs the whole upload
first, since the connection has to finish sending before it can read the answer.

Files that size are not free. Each is decoded to text for the integration - two bytes per character
for XML - so a run over a maximal job peaks well over a gigabyte before anything is parsed, and the
elements it produces are written to the graph in batches rather than one enormous transaction. The
runtime reads its files **one at a time** to keep only one of them decoded at once, but the bytes of
all of them are held for the whole run. Budget memory for the runtime container accordingly; the
ceilings exist precisely because the caller, not the operator, picks the size.

There is deliberately **no schedule, no interval and no list of past runs** anywhere in the runtime.
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

### `scope`: when one source needs more than one job

A complete snapshot withdraws what it does not mention, and by default it is complete over
**everything the identity claims**. That is right for a source one job can carry, and it makes a
larger source impossible: split it across two jobs and each is a complete snapshot that never
mentions the other's elements, so each withdraws the other's.

Naming a **`scope`** on the job says what this run is complete *over*. Reconciliation then compares
only that part, so two jobs under one identity coexist:

```json
{ "providerId": "autosar-arxml", "integrationInstanceId": "fleet", "scope": "chassis-buses" }
```

Use the **same** scope for every job describing the same part, and a different one for a different
part. Omit it and nothing changes: the run is complete over the whole identity, as before.

The part worth understanding is what happens to an element **two scopes both describe**. It is one
element carrying both scopes' claims, and withdrawing one scope removes only that scope's claim: the
element survives, and is deleted only when the last claim of any kind goes. This is the ordinary case
rather than a curiosity - a signal carried on two buses is one bus-independent `SYSTEM-SIGNAL` with a
per-bus `I-SIGNAL` each, so the system signal belongs to every scope carrying any of its buses.

A scope is a **different thing** from any identity a provider puts inside its own claims, such as the
ARXML reader's vehicle. The vehicle says *which element this is*; the scope says *which jobs are
responsible for it*. Folding them together would split every shared element in two.

Its shape is checked on the same allow-list as the identity, and for the same reason: the value is
substituted into a property key and into the claim index.

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
is no credential mount and no files mount, no store, no cache and no keyring: it has nothing to
rotate because it keeps nothing, and whoever submits a job is whoever already holds the credential.
[Files](#files) arrive the same way, for the same reason.

The one thing the container may write, when an operator configures it, is the
[run spool](#restarts), and a credential is deliberately not in it: it is needed only while the
source is being read, so past that point it cannot affect the run. Neither is a file's bytes.

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
  "cancelled": false,
  "error": null,
  "errorKind": null,
  "credentialFingerprint": null,
  "diagnostics": [
    { "code": "rowWithoutMac", "message": "...", "subject": "devices.csv row 7" }
  ]
}
```

Four things on it are worth knowing:

**`issuedMutations` is false on a re-run over an unchanged source.** Every write is conditional
on an actual difference, so running a job on a timer costs nothing when nothing changed: no
change-feed noise, no write-ahead log growth.

**`errorKind` names which system failed** (`configuration`, `credential`, `source`, `graph`, plus
`conflict` on the 409 a second run under one identity gets),
because "the job is wrong", "the key is wrong", "the console will not answer" and "the graph will
not answer" send you to four different places. A source that answers `401` or `403` is a
`credential` failure, not a `source` one: the front door answered, and what it said was no. A run that failed **withdrew
nothing**: the next run starts from the same graph.

**`cancelled` is a third outcome beside succeeded and failed**, and it is not a kind of failure:
nothing is wrong, the counts are what really landed, and the run [did not
reconcile](#stopping-one). It carries no `errorKind` for exactly that reason, so anything alerting
on failures should not fire for a run somebody stopped on purpose.

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

`autosar-arxml` reads **AUTOSAR system extracts**, the XML files the automotive industry uses to
exchange the communication matrix of a vehicle network, and describes the **CAN, FlexRay and
Ethernet** buses they carry. They are [uploaded with the job](#files) like the CSV integration's
file.

**Give it the whole set in one run.** A vehicle is handed over as one extract per domain or per bus,
and those extracts reference each other by AUTOSAR path, so one run over all of them resolves a frame
in `chassis.arxml` against a signal defined in `body.arxml` exactly as it would inside one file. One
run per extract cannot do that: each would be a complete snapshot of its own, so the second would
withdraw everything the first described. The rules that come with a set - order is precedence, the set
is the source, a later run with fewer files withdraws the difference - are in
[One setting, several files](#one-setting-several-files).

That matters more with several buses than it did with one, because an ECU's declaration in an extract
is **bus-local**: a gateway sitting on a CAN bus and a FlexRay bus appears in both extracts, each
carrying only its own bus's connector for it. Run them together and the gateway is one element
attached to both buses, which is the whole point of importing a vehicle rather than a bus. Run them
separately and you get two disconnected graphs, one per run, each having deleted the other.

Where two extracts declare the **same bus**, their channels are merged into one network: that is what
one bus split across extracts needs. It is reported (`arxmlRedeclaredCluster`), because the same
merge is lossy if the two extracts really describe different buses that happen to share a reference
path, and nothing in the file distinguishes those two cases.

What lands in the graph is each bus (`network`), its physical channels (`channel`), its ECUs, the
frames on it, the PDUs inside those frames (including the container and secured layers), the signals
inside the PDUs, the system signals they implement and the scaling methods that give them a unit. One
vocabulary covers every bus technology: a CAN frame and a FlexRay frame are both `frame`, so a query
for "what does this ECU send" never has to enumerate a label per protocol. What differs is a few
properties, and they are absent rather than empty on the protocol that has no use for them: a FlexRay
frame carries `slotId`, `baseCycle` and `cycleRepetition`, a CAN frame carries `canId` and
`canAddressingMode`, an Ethernet channel carries `vlanId` and `vlanName`, and every bus carries
`protocol`, `baudrate` and the protocol name and version the file states. A query that filters on
`protocol` is really filtering on which of those exist. The edges are the questions people actually
ask: `sends` and `deliversTo` point **with** the data flow, so a path from a sending ECU to a
receiving one never traverses an edge backwards; `contains`, `carries` and `secures` walk down the
protocol stack from a frame to a single signal; and `partOf` is plain structure, a channel to its bus.

**A channel is a different thing on each protocol**, which is why it is an element rather than a
count. On CAN it is the single channel a cluster has. On FlexRay it is redundancy: A and B carry one
schedule, so they are two channels of **one** network rather than two networks. On Ethernet a channel
is a **VLAN**, so a backbone has as many as the vehicle has broadcast domains, and which one an ECU
sits on is a question you can ask directly. An ECU is `attachedTo` its bus **and** each channel it is
really on: the first answers "is this unit on this bus" without knowing the protocol, the second
answers "which broadcast domain".

**An Ethernet bus has no frame layer at all.** That is the standard's own shape rather than something
missing: an Ethernet channel carries PDU triggerings directly, and the socket layer does what a frame
does elsewhere. So a query that reaches signals *through frames* finds nothing on an Ethernet bus,
and the protocol-neutral route - the PDU - is the one to write. A frame is better understood as how
CAN and FlexRay **address** a PDU, exactly as a socket connection and a header id is how Ethernet
addresses one.

### The socket layer

What addresses a PDU on Ethernet lands as three more kinds, all `partOf` their channel:

| kind | what it is | properties | reaches |
|---|---|---|---|
| `endpoint` | a network endpoint: an address on the channel | `address`, `ipVersion`, `addressSource`, `networkMask` | |
| `socket` | a socket address and its application endpoint | `port`, `transport` (`udp` or `tcp`) | `boundTo` its endpoint |
| `connection` | the pairing of sockets that carries PDUs | `sourceSpelling`, `headerIdCount` | `serverPort` and `clientPort` to its two ends, `carries` to each PDU |

**`sourceSpelling` is there because this layer is normalised.** Its element names differ between
AUTOSAR revisions **with no overlap**: one revision names a connection bundle under the channel,
another a static socket connection hanging off the serving socket. Mirroring each faithfully would
make the graph's *shape* depend on which revision an extract was written against, so the same vehicle
exported twice would import as two different graphs and every query would need writing twice. They
are read onto the one `connection` kind instead, and `sourceSpelling` keeps the element name the file
actually used, so nothing is hidden. Both spellings are read unconditionally - there is nothing to
detect, since a document can only be using one of them.

### Services, and the switch below them

Two more kinds sit either side of the socket layer.

**`service`** is a SOME/IP service instance, which is the level a modern vehicle's Ethernet traffic is
actually organised at: signals still exist below it, but what an application asks for is a service.
Each is `partOf` the socket that offers or consumes it, and carries `role` (`provided` or `consumed`),
`serviceId` and `instanceId`. One kind for both roles, so "what services does this unit take part in"
is one query. A provided instance and the consumers that use it stay **separate elements** carrying
the same `serviceId`: the extract joins them by nothing, so matching them is a query you write rather
than an inference the reader makes for you.

**`coupling`** is a physical port of the switch fabric, `partOf` the ECU whose Ethernet controller
declares it, and `connectedTo` the port it is wired to. That is a different question from a socket's
port number - "which switch port is this unit plugged into" rather than "which port does this service
listen on" - which is why it is a different kind. A link is **one** edge in the direction the file
states it; two would make every count over the topology wrong. A connection the extract only half
states produces no edge and no complaint, while one naming a port nothing declares is reported like
any other unresolved reference.

**If a channel's socket layer comes up empty, the run says so** (`arxmlSocketLayerUnrecognised`),
naming the schema the file declared and the element names it actually found under that channel. This
is the one diagnostic here more likely to be about the *reader* than about your file: a spelling this
version has not met would otherwise be silent data loss on a bus that imported and looked complete.
An extract that genuinely carries no socket layer produces it too, which is why it is reported once
per run rather than once per channel. If those names look like a socket layer, they are worth
reporting: adding one is a table entry.

### One value, several buses

This is what makes importing a vehicle worth more than importing its buses one at a time. A value
carried on more than one bus is **one bus-independent `system-signal` with a per-bus `signal`
each**, joined by `implements` - never a shared signal, because a signal is a wire representation and
two buses do not share one. So a wheel speed produced on CAN and consumed on Ethernet is one element
both buses reach, and the walk crosses protocols without knowing which are involved:

```
ecu -sends-> signal -implements-> system-signal <-implements- signal -deliversTo-> ecu
```

Nothing configures that. It follows from having both buses in one graph, which is why giving the run
the whole set (or [scoping](#scope-when-one-source-needs-more-than-one-job) each job of it) is the
part that matters.

Identity is the **vehicle you name** plus the element's **AUTOSAR reference path**, which the
standard already makes both its identity and the way every cross-reference in the file addresses
it. Nothing is matched by name or similarity. Run the next release into the same namespace and what
that release removed is withdrawn, which leaves the [change feed](/change-feed/) as the release diff
with nothing extra to set up.

The **Vehicle** setting is required and has no default, because a default would quietly merge one
car into another. AUTOSAR paths are not unique across vehicles: a short-name is unique among its
siblings, so a reference path identifies an element within **one** model, and nothing in the standard
coordinates package names across independently authored models. The standardised packages are common
to all of them by construction, so two vehicles routinely use the same path for different elements.
The ARXML form is specified by AUTOSAR's
[ARXML Serialization Rules](https://www.autosar.org/fileadmin/standards/R20-11/FO/AUTOSAR_TPS_ARXMLSerializationRules.pdf)
(R20-11). Naming the vehicle is what lets both cars live in one namespace
under one identity without resolving onto each other. Use the **same** name for every job describing
one vehicle, so its buses join up into one graph; use a different name for a different vehicle.

Two limits worth knowing up front. This version reads **CAN, FlexRay and Ethernet** clusters. A set
carrying a bus of another kind - LIN, for instance - still imports what it can, and names what it
skipped (`arxmlUnreadCluster`): worth reading, because the run is reported COMPLETE over what it did
read, so a later job that omits those files withdraws whatever only they described. A readable set
carrying **no** bus this version reads fails the run outright rather than reporting an empty network,
because an empty complete snapshot would delete the network a previous run described; one extract of
a set having no cluster is perfectly normal, since the gate is over the union. And the
software-component level (the data mappings between components) is deliberately not read: this is
the network view.

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

Note the `label`. It is not decoration. Only six of the thirteen ARXML entity kinds get a
description read out of the extract at all -- signals, system signals, PDUs, network endpoints,
SOME/IP service instances and coupling ports -- so a network, an ECU, a frame or a socket embeds as
little more than its own name, and those vectors cluster by identifier shape rather than by
meaning. An unconstrained similarity search therefore ranks that noise against real matches.
Constrain every similarity query to the kind of thing you are looking for.

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
