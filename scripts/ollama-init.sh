#!/bin/sh
# MIT License
#
# Ollama initialization (entrypoint of Dockerfile.ollama): start the daemon, then pull the
# models F8 needs. Default set: phi4-mini (base), phi4-f8-mini (the mini fine-tune, the UI
# default), and phi4-f8 (the full-Phi-4 fine-tune, ~9 GB, GPU recommended) - set
# F8_PULL_PHI4F8=0 to skip it on a CPU-only or disk-constrained host, or F8_PULL_ASSIST=0 to skip
# the two mini assist pulls when the chat gateway runs on a hosted provider.
# Feature: delegate-model-variants.
#
# Models already present in the mounted volume are reused, so this only downloads on a cold
# volume. Degradation is deliberate: if a pull fails (no internet, registry hiccup), we log a
# loud, actionable error but KEEP THE DAEMON RUNNING. The Ollama endpoint stays up with
# whatever is present, so partial setups still work and you can retry the pull (or run
# scripts/ensure-models.sh on the host) without the whole container crash-looping.

# Published fine-tune repos (pull sources), tagged locally to the short variant names.
F8_DELEGATE_REPO="${F8_DELEGATE_REPO:-stoic_hellman_728/phi4-f8-mini:v0.0.35}"  # -> phi4-f8-mini
F8_PHI4F8_REPO="${F8_PHI4F8_REPO:-stoic_hellman_728/phi4-f8:v0.0.35}"           # -> phi4-f8
F8_PULL_PHI4F8="${F8_PULL_PHI4F8:-1}"
# The agent purpose's model. Pulled only when the agent host is coming up; see the pull near the
# end. F8_AGENTS is lowercased because the INSTANCE binds the same variable as a .NET Boolean,
# which parses case insensitively: without this, F8_AGENTS=True opened the instance's /agents
# routes while this script skipped the pull, so one switch had two answers.
F8_AGENT_MODEL="${F8_AGENT_MODEL:-phi4-mini:latest}"
F8_AGENTS_ON=$(printf '%s' "${F8_AGENTS:-false}" | tr '[:upper:]' '[:lower:]')
# The embedding model the F8 API's provider is wired to in docker-compose.yml (feature
# embedding-out-of-box); on by default, opted out together with the provider.
F8_EMBEDDINGS="${F8_EMBEDDINGS:-true}"
# The two assist fine-tunes (feature model-providers); on by default. Set 0 when the chat gateway
# runs on a hosted provider, which is what the openai and anthropic overlays do. Deliberately
# independent of F8_EMBEDDINGS: a hosted-chat deployment is exactly the one that keeps bge-m3 here.
F8_PULL_ASSIST="${F8_PULL_ASSIST:-1}"
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
# The 12 hex the daemon reports for a model, which is the leading half of the sha256 of its
# registry manifest - so it can be compared directly against what is published:
#   curl -s https://registry.ollama.ai/v2/<repo>/manifests/latest | sha256sum | cut -c1-12
model_id() { ollama list | awk -v t="$1" 'index($1, t ":") == 1 { print $2; exit }'; }

