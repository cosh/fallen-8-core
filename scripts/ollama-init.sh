#!/bin/sh
# MIT License
#
# Ollama initialization (entrypoint of Dockerfile.ollama): start the daemon, then pull the
# models F8 needs. Default set: phi4-mini (base) + phi4-f8-mini (the mini fine-tune, the UI
# default). Opt-in: phi4-f8 (the full-Phi-4 fine-tune, ~9 GB, GPU) when F8_PULL_PHI4F8 is set.
# Feature: delegate-model-variants.
#
# Models already present in the mounted volume are reused, so this only downloads on a cold
# volume. Degradation is deliberate: if a pull fails (no internet, registry hiccup), we log a
# loud, actionable error but KEEP THE DAEMON RUNNING. The Ollama endpoint stays up with
# whatever is present, so partial setups still work and you can retry the pull (or run
# scripts/ensure-models.sh on the host) without the whole container crash-looping.

# Published fine-tune repos (pull sources), tagged locally to the short variant names.
F8_DELEGATE_REPO="${F8_DELEGATE_REPO:-stoic_hellman_728/phi4-f8-mini}"  # -> phi4-f8-mini
F8_PHI4F8_REPO="${F8_PHI4F8_REPO:-stoic_hellman_728/phi4-f8}"           # -> phi4-f8 (opt-in)
F8_PULL_PHI4F8="${F8_PULL_PHI4F8:-0}"
HEALTH_CHECK_RETRIES=30
HEALTH_CHECK_INTERVAL=1
PULL_RETRIES=3
PULL_TIMEOUT=1800  # 30 minutes per pull (phi4-f8 is ~9GB on a slow link)

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

# A stock base model: pulled under its own name. `ollama list` prints "name:tag", so anchor
# the presence check with a trailing colon (keeps phi4-f8 from matching phi4-f8-mini).
ensure_base() {
  name=$1
  if ollama list | grep -q "^${name}:"; then
    log_info "$name already present - skipping pull"
    return 0
  fi
  pull_model "$name"
}

# A fine-tune: pull <repo> and tag it locally as the short <tag> the UI uses (no f8-delegate
# alias - feature delegate-model-variants, decision: clean rename).
ensure_finetune() {
  repo=$1
  tag=$2
  if ollama list | grep -q "^${tag}:"; then
    log_info "$tag already present - skipping pull"
    return 0
  fi
  if pull_model "$repo"; then
    if timeout 30 ollama cp "$repo" "$tag" >/dev/null 2>&1; then
      log_info "Tagged $repo as $tag"
      return 0
    fi
    log_error "Pulled $repo but could not tag it as $tag"
  fi
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

MISSING=""

# Default set: the base + the CPU-OK mini fine-tune (the UI default).
ensure_base "phi4-mini" || MISSING="$MISSING phi4-mini"
ensure_finetune "$F8_DELEGATE_REPO" "phi4-f8-mini" || MISSING="$MISSING phi4-f8-mini"

# Opt-in: the full-Phi-4 fine-tune. ~9GB and GPU-bound, so never pulled by default.
case "$F8_PULL_PHI4F8" in
  1|true|TRUE|yes|on)
    log_info "F8_PULL_PHI4F8 set - also fetching phi4-f8 (full Phi-4 fine-tune, ~9GB, GPU recommended)"
    ensure_finetune "$F8_PHI4F8_REPO" "phi4-f8" || MISSING="$MISSING phi4-f8"
    ;;
esac

if [ -n "$MISSING" ]; then
  log_error "Some models are missing:$MISSING"
  log_error "The Ollama endpoint stays UP with whatever is present. To finish setup:"
  log_error "  - check this container has internet to registry.ollama.ai, then: npm run env:down && npm run env:up"
  log_error "  - or pre-seed the volume from a host with internet: scripts/ensure-models.sh"
else
  log_info "All requested models are ready."
fi

log_info "Keeping Ollama daemon running..."
wait "$DAEMON_PID"
