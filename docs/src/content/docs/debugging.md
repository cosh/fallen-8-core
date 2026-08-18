---
title: "Debugging in VS Code"
description: "Debug the engine, API, F8 Studio, and tests locally in VS Code, plus running the test suites."
---

> Keep this in sync with `.vscode/fallen-8-core.code-workspace` (launch configs, tasks,
> `playwright.env`), `fallen-8-web-ui/vite.config.ts` (dev port + proxy),
> `fallen-8-web-ui/playwright.config.ts` (the e2e web server), `docker-compose.yml` plus
> `docker-compose.split.yml` (ports, `OLLAMA_ORIGINS`), and
> `fallen-8-core-apiApp/Properties/launchSettings.json`. If any of those change, update this
> file in the same commit.

## Principle: debug locally, not inside the containers

The `docker compose` environment is for running the whole thing together (integration,
"does it work end to end"). Its containers are Release builds with no debugger attached.
For breakpoints, run the pieces **locally** and let VS Code attach its debuggers. The
engine (`fallen-8-core`) runs in-process with the API, so one backend debugger covers both.
The MCP server (`fallen-8-mcp`) is a fourth project and a separate process that no launch config
covers; run it against your local API as described in
[MCP server](/mcp-server/).

Prerequisites (all in the workspace's recommended extensions):
- `ms-dotnettools.csharp`: the C# / `coreclr` debugger
- `ms-playwright.playwright`: Playwright Test Explorer (e2e)
- `formulahendry.dotnet-test-explorer`: MSTest run/debug
- .NET 10 SDK and Node 22 on PATH.

Open the workspace file `.vscode/fallen-8-core.code-workspace` (not just the folder) so the
launch configs and tasks are available.

The one-off environment variables below are written in the POSIX `VAR=value command` form; in
PowerShell set them first (`$env:VAR = "value"`) and then run the command.

## Backend, engine, API, save games, delegate validation (C#)

**F5 → "F8 API App (Debug)".** Builds (`build-api`) and launches `fallen-8-core-apiApp`
under the `coreclr` debugger on `http://localhost:5000` in the `Development` environment,
and opens the Scalar API reference. Breakpoints anywhere in `fallen-8-core-apiApp/` **and**
`fallen-8-core/` are hit (same process).

Useful breakpoint locations:
- `Controllers/SaveGamesController.cs`, `Services/SaveGameRegistry.cs`,
  `Services/DurabilityLifecycleService.cs`: save-game registry + registry-driven startup
- `Helper/DelegateValidationHelper.cs`, `Helper/CodeGenerationHelper.cs`: the Roslyn
  compile / validation path
- `Program.cs` authorization block: to watch the auth decisions (key set vs not, capability flag)
- anything in `fallen-8-core/`: engine, transactions, indices, persistence.

To run against a clean rebuild, use **"F8 API App (Clean Build)"**.

## Frontend, F8 Studio (React / TypeScript)

Start the Vite dev server: run the **`ui-dev`** task (or `npm run dev` in `fallen-8-web-ui/`).
It serves the SPA on `http://localhost:5173` and proxies REST calls to `http://localhost:5000`,
i.e. straight into the backend you're debugging above. `F8_API_URL` overrides that target, so
`F8_API_URL=http://localhost:8080 npm run dev` hot-reloads the UI against the compose data plane
instead. Vite emits source maps, so you debug real `.tsx`:
- Browser devtools against `http://localhost:5173`, or
- a VS Code JS-debug browser session for in-editor breakpoints.

The dev proxy is a **prefix allowlist**, not a catch-all: `API_PREFIXES` in
`fallen-8-web-ui/vite.config.ts` lists the route prefixes that get forwarded. A path outside
that list never reaches the API, and the failure is quiet: a `GET` falls through to Vite's SPA
handling and comes back as `index.html` with status 200, so the screen reports a JSON parse
error instead of a clean 404. Note that every namespace-scoped call leaves the browser as
`/ns/{namespace}/…` ([Namespaces](/namespaces/)), so `/ns` has to be on the list
for the namespace-scoped screens to work at all. If a call fails only under `npm run dev` but
succeeds against `:5000` directly, add its prefix.

## Full stack at once

Run both: the backend under **"F8 API App (Debug)"** (:5000) and the **`ui-dev`** task
(:5173). You then have breakpoints on both sides with UI hot-reload. (There is no single
compound launch yet; start the two above. If you want a one-keypress compound + a browser
launch config, they can be added to the workspace file.)

Alternatively, to debug the SPA the way the all-in-one image serves it (same origin, no Vite
proxy), use **"F8 Studio (API + built UI)"**: it builds the SPA into the API's `wwwroot`
and serves it from `:5000`.

The default compose topology is the other one. `npm run env:up` always layers
`docker-compose.split.yml`, so Studio runs in its own container and reaches the API
**cross-origin** ([Standalone F8 Studio](/standalone-ui/)), which has its own
failure class. To reproduce it locally, register a personal Studio instance whose base URL is
the API's origin (rather than the same-origin default) and allow the UI's origin on the API by
adding `"Fallen8__Security__AllowedCorsOrigins__0": "http://localhost:5173"` to the launch
config's `env`. Without that entry the Connect screen's `/status` probe fails at the fetch
layer, which is indistinguishable from "server down", so it shows a CORS hint next to
`unreachable`.

## Tests

- **Playwright (e2e):** the Playwright Test Explorer runs/debugs individual scenarios and
  has a locator picker. CLI: `npm run e2e`, or `npx playwright test --debug` for the
  inspector. The two entry points do **not** behave the same
  (`fallen-8-web-ui/playwright.config.ts`):
  - **Both entry points now isolate themselves.** The config builds the SPA into the API's
    `wwwroot` and launches its **own** apiApp on `:5099` (override with `F8_E2E_PORT`) with
    `Fallen8__Durability__Volatile=true` and the API key `e2e-key`, so nothing needs to be running
    first. `reuseExistingServer` is `false`, so playwright can never adopt a server it did not
    configure: a port clash is a hard, immediate failure instead of a silently wiped graph. The
    CLI (`npm run e2e`) and the VS Code Test Explorer behave identically.
  - The two functional specs (`e2e/studio.spec.ts`, `e2e/first-run.spec.ts`) **erase the `default`
    namespace** as part of their scenarios, which is safe precisely because the target is always
    the volatile e2e instance.
  - The one way out is explicit and hand-typed: setting `F8_UI_URL` points the run at an
    already-running instance and launches nothing (this is how the `F8_SCREENSHOT=1` specs capture
    against a purpose-built app). Whoever sets it owns that target's durability, so point it at a
    throwaway.
- **Recapturing the docs screenshots:** the `e2e/screenshot-*.spec.ts` specs are skipped unless
  `F8_SCREENSHOT=1`, so a plain `npm run e2e` runs only the two functional specs. They write
  straight into `docs/src/assets/images/` and target an already-running instance, e.g.
  `F8_SCREENSHOT=1 F8_UI_URL=http://127.0.0.1:5000 npx playwright test e2e/screenshot-dashboard.spec.ts`
  from `fallen-8-web-ui/`. They authenticate with `e2e-key` unless `F8_E2E_API_KEY` overrides
  it, and half of them start with `HEAD /tabularasa/all`, which erases **every** namespace, so
  the same throwaway-instance rule applies.
- **UI unit/component:** `npm run test:ui` (Vitest).
- **Backend (MSTest):** the .NET Test Explorer debugs a single method; CLI is
  `dotnet test fallen-8-core.sln`.

### WSL/Linux note for backend tests

- The repository configures backend tests with `fallen-8-unittest/test.runsettings`,
  which sets `DOTNET_hostBuilder__reloadConfigOnChange=false` so full-suite API tests
  do not exhaust inotify instances when many `WebApplicationFactory` hosts spin up.
- The test assembly also sets the same guard in `fallen-8-unittest/TestEnvironmentBootstrap.cs`
  at `[AssemblyInitialize]`, because some Test Explorer runners do not always honor
  project-level runsettings.
- If your machine still reports inotify-limit errors, raise Linux limits once:
  `sudo sysctl -w fs.inotify.max_user_instances=1024`
  and `sudo sysctl -w fs.inotify.max_user_watches=1048576`.

### WSL/Linux note for Playwright e2e

The e2e suite lives in `fallen-8-web-ui/` (see `## Tests` above); WSL only needs two
one-time setup steps beyond `npm ci`:

- Install the browser **and its Linux system libraries**:
  `cd fallen-8-web-ui && npx playwright install --with-deps chromium`. The
  `--with-deps` flag pulls the apt packages headless Chromium needs, which a fresh
  WSL/Ubuntu image lacks.
- Then run it from the repo root with `npm run e2e` (delegates to `fallen-8-web-ui`)
  or from `fallen-8-web-ui/` with `npx playwright test`. The config launches its own volatile
  apiApp on `:5099`, so nothing needs to be running first and no local graph is at risk (see
  `## Tests` above).
- The inotify note above applies here too: the apiApp the suite launches watches its
  config files. Raise the limits if a run fails to start under load.

## Debugging inside a container (only when needed)

Reserve this for a bug that reproduces **only** in Docker (e.g. a volume/path issue).
Attach the .NET debugger (`vsdbg`) to the running `fallen8` container with a `docker`
attach configuration. For everything else, local debugging is faster.

## Gotchas

- **Port clash:** local debugging binds `:5000`; the compose `fallen8` publishes `${F8_PORT}`
  (default 8080). They don't collide by default, but stop the compose environment
  (`npm run env:down`) if you mapped it onto 5000, and don't run two things on 5000.
- **Auth while debugging:** by default the local API runs with no key, so everything is
  open (register the Studio instance with an empty base URL and no key). To debug the
  secured path, set `Fallen8__Security__ApiKey` in the launch config's `env` and register the
  instance with that key. Dynamic code execution (the delegate editor, inline fragments) is
  always on and needs no extra setting.
- **Auth-open is not capability-open:** the launch configs pass no capability environment, so a
  local debug run answers 403 on `/embedding/*`, `POST /chat` and `/document/*` whatever the key
  situation is. Those flags are absent from `appsettings.json` (off) and the gate is independent
  of authentication, so Studio's Knowledge screen, the embeddings tab and NL assist are all dead
  until you switch them on in the launch config's `env`
  ([Configuration keys](/running/#configuration-keys) has the defaults):

  ```json
  "env": {
    "ASPNETCORE_ENVIRONMENT": "Development",
    "ASPNETCORE_URLS": "http://localhost:5000",
    "Fallen8__Chat__Enabled": "true",
    "Fallen8__Embedding__Enabled": "true",
    "Fallen8__Embedding__Backend": "Ollama",
    "Fallen8__Embedding__ModelName": "bge-m3",
    "Fallen8__Embedding__Dimension": "1024",
    "Fallen8__Ingestion__Enabled": "true",
    "Fallen8__Ingestion__Docling__Endpoint": "http://localhost:5001",
    "Fallen8__Nlp__Enabled": "true",
    "Fallen8__Nlp__Endpoint": "http://localhost:8100"
  }
  ```

  Those endpoints are the compose sidecars' published host ports, so you can run the API
  locally against a running `npm run env:up` environment. The chat and embedding Ollama
  endpoints already default to `http://localhost:11434` and need no entry. The embedding
  identity is one unit: the declared `Dimension` is validated against the backend's actual
  output and a mismatch is a hard error, and the default `Onnx` backend wants local model files
  instead of a sidecar. `GET /status` reports the resulting capability state.
- **NL assist in dev:** by default NL assist routes through the instance (browser to
  `POST /chat` to the server's model backend), so what blocks it locally is the chat capability
  above plus `/chat` being on the Vite proxy's prefix list. The browser only calls a model
  directly in the opt-in **custom** mode, and that is the mode where the model server has to
  allow the caller's origin: run Ollama with `OLLAMA_ORIGINS=http://localhost:5173` for the dev
  server. `npm run env:up` sets it to
  `http://localhost:${F8_PORT},http://localhost:${F8_UI_PORT}` (8080 and 8081), neither of
  which is the Vite origin.

## Ports at a glance

| What | Port | Notes |
|---|---|---|
| Local API (debug) | 5000 | `Development`; Scalar at `/scalar/v0.1` |
| e2e apiApp (Playwright) | 5099 (`F8_E2E_PORT`) | Launched by the suite itself, volatile durability, API key `e2e-key`; never reuses an existing listener |
| Vite dev server | 5173 | proxies the `API_PREFIXES` list in `vite.config.ts` to 5000 (`F8_API_URL` overrides the target) |
| Compose F8 Studio UI | `${F8_UI_PORT}` (8081) | `env:up` runs it as its own container (all-in-one bakes it into `:8080` via a bare `docker compose up`) |
| Compose REST API | `${F8_PORT}` (8080) | `env:up` data plane; the UI origin is in its `AllowedCorsOrigins` |
| Ollama (NL assist) | 11434 | `OLLAMA_ORIGINS` must allow the calling origin, which matters only in custom mode |
| Compose docling sidecar | `${F8_DOCLING_PORT}` (5001) | converts binary formats for ingestion |
| Compose NLP sidecar | `${F8_NLP_PORT}` (8100) | entity and key-term enrichment |
| Compose MCP server | `${F8_MCP_PORT}` (8090) | separate deployable, no launch config; defaults to targeting `:8080` |
