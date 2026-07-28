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
sidecar (feature instance-config). A `403` means the chat gateway is off
(`Fallen8:Chat:Enabled` / `F8_CHAT=false`); a `503`/`404` means the assist model
(`phi4-f8-mini`) is not in the sidecar's volume yet — usually the first-start pull has not
finished, or it failed (no internet to `registry.ollama.ai`). The container uses its own
`f8-ollama-models` volume, **not** any Ollama installed on the host, so pulling on the host
does not help the container. (A custom browser-direct backend bypasses the instance; a 404
there is the model missing on _that_ endpoint.)

A common variant with the **custom browser-direct** backend: the editor's preset list offers
**"Ollama (fine-tuned phi4-f8 — GPU)"** (model `phi4-f8`) alongside the default
`phi4-f8-mini`. `phi4-f8` (~9GB) is pulled by default too, but it is queued **after** the
mini model and `bge-m3`, so it takes longer to become available on a fresh volume; picking
that preset before its pull finishes 404s. If you (or someone) set `F8_PULL_PHI4F8=0` for this
environment, `phi4-f8` is never pulled at all and that preset always 404s.

**Fix.**

```bash
npm run env:logs          # is the pull still running, or did it error?
```

- Still pulling → wait; assist answers as soon as it finishes (`phi4-f8` finishes later than
  the mini model on a cold volume — over 10 GB total on first start).
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

- Meanwhile, switch the editor's backend preset to `phi4-f8-mini` or stock `phi4-mini` — both
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

## "Import requires an empty graph" / loading a sample refuses

**Cause.** [Bulk import](/fallen-8-core/bulk-import-export/) and sample loading require an empty target so
ids do not clash. Studio gates a load into a non-empty graph behind a typed confirm that
erases first.

**Fix.** Save a checkpoint if you need the current data ([Save games](/fallen-8-core/save-games/)), then
let the load erase, or point at a fresh [namespace](/fallen-8-core/namespaces/).

## A path/subgraph/storedquery request returns 401

**Cause.** An API key is configured on the instance and the request did not carry it. Dynamic
code execution is always on — inline C# [delegates](/fallen-8-core/delegates/) are never refused for a
"code disabled" reason — so the only gate on the code endpoints is authentication.

**Fix.** Send the key in `X-Api-Key` (or `Authorization: Bearer <key>`) — see
[Security](/fallen-8-core/security/). `GET /status` reports `apiKeyRequired`/`authenticated` so you can tell
"reachable" from "authorized".

## No OpenAPI / Scalar at :8080

**Cause.** The compose container runs in the Production environment; the OpenAPI document and
the Scalar reference are served **only** in Development.

**Fix.** Run a bare `dotnet run --project fallen-8-core-apiApp` (Development) and open
`http://localhost:5000/scalar/v0.1`. See [REST API](/fallen-8-core/rest-api/).

## GPU not detected / the sidecar runs on CPU

The GPU reaches the container through the NVIDIA Container Toolkit. Verify and force behavior
with `F8_GPU`; the full setup (Docker Desktop vs. native Linux, the verification command, the
AMD note) lives in [Running](/fallen-8-core/running/#gpu-acceleration).

## See also

- [Running](/fallen-8-core/running/) — models, GPU, and every launch option
- [Security](/fallen-8-core/security/) — the API key (dynamic code execution is always on)
- [Studio](/fallen-8-core/studio/) — where the assist and semantic features surface in the UI
