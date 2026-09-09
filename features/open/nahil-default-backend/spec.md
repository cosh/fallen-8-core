# Spec: Nahil is the default chat backend, and an unservable model says so

> **Status:** specified 2026-09-09, implementing on branch `feature/nahil-default-backend`
> (branch-only workflow: no GitHub issue or PR). Operator decision of the same date, taken while
> revising [agent-host](../agent-host/spec.md): "the instance's shipped default backend should flip
> from Ollama to Nahil". This file is the record for that flip; the living documents for provider
> selection remain [model-providers](../../done/model-providers/spec.md) and
> [nahil-backend](../../done/nahil-backend/spec.md), both of which this feature amends in place.

Two changes, shipped together because the second is what makes the first honest:

1. **`Fallen8:Chat:Backend` defaults to `Nahil`** instead of `Ollama`.
2. **A model no worker can serve is refused immediately**, instead of being retried as a warm-up
   until the caller's budget expires.

## 1. Why the flip

[nahil-backend](../../done/nahil-backend/spec.md) decision 2 records "Ollama stays the default" and
that sentence has been true of the code since. It is no longer true of the deployments: Nahil is
the standard provider the operator runs, and the shipped default should say what the project
actually expects rather than what it expected first.

There is a second, sharper reason, and it is the one that makes this a correctness change rather
than a preference. **Today an incompletely configured chat gateway silently dials `localhost`.**
`Fallen8:Chat:Ollama:Endpoint` defaults to `http://localhost:11434`, so an operator who enables
chat on a hand-rolled deployment and names no backend gets an attempt at a sidecar that is
probably not there, reported as "the backend could not be reached". Worse, if some unrelated Ollama
IS listening on that host, the gateway quietly serves from it. Nahil has **no default endpoint by
construction** (that was deliberate in the nahil-backend spec: "selecting this backend without
configuring one is refused with the reason rather than silently dialling localhost"), so with
Nahil as the default the same mistake produces a refusal that names the key to fix. Failing closed
with a name beats dialling a guess.

## 2. What is NOT flipping, and why

- **`Fallen8:Embedding:Backend` stays `Onnx`.** It is not an Ollama default to begin with: `Onnx`
  is the in-process generator, so a bare deployment embeds with no network, no sidecar and no
  credential. Moving that default to a remote backend would make a credential mandatory for a
  capability that currently needs nothing, and the key is `NotWritable` under rule R3 precisely
  because the embedding backend carries an identity stamp. The operator's instruction was about
  the chat backend and this spec reads it narrowly on purpose. *Revisit trigger:* the operator
  wants embeddings remote by default too, which is a one-line change plus the identity-stamp
  argument re-checked.
- **`Fallen8:Chat:Enabled` stays `false`.** A bare `dotnet run` is therefore **unaffected by this
  flip**: with chat off, no backend is resolved, no client is constructed and no validation runs.
  The flip bites exactly one case, which is the case it is meant to bite: chat turned on with no
  backend named.
- **The accepted value set, the tiers, the catalog rules and the overlay mechanics** are
  unchanged. This is a default, not a new mechanism.

## 3. What the flip touches

| Site | Change |
|---|---|
| `Fallen8ChatOptions.Backend` | `= "Ollama"` becomes `= "Nahil"`, and the doc comment states the fail-closed reason rather than "the default" |
| `docker-compose.yml` (the `fallen8` service) | gains an explicit `Fallen8__Chat__Backend=Ollama`. **Not a contradiction of the flip:** the base environment *ships an Ollama sidecar*, pulls its models and wires its endpoint, so it must say which backend it is rather than inherit a default that no longer matches it. An environment that brings its own model names its own backend. |
| `docker-compose.nahil.yml` | keeps its explicit `Fallen8__Chat__Backend=Nahil` line: it must override the base file's explicit Ollama, and an overlay that relied on a code default would be a silent dependency |
| `McpBridgeTest` | asserts the shipped chat default reaches an agent through `f8_overview`; the expected value becomes `Nahil` and its comment says what that now means |
| Startup posture | already logs the resolved backend and the validation problem; the flip only changes which of the two an unconfigured instance sees. No new log line |
| Docs | `model-providers.md`, `nahil.md` and `running.mdx` state which backend is the default; each is corrected, and `nahil.md` loses "the local sidecar stays the default" |
| The two amended specs | `nahil-backend` decision 2 and the corresponding `model-providers` sentence get a dated amendment note in place, with the original left standing, per this repository's rule that a spec is a historical record |

## 4. Why the refusal fix belongs in the same push

A default that fails closed has to fail closed **honestly**, and today it does not.

Measured live on 2026-09-09 against `https://api.nahil.dev`, asking for `qwen3:4b`:

```
HTTP 503, no Retry-After header
{"error":"model 'qwen3:4b' is in the catalog but no attached worker serves class S1
  (2 worker(s) attached). Retrying will not help until a worker subscribes to that class ..."}
```

`NahilWarmupRetryHandler.ShouldRetry` returns true for every `503`, and `RetryAfterHandler`
deliberately has no retry-count cap (the caller's budget is the only bound). With no `Retry-After`
the wait falls back to the 2s-to-30s backoff, so a request for a model no worker serves spends the
whole chat budget (600 s in the shipped Nahil profile), writes about 25 log lines saying "warming
up", and then reports that the model "was not available in time". The first response already said
retrying is futile.

This matters more after the flip, because more deployments resolve to Nahil, and it matters most
to [agent-host](../agent-host/spec.md): one such step would consume an agent's entire wall-clock
cap.

**The fix.** A `503` whose body says no worker serves the model is a **refusal, not a warm-up**:
it reaches the caller at once with that sentence, as a 502-class backend refusal rather than a
timeout. Everything else about the warm-up contract is unchanged, and the fix is deliberately
narrow:

- The discriminator is read from the **response body**, not from the missing `Retry-After`. A
  missing header means "the provider did not say how long", which is exactly the case the backoff
  exists for and must keep serving. Treating absence as permanence would break every warm-up that
  omits the header.
- The match is on the body's stable phrase (`no attached worker serves`, case-insensitively),
  read from a bounded prefix of the body so a hostile or huge body cannot be pulled into memory.
  A body that does not match is retried exactly as today.
- The body is read once and the response is not left half-consumed for the retry path.
- The message the operator sees quotes the provider's own sentence, because it names the fix
  (a worker must subscribe to that class) and nothing local can.

**Non-goals here.** No new configuration key, no retry-count cap, no change to the `429` path, and
no attempt to classify any other provider's `503`. This is one provider's one documented refusal.

## 5. Acceptance

- A default-constructed `Fallen8ChatOptions` reports backend `Nahil`, and with no Nahil block
  configured the validation message names `Fallen8:Chat:Nahil:Endpoint`.
- With chat disabled, nothing about a bare instance changes: no client, no validation, no warning.
- `docker compose up` with no overlay still serves NL assist from the local sidecar, unchanged.
- The Nahil overlay still selects Nahil, unchanged.
- A `503` whose body carries the no-worker sentence fails the call **immediately**, with the
  provider's sentence in the message, and is not retried.
- A `503` with a `Retry-After`, a `503` with an unrecognised body, and a `429` are all still waited
  out on the existing schedule.
- Build clean, full suite green.

## 6. Impact on existing features

| Layer | Impact |
|---|---|
| The engine | **No change.** |
| `model-providers` / `nahil-backend` | amended in place with a dated note; no behaviour of theirs changes beyond the default and the refusal classification |
| The setting catalog | **No change.** Same key, same tier, same accepted values. The catalog reports the effective value, so it reports the new default without an edit |
| `Fallen8:Chat` validation and the latched 503 | unchanged mechanism; only which message an unconfigured instance gets |
| MCP | `f8_overview` reports `chatBackend`, so the value an agent sees changes. One test expectation, no bridge change, no new deferral |
| F8 Studio | renders `/config`, so the Configuration card shows the new default with no code change. The Connect and Configuration screenshots show a backend name, so they are recaptured |
| The compose environment | one new explicit line on the base `fallen8` service; overlays unchanged in behaviour |
| Docs | three pages state the default; each corrected. No new page |
| Architecture diagrams | **No change.** The set of deployables and channels is the same; only which provider a default resolves to |
| `agent-host` | benefits twice: its steps run on the operator's standard provider by default, and its wall-clock cap is no longer at risk from a futile ten-minute retry |
| Browser probe, stored queries, provider descriptors, sample graphs | **No change.** Nothing here touches the engine, persistence or an index |
