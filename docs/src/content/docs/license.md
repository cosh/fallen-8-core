---
title: License
description: Fallen-8 is released under the MIT License.
---

Fallen-8 is released under the MIT License. The canonical copy is
[LICENSE](https://github.com/cosh/fallen-8-core/blob/main/LICENSE) in the repository root, and the
text below reproduces it.

Copyright (c) 2011-2026 Henning Rauch

Permission is hereby granted, free of charge, to any person obtaining a copy of this software
and associated documentation files (the "Software"), to deal in the Software without
restriction, including without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## What MIT covers

All of Fallen-8's own code: the engine, the REST API, the MCP server, the NLP sidecar, F8 Studio,
and this documentation site. Source files carry the header verbatim, and no foreign source is
vendored into the tree, so the single copyright holder above covers the whole repository.

## Third-party components

MIT does not extend to material Fallen-8 derives from elsewhere, pulls at run time, or runs
alongside itself. Each carries its own upstream licence, and that licence governs your use of it.

| Class | What it is |
| --- | --- |
| Sample datasets | `air-routes` is derived from [OpenFlights](https://github.com/jpatokal/openflights) `airports.dat` and `routes.dat` at a pinned commit; `karate-club` is Zachary's 1977 karate club; `fallen8-deps` is built from GitHub's SBOM of Fallen-8's own dependencies. The derived `.jsonl` files are committed and ship with the app, see the [sample gallery](/fallen-8-core/samples/). |
| Model weights | None are shipped. The model sidecar pulls `bge-m3` (embeddings) and the assist models `phi4-mini`, `phi4-f8-mini` and `phi4-f8` (Phi-4 family) on first start, and the NLP image bakes in a spaCy English model (`en_core_web_lg`, or `en_core_web_trf` on the GPU build) at build time. All are MIT. See [running](/fallen-8-core/running/). |
| Container images | The images build on `mcr.microsoft.com/dotnet/*`, `node:22-alpine`, `nginx:1.27-alpine`, `python:3.12-slim` and `ollama/ollama`, and the stack runs docling-serve plus the observability sidecars (OpenTelemetry Collector, Prometheus, Tempo, Loki, Grafana) as unmodified upstream images. |
| Package dependencies | The built artifacts bundle their NuGet, npm and pip dependencies. Most are MIT, but the set also includes Apache-2.0, MPL-2.0, BSD, ISC and other terms. Per-package licences are in the SBOM, and the `fallen8-deps` sample carries them on each package vertex as a `license` property. |
