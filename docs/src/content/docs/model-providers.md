---
title: "Model providers"
description: "Choose where the chat gateway and the embedding provider send their requests: the local Ollama sidecar, Nahil, OpenAI or Anthropic. Where the credential lives, what moves with a switch, which models a backend catalogues, and which backend served a call."
---

Fallen-8 has exactly two model capabilities, and both live in the REST app rather than in the
engine: the **embedding provider** behind [text-in embeddings and semantic
traversal](/semantic-traversal/), and the **chat gateway** (`POST /chat`) behind
[Studio's natural-language assist](/nl-assist/). The engine (`fallen-8-core`) never loads a model
and never opens a socket to one, which is why a bare `dotnet run` is model-free and both
capabilities answer `403` until you enable them.

Each capability picks its own backend, with its own setting. They are **independent on purpose**:
chat on a hosted provider while embeddings stay on a local sidecar is a normal configuration, not
a half-finished one.

## Which provider serves which capability

| Provider | Chat | Embeddings | Where it runs |
| --- | --- | --- | --- |
| Ollama (the shipped sidecar) | yes | yes | a container beside the instance, or any Ollama you point at |
| [Nahil](/nahil/) | yes | yes | nahil.dev, serving the same models over the same API |
| OpenAI | yes | yes, by explicit configuration only | api.openai.com |
| Anthropic | yes | **no** | api.anthropic.com |
| Onnx | - | yes | in-process, CPU, from an operator-provided ONNX export |
| LLamaSharp | - | yes | in-process, CPU, from an operator-provided GGUF |

Anthropic publishes no embeddings API, so `Fallen8:Embedding:Backend=Anthropic` is refused at
construction with that sentence rather than failing later on a request. Chat may stay on Anthropic
while embeddings run anywhere in the column above.

`Fallen8:Chat:Backend` takes `Ollama`, `Nahil`, `OpenAI` or `Anthropic`;
`Fallen8:Embedding:Backend` takes `Onnx`, `LLamaSharp`, `Ollama`, `Nahil` or `OpenAI`. Both match
**exactly**, case included: `openai` is refused with a message listing the accepted values, rather
than silently becoming a backend nobody chose.

## Turn one on

In the compose environment one variable picks the provider and applies its overlay:

```bash
F8_MODEL_PROVIDER=openai    F8_OPENAI_API_KEY=your-key    npm run env:up
F8_MODEL_PROVIDER=anthropic F8_ANTHROPIC_API_KEY=your-key npm run env:up
F8_MODEL_PROVIDER=local     npm run env:up                # the default: the sidecar
```

`F8_MODEL_PROVIDER=nahil` selects [Nahil](/nahil/), which also still selects itself from
`F8_NAHIL_API_KEY` alone. A value that is not one of the four is **refused before anything
starts**: a typo must not quietly leave you on a local sidecar you believe is off-box. An explicit
`F8_MODEL_PROVIDER` wins over leftover `F8_NAHIL_*` variables, and says so while it does.

There is no default for any key and there will not be one: a credential that appears from nowhere
is a credential nobody can rotate, so the overlay fails closed naming the variable. To set it
**once instead of per shell**, put the line in a
[`.env` file beside `docker-compose.yml`](/running/#environment-variables-compose) - it is
gitignored, and copying
[`.env.example`](https://github.com/cosh/fallen-8-core/blob/main/.env.example) is the quick way to
one. A variable set in the shell still wins over the file.

Unlike the Nahil overlay, the `openai` and `anthropic` overlays **keep the local sidecar running**,
because it is still the embedding backend. What it stops doing is pulling the two mini assist
models (`F8_PULL_ASSIST=0`, ~4.8 GB nothing would ask for); `bge-m3` still pulls. The larger
`phi4-f8` keeps its own gate, so add `F8_PULL_PHI4F8=0` if you do not want its ~9 GB either
([first start pulls models](/running/#first-start-pulls-models)).

| Variable | Provider | Meaning |
| --- | --- | --- |
| `F8_MODEL_PROVIDER` | all | `local` (default), `nahil`, `openai` or `anthropic` |
| `F8_OPENAI_API_KEY` | openai | Required by the overlay; it fails closed without one |
| `F8_OPENAI_URL` | openai | Defaults to `https://api.openai.com`. A **host root** |
| `F8_OPENAI_CHAT_MODEL` | openai | Defaults to `gpt-4o-mini` |
| `F8_OPENAI_CHAT_TIMEOUT` | openai | The chat budget in seconds; the overlay leaves it at `120` |
| `F8_ANTHROPIC_API_KEY` | anthropic | Required by the overlay; it fails closed without one |
| `F8_ANTHROPIC_URL` | anthropic | Defaults to `https://api.anthropic.com`. A **host root** |
| `F8_ANTHROPIC_CHAT_MODEL` | anthropic | Defaults to `claude-opus-5` |
| `F8_ANTHROPIC_MAX_TOKENS` | anthropic | Defaults to `4096`; the Messages API requires it per request |
| `F8_ANTHROPIC_CHAT_TIMEOUT` | anthropic | The chat budget in seconds; the overlay leaves it at `120` |

The model names are config strings and nothing more. Fallen-8 keeps no list of either vendor's
models and follows neither vendor's renames, so a renamed or retired model is one environment
variable. It can *ask* a backend what it lists today, which is a read you trigger rather than a
catalog it maintains: [which models the backend has](#which-models-the-backend-has).

### The settings underneath

An overlay writes ordinary [configuration](/configuration/) keys, so any other deployment method
sets the same ones:

```
Fallen8__Chat__Enabled=true
Fallen8__Chat__Backend=OpenAI                          # or Anthropic, Nahil, Ollama
Fallen8__Chat__OpenAI__Endpoint=https://api.openai.com
Fallen8__Chat__OpenAI__ApiKey=...
Fallen8__Chat__OpenAI__Model=gpt-4o-mini
Fallen8__Chat__TimeoutSeconds=120
Fallen8__Chat__Stream=true                             # the default; listed so the profile is complete

Fallen8__Chat__Anthropic__Endpoint=https://api.anthropic.com
Fallen8__Chat__Anthropic__ApiKey=...
Fallen8__Chat__Anthropic__Model=claude-opus-5
Fallen8__Chat__Anthropic__MaxTokens=4096

Fallen8__Embedding__OpenAI__Endpoint=https://api.openai.com
Fallen8__Embedding__OpenAI__ApiKey=...
Fallen8__Embedding__OpenAI__Model=text-embedding-3-small
```

`MaxTokens` exists because the Messages API requires a bound on every request; no other provider
has the knob. A `temperature` a caller sends with `POST /chat` reaches OpenAI and is **ignored on
Anthropic**: current Claude models reject `temperature`, `top_p` and `top_k` with a `400`, so that
backend sends no sampling field at all. `stop` maps to the provider's own stop sequences
everywhere. Nothing the caller did not ask for is sent: omit both and no options block goes out.

For a bare `dotnet run --project fallen-8-core-apiApp` - which reads neither an overlay nor
`.env` - the set-once home for these keys is
[.NET user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets): the project
already carries a `UserSecretsId`, so

```bash
dotnet user-secrets set "Fallen8:Chat:OpenAI:ApiKey" "your-key" --project fallen-8-core-apiApp
```

works as-is and keeps the credential outside the repository.

### Rules the configuration is held to

Every provider endpoint must be a **host root**: scheme, host, optional port, nothing more.
`https://api.openai.com/v1` is refused with a message naming the key, because each backend builds
its request URL by adding *its own* route to this value and none of them keeps a path you put there:
the Ollama-protocol client drops it (that is what .NET's `HttpClient` does to a base address as soon
as a request path starts with `/`), and the provider SDKs append to it, so `https://host/v1` would
become `https://host/v1/v1/...`. Either way the request goes somewhere you never configured and
reports only a puzzling 404. It is refused rather than rewritten, since guessing which half you meant
is how a prefix ends up quietly unreachable. Whatever route suffix a provider's API actually lives
under is a transport detail the backend adds itself, and it never appears in configuration.

HTTPS is expected for anything off your own network and is not enforced. There is no
certificate-validation bypass and none will be added.

A misconfigured backend does **not** stop the server. The model backends load on first use, so a
bad endpoint, a missing key or a missing model name becomes a `503` on that capability's endpoints,
carrying the exact reason, while the rest of the database runs normally. The same reason is logged
once at startup, so you do not have to make a request to find out.

## What moves, and what deliberately does not

Switching the **chat** backend moves prompts and nothing else. There is no stored state behind
`POST /chat`: a draft is a draft.

Switching the **embedding** backend is a different kind of decision, and the presets treat it as
one:

- **Nahil is the one provider that also moves embeddings**, and only because it serves the same
  `bge-m3`: same 1024 dimensions, same `Cosine` metric, same identity stamp. Nothing re-embeds and
  no index is rebuilt.
- **OpenAI embeddings are a different embedding function.** A different identity
  (`text-embedding-3-*`) and a different dimension (1536 or 3072), and once you declare that
  identity every vector and index you built under `bge-m3` reports an honest
  [identity mismatch](/vector-search/) rather than being ranked against the new vectors. That is not
  something anyone should get by picking a chat provider, so the `openai` and `anthropic` overlays
  leave `Fallen8:Embedding` alone entirely.

Moving embeddings to OpenAI is therefore a deliberate manual configuration, and it is **two**
changes, not one: set `Fallen8:Embedding:Backend=OpenAI` with its endpoint, key and model, **and**
set `ModelName`/`Dimension`/`IntendedMetric` to the new function's identity. The second half is what
produces the mismatch that protects the vectors you already have. Fallen-8 cannot check a stamp
against a function on the far side of an endpoint, so an old identity left in place would quietly
file OpenAI's vectors under the `bge-m3` stamp and rank the two together - confident, wrong
neighbours, with nothing to notice it. Vectors stored under the old stamp do not convert.
Re-embedding is a re-run you decide to make, and it is the reason `Fallen8:Embedding:Backend` and
`Fallen8:Embedding:OpenAI:Model` are never writable over REST: a write there would produce vectors
that no longer match the ones already stored under the same stamp.

There is no per-request backend selection and no failover between providers. One backend per
capability per deployment.

## Where the credential lives

In the instance's environment, and nowhere else. `GET /config` publishes Fallen-8's whole setting
inventory, and on an instance with no API key configured that route is anonymous, so every provider
key is catalogued **never writable**: the tier that publishes a key's name, tier and reason but
*no value*. They cannot be written over REST either, because a writable credential would let any
caller redirect your metered spend. No log line, error message or diagnostic contains the key, and
none quotes the endpoint either, since a URL can carry a credential inside it.

The two **model names** differ from each other, which is worth knowing before you try to change
one. A chat model is writable-tier and takes effect at the next boot. The embedding model is
never-writable, because it *is* the embedding function. And when compose supplies either as an
environment variable, the environment *wins* over a stored override: Studio renders that row
read-only and a write is refused naming the variable to change instead. That is
[how configuration authority works](/configuration/) generally, not something specific to a
provider.

## Which models the backend has

Because the chat model is the writable one, it is also the one you can pick from a list instead of
remembering. An instance will tell you what its backend catalogues, in two places:

- **F8 Studio**, on the [configuration](/configuration/) surface: with the Chat section open and
  the active backend's model row editable, that row suggests catalogued names as you type.
- **`GET /chat/models`** over REST, which answers with the running backend's name and its models.
  It is gated exactly like `POST /chat` - a `403` while chat is off, a key when the instance has
  one - and carries the same [rate limit](/security/#other-perimeter-controls), because one read can
  fan out a metadata call per catalogued model rather than making a single request.

It takes no body, and the answer is the backend plus one entry per catalogued model:

```bash
curl -H "X-Api-Key: $F8_KEY" http://localhost:8080/chat/models
```

```json
{
  "backend": "Nahil",
  "models": [
    { "name": "bge-m3:latest", "capability": "embedding", "available": true, "class": "C2" },
    { "name": "phi4-f8-mini:latest", "capability": "completion", "available": true, "class": "S1" }
  ]
}
```

Names come back verbatim and sorted, and every field except `name` is null wherever the backend
does not report it (the table below says which backend reports what).

The route polls nothing and caches nothing: it reads the backend at the moment you ask, under one
five-second budget for the whole read. How wide that read is depends on the backend: an
Ollama-protocol backend names its models in one call and then asks about each one, at most eight of
those in flight at a time, while OpenAI and Anthropic each answer in a single call (Anthropic's
first page of it, which is the one limit worth knowing here). Only the calls that go to a backend
needing a credential carry one, so a local sidecar is asked without one, exactly as every other
request to it is. Studio asks at most once per visit to the Chat section, and holds the answer for
a few minutes after you close the surface, so a model you pull while it is open may not appear
until that has lapsed. Merely viewing configuration triggers **no catalog fan-out**. That is not the same as
sending nothing: on an Ollama-protocol backend `GET /config` still runs its small, 3-second
[model-residency probe](/nahil/#checking-it-works), which Studio re-reads every ten seconds while
the Configuration card is open. The catalog is the read that can cost a call per model, and it is
the one nothing triggers on its own.

Whenever the catalog answers anything other than a list, the row stays the plain text field it has
always been, with one line naming the reason the instance gave, and typing is never blocked. The
realistic reasons: a backend that cannot answer inside the budget, a misconfigured backend saying so
the way it [always does](#rules-the-configuration-is-held-to), a credential the provider refused,
and this instance's own sensitive-endpoint rate limit, whose window is shared process-wide, so a
busy import loop can be why a list is missing rather than anything about your provider. These are
metadata reads and cost no tokens.

**The list is the running backend's.** It comes from the backend serving requests now, not from the
one your stored configuration is waiting to become. A backend switch takes effect at the next boot,
so between writing `Fallen8:Chat:Backend=Nahil` and restarting into it the list still answers for
the backend you are leaving, and the incoming backend's model is a name you type. The alternative
would be a list describing a backend that has not served a single answer yet, which is the same
reason the next section stamps provenance on responses rather than reading it off current
configuration.

**The list is a suggestion, not the set of names a backend will accept.** So the field stays free
text everywhere, and a name that is not in the list is not an error. Nahil is the worked example:
`f8-delegate:latest` is absent from its catalog listing, yet Nahil resolves it and serves it, and
the [naming section](/nahil/#one-name-can-mean-two-different-builds) tells you to configure exactly
that name when your sidecar volume predates a rename. A closed dropdown would have hidden a name
this documentation recommends.

**Embedding models are listed, not offered.** The list is neutral and names every model the backend
has, embedding models included, so you can tell one kind from the other. What it will not do is
offer one for selection, because an embedding model name is never writable: it *is* the identity
stamp beside every vector you have stored, so a picker on that row would be a control wired to a
refusal. Changing the embedding function stays the deliberate two-change configuration above.

What an entry can tell you depends on the backend, and it says only what the backend itself reports:

| Backend | What each entry carries |
| --- | --- |
| Ollama (the sidecar) | the name, whether it is a chat or an embedding model, and that it is present locally. An older sidecar that reports no capabilities leaves that unknown, and the entry still appears |
| [Nahil](/nahil/) | the same, plus whether a worker can serve it **right now** (a cold one answers `503` first and is waited out), plus Nahil's own class label, which has no published legend |
| OpenAI | names only, including models that are not chat models at all: the vendor's list reports no capability |
| Anthropic | names only, and only the first page of them; paging further is deliberately skipped |

**The model stays server-owned.** Reading a catalog changes what you can configure, not what a
caller may ask for: `POST /chat` carries no model field and gains none, just as there is no
per-request backend selection. One backend and one model per deployment, chosen where the rest of
the configuration is.

## Which backend served this call

A provider switch rewrites where requests go, so a stat display that reads *current configuration*
would rewrite the history of every answer that arrived before the switch. Fallen-8 therefore
**stamps provenance on the response** and the UI reads it from there:

| Where | What it says |
| --- | --- |
| `POST /chat` response | `backend`, the selector value that served **this** completion, beside `model` and `stats` |
| `GET /status`, `GET /config` | the chat block's `backend` and `model`, which is the *ambient* answer: where the next request would go. `model` is reported for every backend |
| `/subgraph` semantic summary | `embeddingBackend` and `embeddingIdentity` (the stamp, e.g. `bge-m3#1024#Cosine`) when a `queryText` was embedded for the request |
| Studio | the assist panel's status line names the ambient backend and model; each draft's stats line ends with the backend **that draft** came from |
| MCP `f8_overview` | `chatBackend` and `embeddingBackend`, so an agent can tell where a prompt would go before sending one |

Two honest gaps, both by contract. `resident` and `gpu` stay **null** for OpenAI, Anthropic and
any other backend with no residency API: there is nothing to probe, and "unknown" beats a guess.
The same rule applies one level in on Nahil, which *has* the API but does not report on every
model: only the local Ollama sidecar's `/api/ps` list is exhaustive, so only there does an absent
model mean `resident: false`. On Nahil an absent model is `null`, and `gpu` is always `null`
because a remote worker publishes no VRAM figure this host could read
([nahil.md](/nahil/#checking-it-works)).
And `POST /path` returns a bare JSON array with no envelope to carry a summary, so its semantic
provenance is the ambient `/status` answer rather than a per-call field; `/subgraph`, which does
have a summary, echoes it.

A vector-in request embeds nothing, so it reports no embedding provenance.

## Rate limits, refusals, and a stream that dies

The one deadline is yours: `Fallen8:Chat:TimeoutSeconds` (and
`Fallen8:Embedding:TimeoutSeconds`). There is deliberately **no separate retry budget**, because a
second deadline could only make the answer arrive at a time no setting explains.

Inside that budget:

- **`429` is waited out**, honouring `Retry-After`, with a clamp so a hostile header cannot park
  the request forever. Anthropic's non-standard `529` (overloaded) is treated the same way. Each
  wait logs one line naming the provider, the model and the reason, so a delay is visible while it
  is happening. When the budget runs out, the error says how long was spent waiting and on what.
- **A refusal is reported, not returned as an answer.** An Anthropic refusal stop reason or an
  OpenAI content-filter finish becomes a `502` naming the category, never an empty draft you would
  have to guess about.
- **A stream that dies mid-answer is detectable.** `Fallen8:Chat:Stream` is on by default, so the
  provider is asked to stream; `POST /chat` still answers with a whole completion and the response
  shape is unchanged. The reason to stream anyway is that a truncated answer fails with a `502`
  naming how much arrived, instead of being returned as a short answer the model never gave.
- **An answer that ran out of output budget is also a `502`**, and it names the ceiling:
  `Fallen8:Chat:Anthropic:MaxTokens` for Anthropic, the model's own output limit for OpenAI. Stopping
  at a ceiling means the answer is amputated, so handing it on as a draft would only move the failure
  to whatever consumed it, with nothing left pointing at the cause.
- **Token counts stay honest.** `promptTokens`, `completionTokens` and the derived
  `tokensPerSecond` come from the provider's own usage numbers when it sends them and are
  **null when it does not**, never invented. `durationMs` is wall-clock, measured here.
- **Embedding inputs are never truncated.** An input over the model's token ceiling is refused
  rather than half-embedded, so a chunk cannot be indexed while quietly wrong about its own tail
  ([the input ceiling](/semantic-traversal/#the-input-ceiling-2048-tokens-not-8192)). OpenAI has no
  truncate knob at all: the service refuses over-long input itself, which is the same posture.

The waits above are the only retries there are. Each provider SDK's own retry policy is switched
**off**, deliberately: left on, one call you counted as one request becomes three or four against
the provider, which multiplies metered spend invisibly and takes longer to fail.

## Studio's custom mode is a different thing

F8 Studio's NL assist has a second, older path: a **custom endpoint** the browser calls
**directly**, with presets including OpenAI and Anthropic. That is not one of the backends on this
page. There the prompt leaves the browser rather than the instance, and any API key is held in
browser state and never reaches Fallen-8.

Both remain supported and they answer different questions. Server-side is the better home for a
provider key: one credential in the instance's environment, never published, never logged, and
every Studio user on that instance gets the capability with no per-browser setup. Custom mode is
for a model *you* want to reach that the instance is not configured for. See
[F8 Studio](/studio/#nl-assist).

## See also

- [Nahil](/nahil/): the deep dive on that provider, including cold-model waits and its batch cap
- [Semantic traversal](/semantic-traversal/): the embedding provider, its settings and the input ceiling
- [NL assist](/nl-assist/): the assist models, the fine-tunes, and the training pipeline
- [Configuration](/configuration/): why a setting is never writable and where a value came from
- [Running](/running/): the compose variables and the first-start pulls
- [Troubleshooting](/troubleshooting/): a `403`, a `503` or a `504` from either capability
- [Architecture](/architecture/): where the model backends sit relative to the engine
