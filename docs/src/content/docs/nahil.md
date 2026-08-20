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
F8_NAHIL_URL=https://api.nahil.dev \
F8_NAHIL_API_KEY=your-key \
npm run env:up
```

The local Ollama sidecar is **not started** and nothing is pulled onto the machine. Without the
helper script, the overlay is a normal compose file:

```bash
docker compose -f docker-compose.yml -f docker-compose.nahil.yml up
```

| Variable                 | Required | Meaning                                                                     |
| ------------------------ | -------- | --------------------------------------------------------------------------- |
| `F8_NAHIL_URL`           | yes      | The Nahil base URL. A **host root**: scheme, host, optional port, no path.   |
| `F8_NAHIL_API_KEY`       | yes      | The bearer credential.                                                       |
| `F8_NAHIL_CHAT_MODEL`    | no       | The chat model, as Nahil's catalog names it.                                 |
| `F8_NAHIL_EMBED_MODEL`   | no       | The embedding model. Must be the one your stored vectors came from.          |
| `F8_NAHIL_EMBED_API_KEY` | no       | A separate key for embeddings, when you want the two metered apart.          |
| `F8_NAHIL_CHAT_TIMEOUT`  | no       | The chat budget in seconds; the overlay sets `600`.                          |
| `F8_NAHIL_EMBED_BATCH`   | no       | Items per embedding request; the overlay sets `32`.                          |

### The settings underneath

The overlay writes ordinary [configuration](/configuration/) keys, so any other deployment
method sets the same ones:

```
Fallen8__Chat__Backend=Nahil
Fallen8__Chat__Nahil__Endpoint=https://api.nahil.dev
Fallen8__Chat__Nahil__ApiKey=...
Fallen8__Chat__Nahil__Model=phi4-f8-mini:latest
Fallen8__Chat__TimeoutSeconds=600

Fallen8__Embedding__Backend=Nahil
Fallen8__Embedding__Nahil__Endpoint=https://api.nahil.dev
Fallen8__Embedding__Nahil__ApiKey=...
Fallen8__Embedding__Nahil__Model=bge-m3:latest
Fallen8__Embedding__MaxBatchSize=32
```

The two capabilities are independent: embeddings can run on Nahil while chat stays on a local
sidecar, or the other way round.

`Fallen8:Embedding:ModelName` is **not** in that list on purpose. It is the identity stamp
written beside every vector you have stored, not a request identifier - retagging it would make
every existing index report an identity mismatch for no benefit on the wire.

## Rules the configuration is held to

The endpoint must be a **host root**. `https://api.nahil.dev/v1` is refused with a message naming
the key, because .NET's `HttpClient` silently *drops* a path prefix as soon as a request path
starts with `/`: accepted, it would send every request to the wrong URL and report only a puzzling
404. It is refused rather than rewritten, since guessing which half you meant is how a prefix ends
up quietly unreachable.

HTTPS is expected for anything off your own network and is not enforced. There is no
certificate-validation bypass and none will be added.

A misconfigured backend does **not** stop the server. The model backends load on first use, so a
bad endpoint or a missing key becomes a `503` on that capability's endpoints - carrying the exact
reason - while the rest of the database runs normally. The same reason is logged once at startup,
so you do not have to make a request to find out.

### The credential is never published

`GET /config` publishes Fallen-8's whole setting inventory, and on an instance with no API key
configured that route is anonymous. Both Nahil keys are therefore catalogued **never writable**,
which is the tier whose entries publish a key's name, tier and reason but *no value*. They cannot
be written over REST either: a writable credential would let a caller redirect your metered
spend. No log line, error message or diagnostic contains the key.

Note that the model names, unlike the keys, are writable-tier - but when compose supplies them as
environment variables, the environment *wins* over a stored override. Studio renders those rows
read-only and a write is refused with `409` naming the variable to change instead. That is
[how configuration authority works](/configuration/) generally, not something specific to Nahil.

## Waiting for a cold model

The first request for a model that is not yet resident on a worker gets `503` plus a
`Retry-After`, because Nahil has started a pull that can take minutes. Fallen-8 waits it out
instead of failing:

- `Retry-After` is honoured in both forms, delta-seconds and HTTP-date. Without a usable one,
  the wait backs off from 2 s, capped at 30 s, with jitter so a fleet of instances does not
  return in lockstep.
- Each individual wait is clamped to 60 s, so a broken or hostile `Retry-After` cannot park a
  request.
- **The total is bounded by your own budget** - `Fallen8:Chat:TimeoutSeconds` /
  `Fallen8:Embedding:TimeoutSeconds` - and by nothing else. There is deliberately no separate
  retry budget: a second deadline could only make the answer arrive at a time no setting
  explains. When the budget runs out, the error says the model was not available in time, names
  it, and says how long was spent waiting.
- `429` is retried the same way, and stays distinguishable from `503` in the logs and in the
  error, because they call for different actions: wait for a pull, versus wait for a quota.
- Each retry logs one line, not one per poll.

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

## Embedding in batches

Nahil caps a request at 64 items, so the shipped default of 64 sits exactly *on* the limit. The
overlay uses **32** for headroom. Long documents are embedded in several capped requests and
reassembled in input order.

If a batch part-way through a long run fails - a spent quota, a model evicted between requests -
the report names which chunks did not make it and how many already had. Nothing is written until
every chunk has a vector, so the document is re-runnable rather than half-indexed.

## Streaming, and why it matters here

`Fallen8:Chat:Stream` is on by default, so the backend is asked to stream the completion.
`POST /chat` still answers with a whole completion; the response shape is unchanged. The reason
to stream anyway is that Nahil can run its own verification pass *after* delivery instead of in
front of it, and that a stream which dies half way is **detectable** - a truncated answer fails
with `502` naming how much arrived, instead of being returned as a short answer the model never
gave.

## Model names

Configure model names **with an explicit tag** (`bge-m3:latest`, not `bge-m3`). A bare name relies
on both ends agreeing about a default; a tagged one names the same thing everywhere. The
configured string reaches the request body verbatim - nothing strips, appends or normalizes a tag.

Nahil catalogs models under their **published registry names**, which may differ from a name you
gave a local copy of the same weights. Where they differ, Nahil's name is the one to configure,
and the digest is what tells you they are the same model.

## Checking it works

`GET /config` reports each capability's backend, its model, and whether the model is currently
resident on the backend. Residency is a best-effort probe with a 3 s bound: it authenticates like
everything else, and answers "unknown" rather than delaying the page. F8 Studio shows the same
thing in Connect → **Configuration**.
