#!/usr/bin/env bash
# MIT License
#
# Runs UNATTENDED on the Azure A10 VM (started by cloud-init as the systemd unit
# f8-finetune.service). Clones the repo, installs the toolchain via the repo's SINGLE shared
# installer (nl-assist-finetune/install-prereqs.sh - NOT re-implemented here), trains VARIANT
# (default phi4-f8 = full Phi-4 14B), publishes it to Ollama, then DELETES the whole resource
# group so nothing lingers. Everything is mirrored to /var/log/f8-finetune.log and journald so
# you can watch it live (see the deploy script's printed watch command).
#
# Failure handling is explicit: any critical step that fails aborts with a real non-zero code,
# so a run is never falsely reported "done" - but teardown STILL runs (EXIT trap + an
# independent backstop timer), so the expensive VM is never stranded.

set -Euo pipefail

LOG=/var/log/f8-finetune.log
touch "$LOG" && chmod 644 "$LOG"
exec > >(tee -a "$LOG") 2>&1     # mirror stdout/stderr to the log (and journald)

set -a; . /etc/f8-finetune.env; set +a
: "${VARIANT:=phi4-f8}"
: "${REPO_URL:?REPO_URL missing}"; : "${REPO_REF:=main}"
: "${AZ_RESOURCE_GROUP:?}"; : "${AZ_SUBSCRIPTION:?}"
: "${DESTROY_ON_FINISH:=1}"
: "${PUBLISH_REPO:=}"
: "${GIT_TOKEN:=}"
WORK=/opt/f8
MARKER="$WORK/.done"
mkdir -p "$WORK"
export npm_config_yes=true   # so run.sh's `npx tsx` never prompts in this non-TTY context

log(){ echo "[f8 $(date -u +%H:%M:%S)] $*"; }
fail(){ log "FATAL: $1"; exit "${2:-40}"; }

# --- teardown: always runs on exit; depends only on /opt/f8/teardown.sh (curl+jq, no az) ----
teardown(){
  local rc=$1; set +e
  echo ""
  if [ "$rc" -eq 0 ]; then log "=== DONE: pipeline succeeded. ==="; else log "=== FAILED (exit $rc) - see the log above. ==="; fi
  if [ "$DESTROY_ON_FINISH" != "1" ]; then
    log "DESTROY_ON_FINISH != 1 -> leaving the VM up for debugging. Delete it yourself:"
    log "  az group delete --name $AZ_RESOURCE_GROUP --yes"
    exit "$rc"
  fi
  log "Destroying resource group '$AZ_RESOURCE_GROUP' and ALL its resources in 60s..."
  log "(watchers: last chance to read this log; re-run with DESTROY_ON_FINISH=0 to keep the VM)"
  sleep 60
  bash "$WORK/teardown.sh" || log "teardown.sh failed; the f8-teardown.timer backstop will retry (or delete manually)."
  exit "$rc"
}
trap 'teardown $?' EXIT

if [ -f "$MARKER" ]; then log "marker present -> already completed on a previous boot; tearing down."; exit 0; fi

export DEBIAN_FRONTEND=noninteractive

# --- minimal deps to clone the repo (curl/jq for teardown are installed by cloud-init) ------
log "installing git to clone the repo..."
# Lock timeout: the NVIDIA driver extension installs concurrently and holds the apt/dpkg lock.
apt-get -o DPkg::Lock::Timeout=600 update -y || fail "apt update failed" 40
apt-get -o DPkg::Lock::Timeout=600 install -y git ca-certificates || fail "git install failed" 40

# --- clone the repo --------------------------------------------------------------------------
CLONE_URL="$REPO_URL"
[ -n "$GIT_TOKEN" ] && CLONE_URL="$(echo "$REPO_URL" | sed -E "s#https://#https://x-access-token:${GIT_TOKEN}@#")"
if [ ! -d "$WORK/repo/.git" ]; then
  log "cloning $REPO_URL @ $REPO_REF ..."
  git clone --depth 1 --branch "$REPO_REF" "$CLONE_URL" "$WORK/repo" || fail "git clone failed" 22
