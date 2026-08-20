# Plan: Nahil backend

Spec: [spec.md](spec.md). Built on `feature/nahil-backend`, merged to `main` 2026-08-20.

**Status: phases 1-8 done; phase 9 declined.** This file is kept as the record of
how the work was sequenced, with each phase's outcome noted. The spec's "As built" section is
where the deviations live; this one records what was done and what the gates said.

Construction sites (verified 2026-08-20): `Helper/OllamaHttpClientFactory.cs` (the one transport
builder; the deadline rule lives there and survived every phase), `Helper/OllamaConnection.cs` and
`Helper/NahilWarmupRetryHandler.cs` (new), `Chat/ChatBackendFactory.cs`,
`Chat/OllamaChatBackend.cs`, `Chat/OllamaModelProbe.cs` (+ its caller in
`Controllers/AdminController.cs`), `Chat/Fallen8ChatProvider.cs`, `Chat/IChatBackend.cs`,
`Embedding/EmbeddingBackendFactory.cs`, `Embedding/Fallen8EmbeddingProvider.cs`,
`Ingestion/DocumentIngestionService.cs`, `Configuration/Fallen8ChatOptions.cs`,
`Configuration/Fallen8EmbeddingOptions.cs`, `Configuration/Fallen8SettingCatalog.cs`,
`Controllers/Model/ChatREST.cs`, `Controllers/ChatController.cs`, `Program.cs`.

## Phase 1: pin explicit tags (config only) - DONE

`docker-compose.yml` and the two C# option defaults now carry `phi4-f8-mini:latest` /
`bge-m3:latest`. `Fallen8__Embedding__ModelName` stays `bge-m3` (spec decision 5). Docs swept for
the bare names used as CONFIG VALUES (`studio.md`, `semantic-traversal.mdx`, the semantic-search
screenshot spec's env block) and left alone where they name the model in prose.

## Phase 2: endpoint host-root validation - DONE, differently

`OllamaConnection.IsValid` is the one home: absolute `http`/`https`, `AbsolutePath == "/"`, no
query, no fragment, non-blank model, and a credential when it is Nahil. The message names the key
and says WHY (BaseAddress drops path prefixes) and never rewrites the URL. Reported at boot as a
warning and enforced at factory time as a latched 503, rather than as a startup throw - see the
spec's "As built" for the reasoning.

## Phase 3: the Nahil backend - DONE

`NahilOptions {Endpoint, ApiKey, Model}` on both option classes; a `Nahil` case in both factories
resolving through one `ResolveConnection` per provider, which is also what the residency probe and
the config view read, so none of the three can disagree about the target. Bearer credential set
once in `OllamaHttpClientFactory`. Catalog gained the six keys at the spec's tiers, including the
new R8 rule.

## Phase 4: retry on 503/429 - DONE

`NahilWarmupRetryHandler`, composed only onto a Nahil PROVIDER transport (never the sidecar, never
the probe - `CreateForProvider` / `CreateForProbe` make that structural rather than a flag a call
site can get wrong). Retry-After in both forms, backoff with jitter, 60 s per-wait clamp, the
caller's token as the only budget, one log line per retry, 503 and 429 kept distinguishable. No
new package: the handler is a screen of code and the repo has no resilience dependency.

## Phase 5: chat streaming - DONE

`Stream = true` behind `Fallen8:Chat:Stream` (Bool, Restart, default true, catalogued). Truncation
and mid-stream death both raise `ChatBackendOutputException` naming the partial length, which the
provider maps to 502 ahead of its generic catch. A cancelled call is explicitly NOT a truncation.

## Phase 6: embedding hardening - DONE

Order preservation across capped batches pinned end-to-end through the real ingestion path; the
mid-run failure report now names the un-embedded chunk range; the dimension-mismatch message
pinned with both numbers.

## Phase 7: per-request stop tokens - DONE

`stop` on `ChatOptionsSpecification` -> `ChatBackendOptions.Stop` -> `RequestOptions.Stop`, merged
with temperature and omitted entirely when neither is asked for. OpenAPI snapshot regenerated
(additions only).

## Phase 8: deployment profile + docs - DONE

`docker-compose.nahil.yml` (sidecar parked on an unactivated profile; endpoint and key have no
defaults so it fails closed), `scripts/env-up.js` applies it on `F8_NAHIL_URL`, and a CI
`docker compose config -q` step gates it - the only gate a compose file has here. Docs: new
`nahil` page, plus `running.mdx`, `nl-assist.md`, `troubleshooting.md`, `configuration.md`,
`semantic-traversal.mdx`, `studio.md`, `architecture.md` (prose and both diagrams) and the README
key-features line.

## Phase 9: renaming the chat model - DECLINED, not deferred

The change-request list's last item asked for a rename of the configured chat model at cutover.
Declined (spec decision 6): the fine-tunes are the operator's own, they are named `phi4-f8-mini`
and `phi4-f8` on every backend, and the model name is a configured string, so nothing here depends
on the choice.

## Gates, as run

- `dotnet build fallen-8-core.sln`: clean, no new warnings (warnings are errors).
- `dotnet test fallen-8-core.sln`: 2040 passed, 0 failed, 30 skipped. Never run with `-v q`.
- `npm --prefix docs run build`: 40 pages, all internal links valid.
- `docker compose -f docker-compose.yml -f docker-compose.nahil.yml config -q`: valid, and the
  service list confirms the sidecar is not started.
- OpenAPI snapshot regenerated and reviewed: additions only.
- Browser probe: not run, and not implicated - nothing under `fallen-8-core/` was touched.

## Outstanding

Nothing. The Studio configuration and connect screenshots were recaptured for the retagged model
name; phase 9 is declined rather than pending.
