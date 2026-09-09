// MIT License
//
// env-info.js
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

// Prints where the F8 environment's services are reachable. Used by the root
// npm scripts (env:up / env:status) after docker compose runs.
//
// The F8 host port is taken from the RUNNING container when possible (it may
// have been started with a different F8_PORT than the current shell has).
// Otherwise it is resolved the way docker compose resolves it: the process
// environment, then a root .env file, then the default 8080.

const path = require('path');
const { execSync } = require('child_process');
const { parseDotEnv } = require('./env-file');

function portFromRunningContainer() {
  try {
    const out = execSync('docker compose port fallen8 8080', {
      cwd: path.join(__dirname, '..'),
      stdio: ['ignore', 'pipe', 'ignore'],
    }).toString();
    const match = out.match(/:(\d+)\s*$/m);
    return match ? match[1] : undefined;
  } catch {
    return undefined;
  }
}

const dotEnv = parseDotEnv();

const f8Port =
  portFromRunningContainer() || process.env.F8_PORT || dotEnv.F8_PORT || '8080';

const grafanaPort = process.env.F8_GRAFANA_PORT || dotEnv.F8_GRAFANA_PORT || '3000';
const uiPort = process.env.F8_UI_PORT || dotEnv.F8_UI_PORT || '8081';

console.log('');
console.log('Services:');
console.log(`  F8 Studio UI:  http://localhost:${uiPort}  (its own container; talks to the REST API below)`);
// REST only: the OpenAPI document and the Scalar reference are mapped in Development, and the
// container runs Production, so do NOT advertise /openapi or /scalar here - they 404.
console.log(`  F8 REST API:   http://localhost:${f8Port}  (REST only; /openapi + /scalar are Development-only, so run "dotnet run --project fallen-8-core-apiApp" for those)`);
// Provider-aware on purpose: this line used to assert Ollama unconditionally, which was already
// wrong under a provider overlay and is wrong by default now that the shipped chat backend is
// Nahil. It resolves the provider from the same two inputs env-up.js does, normalised the same
// way (trim + lowercase) and read from the process environment THEN the root .env - the latter
// because these are separate processes: env-up applies .env to its own environment and env-info
// cannot see that, so reading only process.env would print "local" on exactly the machines whose
// .env selects Nahil. Every other value on this screen is already read that way.
const selector = (n) => (process.env[n] || dotEnv[n] || '').trim();
const chatProvider =
  selector('F8_MODEL_PROVIDER').toLowerCase() ||
  (selector('F8_NAHIL_API_KEY') || selector('F8_NAHIL_URL') ? 'nahil' : 'local');

// The three shapes differ in what happens to the SIDECAR, which is the thing an operator is
// looking at this screen for. Only the Nahil overlay parks it; openai and anthropic move chat
// alone and keep it serving bge-m3, so its URL still belongs on the screen. An unrecognised value
// is not resolved to a guess: env-up refuses one outright, but env:status runs this script without
// env-up, so it has to be able to say it does not know.
if (chatProvider === 'local') {
  console.log(
    '  NL assist:     http://localhost:11434  (the Ollama sidecar, default model "phi4-f8-mini"; opt-in "phi4-f8")'
  );
} else if (chatProvider === 'nahil') {
  console.log('  NL assist:     served by Nahil  (no local sidecar; docs.fallen-8.com/nahil/)');
} else if (chatProvider === 'openai' || chatProvider === 'anthropic') {
  console.log(
    `  NL assist:     served by ${chatProvider}  (chat only; the sidecar still serves embeddings)`
  );
  console.log('  Embeddings:    http://localhost:11434  (the Ollama sidecar, bge-m3)');
} else {
  console.log(
    `  NL assist:     F8_MODEL_PROVIDER='${chatProvider}' is not a provider; docs.fallen-8.com/model-providers/`
  );
}
// The API-side URL and deliberately no localhost URL for the runtime itself: the f8-integrations
// sidecar publishes no host port (jobs hand it third-party credentials), so the API is the only way in.
console.log(`  Integrations:  http://localhost:${f8Port}/integrations/providers  (through the API; the sidecar has no host port of its own)`);
console.log(`  Observability: http://localhost:${grafanaPort}  (Grafana; fleet + per-tenant dashboards, open on the trusted network)`);
console.log('  OTLP ingest:   localhost:4317 (gRPC) / :4318 (HTTP)  (point external Fallen-8 instances here)');
console.log('');

// A stray F8_API_KEY - in the shell or forgotten in the root .env file - silently secures the
// data plane, which reads as an "unauthorized" instance in Studio. Surface it here (env-info
// runs on env:up AND env:status) so the cause is never a mystery, naming the source since the
// remedy differs. The key stays fully opt-in; env:up itself is unchanged.
if (process.env.F8_API_KEY) {
  console.log('! F8_API_KEY is set in this shell, so the data plane REQUIRES that key.');
  console.log('  For an OPEN demo, run env:up from a shell without it, or clear it:');
  console.log('    PowerShell:  Remove-Item Env:F8_API_KEY');
  console.log('    bash:        unset F8_API_KEY');
  console.log('');
} else if (dotEnv.F8_API_KEY) {
  console.log('! F8_API_KEY is set in the root .env file, so the data plane REQUIRES that key.');
  console.log('  For an OPEN demo, remove that line from .env and run env:up again.');
  console.log('');
}