fi
cd "$WORK/repo/nl-assist-finetune"

# --- install the toolchain via the ONE shared installer, then load its env ------------------
log "installing the fine-tune toolchain (shared install-prereqs.sh)..."
bash ./install-prereqs.sh || fail "install-prereqs.sh failed" 40
# shellcheck disable=SC1091
. ./.prereqs-env.sh     # DOTNET_ROOT, PATH (dotnet + uv), PY313
[ -n "${PY313:-}" ] || fail "install-prereqs.sh did not report a Python 3.13 (PY313 unset)" 40

# --- Ollama running (installed by install-prereqs; start + wait) -----------------------------
systemctl enable --now ollama
for _ in $(seq 1 30); do ollama list >/dev/null 2>&1 && break; sleep 2; done

# --- wait for the GPU (Azure NVIDIA driver extension installs GRID 535.161; may reboot once) --
log "waiting for the NVIDIA GPU driver (Azure extension)..."
gpu_ready=0
for _ in $(seq 1 120); do            # up to ~30 min
  if nvidia-smi >/dev/null 2>&1; then gpu_ready=1; break; fi
  sleep 15
done
[ "$gpu_ready" = 1 ] || fail "GPU not visible after ~30 min (driver extension not done / failed)" 20
log "GPU ready:"; nvidia-smi -L

# --- start the apiApp (compile authority) on :5000 -------------------------------------------
log "starting the apiApp (volatile + dynamic code) on http://localhost:5000 ..."
cd "$WORK/repo"
Fallen8__Durability__Volatile=true \
Fallen8__Security__EnableDynamicCodeExecution=true \
ASPNETCORE_URLS=http://localhost:5000 \
  dotnet run --project fallen-8-core-apiApp -c Release >/var/log/f8-apiapp.log 2>&1 &
for _ in $(seq 1 100); do curl -sf http://localhost:5000/status >/dev/null 2>&1 && break; sleep 3; done
curl -sf http://localhost:5000/status >/dev/null 2>&1 || fail "apiApp did not become healthy (see /var/log/f8-apiapp.log)" 21
log "apiApp healthy."

cd "$WORK/repo/nl-assist-finetune"

# --- build the venv, then LOG the base template / LoRA targets so a watcher can sanity-check --
# (train-config.$VARIANT.json carries "VERIFY before spending GPU hours" notes; unattended we
#  can't act on them, but we surface them in the log.)
VARIANT="$VARIANT" PYTHON="$PY313" ./run.sh deps || fail "run.sh deps failed" 30
log "inspecting base chat template + LoRA target modules for $VARIANT (sanity-check in the log):"
train/.venv/bin/python train/train_lora.py --inspect --config "train/train-config.$VARIANT.json" 2>&1 | head -n 40 || true

# --- run the whole pipeline (deps reused): dataset -> train -> merge -> gguf -> ollama create -
log "running: VARIANT=$VARIANT PYTHON=$PY313 ./run.sh all  (the long GPU step)"
NL_EVAL_F8=http://localhost:5000 VARIANT="$VARIANT" PYTHON="$PY313" timeout 6h ./run.sh all \
  || fail "run.sh all (train/merge/gguf/create) failed" 30
log "training + model registration complete."

# --- publish to Ollama (unattended only if the registered key was injected) ------------------
if [ -n "$PUBLISH_REPO" ] && [ -f /root/.ollama/id_ed25519 ]; then
  log "publishing $VARIANT to $PUBLISH_REPO ..."
  VARIANT="$VARIANT" PUBLISH_REPO="$PUBLISH_REPO" ./run.sh publish || fail "ollama push failed" 31
  log "pushed $PUBLISH_REPO."
else
  log "skipping push (PUBLISH_REPO unset or no /root/.ollama/id_ed25519 key). Model is local only and lost on teardown."
fi

touch "$MARKER"
log "SUCCESS - VARIANT=$VARIANT produced${PUBLISH_REPO:+ and published to $PUBLISH_REPO}."
# teardown runs via the EXIT trap (rc = 0)
