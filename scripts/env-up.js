// MIT License
//
// env-up.js
//
// Copyright (c) 2011-2026 Henning Rauch
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

// Starts the F8 environment (docker compose up). Three jobs:
//  1. GPU: when the host has an NVIDIA GPU, add docker-compose.gpu.yml (Ollama on the
//     device; runtime-only, so it applies in BOTH modes) and, when building locally, also
//     docker-compose.gpu-nlp.yml (the NLP transformer tier, a build-time variant - why the
//     two files are separate: see their headers). Detection is "nvidia-smi works";
//     F8_GPU=1 / F8_GPU=0 forces it either way.
//  2. Model provider: F8_MODEL_PROVIDER picks where the two model capabilities send their
//     requests and applies that overlay. https://docs.fallen-8.com/model-providers/
//  3. Nothing else. The Ollama container pulls the models itself on first start (see
//     scripts/ollama-init.sh) - this script does NOT gate startup on a host Ollama or a
//     pre-populated volume. To pre-seed the volume for an offline/faster first start, run
//     scripts/ensure-models.sh once (optional); it is never required.

const path = require('path');
const { execSync, spawnSync } = require('child_process');
const { applyDotEnv } = require('./env-file');

function hostHasNvidiaGpu() {
  if (process.env.F8_GPU === '0') return false;
  if (process.env.F8_GPU === '1') return true;
  try {
    execSync('nvidia-smi -L', { stdio: 'ignore' });
    return true;
  } catch {
    return false;
  }
}

