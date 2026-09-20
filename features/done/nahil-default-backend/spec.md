# Spec: Nahil is the default chat backend, and an unservable model says so

> **Status:** IMPLEMENTED and merged to `main` on 2026-09-09, from
> `feature/nahil-default-backend` (branch-only workflow: no GitHub issue or PR). Six commits: the
> refusal fix, its test guard, the flip, the review fixes, one docs contradiction, and
> [findings.md](./findings.md). Verified live against the real service end to end, not only at the
> handler seam: an unservable model answers `503` in about a second carrying the provider's own
> sentence, where the same request previously spent the full 600 s budget, and a servable one still
> answers `200` naming Nahil as the backend. Operator decision of the same date, taken while
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

**First, what it does NOT reach, because that turned out to be most of the environment.** The base
`docker-compose.yml` already named both backends explicitly before this feature
(`Fallen8__Chat__Backend=Ollama`, `Fallen8__Embedding__Backend=Ollama`), and environment variables
outrank everything, so `docker compose up` and `npm run env:up` are byte-identical before and
after. CI's compose check passes either way. The flip is therefore observable in exactly three
places: a hand-rolled deployment (a container run by hand, a systemd unit, a manifest) that enables
chat and names no backend; the `chat.backend` value `/status` and `/config` publish on a bare
instance; and the documented posture. That is a narrower blast radius than the change sounds like,
and it is worth stating plainly rather than discovering later.

| Site | Change |
|---|---|
| `Fallen8ChatOptions.Backend` | `= "Ollama"` becomes `= "Nahil"`, with the doc comment stating the fail-closed reason rather than naming a favourite |
| `Fallen8ChatOptions.NahilOptions.Endpoint` and `.Model` | **gain defaults** (`https://api.nahil.dev`, `phi4-f8-mini:latest`), which is what makes the flip land on the intended message. Validation reports the FIRST missing value, so without this an unconfigured instance is told about an endpoint that has one canonical value and a model its sibling block already names, instead of the credential. The credential keeps no default, ever: the asymmetry IS the fail-closed story. Both values are already stated twice in the repository (the Nahil overlay's `F8_NAHIL_URL` default and the Ollama block's model), so this adds no new fact |
| `Fallen8ChatOptions.TimeoutSeconds` | `120` becomes `600`, **entailed rather than incidental.** The transport waits out Nahil's 503-while-pulling *inside* this budget, and the Nahil overlay already raised it to 600 with the comment that 120 "would answer 504 to requests that were about to succeed". Shipping `Backend=Nahil` beside a 120 s budget would ship a default combination this repository already documents as broken. It is a ceiling and not a delay, so a fast backend is unaffected; the two overlays that want 120 (OpenAI, Anthropic) already set it explicitly. It also matches the measured local-CPU reality, where an assist prompt takes minutes |
| `docker-compose.yml` (the `fallen8` service) | **no new setting**, one new comment: the existing `Fallen8__Chat__Backend=Ollama` line is now load-bearing rather than tidy, because deleting it no longer falls back to Ollama. The comment says so, and says the rule: an environment that brings its own model names its own backend |
| `docker-compose.nahil.yml` | unchanged. It still names `Nahil` explicitly, which it must, to override the base file |
| `scripts/env-info.js` | the NL-assist line asserted `http://localhost:11434 (Ollama, ...)` unconditionally. It was already wrong under any provider overlay and would be wrong by default now, and it prints on both `env:up` and `env:status`, so it becomes provider-aware from the same variables `env-up.js` selects the overlay from |
| `ChatBackendFactoryTest` | three assertions were built on `new Fallen8ChatOptions()` meaning "a usable Ollama". They now pin the new contract: the shipped default is REFUSED naming `Fallen8:Chat:Nahil:ApiKey`, supplying that one value is enough, and the model resolver really reads the Nahil block (proved with a distinct value, since both Ollama-protocol blocks otherwise name the same model and a wrong-block resolver would look correct) |
| `McpBridgeTest` | asserts the shipped chat default reaches an agent through `f8_overview`; the expected value becomes `Nahil` |
| Startup posture | already logs the resolved backend and the validation problem, gated on `Enabled`; the flip only changes which of the two an unconfigured instance sees. No new log line |
| Docs | `nahil.md` loses "the local sidecar stays the default" and gains the narrow reading; `model-providers.md` gains a "the defaults, and the one case they decide" section and has its two overlay timeout rows corrected; the README key-features line is corrected |
| The two amended specs | `nahil-backend`'s "Ollama stays the default" sentence and the `model-providers` selector decision each get a dated amendment note, with the original left standing, per this repository's rule that a spec is a historical record |

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
  configured the validation message names `Fallen8:Chat:Nahil:ApiKey` and nothing else. Supplying
  that one value makes the default usable.
- With chat disabled, nothing about a bare instance changes: no client, no validation, no warning.
- `docker compose up` with no overlay still serves NL assist from the local sidecar, unchanged,
  because the base file names its backend.
- The Nahil overlay still selects Nahil, unchanged.
- `Fallen8:Chat:TimeoutSeconds` defaults to 600, so the default backend's warm-up fits inside the
  default budget.

**One coincidence, recorded rather than fixed.** Studio's NL-assist client gives up at exactly
600 000 ms (`NL_REQUEST_TIMEOUT_MS`), so the server budget and the browser's patience are now the
same number and which one reports first is a race. This is not new: the Nahil overlay has set 600
since that feature shipped, so it was already the case for every Nahil deployment, and the flip
only generalises it to un-overlaid ones. It is left alone deliberately, because the alternatives
are worse: lowering only the code default would leave the overlay racy and add a second number to
keep in step, and lowering both is a change to the Nahil deployment profile rather than to a
default. *Revisit trigger:* an operator reports a chat give-up whose message came from the browser
and did not explain itself, at which point the fix is to lower **both** to something like 540 so the
server always speaks first.
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
| F8 Studio | renders `/config`, so the Configuration card shows the new default with no code change. **No screenshot is recaptured**, and that was checked rather than assumed: the capture app is run with `Fallen8__Chat__*` wired explicitly (`screenshot-connect.spec.ts` guards on the Chat card reading `Ollama`), so like compose it names its own backend and the frames are unchanged. That guard's failure message now says the backend must be named explicitly, because the default no longer supplies a working one |
| The compose environment | one new explicit line on the base `fallen8` service; overlays unchanged in behaviour |
| Docs | three pages state the default; each corrected. No new page |
| Architecture diagrams | **No change.** The set of deployables and channels is the same; only which provider a default resolves to |
| `agent-host` | benefits twice: its steps run on the operator's standard provider by default, and its wall-clock cap is no longer at risk from a futile ten-minute retry |
| Browser probe, stored queries, provider descriptors, sample graphs | **No change.** Nothing here touches the engine, persistence or an index |
