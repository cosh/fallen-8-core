#!/bin/sh
# MIT License
#
# Ollama initialization script: starts the daemon, waits for health, pulls models,
# and keeps the container running. Handles network timeouts gracefully with retries.

# Configuration
F8_DELEGATE_REPO="${F8_DELEGATE_REPO:-stoic_hellman_728/f8-delegate}"
HEALTH_CHECK_RETRIES=30
HEALTH_CHECK_INTERVAL=1
PULL_RETRIES=3
PULL_TIMEOUT=900  # 15 minutes per model pull (~7GB models need time)

log_info() {
  echo "[ollama-init] INFO: $*"
}

log_error() {
  echo "[ollama-init] ERROR: $*" >&2
}

wait_for_health() {
  local attempt=0
  log_info "Waiting for Ollama daemon to be healthy..."
  
  while [ $attempt -lt "$HEALTH_CHECK_RETRIES" ]; do
    if timeout 5 ollama list >/dev/null 2>&1; then
      log_info "Ollama daemon is healthy"
      return 0
    fi
    attempt=$((attempt + 1))
    printf "  Attempt %d/%d...\n" "$attempt" "$HEALTH_CHECK_RETRIES"
    sleep "$HEALTH_CHECK_INTERVAL"
  done
  
  log_error "Ollama daemon did not become healthy after ${HEALTH_CHECK_RETRIES} attempts"
  return 1
}

pull_model() {
  local model=$1
  local attempt=0
  
  log_info "Pulling model: $model"
  
  while [ $attempt -lt "$PULL_RETRIES" ]; do
    if timeout "$PULL_TIMEOUT" ollama pull "$model" 2>&1 | tee -a /tmp/ollama-pull.log; then
      # Verify model actually exists in the list
      if ollama list | grep -q "^${model}"; then
        log_info "Successfully pulled: $model"
        return 0
      else
        log_error "Pull appeared to succeed but model not found in ollama list"
      fi
    fi
    attempt=$((attempt + 1))
    if [ $attempt -lt "$PULL_RETRIES" ]; then
      log_info "Pull attempt $attempt/$PULL_RETRIES failed for $model, retrying in 5s..."
      sleep 5
    fi
  done
  
  log_error "Failed to pull $model after $PULL_RETRIES attempts"
  return 1
}

# Start the Ollama daemon in the background
log_info "Starting Ollama daemon..."
/bin/ollama serve &
DAEMON_PID=$!

# Wait for daemon to be healthy
if ! wait_for_health; then
  log_error "Daemon failed to start"
  kill $DAEMON_PID 2>/dev/null || true
  exit 1
fi

# Check if models are already cached (from docker volume) - skip pulls if present
log_info "Checking for cached models..."
PULL_PHI4=true
PULL_DELEGATE=true

if ollama list | grep -q "^phi4-mini"; then
  log_info "phi4-mini already present in cache - skipping pull"
  PULL_PHI4=false
fi

if ollama list | grep -q "^f8-delegate"; then
  log_info "f8-delegate already present in cache - skipping pull"
  PULL_DELEGATE=false
fi

# If both models are present, we're done - no pulls needed
if [ "$PULL_PHI4" = false ] && [ "$PULL_DELEGATE" = false ]; then
  log_info "Both required models are ready. Keeping Ollama daemon running..."
  wait $DAEMON_PID
  exit 0
fi

# Models are missing - attempt pulls (may fail on isolated networks)
log_info "One or more models need to be pulled..."

if [ "$PULL_PHI4" = true ]; then
  if pull_model "phi4-mini"; then
    log_info "phi4-mini successfully pulled"
  else
    log_error "FATAL: Could not load phi4-mini model"
    log_error ""
    log_error "Models could not be pulled due to network constraints."
    log_error "To fix this:"
    log_error "  1. Install ollama locally: https://ollama.ai"
    log_error "  2. Run on your host: scripts/ensure-models.sh"
    log_error "  3. Then: npm run env:up"
    kill $DAEMON_PID 2>/dev/null || true
    exit 1
  fi
fi

if [ "$PULL_DELEGATE" = true ]; then
  if pull_model "$F8_DELEGATE_REPO"; then
    log_info "Successfully pulled $F8_DELEGATE_REPO"
  else
    log_error "FATAL: Could not load f8-delegate model"
    log_error ""
    log_error "Models could not be pulled due to network constraints."
    log_error "To fix this:"
    log_error "  1. Install ollama locally: https://ollama.ai"
    log_error "  2. Run on your host: scripts/ensure-models.sh"
    log_error "  3. Then: npm run env:up"
    kill $DAEMON_PID 2>/dev/null || true
    exit 1
  fi
  
  # Create alias for the fine-tuned model
  log_info "Creating f8-delegate alias..."
  if timeout 30 ollama cp "$F8_DELEGATE_REPO" f8-delegate >/dev/null 2>&1; then
    log_info "Successfully created f8-delegate alias"
  else
    log_error "Failed to create f8-delegate alias"
    kill $DAEMON_PID 2>/dev/null || true
    exit 1
  fi
fi

log_info "All required models are ready. Keeping Ollama daemon running..."

# Keep the daemon running in the foreground
wait $DAEMON_PID
