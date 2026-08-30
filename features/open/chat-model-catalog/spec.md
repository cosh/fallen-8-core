# Spec: Chat model catalog

**Status: open (specified 2026-08-30, not implemented).** This fires the recorded revisit
trigger from [instance-config](../../done/instance-config/spec.md) open question 3: "no
`/api/tags` model picker in v1 (right-sized). Revisit if a picker is wanted." A picker is now
wanted. This file is the living record; the plan beside it is the sequencing note.

## Why

Changing the chat model today means typing a free-text name into the Configuration surface (or
`PATCH /config`) and finding out at the first call whether it exists: a typo is a 404 minutes
later, a name that is an embedding model is a refusal, and a name the backend catalogues under a
different spelling is a silent mismatch. Every shipped backend can name its own models, so the
instance can offer real names instead of a blank field.

Verified live against `https://api.nahil.dev` on 2026-08-30 (every route authenticated):

- `GET /api/tags` answers `200` with `models[]`, each entry carrying `name`, `model`, `digest`
  and `nahil_class`. It does NOT carry capabilities.
- `POST /api/show {"model": "..."}` answers `200` with `capabilities` (`["completion"]` vs
  `["embedding"]`), `nahil_routable_now` (whether a worker can serve it right now, i.e. the
  cold-start 503 is predictable per model), `nahil_tags` (aliases) and `nahil_digest`.
- **`/api/tags` is not the whole resolvable set.** `f8-delegate:latest` is absent from tags yet
  `/api/show` resolves it (`nahil_routable_now: false`). Our own
  [docs page](../../../docs/src/content/docs/nahil.md) tells operators with a pre-rename sidecar
  volume to configure exactly that name, so a CLOSED dropdown would hide a name we recommend.
  Free-text entry stays first-class everywhere.
- `details` is empty on every entry and `nahil_class` has no published legend (observed: S1/S2
  on completion models, C1/C2 on embedding models). Documenting the class and populating
  `details` are Nahil-side asks, noted here as external and non-blocking.

Real Ollama serves the same two routes unauthenticated (`capabilities` in `/api/show` exists on
current versions; an older sidecar simply omits it). OpenAI and Anthropic each publish
`GET /v1/models` under their own protocols.

## Decisions

1. **One new read route, `GET /chat/models`, on the existing `ChatController`.** It inherits the
   controller's `Fallen8Level` scope and Chat capability gate (403 when `Fallen8:Chat:Enabled`
   is off, credentialed when an API key is configured, anonymous only on a keyless instance:
   the same posture as `POST /chat`). It also takes the sensitive rate-limit policy: one read
   fans out up to 1 + N outbound calls carrying the operator's credential, and the brake that
   bounds `POST /chat` should bound this too.
2. **D8 of instance-config is upheld, not revisited.** `POST /chat` still carries no model
   field; agents and clients cannot choose a model per request. The catalog feeds the
   CONFIGURATION write path that already exists: the picker writes
   `Fallen8:Chat:<Backend>:Model` (Restart tier) through `PATCH /config`, gated by the two-act
   configuration-write rule from
   [writable-instance-config](../../done/writable-instance-config/spec.md). No new write path.
3. **The catalog reflects the RUNNING backend.** `ChatBackendFactory.ResolveConnection` /
   `ResolveRemoteTarget` on the bound options, exactly as the residency probe resolves its
   target. A pending-restart backend switch is not previewed: after writing `Backend=Nahil` the
   catalog still answers for the running backend until the restart, and the Nahil model is typed
   free-text in that window. Revisit trigger: operators repeatedly switching backends blind and
   complaining about it.
4. **Transient and stall-bounded, like the residency probe.** The catalog read never touches the
   provider's lazy backend (reading a catalog must not construct a chat client or flip
   `IsLoaded`), uses a per-request transient transport, and runs under one shared budget of
   **5 s** for the whole read including the `/api/show` fan-out. `OllamaModelProbe` owns that
   rule today; the catalog follows it. The budget is a documented constant, not a setting.
