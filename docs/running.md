# Running Fallen-8

One command brings up the whole thing — engine, REST API, [F8 Studio](studio.md), and the
model sidecar that powers [semantic traversal](semantic-traversal.md) and Studio's
natural-language assist — with every feature on and no authentication in the way. That is
the default and it just works. This doc then covers every other way to run it and every
knob you can turn.

## The one command

```bash
npm run env:up
```

```powershell
npm run env:up
```

This runs `docker compose up` for the full environment and prints where everything is:

| Service | URL | What it is |
|---|---|---|
| F8 Studio + REST API | http://localhost:8080 | The engine, the REST surface, and the browser UI, in one container |
| Model sidecar (Ollama) | http://localhost:11434 | Serves the embedding model and the delegate-assist models |

Stop and inspect it as one unit — never start/stop individual containers:

```bash
npm run env:down     # stop everything; the data + model volumes persist
npm run env:logs     # follow all logs
npm run env:status   # health of the whole environment
```

The environment is **open** (no API key) and has **text-in embeddings on** (semantic search
and GraphRAG work out of the box). It does **not** enable dynamic C# code execution — that
is a separate switch, off by default (see [Security](security.md) and the table below).

### First start pulls models

On the first `env:up` the sidecar pulls its models into the `f8-ollama-models` volume:
`bge-m3` (embeddings), `phi4-mini` (base), and `phi4-f8-mini` (the delegate-assist
fine-tune, Studio's default). That is a few GB, so it takes a few minutes — the API and
Studio are up immediately, and assist starts answering once the pull finishes. Later starts
reuse the cached volume and need no network. Watch progress with `npm run env:logs`.

Offline or want to skip the wait? Pre-seed the volume once from a machine with internet —
this needs only Docker, no host Ollama:

```bash
bash scripts/ensure-models.sh        # pulls into the f8-ollama-models volume
```

If a pull fails the sidecar still comes up with whatever is present (it does not
crash-loop); fix the network and re-run. The 404-in-Studio case is in
[Troubleshooting](troubleshooting.md).

## Environment variables (compose)

Set these before `npm run env:up`. In PowerShell use `$env:NAME = "value"; …` (it stays set
for the session; `Remove-Item Env:NAME` clears it) and chain with `;`, not `&&`.

| Variable | Default | Effect |
|---|---|---|
| `F8_PORT` | `8080` | Host port for Studio + API (container always listens on 8080) |
| `F8_API_KEY` | *(unset)* | Set it and the **entire** service requires the key; unset = open ([Security](security.md)) |
| `F8_ENABLE_DYNAMIC_CODE` | `false` | Turn on to allow compiling inline C# [delegates](delegates.md) and Studio's editor validation |
| `F8_EMBEDDINGS` | `true` | Text-in embeddings via the sidecar's `bge-m3`; `false` disables the provider (bring-your-own-vector still works) |
| `F8_GPU` | *(auto)* | `1` forces the NVIDIA GPU, `0` forces CPU-only; unset auto-detects |
| `F8_PULL_PHI4F8` | `0` | `1` also pulls the larger GPU-only `phi4-f8` assist model (~9 GB) |
| `F8_DELEGATE_REPO` | `stoic_hellman_728/phi4-f8-mini` | Source for the default assist fine-tune (tagged locally as `phi4-f8-mini`) |

Examples:

```bash
F8_API_KEY=change-me F8_ENABLE_DYNAMIC_CODE=true npm run env:up   # secured + editor on
F8_PORT=9090 npm run env:up                                       # Studio on :9090
```

```powershell
$env:F8_API_KEY = "change-me"; $env:F8_ENABLE_DYNAMIC_CODE = "true"; npm run env:up
$env:F8_PORT = "9090"; npm run env:up
```

## Running the engine + API without Docker

A bare run needs only the .NET 10 SDK. It starts in the **Development** environment, which is
the only environment that serves the [OpenAPI document and Scalar reference](rest-api.md):

```bash
dotnet run --project fallen-8-core-apiApp
# → http://localhost:5000   (OpenAPI + Scalar reference are served here in Development)
```

```powershell
dotnet run --project fallen-8-core-apiApp
```

This is engine + REST only. There is no model sidecar, so the embedding provider is off and
Studio's NL assist has no local backend — bring-your-own-vector, analytics, paths, and every
non-embedding feature work regardless.

### With F8 Studio built in

Studio is a React SPA served by the API app from its `wwwroot`. Build it in, then run:

```bash
npm run install:ui        # once
npm run build:apiapp      # build the SPA into the apiApp's wwwroot
dotnet run --project fallen-8-core-apiApp
# open http://localhost:5000
```

To develop the UI against a running API with hot reload, use `npm run dev` (Vite dev server)
instead of `build:apiapp`. Debugging the whole stack in VS Code is covered in
[DEBUGGING.md](../DEBUGGING.md).

## Configuration keys

The compose variables above map onto the app's configuration, which you can also set
directly (via `appsettings.json`, environment variables with the `Fallen8__Section__Key`
form, or the command line). The defaults:

| Key | Default | Owner doc |
|---|---|---|
| `Fallen8:Durability:StorageDirectory` | app base dir (`/data` in the container) | [Save games](save-games.md) |
| `Fallen8:Durability:Volatile` | `false` | [Save games](save-games.md) |
| `Fallen8:Durability:SaveOnShutdown` | `true` | [Save games](save-games.md) |
| `Fallen8:Metadata:Directory` | storage dir | [Save games](save-games.md) |
| `Fallen8:Namespaces:MaxNamespaces` | `10000` | [Namespaces](namespaces.md) |
| `Fallen8:Security:ApiKey` | `null` (open) | [Security](security.md) |
| `Fallen8:Security:EnableDynamicCodeExecution` | `false` | [Security](security.md) |
| `Fallen8:Embedding:Enabled` | `false` (bare run) | [Semantic traversal](semantic-traversal.md) |
| `Fallen8:ChangeFeed:Enabled` | `true` | [Change feed](change-feed.md) |
| `Fallen8:Analytics:DefaultTimeBudgetSeconds` | `30` | [Graph analytics](graph-analytics.md) |
| `Fallen8:Observability:Prometheus:Enabled` | `false` | [Observability](observability.md) |

`Volatile=true` disables all disk writes (no WAL, no checkpoints) — the fastest way to run a
throwaway instance for a demo or a test.

## GPU acceleration

`npm run env:up` auto-detects an NVIDIA GPU (`nvidia-smi` present) and gives the sidecar the
GPU; otherwise it runs CPU-only. Force it with `F8_GPU`:

```bash
F8_GPU=0 npm run env:up   # CPU-only even if a GPU is present
F8_GPU=1 npm run env:up   # require the GPU (fails to create the container if unavailable)
```

The GPU reaches the container through the NVIDIA Container Toolkit, installed where the
Docker engine runs. On **Docker Desktop (WSL2 backend)** install the current NVIDIA driver on
Windows and confirm `nvidia-smi` works inside WSL — the backend then exposes the GPU
automatically. For **Docker running natively in a Linux distro** install
`nvidia-container-toolkit` and `sudo nvidia-ctk runtime configure --runtime=docker`. Verify
the GPU reaches a container before `env:up`:

```bash
docker run --rm --gpus all --entrypoint nvidia-smi ollama/ollama:latest   # should list your GPU
```

If that fails the GPU is not exposed to Docker; the stack still runs CPU-only with `F8_GPU=0`.
AMD GPUs need the `ollama/ollama:rocm` image and are not covered by this compose.

## See also

- [Architecture](architecture.md) — how the container, engine, and sidecar fit together
- [Security](security.md) — the API key and dynamic-code switch you set at launch
- [Studio](studio.md) — the browser UI this serves
- [Save games](save-games.md) — where durable data lives and how startup loads it
- [REST API](rest-api.md) — OpenAPI/Scalar (Development only) and the endpoint map
- [Troubleshooting](troubleshooting.md) — first-start model pulls, GPU, and other snags