function main() {
  // Every variable this script reads can sit in the root .env file instead of the shell -
  // the same file compose interpolates, same precedence (the shell wins). See env-file.js
  // for why the script has to read it too.
  applyDotEnv();
  // --published (npm run env:up:published): run purely from the published GHCR images -
  // no local builds, nothing needed beyond the compose files. F8_IMAGE_TAG pins a version
  // (default latest). GPU detection works exactly like the building mode; only the NLP
  // transformer tier stays out (a build-time variant, deliberately not published).
  const published = process.argv.includes('--published');
  const gpu = hostHasNvidiaGpu();
  // Which provider the two model capabilities talk to. Decided before the banners below, because
  // with the models off-box there is less sidecar to describe than the paragraphs assume.
  //
  //   local      the Ollama sidecar on this machine
  //   nahil      nahil.dev serves both capabilities: the same bge-m3, so no vector re-embeds
  //   openai     chat on OpenAI, embeddings stay on the sidecar
  //   anthropic  chat on Anthropic, embeddings stay on the sidecar
  //
  // An unknown value is refused rather than resolved to the default: a typo'd provider must not
  // quietly start a sidecar the operator believes is off-box.
  const providers = ['local', 'nahil', 'openai', 'anthropic'];
  const selected = (process.env.F8_MODEL_PROVIDER || '').trim().toLowerCase();
  if (selected !== '' && !providers.includes(selected)) {
    console.error(
      `F8_MODEL_PROVIDER='${(process.env.F8_MODEL_PROVIDER || '').trim()}' is not a model provider.` +
        ` Expected one of: ${providers.join(', ')}.`
    );
    process.exit(1);
  }
  // Nahil's original selector stays: either variable alone selects it when nothing else does.
  // F8_NAHIL_URL counts because it used to BE the selector, back when the overlay had no default
  // endpoint; it does now (https://api.nahil.dev), so a deployment that only supplies the
  // credential - the normal case - would otherwise have set the one thing Nahil needs and quietly
  // got the local sidecar.
  const nahilVars =
    (process.env.F8_NAHIL_URL || '').trim() !== '' ||
    (process.env.F8_NAHIL_API_KEY || '').trim() !== '';
  const provider = selected !== '' ? selected : nahilVars ? 'nahil' : 'local';
  // Kept under its own name because everything downstream asks the one question it answers: is
  // there a local sidecar serving BOTH capabilities or not.
  const nahil = provider === 'nahil';
  // An explicit selector wins over leftover credentials, and says so - silently ignoring a key
  // that is still in someone's .env is how a deployment ends up somewhere nobody chose.
  if (selected !== '' && !nahil && nahilVars) {
    console.log(
      `F8_MODEL_PROVIDER=${selected} wins over the F8_NAHIL_* variables that are also set, so Nahil\n` +
        'is NOT selected. Unset F8_MODEL_PROVIDER to select it by credential again.\n'
    );
  }
  if (published) {
    console.log(
      'Published-image mode: pulling ghcr.io/cosh/fallen-8-core* (' +
        `tag ${process.env.F8_IMAGE_TAG || 'latest'}) instead of building.`
    );
  }
  if (nahil) {
    console.log(
      `Nahil is ON (${(process.env.F8_NAHIL_URL || '').trim() || 'https://api.nahil.dev'}) - the models run\n` +
        'there, the local Ollama sidecar is NOT started, and nothing is pulled onto this machine.\n' +
        'Needs F8_NAHIL_API_KEY. Docs: https://docs.fallen-8.com/nahil/\n'
    );
  } else if (provider === 'openai' || provider === 'anthropic') {
    const label = provider === 'openai' ? 'OpenAI' : 'Anthropic';
    const keyVar = provider === 'openai' ? 'F8_OPENAI_API_KEY' : 'F8_ANTHROPIC_API_KEY';
    console.log(
      `Chat runs on ${label} - POST /chat leaves this machine, and needs ${keyVar}.\n` +
        'Embeddings deliberately do NOT move with it: the local sidecar keeps serving bge-m3, so\n' +
        'every vector you have stored keeps its identity. Docs: https://docs.fallen-8.com/model-providers/\n'
    );
  }
  console.log(
    gpu
      ? 'NVIDIA GPU detected - applying docker-compose.gpu.yml (' +
        (nahil ? 'the NLP sidecar on the GPU' : 'Ollama on the GPU') +
        (published
          ? '; the NLP sidecar\nstays on the published CPU image - its transformer tier needs a local build, npm run env:up).'
          : ') and\ndocker-compose.gpu-nlp.yml (the NLP sidecar on the en_core_web_trf transformer).')
      : 'No NVIDIA GPU detected - starting CPU-only, the NLP sidecar on en_core_web_lg\n' +
        '(F8_GPU=1 forces the GPU override).'
  );
  if (provider === 'local') {
    console.log(
      'On first start the Ollama container pulls phi4-mini + phi4-f8-mini (a few GB); the F8\n' +
        'API is up immediately, and NL assist works once the pull finishes. Watch it with\n' +
        '`npm run env:logs`. To pre-seed the models (offline/faster first start): scripts/ensure-models.sh\n'
    );
  } else if (!nahil) {
    console.log(
      'On first start the sidecar pulls bge-m3 for embeddings (~1.2 GB) and skips the two mini\n' +
        'assist models, since chat runs off this machine. The larger phi4-f8 still pulls unless you\n' +
        'set F8_PULL_PHI4F8=0. Watch it with `npm run env:logs`.\n'
    );
  }

  const files = ['-f', 'docker-compose.yml'];
  // The fleet observability stack (feature fleet-observability) always comes up with the
  // environment, so a first-time user sees metrics/traces/logs in Grafana immediately. It is a
  // separate file for readability, included unconditionally here (unlike the GPU override, which
  // is conditional because a device reservation hard-fails on hosts without an NVIDIA GPU).
  files.push('-f', 'docker-compose.observability.yml');
  if (gpu) {
    files.push('-f', 'docker-compose.gpu.yml');
    // The transformer tier only exists as a local build, so it never joins a published run.
    if (!published) files.push('-f', 'docker-compose.gpu-nlp.yml');
  }
  // Split topology is the default dev environment (feature standalone-ui): the data plane serves
  // REST only and F8 Studio runs as its own f8-studio container. Applied LAST so its overrides
  // (UI-less build, CORS allow-list, Ollama origins, the f8-studio service) win. The all-in-one
  // stays available via a bare `docker compose up` (no overlay).
  files.push('-f', 'docker-compose.split.yml');

  // The model-provider overlay (features nahil-backend, model-providers), applied after split.yml
  // so its Fallen8__Chat/Embedding overrides win. Announced above, where the sidecar banners it
  // contradicts are. `local` has no overlay: the base file already wires both capabilities to the
  // sidecar.
  //
  // None of these three belong in package.json's env:down / env:logs / env:status, and that is
  // load-bearing rather than an omission: an overlay joins those commands only when it DEFINES a
  // service (observability.yml and split.yml do). Adding one would also break them, because a
  // ${VAR:?} is evaluated at config-parse time for EVERY compose subcommand, so `npm run env:down`
  // would refuse to run from any shell that does not carry the provider's key.
  const overlay = {
    nahil: 'docker-compose.nahil.yml',
    openai: 'docker-compose.openai.yml',
    anthropic: 'docker-compose.anthropic.yml',
  }[provider];
  if (overlay) files.push('-f', overlay);

  // Unstructured ingestion (feature unstructured-ingestion): the docling-serve sidecar rides
  // the "ingestion" profile, default ON like the rest of the environment. F8_INGESTION=false
  // skips the ~4.4 GB image AND turns the capability off (the fallen8 service reads the same
  // variable). env:down/logs/status always pass the profile, so a running sidecar is covered.
  const ingestion = process.env.F8_INGESTION !== 'false';
  const profiles = ingestion ? ['--profile', 'ingestion'] : [];
  console.log(
    ingestion
      ? 'Ingestion is ON - the docling sidecar (document conversion, ~4.4 GB image) comes up.'
      : 'F8_INGESTION=false - no docling sidecar; txt/md ingestion stays off too (capability disabled).'
  );

  // NLP enrichment (feature semantic-layer): the nlp sidecar (entities + key terms) rides its
  // own "nlp" profile, started only when ingestion is on AND F8_NLP != false. So F8_NLP=false is
  // a true opt-out (no sidecar built, capability off via the fallen8 service's Fallen8__Nlp__Enabled).
  const nlp = ingestion && process.env.F8_NLP !== 'false';
  if (nlp) profiles.push('--profile', 'nlp');
  console.log(
    nlp
      ? 'NLP enrichment is ON - the spaCy sidecar (English entities + key terms) comes up.'
      : ingestion
        ? 'F8_NLP=false - no NLP sidecar; ingestion still writes Document/Chunk vertices (no entity graph).'
        : 'NLP enrichment is off (ingestion is off).'
  );

  // Integration jobs (feature integrations): the f8-integrations sidecar rides its own
  // "integrations" profile, default ON like the rest of the environment. Standalone rather than
  // nested under ingestion (unlike nlp): an integration reads a live system on the user's own
  // network, which has nothing to do with document conversion. F8_INTEGRATIONS=false is a true
  // opt-out - no sidecar, and the apiApp's /integrations routes answer 403 (the fallen8
  // service reads the same variable).
  const integrations = process.env.F8_INTEGRATIONS !== 'false';
  if (integrations) profiles.push('--profile', 'integrations');
  console.log(
    integrations
      ? 'Integrations are ON - the f8-integrations sidecar comes up. It is the one service with NO\n' +
        'host port (jobs hand it third-party credentials), so the API proxy is the only way in.'
      : 'F8_INTEGRATIONS=false - no integrations sidecar; the /integrations routes answer 403.'
  );
  // Both lists are configuration-only (never a job setting) and both default to empty, so the two
  // states worth knowing about at startup are stated here rather than discovered on a failed run.
  if (integrations && !process.env.F8_INTEGRATIONS_ALLOWED_HOSTS) {
    console.log('! F8_INTEGRATIONS_ALLOWED_HOSTS is unset, so a run HOLDING a credential may contact any host.');
    console.log('  Name the sources you trust with one, comma separated, e.g.');
    console.log('    F8_INTEGRATIONS_ALLOWED_HOSTS=console.lan,inverter.lan');
  }
  if (integrations && !process.env.F8_INTEGRATIONS_SELF_SIGNED_HOSTS) {
    console.log('! F8_INTEGRATIONS_SELF_SIGNED_HOSTS is unset, so a source serving a self-signed certificate');
    console.log('  (a UniFi console or a Fronius inverter on a private address no authority will sign)');
    console.log('  cannot be reached until it is named there. It is the ONE place trust is reduced, so it');
    console.log('  is deliberately per-host and never a job setting.');
  }

  console.log(
    'F8 Studio runs as its own container (feature standalone-ui): UI on ' +
      `http://localhost:${process.env.F8_UI_PORT || '8081'}, REST API on ` +
      `http://localhost:${process.env.F8_PORT || '8080'}.`
  );

  // The AI-agent MCP surface (feature mcp-server) starts with the rest of the environment on
  // http://localhost:8090 — anonymous + read-only for local dev. Securing it for an off-box
  // setup is env-var config on the f8-mcp service (F8_MCP_AUTH_MODE / F8_MCP_TOKEN / tier
  // flags); see docs/mcp-server.md.
  // --remove-orphans: if a previous run used a different set of compose files, drop any
  // container no longer in this configuration, so a recreated f8-net never strands a stale
  // container on an old network id ("network ... not found").
  // Published mode swaps --build for --no-build: compose pulls any image it does not have
  // locally from GHCR (refresh an already-pulled tag with `docker compose pull`).
  const buildFlag = published ? '--no-build' : '--build';
  const result = spawnSync('docker', ['compose', ...files, ...profiles, 'up', '-d', buildFlag, '--remove-orphans'], {
    cwd: path.join(__dirname, '..'),
    stdio: 'inherit',
  });
  process.exit(result.status === null ? 1 : result.status);
}

main();
