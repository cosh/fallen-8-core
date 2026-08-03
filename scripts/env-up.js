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

// Starts the F8 environment (docker compose up). Two jobs:
//  1. GPU: add docker-compose.gpu.yml when the host has an NVIDIA GPU so Ollama runs
//     accelerated AND the NLP sidecar swaps to its transformer model on the device (why a
//     separate file: see its header). Detection is "nvidia-smi works"; F8_GPU=1 / F8_GPU=0
//     forces it either way.
//  2. Nothing else. The Ollama container pulls the models itself on first start (see
//     scripts/ollama-init.sh) - this script does NOT gate startup on a host Ollama or a
//     pre-populated volume. To pre-seed the volume for an offline/faster first start, run
//     scripts/ensure-models.sh once (optional); it is never required.

const path = require('path');
const { execSync, spawnSync } = require('child_process');

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
  const gpu = hostHasNvidiaGpu();
  console.log(
    gpu
      ? 'NVIDIA GPU detected - applying docker-compose.gpu.yml (Ollama on the GPU; the NLP sidecar\n' +
        'uses the en_core_web_trf transformer on the GPU).'
      : 'No NVIDIA GPU detected - starting CPU-only, the NLP sidecar on en_core_web_lg\n' +
        '(F8_GPU=1 forces the GPU override).'
  );
  console.log(
    'On first start the Ollama container pulls phi4-mini + phi4-f8-mini (a few GB); the F8\n' +
      'API is up immediately, and NL assist works once the pull finishes. Watch it with\n' +
      '`npm run env:logs`. To pre-seed the models (offline/faster first start): scripts/ensure-models.sh\n'
  );

  const files = ['-f', 'docker-compose.yml'];
  // The fleet observability stack (feature fleet-observability) always comes up with the
  // environment, so a first-time user sees metrics/traces/logs in Grafana immediately. It is a
  // separate file for readability, included unconditionally here (unlike the GPU override, which
  // is conditional because a device reservation hard-fails on hosts without an NVIDIA GPU).
  files.push('-f', 'docker-compose.observability.yml');
  if (gpu) files.push('-f', 'docker-compose.gpu.yml');

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

  // The AI-agent MCP surface (feature mcp-server) starts with the rest of the environment on
  // http://localhost:8090 — anonymous + read-only for local dev. Securing it for an off-box
  // setup is env-var config on the f8-mcp service (F8_MCP_AUTH_MODE / F8_MCP_TOKEN / tier
  // flags); see docs/mcp-server.md.
  // --remove-orphans: if a previous run used a different set of compose files, drop any
  // container no longer in this configuration, so a recreated f8-net never strands a stale
  // container on an old network id ("network ... not found").
  const result = spawnSync('docker', ['compose', ...files, ...profiles, 'up', '-d', '--build', '--remove-orphans'], {
    cwd: path.join(__dirname, '..'),
    stdio: 'inherit',
  });
  process.exit(result.status === null ? 1 : result.status);
}

main();