5. **Per-backend sources, one neutral response shape.**
   - **Ollama / Nahil**: `GET /api/tags`, then one concurrent `POST /api/show` per listed model
     for `capabilities` and `nahil_routable_now`. The connection carries the credential exactly
     as the probe does (Nahil bearer on every call, never a header to a local sidecar). A failed
     or missing `/api/show` degrades that entry to capability unknown rather than dropping it.
   - **OpenAI**: `GET {endpoint}/v1/models` (bearer). The list includes non-chat models and
     reports no capability; entries pass through with capability null.
   - **Anthropic**: `GET {endpoint}/v1/models` (`x-api-key` + `anthropic-version`), first page
     with `limit=1000`; pagination beyond that is deliberately ignored.
6. **Errors are honest and match the chat gateway's vocabulary.** Backend unreachable or the
   configured backend invalid (`ChatBackendFactory.Validate` non-null): `503` problem details
   with the reason. Chat capability off: `403` (controller gate). Otherwise `200`. No endpoint
   value and no credential ever appears in a response or an error message (the nahil-backend
   rule).
7. **Studio renders the picker as a native combobox (`<input list>` + `<datalist>`).** Free text
   stays first-class (decision: the closed-dropdown trap above), no new CSS primitives (the
   writable-instance-config 5.1 rule), and only two rows ever get it: the ACTIVE backend's
   `Fallen8:Chat:<Backend>:Model` row. Embedding model rows never get a picker: every
   `Fallen8:Embedding:*:Model` is NotWritable under R3 (the value is the identity stamp beside
   stored vectors), so a picker there would be a button wired to a refusal.
8. **Studio filters, the route does not.** The route returns every catalogued model with its
   capability; Studio's picker excludes `capability === "embedding"` and keeps unknowns. The
   catalog stays a neutral read (a future informational embedding view can reuse it).
9. **MCP: conscious deferral, not a bridge.** `GET /chat/models` joins `POST /chat` in
   `McpRestCoverageTest`'s deferral list with the same reason: agents bring their own model, and
   the server-owned model is discoverable via `f8_overview`. The OpenAPI snapshot is
   regenerated (additions only).
10. **No engine change, no NL-assist impact.** This is apiApp + Studio + docs. The NL-assist
    prompt, dataset and custom mode are untouched; no `RETRAIN-LOG.md` entry.

## Functional requirements

### FR-1: The route

`GET /chat/models` (Fallen8-level, no `/ns/{ns}` twin, versioned `0.1`) answers:

```json
{
  "backend": "Nahil",
  "models": [
    { "name": "phi4-f8-mini:latest", "capability": "completion", "available": true,  "class": "S1" },
    { "name": "phi4-f8:latest",      "capability": "completion", "available": true,  "class": "S2" },
    { "name": "bge-m3:latest",       "capability": "embedding",  "available": true,  "class": "C2" }
  ]
}
```

- `backend`: the running backend's name, the same spelling `ChatResultREST.Backend` uses.
- `name`: verbatim from the backend's catalog; sorted ordinally for a stable contract.
- `capability`: `"completion"`, `"embedding"`, or null when the backend does not say (OpenAI,
  an old sidecar, a failed `/api/show`).
- `available`: Nahil's `nahil_routable_now`; `true` for a local sidecar's tags entries (they are
  on disk); null when the backend reports nothing (OpenAI, Anthropic).
- `class`: verbatim `nahil_class` passthrough, null elsewhere; documented as carrying no
  published legend.

Responses: `200` (catalog), `401` (key configured, none supplied), `403` (chat off), `429`
(rate limit), `503` (backend unreachable or misconfigured, problem details naming the reason,
never the endpoint value).

### FR-2: Ollama-protocol catalog (Ollama and Nahil)

