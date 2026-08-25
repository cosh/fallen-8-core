---
title: "Troubleshooting"
description: "Common snags (first-start model pulls, the embedding provider, missing-key 401s, GPU detection) and their fixes."
---

The snags people actually hit, with the shortest fix and a pointer to the doc that owns the
full story.

## NL assist fails to draft (404 / 403 / 503)

**Symptom.** Studio's delegate editor cannot draft a fragment. In the default **instance**
backend the instance answers `403` (chat gateway off) or `503` (backend unreachable / model
missing); in a **custom** browser-direct backend you see `Model endpoint returned HTTP 404`.

**Cause.** By default NL-assist routes browser → the instance's `POST /chat` → the Ollama
sidecar. A `403` means the chat gateway is off (`Fallen8:Chat:Enabled` / `F8_CHAT=false`); a
`503`/`404` means the assist model
(`phi4-f8-mini`) is not in the sidecar's volume yet, usually the first-start pull has not
finished, or it failed (no internet to `registry.ollama.ai`). The container uses its own
`f8-ollama-models` volume, **not** any Ollama installed on the host, so pulling on the host
does not help the container. (A custom browser-direct backend bypasses the instance; a 404
there is the model missing on _that_ endpoint.)

**With [Nahil](/nahil/) the model is not on this machine at all, so read the status code.** Nahil
answers `503` while it pulls a catalogued model onto a worker, and the instance waits that out
rather than failing. So a `503` from *your instance*, immediately, is Nahil being unreachable or
misconfigured, and the message names the setting to fix. A `504` after a long wait is the other
case: the pull did not finish inside `Fallen8:Chat:TimeoutSeconds`, and that message names the model
and how long was spent waiting. The logs carry one line per retry.

A common variant with the **custom browser-direct** backend: the editor's preset list offers
**"Ollama (fine-tuned phi4-f8, GPU)"** (model `phi4-f8`) alongside the default
`phi4-f8-mini`. `phi4-f8` (~9GB) is pulled by default too, but it is queued **after** the
mini model and `bge-m3`, so it takes longer to become available on a fresh volume; picking
that preset before its pull finishes 404s. If you (or someone) set `F8_PULL_PHI4F8=0` for this
environment, `phi4-f8` is never pulled at all and that preset always 404s.

**Fix.**

```bash
npm run env:logs          # is the pull still running, or did it error?
```

- Still pulling → wait; assist answers as soon as it finishes (`phi4-f8` finishes later than
  the mini model on a cold volume, over 10 GB total on first start).
- Errored (offline container) → pre-seed the volume from a machine with internet, then
  restart. Needs only Docker:

  ```bash
  bash scripts/ensure-models.sh
  npm run env:down; npm run env:up
  ```

- `F8_PULL_PHI4F8=0` was set and you want the `phi4-f8` (GPU) preset → unset it (or set `1`)
  and restart:

  ```bash
  npm run env:down
  npm run env:up
  ```

- Meanwhile, switch the editor's backend preset to `phi4-f8-mini` or stock `phi4-mini`: both
  pull first. Compile validation still works with any backend.

The model set and pre-seeding are covered in [Running](/running/).

## NL assist spins on "generating", then gives up (504)

**Symptom.** The delegate editor's assist panel sits on **generating&hellip;** for a long time and
then reports a gateway timeout. Nothing is misconfigured: the model is pulled, `/status` shows the
chat gateway on, and a `504` names `Fallen8:Chat:TimeoutSeconds`.

