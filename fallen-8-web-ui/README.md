# F8 Studio (`fallen-8-web-ui`)

Browser frontend for one or more running Fallen-8 instances. Spec, design, and plan live
in [features/web-ui/](../features/web-ui/); the NL-assist model backend is specified in
[features/web-ui/nl-assist/spec.md](../features/web-ui/nl-assist/spec.md).

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

Note: the delegate endpoints (`/path`, `/subgraph`, `/delegates/validate`) require
`Fallen8:Security:EnableDynamicCodeExecution=true` — that capability flag is the
independent kill switch for the code-execution surface. Authentication is separate and
all-or-nothing: if an API key is configured the whole service (including these endpoints)
needs it, and if not, the whole service is open. So the delegate editor works on a keyless
instance as long as dynamic code is enabled.

## NL assist (FR-26)

Configure under the delegate editor's "nl assist → configure": endpoint, API kind
(`ollama` | `openai`-compatible), model. Recommended local setup (MIT-only, per the
nl-assist spec): [Ollama](https://ollama.com) with `ollama run phi4-mini`, and
`OLLAMA_ORIGINS` set to this app's origin so the browser may call it. Non-loopback
endpoints show a "text leaves this machine" notice before the first send. Any model API
key is sent only to the model endpoint, never to a Fallen-8 instance.

## Tests

```bash
npm test             # Vitest unit + component (Monaco/model calls mocked)
npm run e2e          # Playwright; builds the SPA into the apiApp and launches it with
                     # an API key ("e2e-key") + dynamic code enabled (volatile durability)
                     # First time: npx playwright install chromium
```

## Regeneration

- `npm run gen:api` regenerates `src/api/openapi.d.ts` from the checked-in snapshot
  `features/web-ui/openapi-v0.1.json` (refresh the snapshot from a Development apiApp at
  `/openapi/v0.1.json`). The contract test (`tests/api-contract.test.ts`) pins every
  client route against the snapshot.
- `src/delegate/type-model.json` is hand-maintained against spec §6.2, which mirrors
  `fallen-8-core/Model/*.cs` — update both together.
