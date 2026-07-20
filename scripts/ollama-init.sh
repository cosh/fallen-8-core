#!/bin/sh
# MIT License
#
# Ollama initialization (entrypoint of Dockerfile.ollama): start the daemon, then pull the
# models F8 needs - phi4-mini (base) and f8-delegate (the fine-tune, the UI default). Models
# already present in the mounted volume are reused, so this only downloads on a cold volume.
#
# Degradation is deliberate: if a pull fails (no internet, registry hiccup), we log a loud,
# actionable error but KEEP THE DAEMON RUNNING. The Ollama endpoint stays up with whatever is
# present, so partial setups still work and you can retry the pull (or run
# scripts/ensure-models.sh on the host) without the whole container crash-looping.

F8_DELEGATE_REPO="${F8_DELEGATE_REPO:-stoic_hellman_728/f8-delegate}"
HEALTH_CHECK_RETRIES=30
HEALTH_CHECK_INTERVAL=1
PULL_RETRIES=3
PULL_TIMEOUT=900  # 15 minutes per model pull (~2.5GB models need time on a slow link)

log_info()  { echo "[ollama-init] INFO: $*"; }
log_error() { echo "[ollama-init] ERROR: $*" >&2; }

wait_for_health() {
  attempt=0
  log_info "Waiting for Ollama daemon to be healthy..."
  while [ "$attempt" -lt "$HEALTH_CHECK_RETRIES" ]; do
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

# Pull with retries. Returns 0 on success, 1 on failure - never exits the script.
pull_model() {
  model=$1
  attempt=0
  log_info "Pulling model: $model"
  while [ "$attempt" -lt "$PULL_RETRIES" ]; do
    if timeout "$PULL_TIMEOUT" ollama pull "$model"; then
      log_info "Successfully pulled: $model"
      return 0
    fi
    attempt=$((attempt + 1))
    if [ "$attempt" -lt "$PULL_RETRIES" ]; then
      log_info "Pull attempt $attempt/$PULL_RETRIES failed for $model, retrying in 5s..."
      sleep 5
    fi
  done
  log_error "Failed to pull $model after $PULL_RETRIES attempts"
  return 1
}

# Start the Ollama daemon in the background.
log_info "Starting Ollama daemon..."
/bin/ollama serve &
DAEMON_PID=$!

if ! wait_for_health; then
  log_error "Daemon failed to start"
  kill "$DAEMON_PID" 2>/dev/null || true
  exit 1
fi

# Reuse models already in the volume; only pull what is missing.
NEED_PHI4=true
NEED_DELEGATE=true
if ollama list | grep -q "^phi4-mini"; then
  log_info "phi4-mini already present - skipping pull"
  NEED_PHI4=false
fi
if ollama list | grep -q "^f8-delegate"; then
  log_info "f8-delegate already present - skipping pull"
  NEED_DELEGATE=false
fi

MISSING=""

if [ "$NEED_PHI4" = true ]; then
  pull_model "phi4-mini" || MISSING="$MISSING phi4-mini"
fi

if [ "$NEED_DELEGATE" = true ]; then
  if pull_model "$F8_DELEGATE_REPO"; then
    # Tag the pulled repo as the short "f8-delegate" name the UI defaults to.
    if timeout 30 ollama cp "$F8_DELEGATE_REPO" f8-delegate >/dev/null 2>&1; then
      log_info "Tagged $F8_DELEGATE_REPO as f8-delegate"
    else
      log_error "Pulled $F8_DELEGATE_REPO but could not tag it as f8-delegate"
      MISSING="$MISSING f8-delegate"
    fi
  else
    MISSING="$MISSING f8-delegate"
  fi
fi

if [ -n "$MISSING" ]; then
  log_error "Some models are missing:$MISSING"
  log_error "The Ollama endpoint stays UP with whatever is present. To finish setup:"
  log_error "  - check this container has internet to registry.ollama.ai, then: npm run env:down && npm run env:up"
  log_error "  - or pre-seed the volume from a host with internet: scripts/ensure-models.sh"
else
  log_info "All required models are ready (phi4-mini + f8-delegate)."
fi

log_info "Keeping Ollama daemon running..."
wait "$DAEMON_PID"
