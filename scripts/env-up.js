// Starts the F8 environment (docker compose up). Two jobs:
//  1. GPU: add docker-compose.gpu.yml when the host has an NVIDIA GPU so Ollama runs
//     accelerated (why a separate file: see its header). Detection is "nvidia-smi works";
//     F8_GPU=1 / F8_GPU=0 forces it either way.
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
      ? 'NVIDIA GPU detected - applying docker-compose.gpu.yml (Ollama uses the GPU).'
      : 'No NVIDIA GPU detected - starting CPU-only (F8_GPU=1 forces the GPU override).'
  );
  console.log(
    'On first start the Ollama container pulls phi4-mini + f8-delegate (a few GB); the F8\n' +
      'API is up immediately, and NL assist works once the pull finishes. Watch it with\n' +
      '`npm run env:logs`. To pre-seed the models (offline/faster first start): scripts/ensure-models.sh\n'
  );

  const files = ['-f', 'docker-compose.yml'];
  if (gpu) files.push('-f', 'docker-compose.gpu.yml');

  const result = spawnSync('docker', ['compose', ...files, 'up', '-d', '--build'], {
    cwd: path.join(__dirname, '..'),
    stdio: 'inherit',
  });
  process.exit(result.status === null ? 1 : result.status);
}

main();