# ALWAYS attempt the pull, even when the tag is already present locally.
#
# The models volume persists across `compose down/up`, and the fine-tune pipeline republishes over
# the SAME tag rather than a versioned one. So the previous "already present -> skipping pull"
# short-circuit meant a host that had ever pulled the model kept serving those weights forever,
# under an identical model name, with nothing anywhere reporting the drift. An `ollama pull` is
# content-addressed: when nothing changed it is a digest check and no transfer.
#
# When the pull fails and a local copy exists we keep it and say so, which preserves offline and
# air-gapped starts and the scripts/ensure-models.sh pre-seed path exactly as before. The model id
# is logged either way, so the log records WHICH build is being served.
ensure_finetune() {
  repo=$1
  tag=$2
  had_local=0
  if ollama list | grep -q "^${tag}:"; then
    had_local=1
    log_info "$tag is present ($(model_id "$tag")) - checking the registry for a newer build"
  fi
  if pull_model "$repo"; then
    if timeout 30 ollama cp "$repo" "$tag" >/dev/null 2>&1; then
      log_info "Tagged $repo as $tag ($(model_id "$tag"))"
      return 0
    fi
    log_error "Pulled $repo but could not tag it as $tag"
  elif [ "$had_local" = 1 ]; then
    log_info "Registry unreachable - keeping the cached $tag ($(model_id "$tag")); it may be older than what is published."
    return 0
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

# Default set: the base + the CPU-OK mini fine-tune (the UI default) - unless chat runs on a
# hosted provider and nothing here will ever be asked for a token (F8_PULL_ASSIST=0).
case "$F8_PULL_ASSIST" in
  0|false|FALSE|no|off)
    log_info "F8_PULL_ASSIST is off - skipping the phi4-mini and phi4-f8-mini assist pulls"
    ;;
  *)
    ensure_base "phi4-mini" || MISSING="$MISSING phi4-mini"
    ensure_finetune "$F8_DELEGATE_REPO" "phi4-f8-mini" || MISSING="$MISSING phi4-f8-mini"
    ;;
esac

# The embedding model (bge-m3, MIT) - unless embeddings are opted out (F8_EMBEDDINGS=false).
case "$F8_EMBEDDINGS" in
  0|false|FALSE|no|off)
    log_info "F8_EMBEDDINGS is off - skipping the embedding model pull"
    ;;
  *)
    ensure_base "bge-m3" || MISSING="$MISSING bge-m3"
    ;;
esac

# The full-Phi-4 fine-tune: ~9GB and GPU-bound, but pulled by default alongside the mini.
# Set F8_PULL_PHI4F8=0 to skip it on a CPU-only or disk-constrained host.
case "$F8_PULL_PHI4F8" in
  0|false|FALSE|no|off)
    log_info "F8_PULL_PHI4F8 is off - skipping the phi4-f8 (full Phi-4) pull"
    ;;
  *)
    ensure_finetune "$F8_PHI4F8_REPO" "phi4-f8" || MISSING="$MISSING phi4-f8"
    ;;
esac

# The agent purpose's model (feature agent-host), pulled only when the agent host is coming up.
# Off by default like the host itself, and skipped rather than failed when it is off: a deployment
# that runs no agents should not spend a pull on a model nothing will ask for.
#
# A STOCK base model, not a fine-tune: the assist purpose points at the delegate fine-tune and the
# agent purpose at a general model, which is the whole reason the two purposes exist. So ensure_base
# rather than ensure_finetune, and no local retagging.
#
# In the DEFAULT configuration this pull finds the model already there, because F8_PULL_ASSIST
# pulls phi4-mini above and ensure_base skips what is present. It earns its place when
# F8_AGENT_MODEL names something else.
#
# It does NOT make agents run on a local model while chat runs hosted, which this comment claimed
# until the merge gate measured it: ONE Fallen8:Chat:Backend serves both purposes, so an instance
# whose chat is on OpenAI or Anthropic resolves the agent purpose there and never asks this sidecar
# for the model pulled here.
#
# Measured and worth knowing before choosing it: the shipped default emits no parsed tool call when
# ANY instruction text is present (findings.md section 1). It is the stock tool-capable MIT model
# the sidecar can pull, so it is what the default names; a deployment that wants agents that
# actually call tools names one that does in the RUNNING backend's key,
# Fallen8__Chat__<Backend>__Models__Agent, which is the Ollama block whenever this sidecar is what
# serves chat.
if [ "$F8_AGENTS_ON" = "true" ]; then
  ensure_base "$F8_AGENT_MODEL" || MISSING="$MISSING $F8_AGENT_MODEL"
else
  log_info "F8_AGENTS is not true - skipping the agent model pull"
fi

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
