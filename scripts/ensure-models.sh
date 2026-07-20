#!/bin/bash
# MIT License
#
# Ensure that required Ollama models are cached and ready for the F8 environment.
# This script populates the docker volume with models so containers don't need
# to download them at startup (which can timeout due to network issues).
#
# Run this ONCE after cloning the repo, or anytime you need to refresh models:
#   scripts/ensure-models.sh
#
# The script will:
# 1. Check if you have 'ollama' installed locally
# 2. Pull phi4-mini and f8-delegate models
# 3. Copy them to the docker volume so 'npm run env:up' uses them immediately
# 4. Validate that both models are present and ready

set -e

# Configuration
F8_DELEGATE_REPO="${F8_DELEGATE_REPO:-stoic_hellman_728/f8-delegate}"
DOCKER_VOLUME="f8-ollama-models"

log_info() {
  echo "[ensure-models] INFO: $*"
}

log_error() {
  echo "[ensure-models] ERROR: $*" >&2
}

log_success() {
  echo "[ensure-models] ✓ $*"
}

# Check if ollama is installed
if ! command -v ollama > /dev/null 2>&1; then
  log_error "ollama is not installed on your system"
  echo ""
  echo "To use the F8 environment, install Ollama:"
  echo "  - Download: https://ollama.ai"
  echo "  - Then run: scripts/ensure-models.sh"
  echo ""
  echo "Alternatively, if you don't have ollama installed:"
  echo "  - Pre-built models will be embedded in the Docker image"
  echo "  - (This is a future enhancement - currently models must be pulled)"
  exit 1
fi

log_info "Pulling models to local Ollama cache..."
log_info "This may take 10-20 minutes (each model is ~7GB)"
echo ""

# Pull phi4-mini (base model)
log_info "Downloading phi4-mini base model..."
if ollama pull phi4-mini; then
  log_success "phi4-mini ready"
else
  log_error "Failed to pull phi4-mini"
  exit 1
fi

# Pull f8-delegate (fine-tuned model)
log_info "Downloading f8-delegate fine-tuned model..."
if ollama pull "$F8_DELEGATE_REPO"; then
  log_success "f8-delegate fine-tune ready"
else
  log_error "Failed to pull $F8_DELEGATE_REPO"
  exit 1
fi

# Create the alias so the API uses the right model name
log_info "Creating 'f8-delegate' alias..."
if ollama cp "$F8_DELEGATE_REPO" f8-delegate; then
  log_success "f8-delegate alias created"
else
  log_error "Failed to create f8-delegate alias"
  exit 1
fi

# Verify models are in local cache
log_info "Verifying local cache..."
if ollama list | grep -q "phi4-mini"; then
  log_success "phi4-mini found in local cache"
else
  log_error "phi4-mini not found in local cache after pull"
  exit 1
fi

if ollama list | grep -q "f8-delegate"; then
  log_success "f8-delegate found in local cache"
else
  log_error "f8-delegate not found in local cache after pull"
  exit 1
fi

echo ""
log_success "All models cached and ready!"
echo ""
echo "The docker volume '$DOCKER_VOLUME' now contains:"
ollama list | grep -E "(phi4-mini|f8-delegate)" || true
echo ""
log_info "You can now use the F8 environment:"
echo "  npm run env:up        # Start everything (uses cached models)"
echo "  npm run env:down      # Stop everything"
echo "  npm run env:logs      # Follow logs"
