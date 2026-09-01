# Integration file upload

**Status:** implemented on `feature/integration-file-upload`. The "As built" section at the end
records the four places the implementation went beyond this spec and why.

## The problem, exactly

A file-taking integration is unusable from Fallen-8 Studio today.

`csv-device-list` and `autosar-arxml` each declare a `Text` setting named `file` whose help says:

> The NAME of a file, such as `devices.csv`, and not a path: put the file in the files directory
> mounted into this runtime and name it here.

So the operator has to (1) find the host directory that compose maps to the runtime container's
`/files`, (2) put a file there, (3) come back to Studio and type its name. In the shipped
environment that mount is `./samples:/files:ro`, and `samples/` contains **no CSV and no ARXML at
all** - so a user who clicks "configure" on either integration cannot run it without editing
`docker-compose.yml`. The affordance is broken end to end, not merely awkward.

Meanwhile the Knowledge screen has had the right answer since `unstructured-ingestion`: a dropzone
and a file picker. A file the user is holding should go straight from their machine into the run.

## What changes

**A file arrives with the job that needs it and is dropped when the run ends** - the sentence this
runtime already lives by for credentials, now true of files too. The runtime keeps no mount, no
staging area, no upload id and no temp file, so there is still nothing in this container to clean
up, rotate or garbage-collect.

### FR-1 A new setting kind

`SettingKind.File`. A descriptor declaring one says "this run needs a file the caller supplies",
and that is the whole of what a form needs to render an upload control. `csv-device-list.file` and
`autosar-arxml.file` change from `Text` to `File`, and their help text stops describing a mount.

### FR-2 The file rides on the job

A third map on `IntegrationJob`, sibling to `settings` and `credentialValues`:

```json
"files": {
  "file": { "name": "devices.csv", "contentBase64": "bWFjLG5hbWUK..." }
}
```

- `name` is the file's own name, used verbatim in diagnostics and error messages (`devices.csv row 7`)
  and **never** opened, resolved or joined to a path. It is a label.
- `contentBase64` is the file's **bytes**, base64. Bytes and not text: an AUTOSAR extract emitted as
  UTF-16 by a vendor tool decodes correctly today via `File.ReadAllTextAsync`'s BOM detection, and a
  transport that carried "the text" would silently hand the provider mojibake. The runtime decodes
  with the same BOM detection, so what a provider sees is byte-for-byte what it saw before.
- The map is folded case-insensitively before anything looks in it, and two keys differing only in
  case are a rejection, exactly as `settings` and `credentialValues` are.

### FR-3 A File-kind setting's value never arrives in `settings`

Naming a File-kind key in `settings` is refused with a 400 saying so. With no mount there is nothing
for a bare name to open, so accepting one would mean accepting a job that cannot run and failing
later with a worse message. This is the same rule `Credential` already has, for the same reason: one
place per value.

### FR-4 The provider contract does not change at all

`ProviderContext.ReadFileAsync(settingKey, ct)` and `TryResolveFile(settingKey, out failure)` keep
their signatures and their meaning. `CsvDeviceListProvider` and `AutosarArxmlProvider` are **not
edited**. That is the test of whether the contract boundary was drawn in the right place: the
transport by which a file reaches a provider was never the provider's business.

`ProviderContext`'s doc comment already says a provider is handed no file path. That stays true, and
becomes true of the runtime as well.

### FR-5 The mount is gone

Deleted, not deprecated: `Integrations:FilesDirectory`, `DirectoryFileStore`, `RootedNames` and its
tests, the `./samples:/files:ro` compose mount, the startup log line naming the directory, and the
conformance suite's path-escape check. A caller can no longer name a file for the runtime to open,
so there is no path to escape from and no directory to contain a name within. The runtime opens
nothing on disk.

