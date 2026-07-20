# F8 Studio (`fallen-8-web-ui`)

Browser frontend for one or more running Fallen-8 instances. Spec, design, and plan live
in [features/done/web-ui/](../features/done/web-ui/); the NL-assist model backend is specified in
[features/done/web-ui/nl-assist/spec.md](../features/done/web-ui/nl-assist/spec.md).

## Development

```bash
npm install
npm run dev          # Vite dev server; same-origin API calls proxy to http://localhost:5000
                     # (override with F8_API_URL). Start the apiApp separately:
                     # dotnet run --project ../fallen-8-core-apiApp
```

The repo root's package.json mirrors the common scripts, so `npm run build:apiapp`,
`npm run dev`, `npm run test:ui`, and `npm run e2e` also work from the repository root.

The registry's default "local" instance uses the app's own origin — in dev that means the
Vite proxy; in production the apiApp that serves the SPA.

## Production build (served by the apiApp, gap G-1)

```bash
npm run build:apiapp   # emits into ../fallen-8-core-apiApp/wwwroot (gitignored)
dotnet run --project ../fallen-8-core-apiApp
# open http://localhost:5000
```

Cross-origin instances need their `Fallen8:Security:AllowedCorsOrigins` to include the
origin this app is served from.

## Auth (lightweight, Cognito-extensible)

An instance secured with `Fallen8:Security:ApiKey` is registered with that key on the
Connect screen. The client sends it as `Authorization: Bearer <key>` (the apiApp accepts
this alongside `X-Api-Key`); a future OIDC/JWT scheme (e.g. AWS Cognito) reuses the same
header shape — add a new `auth.kind` in `src/instances/types.ts` and a token source, and
no call site changes. Keys live in this browser's local storage and travel only to their
own instance.

The anonymous `GET /status` doubles as the connection probe: it reports whether the
server requires a key and whether this request's credential was accepted
(`StatusREST.ApiKeyRequired`). The app shell locks every nav entry except Connect until
the active instance is reachable AND authorized, so a missing or wrong key shows as
"unauthorized" — never as a working connection (`src/app/AppShell.tsx`,
tests in `tests/app-shell.test.tsx` and e2e scenario 11).

Note: the delegate endpoints (`/path`, `/subgraph`, `/delegates/validate`) require
`Fallen8:Security:EnableDynamicCodeExecution=true` — that capability flag is the
independent kill switch for the code-execution surface. Authentication is separate and
all-or-nothing: if an API key is configured the whole service (including these endpoints)
needs it, and if not, the whole service is open. So the delegate editor works on a keyless
instance as long as dynamic code is enabled.

## NL assist (FR-26 + nl-assist-ux)

Works out of the box: the default **built-in** backend is the stack `docker-compose.yml`
ships (local [Ollama](https://ollama.com) on `:11434`, defaulting to the fine-tuned
`f8-delegate` model with `phi4-mini` as the selectable base — MIT weights + MIT runtime,
nothing bundled into F8). The compose stack pulls both on first start. The panel shows a
reachability status; if it is unreachable, start the stack with `npm run env:up` (and follow
`npm run env:logs` while the first-run model pull completes). Switching to **custom** under "nl assist → configure" exposes endpoint, API kind
(`ollama` | `openai`-compatible), model, temperature, and presets (local Ollama, OpenAI,
Anthropic) as prefills — hosted endpoints must send CORS headers and show a "text leaves
this machine" notice before the first send. Any model API key is sent only to the model
endpoint, never to a Fallen-8 instance. Drafts accumulate as a clickable history with
per-call token/duration stats (raw provider payload expandable per attempt); re-drafting
the same description asks the model for a distinct variant.

## Tests

```bash
npm test             # Vitest unit + component (Monaco/model calls mocked)
npm run e2e          # Playwright; builds the SPA into the apiApp and launches it with
                     # an API key ("e2e-key") + dynamic code enabled (volatile durability)
                     # First time: npx playwright install chromium
```

## Regeneration

- `src/api/types.ts` is the hand-curated client contract, mirroring the checked-in snapshot
  `features/done/web-ui/openapi-v0.1.json` (refresh the snapshot from a Development apiApp at
  `/openapi/v0.1.json`). The contract test (`tests/api-contract.test.ts`) pins every
  client route against the snapshot.
- `src/delegate/type-model.json` is hand-maintained against spec §6.2, which mirrors
  `fallen-8-core/Model/*.cs` — update both together.
