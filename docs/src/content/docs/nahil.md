---
title: Nahil
description: "Run the embedding and chat models on Nahil (nahil.dev) instead of the local Ollama sidecar."
---

Fallen-8's two model capabilities - [text-in embeddings](/semantic-traversal/) and the
[NL-assist chat gateway](/nl-assist/) - talk to a backend over the Ollama HTTP API. By default
that backend is the Ollama sidecar the [compose environment](/running/) starts on the same
machine. It can instead be **Nahil** ([nahil.dev](https://nahil.dev)), which serves the same API
from remote hardware.

Both are supported, side by side, one per deployment. **The local sidecar stays the default** and
nothing about an existing setup changes. Nahil is opt-in, per capability, by configuration alone.

This page is the Nahil deep dive. Everything that is true of every provider - choosing one, where
the credential lives, what a switch does and does not move, and which backend served a given call
- lives once on [Model providers](/model-providers/).

## Why you might want it

A useful local model wants a GPU, and a 14 B model wants a big one. Nahil lets a small Fallen-8
host serve the same features without ~15 GB of weights on its disk or an idle GPU in its budget.
The trade you are making is explicit: the text of every prompt and every document you embed
leaves the machine, and you depend on a service being up.

## What actually changes

Almost nothing, and that is the point. Nahil serves the same models over the same wire format,
so:

- **Your vectors stay valid.** Same model, same 1024 dimensions, same `Cosine` metric, same
  [identity stamp](/vector-search/) stored beside every embedding. **Nothing re-embeds and no
  index is rebuilt.**
- **Your graph, indices and queries are untouched.** This is not an engine change.
- **The browser is not in the path.** F8 Studio keeps calling `POST /chat` and the embedding
  routes on your instance, and the instance calls Nahil server-side. The credential never
  reaches a client. (Studio's *custom* NL-assist mode is a separate, browser-direct path; it is
  unrelated and unsupported against Nahil, because its Ollama-native transport deliberately
  never authenticates.)

Three things are genuinely new, and all three come from Nahil rather than from Ollama:

1. **Every route needs a credential.** Real Ollama authenticates nothing; Nahil wants
   `Authorization: Bearer <key>` on all of them, including its version and residency probes.
2. **A cold model answers `503` first.** When you ask for a model that is catalogued but not yet
   resident on a worker, Nahil starts pulling it in the background and tells you to come back,
   with a `Retry-After`. Fallen-8 waits and retries rather than failing (see below).
3. **There are quotas.** A spent hourly token budget answers `429`, and a single request is
   capped at 64 items on the embedding path.

## Turn it on

With the compose environment, an overlay does the whole thing:

```bash
F8_NAHIL_API_KEY=your-key npm run env:up
```

The key is the whole switch: setting it selects the overlay, and `F8_NAHIL_URL` defaults to
`https://api.nahil.dev`. There is no default for the key and there will not be one - a credential
that appears from nowhere is a credential nobody can rotate - so the overlay fails closed, naming
the variable, if it is unset.

To set it **once instead of per shell**, put the line

```
F8_NAHIL_API_KEY=your-key
```

in a [`.env` file beside `docker-compose.yml`](/running/#environment-variables-compose) - it is
gitignored, and copying [`.env.example`](https://github.com/cosh/fallen-8-core/blob/main/.env.example)
is the quick way to one - and a plain `npm run env:up` selects Nahil from then on. A variable set
in the shell still wins over the file.

The local Ollama sidecar is **not started** and nothing is pulled onto the machine. Without the
helper script, the overlay is a normal compose file:

```bash
docker compose -f docker-compose.yml -f docker-compose.nahil.yml up
```

| Variable                 | Required | Meaning                                                                                    |
| ------------------------ | -------- | ------------------------------------------------------------------------------------------ |
| `F8_NAHIL_API_KEY`       | yes      | The bearer credential. Setting it is what selects the overlay.                              |
| `F8_NAHIL_URL`           | no       | The Nahil base URL; defaults to `https://api.nahil.dev`. A **host root**: scheme, host, optional port, no path. |
| `F8_NAHIL_CHAT_MODEL`    | no       | The chat model, as Nahil's catalog names it. Two live repos spell this fine-tune two ways - [which to pick](#one-name-can-mean-two-different-builds). |
| `F8_NAHIL_EMBED_MODEL`   | no       | The embedding model. Must be the one your stored vectors came from.                          |
| `F8_NAHIL_EMBED_API_KEY` | no       | A separate key for embeddings, when you want the two metered apart.                          |
| `F8_NAHIL_CHAT_TIMEOUT`  | no       | The chat budget in seconds; the overlay sets `600`.                                          |
| `F8_NAHIL_EMBED_BATCH`   | no       | Items per embedding request; the overlay sets `32`.                                          |

### The settings underneath

The overlay writes ordinary [configuration](/configuration/) keys, so any other deployment
method sets the same ones:

```
Fallen8__Chat__Enabled=true
Fallen8__Chat__Backend=Nahil
Fallen8__Chat__Nahil__Endpoint=https://api.nahil.dev
Fallen8__Chat__Nahil__ApiKey=...
Fallen8__Chat__Nahil__Model=phi4-f8-mini:latest
Fallen8__Chat__TimeoutSeconds=600
Fallen8__Chat__Stream=true                  # the default; listed so the profile is complete

Fallen8__Embedding__Enabled=true
Fallen8__Embedding__Backend=Nahil
Fallen8__Embedding__Nahil__Endpoint=https://api.nahil.dev
Fallen8__Embedding__Nahil__ApiKey=...
Fallen8__Embedding__Nahil__Model=bge-m3:latest
Fallen8__Embedding__MaxBatchSize=32

# The geometry, unchanged from a local deployment - which is what "nothing re-embeds" means.
Fallen8__Embedding__ModelName=bge-m3        # the identity stamp; untagged, and NOT retagged
Fallen8__Embedding__Dimension=1024
Fallen8__Embedding__IntendedMetric=Cosine
```

The two capabilities are independent: embeddings can run on Nahil while chat stays on a local
sidecar, or the other way round.

For a bare `dotnet run`, which reads neither the overlay nor `.env`, the set-once home for these
keys is [.NET user secrets](/model-providers/#the-settings-underneath).

`Fallen8:Embedding:ModelName` is in that list **only to say it does not change**. It is the
identity stamp written beside every vector you have stored, not a request identifier, and it stays
untagged: retagging it to `bge-m3:latest` to match the request would make every existing index
report an identity mismatch for no benefit on the wire. The compose overlay accordingly does not
set it - the base environment already did, and that value is the one that must survive.

The rules this configuration is held to - the host-root endpoint, HTTPS, a bad backend becoming a
capability `503` rather than a dead server, the never-writable credential tier and the two model
names' different tiers - are the same for every provider and are stated once on
[Model providers](/model-providers/#rules-the-configuration-is-held-to).

## Waiting for a cold model

The first request for a model that is not yet resident on a worker gets `503` plus a
`Retry-After`, because Nahil has started a pull that can take minutes. Fallen-8 waits that out
instead of failing, so **expect the first call after a quiet period to be slow rather than
broken**. A spent token budget (`429`) is waited out the same way, and each wait logs one line
naming the model and the reason, so the delay is visible while it happens.

The only knob is your own budget, and there is deliberately no separate retry budget:
[how a wait is bounded](/model-providers/#rate-limits-refusals-and-a-stream-that-dies). When it
runs out, the error says the model was not available in time, names it, and says how long was
spent waiting.

Fallen-8 never calls `/api/pull`, `/api/create`, `/api/delete` or `/api/copy`. Model residency is
Nahil's job. (The compose scripts that *do* pull models run against the local sidecar container
and are simply absent from a Nahil deployment.)

### Budgets worth knowing about

`Fallen8:Chat:TimeoutSeconds` defaults to 120, which the overlay raises to **600**. Nahil routes
to workers that can be CPU-only, and this budget now also has to cover waiting for a cold model -
120 s would answer `504` to requests that were about to succeed.

Do not raise it much above 600: F8 Studio's editor gives up at 10 minutes, so a larger server
budget only moves the give-up from the server, which explains itself, to the browser, which
cannot. See [troubleshooting](/troubleshooting/).

`Fallen8:Embedding:TimeoutSeconds` stays at 300. A cold `bge-m3` pull can exceed it; the failure
is honest and the next call succeeds.

The other bound on an embedding request is not a budget but a **token ceiling**, and it is smaller
than `bge-m3` claims: 2048 tokens per input, not the advertised 8192. Fallen-8 asks Nahil never to
truncate, so an input over it is refused rather than half-embedded. That is not a Nahil property -
the local sidecar stops at the same 2048 - so the whole story, including what to set
`Fallen8:Ingestion:ChunkMaxChars` to for your corpus, lives with the embedding provider: [the input
ceiling](/semantic-traversal/#the-input-ceiling-2048-tokens-not-8192).

## Embedding in batches

Nahil caps a request at 64 items, so the shipped default of 64 sits exactly *on* the limit. The
overlay uses **32** for headroom. Long documents are embedded in several capped requests and
reassembled in input order.

If a batch part-way through a long run fails - a spent quota, a model evicted between requests -
the report names which chunks did not make it and how many already had. Nothing is written until
every chunk has a vector, so the document is re-runnable rather than half-indexed.

## Streaming, and why it matters here

Streaming is on by default and a truncated answer is a detectable `502` on every backend
([why](/model-providers/#rate-limits-refusals-and-a-stream-that-dies)). The reason it matters
*here* specifically: Nahil can run its own verification pass **after** delivery instead of in front
of it, so the first token arrives without waiting for that pass.

## Model names

The models are the same ones a local setup uses - `phi4-f8-mini` (or `phi4-f8`) for the assist and
`bge-m3` for embeddings. Configure them **with an explicit tag** (`bge-m3:latest`, not `bge-m3`): a
bare name relies on both ends agreeing about a default, a tagged one names the same thing
everywhere. The configured string reaches the request body verbatim - nothing strips, appends or
normalizes a tag - so if a backend ever catalogues a model under a different name, that is an
environment variable and nothing more. To see what it catalogues, ask the instance:
[which models the backend has](/model-providers/#which-models-the-backend-has).

### One name can mean two different builds

Worth getting right before you compare outputs against a local sidecar:

| Name on Nahil | What it serves |
| --- | --- |
| `phi4-f8-mini:latest` | the published `phi4-f8-mini` repo - the **current** finetune |
| `f8-delegate:latest` | the published `f8-delegate` repo - the finetune under its **pre-rename** name |

`f8-delegate` was this fine-tune's original name and was renamed to `phi4-f8-mini` with no local
alias, deliberately. Both published repos still exist, so both names resolve on Nahil - to
different weights.

That matters because a sidecar volume built before the rename holds the `f8-delegate` build under
*both* local tags. If yours does - `ollama list` showing one id for `phi4-f8-mini:latest` and
`f8-delegate:latest` is the tell - then

```
F8_NAHIL_CHAT_MODEL=f8-delegate:latest
```

is what keeps the output you already had, and the default moves you to the current published
finetune. Neither is more correct; they are different weights, and it is one line either way. It
also creates no alias - it names a different catalog entry on a remote backend, which leaves the
clean rename intact.

What one such local build was is recorded, `ollama show` verbatim plus its blob digests, in
[`nl-assist-finetune/fixtures/phi4-f8-mini/`](https://github.com/cosh/fallen-8-core/tree/main/nl-assist-finetune/fixtures/phi4-f8-mini)
- its system prompt, prompt template and sampling parameters exist nowhere else.

## Checking it works

`GET /config` reports each capability's backend and whether its model is currently resident on
that backend - the chat block naming the model it will ask for, the embedding block naming the
identity stamped beside your vectors. Residency is a best-effort probe with a 3 s bound: it authenticates like
everything else, and answers "unknown" rather than delaying the page. F8 Studio shows the same
thing in Connect → **Configuration**.

**Expect "unknown" for the embedding model on Nahil, and read it as "no answer" rather than
"cold".** Nahil's `/api/ps` reports only the model classes it keeps warm on a worker: the chat
model shows up there once it has served a request (with a warm-worker count and an expiry a few
minutes out), while the embedding model never does - measured, including during a request that
succeeded. So a residency probe cannot see it, `resident` stays `null`, and the status line falls
back to the honest thing it does know: whether the provider has been called at all ("in use" /
"not called yet"). `gpu` is `null` on Nahil for the same kind of reason - the model runs on a
remote worker whose device this host cannot see, and Nahil publishes no VRAM figure, so nothing
here claims GPU or CPU. Whether a model can be served at all is a different question, and
`GET /chat/models` answers it: that read carries Nahil's own routable-now flag per model.
