#!/bin/sh
# MIT License
#
# OPTIONAL pre-seed of the F8 Ollama model volume. `npm run env:up` does NOT need this - the
# Ollama container pulls models itself on first start. Run this only when you want to:
#   - start the environment OFFLINE later (seed once here where there is internet), or
#   - avoid the slow first-run pull inside the container.
#
# It pulls phi4-mini + f8-delegate straight into the named docker volume (f8-ollama-models)
# using a throwaway Ollama container, so it needs ONLY Docker + internet - no host Ollama, no
# host GPU. `npm run env:up` then finds the models already cached and starts instantly.
#
#   scripts/ensure-models.sh
#   F8_DELEGATE_REPO=you/your-finetune scripts/ensure-models.sh   # seed a different publisher

set -e

F8_DELEGATE_REPO="${F8_DELEGATE_REPO:-stoic_hellman_728/f8-delegate}"
VOLUME="f8-ollama-models"
IMAGE="ollama/ollama:latest"

echo "[ensure-models] Seeding docker volume '$VOLUME' with phi4-mini + $F8_DELEGATE_REPO"
echo "[ensure-models] This pulls ~5GB and can take 10-20 minutes on a slow link."
echo ""

docker volume create "$VOLUME" >/dev/null

# Pull inside a throwaway container that mounts the SAME volume the compose stack uses. The
# body is single-quoted (host does not expand it); F8_DELEGATE_REPO crosses in via -e.
docker run --rm \
  --entrypoint /bin/sh \
  -e F8_DELEGATE_REPO="$F8_DELEGATE_REPO" \
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
    echo "[ensure-models] Pulling $F8_DELEGATE_REPO..."
    ollama pull "$F8_DELEGATE_REPO"
    echo "[ensure-models] Tagging $F8_DELEGATE_REPO as f8-delegate..."
    ollama cp "$F8_DELEGATE_REPO" f8-delegate
    echo ""
    echo "[ensure-models] Volume now contains:"
    ollama list
  '

echo ""
echo "[ensure-models] Done. Models are cached in volume '$VOLUME'."
echo "[ensure-models] Start the environment (no download needed):  npm run env:up"