Tags then show, concurrently per model, both under the single 5 s budget; credential handling is
the connection's, identical to the residency probe (bearer to Nahil on every call including
`/api/show`, no Authorization header to a sidecar, ever). A model listed by tags whose show call
fails is returned with `capability: null, available: null, class: null`.

### FR-3: Remote catalogs (OpenAI and Anthropic)

One GET each, per decision 5. Anthropic entries use `id` as `name`. Both return
`capability: null, available: null, class: null` on every entry.

### FR-4: Studio picker

On the Configuration surface, when the Chat section is open, chat is enabled, configuration
write is on, and the active backend's model row is editable (not env-declared, not locked), the
row's text input gains a `datalist` of catalog names filtered per decision 8, each option
labelled with what is known (class and warm state when present). The catalog is fetched at most
once per section visit and only under those conditions (no outbound fan-out from merely viewing
configuration). A non-200 catalog answer degrades the row to today's plain input plus a one-line
caption naming why the list is unavailable; typing is never blocked, and a typed value that is
not in the list is not an error (it may be `f8-delegate:latest`).

### FR-5: Gates

OpenAPI snapshot regenerated (additions only); `McpRestCoverageTest` deferral entry per
decision 9; convention tests hold (MIT header, no `Console.Write*`, exact package versions:
no new packages expected).

## Non-goals

- A per-request model on `POST /chat` (D8 upheld; would be a REST contract change with MCP
  propagation and a metering story).
- An embedding model picker or any embedding write path (R3).
- Previewing a pending-restart backend's catalog (`?backend=` parameter): revisit per
  decision 3.
- Server-side caching of catalog answers: one bounded read per picker visit does not need it.
- Any Nahil-side change: the catalog as served today is sufficient. The two external asks
  (a `nahil_class` legend, populated `details`) are nice-to-haves that would only enrich labels.
- Touching custom NL-assist mode, which remains browser-direct and unrelated.

## Impact on existing features (mandatory sweep)

| Feature / layer | Impact | Action |
|---|---|---|
| Engine (`fallen-8-core`) | None | No engine edit |
| [instance-config](../../done/instance-config/) | Open question 3's revisit trigger fires; D8 (server-owned model) explicitly upheld | Historical spec unchanged; this spec records the revisit |
| [writable-instance-config](../../done/writable-instance-config/) | `SettingRow` gains an optional generic suggestions affordance (kind `string` + suggestions renders input + datalist); dirty-state and poll-suspension rules (its 5.2) unaffected | Studio work in FR-4 |
| [nahil-backend](../../done/nahil-backend/) | No wire change; its catalog routes are consumed for the first time (verified live 2026-08-30) | None |
| [model-providers](../../done/model-providers/) / docs | The catalog/picker story needs a single home | One section on `docs/src/content/docs/model-providers.md`; one-line pointers from `configuration.md` and `nahil.md` (its "Model names" section already owns the naming story) |
| MCP (engine to REST to MCP) | New REST operation | Deferral entry with reason (decision 9); snapshot regenerated first |
| `fallen-8-rest-client` / integrations | Not affected (neither consumes chat) | None |
| Architecture diagrams | No new channel, layer or deployable | None |
| Screenshots | `screen-configuration.png` captures the Change feed section, so the picker does not appear in it | No recapture; no new screenshot (the picker is a row-level affordance, described in prose) |
| NL-assist dataset / eval | Prompt and dataset untouched | No RETRAIN-LOG entry |
| README "Key features" | Sub-feature of the existing model-providers entry, not a new key feature | No new bullet |

## Security notes

- The route reveals what the operator's credentialed backend catalogues. Its gate is the chat
  capability policy plus the instance's API key posture, the same boundary `POST /chat` already
  draws, and the sensitive rate limit bounds abuse of the outbound fan-out. The fan-out routes
  (`/api/tags`, `/api/show`, `/v1/models`) are metadata reads costing no tokens on Nahil's
  metering.
- No credential is ever emitted; no endpoint value appears in any message (both inherited rules,
  restated here because this route is the first to proxy a LIST from a credentialed backend).