`FixtureFileStore` (the conformance suite's in-memory store) survives and becomes the shape the
real per-run store also has, which is the point: the offline harness and the live path now differ
only in where the bytes came from.

### FR-6 A ceiling, stated where it is enforced

`Integrations:MaxFileBytes`, default 33_554_432 (32 MiB), enforced on the **decoded** byte count of
each file, with a 400 naming the setting, the actual size and the ceiling. Above it sits a transport
bound on the apiApp's proxy route, sized for base64 inflation, so an oversized body is refused at
the door rather than buffered whole - the pattern `DocumentController` already uses for
`Fallen8:Ingestion:MaxUploadBytes`. The integrations runtime's own Kestrel body limit is raised to
match, because its default 30 MiB would refuse a job at the ceiling.

### FR-7 A file is dropped when the run ends

The per-run file store is created inside the same scope as the credential lease and reading from it
afterwards throws, so "dropped when the run ends" is observable behaviour and not a claim in a
document. A provider that squirrels the context away finds out, rather than quietly reading caller
data after the run it belonged to.

### FR-8 Studio renders it like the Knowledge screen

`SettingField` gains a `File` arm: a dropzone plus a file picker, showing the staged file's name and
size, with a clear affordance to replace it. There is still **no switch on provider id anywhere** in
`IntegrationsScreen.tsx` - the arm is chosen by the setting's kind, so the fifth integration that
needs a file gets the control for free.

It differs from Knowledge in one deliberate way: dropping a file **stages** it. A job also needs an
identity and the other settings, so the run happens when "run now" is pressed, not on drop.

The file is read and base64-encoded in the browser at submit time. An unknown setting kind still
falls back to a text input, so a Studio built before this change keeps working against a runtime
built after it (the field renders as text, the job is refused with a message saying why).

## Non-goals

- **No staged upload, no upload id, no resumable transfer.** One request carries the run. A staging
  area is state, and this runtime's whole thesis is that it holds none.
- ~~**No multipart on `/integrations/job`.**~~ **OVERTURNED** by
  [integration-file-transport](../integration-file-transport/spec.md). The reasoning above was
  sound and the conclusion was still wrong, which is worth recording rather than quietly editing.
  What it missed: base64-in-a-document has a ceiling that no configuration reaches. A browser
  composing that body holds the file's bytes, its base64 string and the serialised request at once,
  and a JavaScript string caps at 512 MiB, so the encoder failed at roughly 384 MiB of input while
  the runtime was configured to accept 512 MiB. An operator hit it with a real AUTOSAR handover.
  Multipart was not a convenience; it was the only shape that scales.

  The drift this non-goal feared is real, and the price paid for overturning it is structural rather
  than a promise to be careful: both transports deserialise into the SAME `IntegrationJob` and go
  through the same `TryNormalize`, with one set of messages, and
  `IntegrationsMultipartTest.OneJobSubmittedEitherWay_NormalizesToTheSameThing` fails if they ever
  stop agreeing. The JSON transport is unchanged and still the one a shell script writes.
- **No binary-in, binary-out provider API.** `IProviderFileStore.ReadAsync` still returns text,
  because both file providers parse text. Revisit when a provider needs actual bytes (a `.dbc`,
  a zip): the wire format is already bytes, so only the store's return type would move.
- **Nothing about the savegame load path.** `POST /loadgraph` names a file in the **server's own**
  persistence directory - a path the server wrote itself, not a file the user is holding. Different
  concept, deliberately untouched.

## Impact on existing features

| Feature / asset | Impact |
| --- | --- |
| `integrations` (runtime contract) | The job gains `files`; `FilesDirectory` and the directory store are removed. Its feature record and the published docs page both describe the mount and must be rewritten. |
| `autosar-arxml` | Its descriptor's `file` setting changes kind and help. No provider code changes. |
| Provider-descriptor snapshot | `features/done/integrations/provider-descriptors.json` changes for both providers; regenerate and recapture `screen-integrations.png`, which is exactly why that gate exists. |
| OpenAPI snapshot | `/integrations/job`'s remarks and its new transport bound change the snapshot; regenerate. |
| Studio | `SettingKind` union, `IntegrationJobRequest`, `SettingField`, `buildJob`, and the integrations screen tests. |
| MCP | `/integrations/*` is not bridged (a recorded deferral). The route's path and method are unchanged, so `McpContractTest` and `McpRestCoverageTest` are untouched. The deferral note is reviewed for honesty. |
| Compose / `docs/running` | The `:/files:ro` mount and its comment are deleted; no environment variable existed for it. |
| NL-assist dataset / eval | Checked: no dataset or eval item mentions an integrations file setting. No `RETRAIN-LOG.md` entry needed. |
| Architecture diagrams | No new deployable and no new channel: a file now travels inside a request that already existed. Diagrams unchanged. |
| Engine (`fallen-8-core`) | Untouched. Nothing here reaches the engine, so the browser-wasm probe is not implicated. |

## As built

Five things the implementation added that this spec did not call for. Each was forced by something
the code turned out to say.

**1. `ProviderSetting.Accept`.** A dropzone with no idea what the integration reads offers every file
on the machine. The descriptor carried nothing machine-readable to derive that from, and deriving it
from the `help` prose would be a per-provider special case in disguise, so the descriptor gained one
nullable string. It is a picker HINT only - a browser ignores it for a dropped file and the runtime
never checks it - and the catalog refuses it on any non-`File` setting. It also means the snapshot
diff is file-wide: `"accept": null` now sits on all 17 settings, deliberately, because
`"defaultValue": null` already does and one of the two being present is worse than both.

**2. `ConformanceCheck.NoPathEscape` became `FilesOnlyFromTheJob`, in the same slot.** Deleting it
was the plan. But `RunsOffline` shares that seam and its own comment says why the file half is
load-bearing: a run that produced entities while attempting no request got its data from somewhere,
and a check that could only see the network half would pass it. So the file seam survives as
`IJobFilesFactory` (the third seam beside `IProviderHttpFactory` and `IGraphTargetFactory`, with a
recording substitute in the suite), and check 9 now asserts the property that is still falsifiable:
every file the run read was one the job carried, for a setting the descriptor declares. That catches
the author who declares one key and reads another - previously a mid-run source failure, now a named
verdict. The report serialises check names, not numbers, so no archived report is misread.

**3. Two transport bounds, not one.** Both hops run on the framework's 30 MB default, so a 32 MiB
file (44.7 MB base64) fitted through neither. The runtime raises Kestrel's limit and the proxy route
carries a `[RequestSizeLimit]`, both set far ABOVE any legal job on purpose: the only size refusal a
caller should ever read is the runtime's `MaxFileBytes` message, which names both numbers. A bare
transport 413 would replace it with an empty body - or, worse, surface through the proxy as "the
runtime did not answer". `docs/security.mdx` gained the second body-limit exception this creates.

**4. The staged file is KEPT after a run, unlike a credential.** FR-7 drops it in the runtime, which
is what matters. In the browser it stays: a file is not a secret, it is the data the run wrote, and
re-running the same extract after fixing a setting is the common next action. It is cleared when
another integration is selected, because two integrations can declare the same setting key and a file
that rode along would be sent to the wrong one with nothing afterwards able to tell.

**5. `src/components/FileDropzone.tsx`, with the Knowledge screen moved onto it.** The alternative was
a second copy of the drag handlers, including the `preventDefault`-on-drag-over rule that silently
navigates the browser to the file when forgotten. It is the DROP half only: each screen keeps its own
picker, because Knowledge ingests on pick and Integrations stages for a run that also needs an
identity.

One thing the plan asked for and the code did not need: no provider `ObserveAsync` changed. Both file
providers read their file's name out of `settings` for their messages and diagnostic subjects, which
kept working because the runtime writes the uploaded file's name there as the setting's effective
value. That was the test of whether the contract boundary sat in the right place, and it held.

## What the review gate found

A green suite is not the same as a correct one; the review pass found these, and each is fixed with a
test where a test can hold it.

**Conformance check 9 failed a CORRECT provider** - the one defect that survived adversarial
verification, and two reviewers confirmed it by *running* the verifier rather than reading it. The
first cut treated every setting key a run ASKED about as one the job had to have carried, so a
provider politely probing an optional file with `TryResolveFile` - the only remaining use of that API
- was reported as non-conforming, and the verdict text told its author to look at the fixture. It
also consulted the raw ordinal `IntegrationJob.Files` rather than the folded map the run actually read
from, so a job whose file key differed only in case *ran fine and was reported as broken*, and it
would have thrown `NullReferenceException` on a job with a null `files` map. The check now asserts the
one thing that is still both true and falsifiable - every ask names a file setting the DESCRIPTOR
declares - which drops all three arms at once, and `JobFiles` counts asks and reads separately so the
offline check still sees reads only. Pinned by `AProviderProbingAnOptionalFileNobodySentStillConforms`,
mutation-checked.

**The two transport bounds were ordered wrongly.** The proxy's 64 MiB sat *above* the runtime's 56
MiB, so a 60 MiB body passed the proxy, failed the runtime while being forwarded, and surfaced as 503
"the runtime did not answer" - pointing whoever sent a too-big file at a perfectly healthy sidecar.
The proxy's bound is now 48 MiB and the runtime's is a fixed 64 MiB rather than derived from
`MaxFileBytes`, so the ordering holds for every configured ceiling instead of only the default.

**`MaxFileBytes = 0` logged "up to 0 bytes"** while meaning no ceiling at all. It now warns, and the
option says so.

**Studio: three real ones.** The file field's buttons were inside a `<label>`, so clicking the caption
activated the first one; it is a `<div>` now. Staging replaced the dropzone with a plain row, leaving
the form with no drop target - so a second drop would land on the document and navigate away from a
half-filled form; the zone stays and shows the staged file inside itself. And a file whose read
finished *after* the operator switched integration was staged onto the new one, which is exactly what
`select()`'s reset exists to prevent, arriving a moment too late; staging now checks that the
integration which asked for it is still selected.

## What a real file found (2026-08-24)

The first extract anybody pointed at this feature was an ordinary ARXML of a few tens of
megabytes, and it was refused with a bare transport message about the max request body size. Every
number above had been derived from a 32 MiB ceiling picked for symmetry with document upload, and the symmetry was the
mistake - an AUTOSAR system extract for one vehicle platform is nothing like a document. Three
separate things had to change, and only the first was the one being complained about.

**1. The ceiling and both bounds.** `Integrations:MaxFileBytes` is now 128 MiB, the proxy's transport
bound 192 MiB and the runtime's a fixed 256 MiB, still in that order so the only size refusal anyone
reads is the runtime's own message naming both numbers.

**2. The proxy stopped buffering.** `POST /integrations/job` no longer binds `[FromBody] JsonElement`:
it streams `Request.Body` straight through. Binding it held the job about four times over in the
apiApp - the parsed document, the UTF-16 string `GetRawText()` produces, and the UTF-8 bytes
`StringContent` encodes it back into - which at 32 MiB was merely wasteful and at 128 MiB is hundreds
of megabytes of large-object heap per in-flight request, for a hop whose whole contract is not to look
at the body. The cost, stated: a malformed body is now refused by the RUNTIME rather than by this
app's input formatter, which is the direction this controller already pointed. `/bulk/import` already
streamed the same way, and its OpenAPI operation has no `requestBody` either - so the snapshot losing
that block here is the house pattern, not an accident.

**3. The graph write had to be batched, and nothing in the plan foresaw it.** With transport fixed, the
run still failed - `errorKind: graph`, "Error while copying content to a stream" - because
`Fallen8RestTarget` sent every vertex in ONE `PUT /vertices`, a body far past what the graph's
own route refuses at Kestrel's 30 MB default. A perfectly healthy graph, reported as a graph that
would not answer. Vertices, edges, property writes and deletions now go in batches of 500. Batching
here rather than raising the cap on a shared route is deliberate: the write body is this deployable's
doing and is unbounded in exactly the way an uploaded file is.

Measured end to end afterwards, through the real chain (Studio's own proxy route → runtime → provider
→ graph), with a genuine 99.2 MiB device list:

| | result |
| --- | --- |
| body on the wire | 132.2 MiB |
| first run | HTTP 200, 40,000 elements created, 28 s |
| second run | HTTP 200, 0 created, **40,000 matched, `issuedMutations: false`** |

The second row is the one that matters: idempotence and claim reconciliation still hold at that size,
so batching did not quietly break the thing the whole identity model rests on. The batch-order
contract is pinned by `AWriteBiggerThanOneBatch_KeepsTheIdsInInputOrder_AcrossEveryBatch` on the
shared graph-target contract, so both targets are held to it, and a mutation that reverses ids inside
a batch fails it.

**The lesson, recorded because it was expensive:** the original bounds were computed rather than
tried. A single real file would have caught all three defects in one run, and one of them was not a
size limit at all.

**Four comments were left describing a mount that no longer exists** (`JobReport`, `ProviderContext`
twice, `ArxmlReader`), and the browser's `readBytes` claimed byte-exact transport fixed a
Windows-1252 file, which byte-order-mark detection does not do. Both corrected: in this repo a comment
that overclaims is a defect, and the ARXML reader's XML hardening is now MORE load-bearing than it
was, because whoever can reach the API chooses the document rather than an operator preparing a mount.
