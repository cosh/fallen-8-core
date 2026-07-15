# Debugging Fallen-8 in VS Code

> Keep this in sync with `.vscode/fallen-8-core.code-workspace` (launch configs + tasks),
> `fallen-8-web-ui/vite.config.ts` (dev port + proxy), `docker-compose.yml` (ports,
> `OLLAMA_ORIGINS`), and `fallen-8-core-apiApp/Properties/launchSettings.json`. If any of
> those change, update this file in the same commit.

## Principle: debug locally, not inside the containers

The `docker compose` environment is for running the whole thing together (integration,
"does it work end to end"). Its containers are Release builds with no debugger attached.
For breakpoints, run the pieces **locally** and let VS Code attach its debuggers. The
engine (`fallen-8-core`) runs in-process with the API, so one backend debugger covers both.

Prerequisites (all in the workspace's recommended extensions):
- `ms-dotnettools.csharp` — the C# / `coreclr` debugger
- `ms-playwright.playwright` — Playwright Test Explorer (e2e)
- `formulahendry.dotnet-test-explorer` — MSTest run/debug
- .NET 10 SDK and Node 22 on PATH.

Open the workspace file `.vscode/fallen-8-core.code-workspace` (not just the folder) so the
launch configs and tasks are available.

## Backend — engine, API, save games, delegate validation (C#)

**F5 → "F8 API App (Debug)".** Builds (`build-api`) and launches `fallen-8-core-apiApp`
under the `coreclr` debugger on `http://localhost:5000` in the `Development` environment,
and opens the Scalar API reference. Breakpoints anywhere in `fallen-8-core-apiApp/` **and**
`fallen-8-core/` are hit (same process).

Useful breakpoint locations:
- `Controllers/SaveGamesController.cs`, `Services/SaveGameRegistry.cs`,
  `Services/DurabilityLifecycleService.cs` — save-game registry + registry-driven startup
- `Helper/DelegateValidationHelper.cs`, `Helper/CodeGenerationHelper.cs` — the Roslyn
  compile / validation path
- `Program.cs` authorization block — to watch the auth decisions (key set vs not, capability flag)
- anything in `fallen-8-core/` — engine, transactions, indices, persistence.

To run against a clean rebuild, use **"F8 API App (Clean Build)"**.

## Frontend — F8 Studio (React / TypeScript)

Start the Vite dev server: run the **`ui-dev`** task (or `npm run dev` in `fallen-8-web-ui/`).
It serves the SPA on `http://localhost:5173` and proxies all API routes to
`http://localhost:5000` — i.e. straight into the backend you're debugging above. Vite emits
source maps, so you debug real `.tsx`:
- Browser devtools against `http://localhost:5173`, or
- a VS Code JS-debug browser session for in-editor breakpoints.

## Full stack at once

Run both: the backend under **"F8 API App (Debug)"** (:5000) and the **`ui-dev`** task
(:5173). You then have breakpoints on both sides with UI hot-reload. (There is no single
compound launch yet; start the two above. If you want a one-keypress compound + a browser
launch config, they can be added to the workspace file.)

Alternatively, to debug the SPA exactly as it is served in production (same origin, no Vite
proxy), use **"F8 Studio (API + built UI)"** — it builds the SPA into the API's `wwwroot`
and serves it from `:5000`.

## Tests

- **Playwright (e2e):** the Playwright Test Explorer runs/debugs individual scenarios and
  has a locator picker. CLI: `npm run e2e`, or `npx playwright test --debug` for the
  inspector. The suite launches its own apiApp (see `fallen-8-web-ui/playwright.config.ts`).
- **UI unit/component:** `npm run test:ui` (Vitest).
- **Backend (MSTest):** the .NET Test Explorer debugs a single method; CLI is
  `dotnet test fallen-8-core.sln`.

## Debugging inside a container (only when needed)

Reserve this for a bug that reproduces **only** in Docker (e.g. a volume/path issue).
Attach the .NET debugger (`vsdbg`) to the running `fallen8` container with a `docker`
attach configuration. For everything else, local debugging is faster.

## Gotchas

- **Port clash:** local debugging binds `:5000`; the compose `fallen8` publishes `${F8_PORT}`
  (default 8080). They don't collide by default, but stop the compose environment
  (`npm run env:down`) if you mapped it onto 5000, and don't run two things on 5000.
- **NL-assist CORS in dev:** the compose Ollama allows the compose origin
  (`http://localhost:${F8_PORT}`), not the Vite dev origin `http://localhost:5173`, so a
  browser→model call from the dev server is CORS-blocked. To debug NL assist locally, run
  Ollama with `OLLAMA_ORIGINS=http://localhost:5173`, or debug against the served SPA
  (`"F8 Studio (API + built UI)"` on :5000, which matches the compose origin if `F8_PORT=5000`).
- **Auth while debugging:** by default the local API runs with no key, so everything is
  open (register the Studio instance with an empty base URL and no key). To debug the
  secured path, set `Fallen8__Security__ApiKey` (and `Fallen8__Security__EnableDynamicCodeExecution=true`
  for the delegate editor) in the launch config's `env` and register the instance with that key.

## Ports at a glance

| What | Port | Notes |
|---|---|---|
| Local API (debug) | 5000 | `Development`; Scalar at `/scalar/v0.1` |
| Vite dev server | 5173 | proxies API routes to 5000 |
| Compose F8 Studio | `${F8_PORT}` (8080) | one-unit environment |
| Ollama (NL assist) | 11434 | `OLLAMA_ORIGINS` must allow the calling origin |
