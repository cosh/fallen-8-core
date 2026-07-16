// Starts the F8 environment (docker compose up), adding docker-compose.gpu.yml when
// the host has an NVIDIA GPU so Ollama runs accelerated (why a separate file: see its
// header). Detection is "nvidia-smi works"; F8_GPU=1 / F8_GPU=0 forces it either way.

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

const gpu = hostHasNvidiaGpu();
console.log(
  gpu
    ? 'NVIDIA GPU detected - applying docker-compose.gpu.yml (Ollama uses the GPU).'
    : 'No NVIDIA GPU detected - starting CPU-only (F8_GPU=1 forces the GPU override).'
);

const files = ['-f', 'docker-compose.yml'];
if (gpu) files.push('-f', 'docker-compose.gpu.yml');

const result = spawnSync('docker', ['compose', ...files, 'up', '-d', '--build'], {
  cwd: path.join(__dirname, '..'),
  stdio: 'inherit',
});
process.exit(result.status === null ? 1 : result.status);