**Cause.** The model is running on **CPU**, and a local SLM on CPU is far slower than most people
expect. Measured on a 16-core laptop with `phi4-f8-mini` already resident, generation ran between
**0.07 and 0.18 tokens per second** across runs, i.e. roughly **5 to 15 seconds per generated
token**, with prompt evaluation around 7 to 9 tokens/second. An assist prompt is not small (it
carries the slot's member surface and few-shot examples, on the order of 1,800 tokens), so
prompt evaluation alone can outlast a 105-second budget, and one draft needs **minutes**, not
seconds. `ollama ps` showing `100% CPU` in the `PROCESSOR` column is the confirmation.

**Fix.** Give the sidecar a GPU: that is the only change that moves this from minutes to seconds.
See [GPU not detected](#gpu-not-detected--the-sidecars-run-on-cpu) below and
[Running](/running/#gpu-acceleration).

Raising `Fallen8:Chat:TimeoutSeconds` does **not** make a CPU host usable; it only decides how long
the editor waits before telling you the truth. It is worth raising only if you are content to wait
minutes per draft. Two things that do help a little on CPU: keep the prompt short (a terse intent
beats a paragraph), and prefer `phi4-f8-mini` over the ~9 GB `phi4-f8`, which is GPU-oriented.

The timeout is honest on the server side: `Fallen8:Chat:TimeoutSeconds` is the only deadline the
instance applies, so the wait you configure there is the wait it gives you. (It once was not: an
undocumented 100-second transport timeout pre-empted it and surfaced as a `500`.)

**Studio adds a ceiling of its own: it stops waiting after 10 minutes.** Below that the instance's
`504` is what you see, naming the setting you can change. Above it the editor gives up first, so
raising `Fallen8:Chat:TimeoutSeconds` past 10 minutes has no effect in the editor. That ceiling also
bounds a **custom** browser-direct backend, which has no Fallen-8 budget in front of it at all.

### "the input length exceeds the context length"

A `503` from the embedding surface carrying that sentence means one input was over the model's
per-input token ceiling - **2048 tokens for `bge-m3`**, on the local Ollama sidecar and on Nahil
alike, whatever `/api/show` advertises. Fallen-8 asks the backend never to truncate, so this is
reported rather than answered with a vector for part of the input. The message names what to
change; the short version:

| Where the text came from | What to do |
| --- | --- |
| Document ingestion | Lower `Fallen8:Ingestion:ChunkMaxChars` (default 3,600). A CJK corpus wants ~1,800 |
| `POST /embedding/...` | Shorten the item. `Fallen8:Embedding:MaxTextLength` only rejects the hopeless |
| `semantic.queryText` on `/path`, `/subgraph`, document search | Shorten the query - a query this long is not doing what you think anyway |

Nothing is written until every chunk of a document has a vector, so a failed ingest is
re-runnable rather than half-indexed. Background, including the measured chars-per-token table:
[the input ceiling](/semantic-traversal/#the-input-ceiling-2048-tokens-not-8192).

This is why a [Nahil](/nahil/) deployment sets the budget to **600** seconds and
not more: 600 s is exactly that 10-minute ceiling, so anything larger only moves the give-up from
the server, which explains itself in the response, to the browser, which cannot.

While a draft is running the editor tells you it is alive rather than leaving you guessing:

- The **Draft fragment** button counts the seconds it has been waiting, so a slow-but-working call
  looks different from a hung one. It counts the whole run, including any automatic refine attempt,
  while the 10-minute ceiling applies to each model call separately.
- **Cancel** aborts the request, and closing the editor does too. An abandoned draft no longer keeps
  running in the background, which previously meant the next draft queued behind it.

## Semantic search says the provider is off, or returns 409

**Symptom.** A sample's text-in semantic search is disabled in Studio, or `POST
/embedding/search` / `semantic.queryText` returns 403/409.

**Cause and fix.** Text-in search needs the embedding provider **and** a model whose identity
matches the stored vectors. A bare `dotnet run` has no provider (403); the compose
environment has it on unless you set `F8_EMBEDDINGS=false`. A 409 means the provider's model
name/dimension/metric does not match the vectors baked into your data. Bring-your-own-vector
scans work regardless. Full rules: [Semantic traversal](/semantic-traversal/).

## An integration run vanished, or timed out while the graph kept changing

**Symptom.** You started a run, the call failed or timed out after a couple of minutes, and yet the
graph carried on filling. Or the page was closed and now there is no way to tell whether the run
finished.

**Cause.** A run is deliberately built to outlive its caller: interrupting it midway would leave a
half-applied snapshot, so it finishes what it started even when nobody is left to read the answer.
Older builds paired that with a synchronous job call, so the connection that would have carried the
report was gone long before the run ended and the outcome was lost for good.

**Fix.** Nothing is wrong with the graph, and the run is almost certainly fine. Ask for it:

```bash
curl -sS http://localhost:8080/integrations/run/<your-integration-identity>
```

That answers the phase it is in while it runs, and the report once it has ended. F8 Studio shows the
same thing as a run panel on the Integrations screen, and it re-attaches after a reload. If you get a
`404`, the runtime has no slot for that identity: it has not run in this process, or a restart or
enough other identities have displaced it - the runtime keeps only the current and most recent run
per identity, in memory.

If a run really is taking hours, check whether it is in `embed-summaries`. That phase is model
inference, not graph work, and on a CPU-backed model it costs seconds per element
([Integrations](/integrations/)).

## Semantic search succeeds but finds nothing

**Symptom.** A vector or text-in search returns `200` with an empty result list. Nothing is
wrong-looking: no error, no warning, just no hits.

**Cause.** kNN over an empty index **succeeds** - there is simply nothing to rank - so an index
with no members is indistinguishable from "nothing is similar" at the search surface. Studio now
says so beside the Run button when the selected index reports zero members; over REST you have to
look. Check the member count in the `indices` block of `GET /status`, or the Indexes screen.

**Fix**, in the order these actually bite:

1. **Nothing was ever embedded.** For an integration run, the embed opt-in is off by default -
   tick *embed entity summaries* on the run form, or send `"embedSummaries": true`
   ([Integrations](/integrations/)). Note that re-running over an unchanged source embeds
   nothing, because only created or changed entities get a new summary: clear the namespace
   (`HEAD /ns/<name>/tabularasa`) and run again. Clearing drops index definitions too, so
   recreate the bound vector index afterwards.
2. **The index is bound to a different name.** A bound index only ever projects the one embedding
   name it declares. The Indexes screen shows it as `bound:<name>`; if the run wrote `default`
   and the index binds `arxml-summary`, neither is wrong and they will never meet.
3. **The label constraint excludes everything.** Constraints are applied *before* scoring, so a
   label nothing carries yields nothing rather than the unconstrained answer. Drop the `label`
   and see whether hits appear.
4. **The index is raw, not bound.** An unbound index holds only vectors somebody added
   explicitly, so element embeddings never reach it ([Vector search](/vector-search/)).

## Document ingestion answers 403 / 428 / 503 / 507, or no entities appear

**Symptom.** An upload on Studio's **Knowledge** screen, or `POST /document` /
`POST /document/text`, is refused; or it is accepted and the entity network stays empty.

**Cause and fix.**

| Response | Cause | Shortest fix |
|---|---|---|
| `403` | The ingestion capability is off (`Fallen8:Ingestion:Enabled`, default **off**, so a bare `dotnet run` has it off), or `embed=true` while the embedding provider is off | `F8_INGESTION=true` (the compose default) and restart; or ingest with `embed=false` |
| `428` | The semantic layer is not bound: it never creates an index implicitly | Bind once: the Knowledge screen's **State** panel, or `POST /document/binding/ensure` |
| `503` | No docling endpoint is configured and the upload is a binary format (txt/md need none), or the global ingestion queue is at capacity (`MaxQueueLength`, 256) | Start the `docling` sidecar / set `Fallen8:Ingestion:Docling:Endpoint`; for a full queue, retry shortly |
| `507` | The namespace chunk ceiling is reached (`MaxChunksPerNamespace`, 100,000) | Delete documents, raise the ceiling, or ingest into another [namespace](/namespaces/) |
| `202`, then the row goes `failed` | Ingestion is asynchronous, so anything only knowable after conversion fails the queued document, not the call: a configured-but-unreachable docling sidecar, page/chunk caps, a dead worker | Read the reason in the document's `error` property; `GET /document` lists status per row |
| `202`, `indexed`, no entities | NLP enrichment is off or its sidecar is unreachable (`Fallen8:Nlp:Enabled`, default **off**). Enrichment is additive, so it never fails an ingest | `F8_NLP=true` (the compose default) and restart, then re-ingest |

`GET /status` carries the whole capability state (`ingestion`, `nlp`, sidecar reachability),
which is what Studio gates its UI on. Full rules:
[Semantic layer](/unstructured-ingestion/).

## "Import requires an empty graph" / loading a sample refuses

**Cause.** [Bulk import](/bulk-import-export/) and sample loading require an empty target so
ids do not clash. Studio gates a load into a non-empty graph behind a typed confirm that
erases first.

**Fix.** Save a checkpoint if you need the current data ([Save games](/save-games/)), then
let the load erase, or point at a fresh [namespace](/namespaces/).

A sample that ingests **documents** has a second, unrelated refusal: its **Load** button is
disabled, with the reason, when the instance lacks a capability the sample needs (ingestion off,
embedding provider off, the docling sidecar unreachable, or `/status` not resolved yet). That one
is fixed by the capability, not by erasing or switching namespace (see the ingestion entry above).

## A path/subgraph/storedquery request returns 401

**Cause.** An API key is configured on the instance and the request did not carry it. Dynamic
code execution is always on, inline C# [delegates](/delegates/) are never refused for a
"code disabled" reason, so the only gate on the code endpoints is authentication. A configured
key gates **every** route outside the anonymous allowlist, not just the code endpoints: Studio's
health chip then reads "unauthorized" and every screen except Connect is replaced by a prompt to
set the key.

**Fix.** Send the key in `X-Api-Key` (or `Authorization: Bearer <key>`); see
[Security](/security/). `GET /status` stays anonymous and reports
`apiKeyRequired`/`authenticated`, so you can tell "reachable" from "authorized".

**If you never meant to secure it:** the compose environment passes `F8_API_KEY` straight
through, so a value left in the shell that ran `npm run env:up` secures the data plane silently.
`env:up` and `env:status` print a warning when they see it. Clear it and re-run:

```bash
unset F8_API_KEY                 # PowerShell: Remove-Item Env:F8_API_KEY
npm run env:up
```

## Studio says the instance is unreachable, but it is up

**Cause.** `npm run env:up` runs Studio as its own container on its own origin
(`http://localhost:8081` by default) talking to the API on `http://localhost:8080`, so every
call is cross-origin, and CORS is deny-all until the UI's origin is allow-listed
(`Fallen8:Security:AllowedCorsOrigins`). A blocked preflight fails at the fetch layer exactly
like a dead server, which is why the Connect screen shows a CORS hint for cross-origin
instances.

**Fix.** Allow-list the UI's origin on the data plane. The compose overlay allow-lists **both
loopback spellings** of the UI (`http://localhost:<F8_UI_PORT>` and
`http://127.0.0.1:<F8_UI_PORT>`), so this bites when you reach Studio under a name that is neither
- a LAN address, a hostname, a reverse proxy - or run a hand-rolled deployment. Exact key and
form: [Standalone deployment](/standalone-ui/).

## Studio says "checking…" for ever, and never says anything else

**Symptom.** The health chip, the Configuration panel and the instance row all sit on `checking…`
indefinitely. Not "unreachable", not "unauthorized" - no verdict at all. The API is up, and
`docker ps` says its container is healthy.

**Cause.** Something **accepted** the TCP connection and then sent nothing back. A refused
connection fails fast and reads as `unreachable`; a silent one leaves the request pending, and a
pending request is not an error, so the UI keeps waiting. The usual culprit on Windows with Docker
Desktop is a wedged IPv6 loopback forward for one published port: `localhost` resolves to `::1`
first, so every call goes down the dead path while the service answers perfectly on IPv4.

**Confirm it** - the asymmetry is the whole diagnosis, and it takes two commands:

```bash
curl -4 -m 5 -o /dev/null -w '%{http_code}\n' http://127.0.0.1:8080/status   # 200
curl -6 -m 5 -o /dev/null -w '%{http_code}\n' http://[::1]:8080/status       # hangs, then 000
```

Compare another published port (`3000`, `8081`) the same way. If only one port is affected, it is
the forward and not the app.

**Fix.** Point the instance at `http://127.0.0.1:<port>` - and reach Studio at a `127.0.0.1`
origin too if it is on its own port, since the page's origin has to stay allow-listed (both
loopback spellings are, above). To get `localhost` working again, `wsl --shutdown` and start Docker
Desktop: restarting Docker Desktop **alone does not recycle `wslrelay`**, which is the process
holding the broken forward, so the fault survives it. Rule out a genuine reservation first with
`netsh int ipv6 show excludedportrange protocol=tcp`.

Since the [Studio's](/studio/) reachability probe carries a 10 s deadline, a silence now reads as
**no answer** with the address named, rather than as an endless spinner - but an older Studio, or a
hung call on any other screen, still shows the spinner.

## The F8 Studio container will not start

**Symptom.** The standalone UI container exits at start instead of serving the SPA, and `docker
logs` ends with `f8-config: F8_API_URL contains a quote or backslash; refusing to start` (or the
same line for a newline).

**Cause.** The entrypoint writes the browser-facing endpoint into a one-line JS literal in
`config.js` before nginx launches. A quote, backslash, or newline would break out of that literal,
so it refuses to start rather than serve a `config.js` that silently sends the app back to
same-origin.

**Fix.** Pass the value unquoted and on one line (`-e F8_API_URL=https://graph.example.com`, or
`F8_API_URL: https://graph.example.com` in your compose file), then restart. Only a deployment that
sets `F8_API_URL` itself can hit this: `npm run env:up` pins it for you. Full story:
[Standalone deployment](/standalone-ui/).

## The instance will not start: missing checkpoint or corrupt registry

**Symptom.** Startup aborts with "which does not exist; startup is aborted", "failed its
integrity check", or "is corrupt (invalid JSON)".

**Cause.** Nothing is masked on purpose: a registered save game whose files are gone, a
checkpoint that fails its CRC, or a `savegames.json` / namespace catalog that is not valid JSON
all abort startup rather than serving an empty graph or overwriting a bad file.

**Fix.** Restore the missing files, or remove the entry (`DELETE /savegames/{id}`) and restart;
for corrupt JSON, fix the file or move it aside and re-adopt the checkpoints with `PUT /load`.
The startup rules are in [Save games](/save-games/).

## `/generate` or `/benchmark` answers 400 "Namespace required"

**Symptom.** A `curl` or script that used to work now returns `400` problem+json titled `Namespace
required`, whose detail names `/ns/{namespace}/generate` (or `/benchmark`). Nothing was generated.

**Cause.** Those two are the only namespace-scoped routes with **no bare-URL alias** to `default`.
One writes a graph and the other reports a graph's throughput as yours, and while they were
Fallen-8-level a call meaning "the namespace I am working in" silently hit `default` instead. They
now refuse rather than choose. Every other bare route still aliases `default` unchanged.

**Fix.** Name the namespace. `GET /ns` lists the ones this Fallen-8 holds:

```bash
curl "http://localhost:8080/ns/flights/generate?nodeCount=200&edgeCount=5"
curl "http://localhost:8080/ns/default/benchmark?iterations=100"   # when default is what you meant
```

The response of the first names the namespace it wrote into. Details: [Benchmark](/benchmark/).

## A namespace answers 503 "Namespace not loaded"

**Symptom.** Every call against one namespace returns `503` problem+json titled `Namespace not
loaded`, with `"namespaceState": "notLoaded"`. `GET /ns` still lists it, with no counts, and F8
Studio tags it `not loaded` instead of showing a screen. Other namespaces are fine.

**Cause.** This process did not load it. Either its own startup-load policy says so
(`loadOnStartupEnabled: false` on its catalog entry), or it inherited
`Fallen8:Namespaces:LoadOnStartup=false`, or the boot ran with
`Fallen8:Namespaces:StartupLoadMode=DefaultOnly`. The boot log says which, one line per namespace.
**Nothing is lost:** no engine was constructed, so its checkpoint and its write-ahead log were never
opened, and a namespace with no engine is never a member of a save.

**Fix.** Load it now, and separately decide about the next boot:

```bash
curl -X POST http://localhost:8080/ns/archived/activate           # this process, no restart
curl -X PATCH http://localhost:8080/ns/archived \
  -H "Content-Type: application/json" -d '{"loadOnStartup":"enabled"}'   # every boot from now on
```

In F8 Studio, opening that namespace offers the same two: an **Activate now** button (the first call) and the **at startup** selector in the Connect screen's Namespaces panel (the second).

If the selection itself is what went wrong, boot once with
`Fallen8__Namespaces__StartupLoadMode=All`: it ignores every exclusion, so you never have to
hand-edit the one file whose malformation aborts startup. Full rules:
[Namespaces](/namespaces/#startup-load).

**If the activation answers `409` instead**, the namespace's directory holds checkpoint files that no
registered save game contains, and loading it would come up empty beside them. The refusal's detail
names the file and the fix, which is to register it: set `loadOnStartup` to `enabled`, restart, then
`PUT /ns/{name}/load` that checkpoint once
([activation](/namespaces/#loading-one-now)).

## No OpenAPI / Scalar at :8080

**Cause.** The compose container runs in the Production environment; the OpenAPI document and
the Scalar reference are served **only** in Development.

**Fix.** Run a bare `dotnet run --project fallen-8-core-apiApp` (Development) and open
`http://localhost:5000/scalar/v0.1`. See [REST API](/rest-api/).

## GPU not detected / the sidecars run on CPU

Two containers can use a GPU: Ollama (assist and embedding inference speed) and the NLP sidecar,
which swaps `en_core_web_lg` for the `en_core_web_trf` transformer on the device (extraction
quality, not speed). `npm run env:up` prints which tier it picked; `/status` does not report it,
so that line and the sidecar log are how you tell. Ollama's GPU handoff is runtime-only
(`docker-compose.gpu.yml`) and works in the published-image mode too; the NLP transformer model
is baked in at build time, so applying `docker-compose.gpu-nlp.yml` by hand needs `--build`
(which `env:up` always passes), and `env:up:published` stays on the CPU-tier NLP image.

The GPU reaches the container through the NVIDIA Container Toolkit. Verify and force behavior
with `F8_GPU`; the full setup (Docker Desktop vs. native Linux, the verification command, the
AMD note) lives in [Running](/running/#gpu-acceleration).

## See also

- [Namespaces](/namespaces/): the startup-load policy, activation, and what a not-loaded namespace answers
- [Running](/running/): models, GPU, and every launch option
- [Security](/security/): the API key (dynamic code execution is always on)
- [Studio](/studio/): where the assist and semantic features surface in the UI
- [Standalone deployment](/standalone-ui/): the split UI/API topology, `F8_API_URL`, and the CORS allow-list
- [Semantic layer](/unstructured-ingestion/): document ingestion, the sidecars, and their limits
