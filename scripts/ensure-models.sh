#!/bin/sh
# MIT License
#
# OPTIONAL pre-seed of the F8 Ollama model volume. `npm run env:up` does NOT need this - the
# Ollama container pulls models itself on first start. Run this only when you want to:
#   - start the environment OFFLINE later (seed once here where there is internet), or
#   - avoid the slow first-run pull inside the container.
#
# It pulls into the named docker volume (f8-ollama-models) using a throwaway Ollama container,
# so it needs ONLY Docker + internet - no host Ollama, no host GPU. `npm run env:up` then finds
# the models already cached and starts instantly.
#
# Default set - the same one the container entrypoint pulls (scripts/ollama-init.sh), so a
# pre-seeded volume can start OFFLINE with nothing missing: phi4-mini (base), phi4-f8-mini (mini
# fine-tune, tagged from F8_DELEGATE_REPO) and phi4-f8 (full-Phi-4 fine-tune, ~9GB, GPU
# recommended) from feature delegate-model-variants, plus bge-m3 (~1.2GB), the embedding model
# the compose stack's provider is wired to and enabled by default. Together ~15GB unless you opt
# out; the opt-outs are the same variables the compose stack reads.
#
#   scripts/ensure-models.sh                          # full default set (~15GB)
#   F8_PULL_PHI4F8=0 scripts/ensure-models.sh         # skip phi4-f8 (~6GB, CPU-only friendly)
#   F8_EMBEDDINGS=false scripts/ensure-models.sh      # skip bge-m3 (embeddings opted out too)
#   F8_DELEGATE_REPO=you/your-mini  F8_PHI4F8_REPO=you/your-14b  scripts/ensure-models.sh

set -e

F8_DELEGATE_REPO="${F8_DELEGATE_REPO:-stoic_hellman_728/phi4-f8-mini}"
F8_PHI4F8_REPO="${F8_PHI4F8_REPO:-stoic_hellman_728/phi4-f8}"
F8_PULL_PHI4F8="${F8_PULL_PHI4F8:-1}"
# Embeddings are on by default in docker-compose.yml, so the embedding model belongs in the
# pre-seed too; opted out with the same variable as the provider and the sidecar.
F8_EMBEDDINGS="${F8_EMBEDDINGS:-true}"
VOLUME="f8-ollama-models"
IMAGE="ollama/ollama:latest"

echo "[ensure-models] Seeding docker volume '$VOLUME': phi4-mini + phi4-f8-mini ($F8_DELEGATE_REPO)"
case "$F8_EMBEDDINGS" in
  0|false|FALSE|no|off)
    echo "[ensure-models] F8_EMBEDDINGS is off - skipping bge-m3 (saves ~1.2GB)";;
  *)
    echo "[ensure-models] + bge-m3 (embedding model) - ~1.2GB extra (default; set F8_EMBEDDINGS=false to skip)";;
esac
case "$F8_PULL_PHI4F8" in
  0|false|FALSE|no|off)
    echo "[ensure-models] F8_PULL_PHI4F8=0 - skipping phi4-f8 (saves ~9GB)";;
  *)
    echo "[ensure-models] + phi4-f8 ($F8_PHI4F8_REPO) - ~9GB extra (default; set F8_PULL_PHI4F8=0 to skip)";;
esac
echo "[ensure-models] This can take 10-30 minutes on a slow link."
echo ""

docker volume create "$VOLUME" >/dev/null

# Pull inside a throwaway container that mounts the SAME volume the compose stack uses. The
# body is single-quoted (host does not expand it); the vars cross in via -e.
docker run --rm \
  --entrypoint /bin/sh \
  -e F8_DELEGATE_REPO="$F8_DELEGATE_REPO" \
  -e F8_PHI4F8_REPO="$F8_PHI4F8_REPO" \
  -e F8_PULL_PHI4F8="$F8_PULL_PHI4F8" \
  -e F8_EMBEDDINGS="$F8_EMBEDDINGS" \
  -v "$VOLUME":/root/.ollama \
  "$IMAGE" -c '
    set -e
    ollama serve >/tmp/ollama-serve.log 2>&1 &
    for i in $(seq 1 30); do
      ollama list >/dev/null 2>&1 && break
      sleep 1
    done
    echo "[ensure-models] Pulling phi4-mini..."
    ollama pull phi4-mini
    echo "[ensure-models] Pulling $F8_DELEGATE_REPO -> phi4-f8-mini..."
    ollama pull "$F8_DELEGATE_REPO"
    ollama cp "$F8_DELEGATE_REPO" phi4-f8-mini
    case "$F8_EMBEDDINGS" in
      0|false|FALSE|no|off) ;;
      *)
        echo "[ensure-models] Pulling bge-m3 (embedding model)..."
        ollama pull bge-m3
        ;;
    esac
    case "$F8_PULL_PHI4F8" in
      0|false|FALSE|no|off) ;;
      *)
        echo "[ensure-models] Pulling $F8_PHI4F8_REPO -> phi4-f8..."
        ollama pull "$F8_PHI4F8_REPO"
        ollama cp "$F8_PHI4F8_REPO" phi4-f8
        ;;
    esac
    echo ""
    echo "[ensure-models] Volume now contains:"
    ollama list
  '

echo ""
echo "[ensure-models] Done. Models are cached in volume '$VOLUME'."
echo "[ensure-models] Start the environment (no download needed):  npm run env:up"
