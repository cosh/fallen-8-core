// Starts the F8 environment (docker compose up), adding docker-compose.gpu.yml when
// the host has an NVIDIA GPU so Ollama runs accelerated (why a separate file: see its
// header). Detection is "nvidia-smi works"; F8_GPU=1 / F8_GPU=0 forces it either way.

const path = require('path');
const fs = require('fs');
const { execSync, spawnSync } = require('child_process');
const readline = require('readline');

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

function hasOllamaInstalled() {
  try {
    execSync('which ollama', { stdio: 'ignore' });
    return true;
  } catch {
    return false;
  }
}

function checkModelsAreCached() {
  try {
    // Simple check: try to get the volume mountpoint and see if it has ollama directory
    const volumeInfo = JSON.parse(
      execSync('docker volume inspect f8-ollama-models', {
        encoding: 'utf-8',
        stdio: ['pipe', 'pipe', 'ignore'],
      })
    );

    if (!volumeInfo || volumeInfo.length === 0) {
      return false;
    }

    const mountpoint = volumeInfo[0].Mountpoint;
    // Check if the models directory exists and has content
    const hasModelsDir = fs.existsSync(path.join(mountpoint, 'models'));
    return hasModelsDir;
  } catch {
    return false;
  }
}

function promptUserSync(question) {
  const rl = readline.createInterface({
    input: process.stdin,
    output: process.stdout,
  });

  return new Promise((resolve) => {
    rl.question(question, (answer) => {
      rl.close();
      resolve(answer.toLowerCase());
    });
  });
}

async function ensureModelsAreCached() {
  // Allow skipping the check via environment variable (for CI, testing, etc.)
  if (process.env.F8_SKIP_MODEL_CHECK === '1') {
    console.log('(Model check skipped via F8_SKIP_MODEL_CHECK=1)\n');
    return true;
  }

  const modelsCached = checkModelsAreCached();
  
  if (modelsCached) {
    console.log('✓ Models are cached and ready.\n');
    return true;
  }

  console.log('⚠ No cached models found in docker volume f8-ollama-models.');
  console.log('  The F8 environment needs phi4-mini and f8-delegate to work.\n');

  const hasOllama = hasOllamaInstalled();

  if (!hasOllama) {
    console.log('✗ Ollama is not installed on your system.');
    console.log('  Install it from https://ollama.ai and try again.');
    console.log('  Once installed, run: scripts/ensure-models.sh\n');
    return false;
  }

  console.log('To fix this, we need to pre-cache the models. This takes ~20 minutes (one-time).\n');
  const response = await promptUserSync('Run scripts/ensure-models.sh now? (yes/no): ');

  if (response === 'yes' || response === 'y') {
    console.log('\nPre-caching models...\n');
    try {
      execSync('sh scripts/ensure-models.sh', {
        cwd: path.join(__dirname, '..'),
        stdio: 'inherit',
      });
      console.log('\n✓ Models cached successfully!\n');
      return true;
    } catch (err) {
      console.error('\n✗ Failed to cache models');
      return false;
    }
  } else {
    console.log(
      '\nTo cache models manually later, run: scripts/ensure-models.sh\n'
    );
    console.log('Starting environment anyway (may fail if models cannot be pulled)...\n');
    return true; // Proceed anyway, let docker container try to pull
  }
}

async function main() {
  const gpu = hostHasNvidiaGpu();
  console.log(
    gpu
      ? 'NVIDIA GPU detected - applying docker-compose.gpu.yml (Ollama uses the GPU).'
      : 'No NVIDIA GPU detected - starting CPU-only (F8_GPU=1 forces the GPU override).'
  );
  console.log('');

  // Check if models are cached; offer to pre-cache if not
  const canProceed = await ensureModelsAreCached();
  if (!canProceed) {
    process.exit(1);
  }

  const files = ['-f', 'docker-compose.yml'];
  if (gpu) files.push('-f', 'docker-compose.gpu.yml');

  const result = spawnSync('docker', ['compose', ...files, 'up', '-d', '--build'], {
    cwd: path.join(__dirname, '..'),
    stdio: 'inherit',
  });
  process.exit(result.status === null ? 1 : result.status);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
