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

The model set and pre-seeding are covered in [Running](/fallen-8-core/running/).

## Semantic search says the provider is off, or returns 409

**Symptom.** A sample's text-in semantic search is disabled in Studio, or `POST
/embedding/search` / `semantic.queryText` returns 403/409.

**Cause and fix.** Text-in search needs the embedding provider **and** a model whose identity
matches the stored vectors. A bare `dotnet run` has no provider (403); the compose
environment has it on unless you set `F8_EMBEDDINGS=false`. A 409 means the provider's model
name/dimension/metric does not match the vectors baked into your data. Bring-your-own-vector
scans work regardless. Full rules: [Semantic traversal](/fallen-8-core/semantic-traversal/).

## Document ingestion answers 403 / 428 / 503 / 507, or no entities appear

**Symptom.** An upload on Studio's **Knowledge** screen, or `POST /document` /
`POST /document/text`, is refused; or it is accepted and the entity network stays empty.

**Cause and fix.**

| Response | Cause | Shortest fix |
|---|---|---|
| `403` | The ingestion capability is off (`Fallen8:Ingestion:Enabled`, default **off**, so a bare `dotnet run` has it off), or `embed=true` while the embedding provider is off | `F8_INGESTION=true` (the compose default) and restart; or ingest with `embed=false` |
| `428` | The semantic layer is not bound: it never creates an index implicitly | Bind once: the Knowledge screen's **State** panel, or `POST /document/binding/ensure` |
| `503` | No docling endpoint is configured and the upload is a binary format (txt/md need none), or the global ingestion queue is at capacity (`MaxQueueLength`, 256) | Start the `docling` sidecar / set `Fallen8:Ingestion:Docling:Endpoint`; for a full queue, retry shortly |
| `507` | The namespace chunk ceiling is reached (`MaxChunksPerNamespace`, 100,000) | Delete documents, raise the ceiling, or ingest into another [namespace](/fallen-8-core/namespaces/) |
| `202`, then the row goes `failed` | Ingestion is asynchronous, so anything only knowable after conversion fails the queued document, not the call: a configured-but-unreachable docling sidecar, page/chunk caps, a dead worker | Read the reason in the document's `error` property; `GET /document` lists status per row |
| `202`, `indexed`, no entities | NLP enrichment is off or its sidecar is unreachable (`Fallen8:Nlp:Enabled`, default **off**). Enrichment is additive, so it never fails an ingest | `F8_NLP=true` (the compose default) and restart, then re-ingest |

`GET /status` carries the whole capability state (`ingestion`, `nlp`, sidecar reachability),
which is what Studio gates its UI on. Full rules:
[Semantic layer](/fallen-8-core/unstructured-ingestion/).

## "Import requires an empty graph" / loading a sample refuses

**Cause.** [Bulk import](/fallen-8-core/bulk-import-export/) and sample loading require an empty target so
ids do not clash. Studio gates a load into a non-empty graph behind a typed confirm that
erases first.

**Fix.** Save a checkpoint if you need the current data ([Save games](/fallen-8-core/save-games/)), then
let the load erase, or point at a fresh [namespace](/fallen-8-core/namespaces/).

A sample that ingests **documents** has a second, unrelated refusal: its **Load** button is
disabled, with the reason, when the instance lacks a capability the sample needs (ingestion off,
embedding provider off, the docling sidecar unreachable, or `/status` not resolved yet). That one
is fixed by the capability, not by erasing or switching namespace (see the ingestion entry above).

## A path/subgraph/storedquery request returns 401

**Cause.** An API key is configured on the instance and the request did not carry it. Dynamic
code execution is always on, inline C# [delegates](/fallen-8-core/delegates/) are never refused for a
"code disabled" reason, so the only gate on the code endpoints is authentication. A configured
key gates **every** route outside the anonymous allowlist, not just the code endpoints: Studio's
health chip then reads "unauthorized" and every screen except Connect is replaced by a prompt to
set the key.

**Fix.** Send the key in `X-Api-Key` (or `Authorization: Bearer <key>`); see
[Security](/fallen-8-core/security/). `GET /status` stays anonymous and reports
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

**Fix.** Allow-list the UI's origin on the data plane. The compose overlay allow-lists exactly
`http://localhost:<F8_UI_PORT>`, so this bites when you reach Studio under another host name
(`127.0.0.1`, a LAN address) or run a hand-rolled deployment. Exact key and form:
[Standalone deployment](/fallen-8-core/standalone-ui/).

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
[Standalone deployment](/fallen-8-core/standalone-ui/).

## The instance will not start: missing checkpoint or corrupt registry

**Symptom.** Startup aborts with "which does not exist; startup is aborted", "failed its
integrity check", or "is corrupt (invalid JSON)".

**Cause.** Nothing is masked on purpose: a registered save game whose files are gone, a
checkpoint that fails its CRC, or a `savegames.json` / namespace catalog that is not valid JSON
all abort startup rather than serving an empty graph or overwriting a bad file.

**Fix.** Restore the missing files, or remove the entry (`DELETE /savegames/{id}`) and restart;
for corrupt JSON, fix the file or move it aside and re-adopt the checkpoints with `PUT /load`.
The startup rules are in [Save games](/fallen-8-core/save-games/).

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
[Namespaces](/fallen-8-core/namespaces/#startup-load).

**If the activation answers `409` instead**, the namespace's directory holds checkpoint files that no
registered save game contains, and loading it would come up empty beside them. The refusal's detail
names the file and the fix, which is to register it: set `loadOnStartup` to `enabled`, restart, then
`PUT /ns/{name}/load` that checkpoint once
([activation](/fallen-8-core/namespaces/#loading-one-now)).

## No OpenAPI / Scalar at :8080

**Cause.** The compose container runs in the Production environment; the OpenAPI document and
the Scalar reference are served **only** in Development.

**Fix.** Run a bare `dotnet run --project fallen-8-core-apiApp` (Development) and open
`http://localhost:5000/scalar/v0.1`. See [REST API](/fallen-8-core/rest-api/).

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
AMD note) lives in [Running](/fallen-8-core/running/#gpu-acceleration).

## See also

- [Namespaces](/fallen-8-core/namespaces/): the startup-load policy, activation, and what a not-loaded namespace answers
- [Running](/fallen-8-core/running/): models, GPU, and every launch option
- [Security](/fallen-8-core/security/): the API key (dynamic code execution is always on)
- [Studio](/fallen-8-core/studio/): where the assist and semantic features surface in the UI
- [Standalone deployment](/fallen-8-core/standalone-ui/): the split UI/API topology, `F8_API_URL`, and the CORS allow-list
- [Semantic layer](/fallen-8-core/unstructured-ingestion/): document ingestion, the sidecars, and their limits
