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
PULL_TIMEOUT=300  # 5 minutes per model pull

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
      log_info "Successfully pulled: $model"
      return 0
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

# Pull required model (base + fallback)
if ! pull_model "phi4-mini"; then
  log_error "Failed to pull phi4-mini (required model)"
  kill $DAEMON_PID 2>/dev/null || true
  exit 1
fi

# Pull optional fine-tuned model (best-effort)
if pull_model "$F8_DELEGATE_REPO"; then
  log_info "Tagging $F8_DELEGATE_REPO as f8-delegate..."
  if timeout 30 ollama cp "$F8_DELEGATE_REPO" f8-delegate >/dev/null 2>&1; then
    log_info "Successfully created f8-delegate alias"
  else
    log_error "Failed to create f8-delegate alias (non-fatal)"
  fi
else
  log_error "Failed to pull $F8_DELEGATE_REPO (optional; UI will fall back to phi4-mini)"
fi

log_info "Model initialization complete. Ollama is ready."

# Keep the daemon running in the foreground
wait $DAEMON_PID
