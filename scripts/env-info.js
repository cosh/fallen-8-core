// Prints where the F8 environment's services are reachable. Used by the root
// npm scripts (env:up / env:status) after docker compose runs.
//
// The F8 host port is taken from the RUNNING container when possible (it may
// have been started with a different F8_PORT than the current shell has).
// Otherwise it is resolved the way docker compose resolves it: the process
// environment, then a root .env file, then the default 8080.

const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

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

function portFromDotEnv() {
  try {
    const content = fs.readFileSync(path.join(__dirname, '..', '.env'), 'utf8');
    const match = content.match(/^\s*F8_PORT\s*=\s*(\S+)/m);
    return match ? match[1] : undefined;
  } catch {
    return undefined;
  }
}

const f8Port =
  portFromRunningContainer() || process.env.F8_PORT || portFromDotEnv() || '8080';

console.log('');
console.log('Services:');
console.log(`  F8 Studio UI:  http://localhost:${f8Port}`);
console.log(`  F8 REST API:   http://localhost:${f8Port}  (OpenAPI: /openapi/v0.1.json, Scalar: /scalar/v0.1)`);
console.log('  NL assist:     http://localhost:11434  (Ollama, model "phi4-mini")');
console.log('');
