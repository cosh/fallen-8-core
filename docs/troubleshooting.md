# Troubleshooting

The snags people actually hit, with the shortest fix and a pointer to the doc that owns the
full story.

## NL assist returns "Model endpoint returned HTTP 404"

**Symptom.** Studio's delegate editor, using the built-in (local Ollama) backend, returns
`Model endpoint returned HTTP 404`.

**Cause.** The assist model (`phi4-f8-mini`) is not in the sidecar's volume yet — usually the
first-start pull has not finished, or it failed (no internet to `registry.ollama.ai`). The
container uses its own `f8-ollama-models` volume, **not** any Ollama installed on the host,
so pulling on the host does not help the container.

**Fix.**

```bash
npm run env:logs          # is the pull still running, or did it error?
```

- Still pulling → wait; assist answers as soon as it finishes (a few GB on first start).
- Errored (offline container) → pre-seed the volume from a machine with internet, then
  restart. Needs only Docker:

  ```bash
  bash scripts/ensure-models.sh
  npm run env:down; npm run env:up
  ```

- Meanwhile, switch the editor's backend preset to **stock `phi4-mini`** if that pulled but
  the fine-tune did not. Compile validation still works with any backend.

The model set and pre-seeding are covered in [Running](running.md).

## Semantic search says the provider is off, or returns 409

**Symptom.** A sample's text-in semantic search is disabled in Studio, or `POST
/embedding/search` / `semantic.queryText` returns 403/409.

**Cause and fix.** Text-in search needs the embedding provider **and** a model whose identity
matches the stored vectors. A bare `dotnet run` has no provider (403); the compose
environment has it on unless you set `F8_EMBEDDINGS=false`. A 409 means the provider's model
name/dimension/metric does not match the vectors baked into your data. Bring-your-own-vector
scans work regardless. Full rules: [Semantic traversal](semantic-traversal.md).

## "Import requires an empty graph" / loading a sample refuses

**Cause.** [Bulk import](bulk-import-export.md) and sample loading require an empty target so
ids do not clash. Studio gates a load into a non-empty graph behind a typed confirm that
erases first.

**Fix.** Save a checkpoint if you need the current data ([Save games](save-games.md)), then
let the load erase, or point at a fresh [namespace](namespaces.md).

## A path/subgraph request with an inline filter returns 403

**Cause.** Inline C# [delegates](delegates.md) require dynamic code execution, which is off by
default.

**Fix.** Launch with `F8_ENABLE_DYNAMIC_CODE=true` ([Security](security.md)), or use a
registered [stored query](stored-queries.md) or a code-free
[semantic block](semantic-traversal.md) — both run with the switch off.

## No OpenAPI / Scalar at :8080

**Cause.** The compose container runs in the Production environment; the OpenAPI document and
the Scalar reference are served **only** in Development.

**Fix.** Run a bare `dotnet run --project fallen-8-core-apiApp` (Development) and open
`http://localhost:5000/scalar/v0.1`. See [REST API](rest-api.md).

## GPU not detected / the sidecar runs on CPU

The GPU reaches the container through the NVIDIA Container Toolkit. Verify and force behavior
with `F8_GPU`; the full setup (Docker Desktop vs. native Linux, the verification command, the
AMD note) lives in [Running](running.md#gpu-acceleration).

## See also

- [Running](running.md) — models, GPU, and every launch option
- [Security](security.md) — the API key and dynamic-code switch
- [Studio](studio.md) — where the assist and semantic features surface in the UI
