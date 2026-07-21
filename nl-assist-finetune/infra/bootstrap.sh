#!/usr/bin/env bash
# MIT License
#
# Runs UNATTENDED on the Azure A10 VM (started by cloud-init as the systemd unit
# f8-finetune.service). Clones the repo, installs the toolchain via the repo's SINGLE shared
# installer (nl-assist-finetune/install-prereqs.sh - NOT re-implemented here), trains every
# variant in VARIANTS (default: phi4-f8-mini + phi4-f8, in one session sharing the dataset),
# publishes each to Ollama, then DELETES the whole resource group so nothing lingers.
# Everything is mirrored to /var/log/f8-finetune.log and journald so you can watch it live.
#
# Failure handling is explicit: any critical step that fails aborts with a real non-zero code,
# so a run is never falsely reported "done" - but teardown STILL runs (EXIT trap + an
# independent backstop timer), so the expensive VM is never stranded.

set -Euo pipefail

LOG=/var/log/f8-finetune.log
touch "$LOG" && chmod 644 "$LOG"
exec > >(tee -a "$LOG") 2>&1     # mirror stdout/stderr to the log (and journald)

set -a; . /etc/f8-finetune.env; set +a
: "${VARIANTS:=phi4-f8-mini phi4-f8}"    # space-separated; trained in one session, dataset shared
: "${REPO_URL:?REPO_URL missing}"; : "${REPO_REF:=main}"
: "${AZ_RESOURCE_GROUP:?}"; : "${AZ_SUBSCRIPTION:?}"
: "${DESTROY_ON_FINISH:=1}"
: "${DESTROY_ON_FAILURE:=0}"              # 0 = keep the VM on FAILURE (so the log is readable); the 8h backstop still caps cost
: "${PUBLISH_PREFIX:=}"                   # each variant publishes to $PUBLISH_PREFIX/<variant>
: "${GIT_TOKEN:=}"
: "${F8_DEBUG:=0}"                        # 1 = set -x full command trace into the log
WORK=/opt/f8
MARKER="$WORK/.done"
mkdir -p "$WORK"
export npm_config_yes=true   # so run.sh's `npx tsx` never prompts in this non-TTY context
[ "$F8_DEBUG" = "1" ] && set -x   # full command trace into /var/log/f8-finetune.log

log(){ echo "[f8 $(date -u +%H:%M:%S)] $*"; }
fail(){ log "FATAL: $1"; exit "${2:-40}"; }

# --- teardown: always runs on exit; depends only on /opt/f8/teardown.sh (curl+jq, no az) ----
teardown(){
  local rc=$1; set +e
  echo ""
  if [ "$rc" -eq 0 ]; then log "=== DONE: pipeline succeeded. ==="; else log "=== FAILED (exit $rc) - see the log above. ==="; fi
  # Success -> self-destruct (per DESTROY_ON_FINISH). FAILURE -> KEEP the VM so
  # /var/log/f8-finetune.log stays readable for debugging; the f8-teardown.timer backstop (8h
  # after boot) still deletes everything, so a failed run can't run up cost. DESTROY_ON_FAILURE=1
  # forces self-destruct on failure too.
  if [ "$DESTROY_ON_FINISH" = "1" ] && { [ "$rc" -eq 0 ] || [ "$DESTROY_ON_FAILURE" = "1" ]; }; then
    log "Destroying resource group '$AZ_RESOURCE_GROUP' and ALL its resources in 60s..."
    log "(watchers: last chance to read this log.)"
    sleep 60
    bash "$WORK/teardown.sh" || log "teardown.sh failed; the f8-teardown.timer backstop will retry (or delete manually)."
  elif [ "$rc" -ne 0 ]; then
    log "FAILED -> keeping the VM UP for 1h so you can read the error. SSH in NOW and check:"
    log "  /var/log/f8-finetune.log   and   /var/log/f8-apiapp.log"
    if [ "$DESTROY_ON_FINISH" = "1" ]; then
      log "This VM auto-deletes in 1h. Delete sooner with: az group delete --name $AZ_RESOURCE_GROUP --yes"
      sleep 3600
      bash "$WORK/teardown.sh" || log "teardown.sh failed - delete manually: az group delete --name $AZ_RESOURCE_GROUP --yes"
    else
      log "DESTROY_ON_FINISH=0 -> no auto-delete; delete it yourself: az group delete --name $AZ_RESOURCE_GROUP --yes"
    fi
  else
    log "DESTROY_ON_FINISH != 1 -> leaving the VM up. Delete it yourself: az group delete --name $AZ_RESOURCE_GROUP --yes"
  fi
  exit "$rc"
}
trap 'teardown $?' EXIT

if [ -f "$MARKER" ]; then log "marker present -> already completed on a previous boot; tearing down."; exit 0; fi

# Never depend on a specific Linux user: derive HOME from the RUNNING user's passwd entry
# (immune to a leaked/incorrect HOME in the environment). systemd runs this as root -> /root,
# but nothing here assumes that. install-prereqs.sh (a child process) inherits this HOME.
_home="$(getent passwd "$(id -un)" 2>/dev/null | cut -d: -f6)"
export HOME="${_home:-$HOME}"
export OLLAMA_KEY="$HOME/.ollama/id_ed25519"

# Install the injected Ollama signing key (if any) into the running user's ~/.ollama - cloud-init
# drops it at a neutral path so this script, not the YAML, decides where it lives.
if [ -f "$WORK/ollama_id_ed25519" ]; then
  mkdir -p "$HOME/.ollama" && chmod 700 "$HOME/.ollama"
  install -m 600 "$WORK/ollama_id_ed25519" "$OLLAMA_KEY"
  [ -f "$WORK/ollama_id_ed25519.pub" ] && install -m 644 "$WORK/ollama_id_ed25519.pub" "$OLLAMA_KEY.pub"
  log "installed the Ollama signing key into $HOME/.ollama/"
fi

export DEBIAN_FRONTEND=noninteractive

# --- minimal deps to clone the repo (curl/jq for teardown are installed by cloud-init) ------
log "installing git to clone the repo..."
# Lock timeout: cloud-init / unattended-upgrades may run apt concurrently and hold the lock.
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

# --- install the Azure GRID (vGPU) driver for the A10 ----------------------------------------
# THE one home for this explanation. NVadsA10v5 exposes the A10 as a LICENSED vGPU (SR-IOV), so
# it needs NVIDIA's *Azure GRID* guest driver - NOT the datacenter/CUDA driver. The datacenter
# driver loads but binds no device ("modprobe nvidia: No such device"), which is exactly what the
# HpcCompute GPU-driver extension's default (610.43.02) did and why we dropped the extension from
# the Bicep. Microsoft's N-series Linux driver-setup doc lists NVadsA10_v5 = vGPU18 =
# 570.211.01-grid-azure, redistributed at the URL below (GRID licensing bundled - no license
# server). Guarded by nvidia-smi so a boot that re-enters this script is a no-op.
if ! nvidia-smi -L >/dev/null 2>&1; then
  log "installing the Azure GRID (vGPU) driver 570.211.01 for the A10..."
  apt-get -o DPkg::Lock::Timeout=600 install -y build-essential dkms "linux-headers-$(uname -r)" \
    || fail "build-essential / kernel headers install failed" 40
  # A datacenter/CUDA driver (base image or a prior attempt) would shadow GRID - remove it first.
  apt-get -o DPkg::Lock::Timeout=600 purge -y 'cuda-drivers*' 'nvidia-driver-*' 'libnvidia-*' >/dev/null 2>&1 || true
  apt-get -o DPkg::Lock::Timeout=600 autoremove -y >/dev/null 2>&1 || true
  # nouveau would claim the device and block the NVIDIA module; unload + blacklist it (best effort).
  printf 'blacklist nouveau\noptions nouveau modeset=0\n' > /etc/modprobe.d/blacklist-nouveau.conf
  update-initramfs -u >/dev/null 2>&1 || true
  modprobe -r nouveau 2>/dev/null || true
  GRID_RUN=NVIDIA-Linux-x86_64-570.211.01-grid-azure.run
  GRID_URL="https://download.microsoft.com/download/2a04ca6a-9eec-40d9-9564-9cdea1ab795f/$GRID_RUN"
  curl -fSL --connect-timeout 30 --max-time 900 --retry 3 -o "/tmp/$GRID_RUN" "$GRID_URL" \
    || fail "GRID driver download failed ($GRID_URL)" 20
  chmod +x "/tmp/$GRID_RUN"
  "/tmp/$GRID_RUN" --silent --dkms || fail "GRID driver install failed (see /var/log/nvidia-installer.log)" 20
  modprobe nvidia 2>/dev/null || true
  log "GRID driver installed."
fi

# --- confirm the GPU is live (the driver just loaded; the loop also rides out a rare reboot) --
log "confirming the NVIDIA GPU is visible..."
gpu_ready=0
for _ in $(seq 1 120); do            # up to ~30 min
  if nvidia-smi >/dev/null 2>&1; then gpu_ready=1; break; fi
  sleep 15
done
[ "$gpu_ready" = 1 ] || fail "GPU not visible after ~30 min (GRID driver did not bind - see /var/log/nvidia-installer.log)" 20
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

# Build the venv ONCE - it is variant-agnostic (same torch/deps for both the mini and the 14B).
PYTHON="$PY313" ./run.sh deps || fail "run.sh deps failed" 30

# Train each variant in this one session. run.sh's dataset stage generates dataset/train.jsonl
# on the first variant and REUSES it thereafter, so the (compile-gated) dataset generation +
# the apiApp + the venv are shared across variants - the whole point of doing them together.
# Each variant publishes to $PUBLISH_PREFIX/<variant> (e.g. .../phi4-f8-mini, .../phi4-f8).
for v in $VARIANTS; do
  log "================  variant: $v  ================"
  # train-config.$v.json carries "VERIFY before spending GPU hours" notes (marker + LoRA
  # targets); unattended we can't act on them, but we surface them in the watchable log.
  log "inspecting base chat template + LoRA target modules for $v:"
  train/.venv/bin/python train/train_lora.py --inspect --config "train/train-config.$v.json" 2>&1 | head -n 40 || true

  log "running: VARIANT=$v PYTHON=$PY313 ./run.sh all  (dataset built once, reused across variants)"
  NL_EVAL_F8=http://localhost:5000 VARIANT="$v" PYTHON="$PY313" timeout 6h ./run.sh all \
    || fail "run.sh all (train/merge/gguf/create) failed for $v" 30
  log "$v: training + model registration complete."

  if [ -n "$PUBLISH_PREFIX" ] && [ -f "$OLLAMA_KEY" ]; then
    log "publishing $v to $PUBLISH_PREFIX/$v ..."
    VARIANT="$v" PUBLISH_REPO="$PUBLISH_PREFIX/$v" ./run.sh publish || fail "ollama push failed for $v" 31
    log "pushed $PUBLISH_PREFIX/$v."
  else
    log "skipping push for $v (no PUBLISH_PREFIX or no signing key at $OLLAMA_KEY)."
  fi
done

touch "$MARKER"
log "SUCCESS - variants [$VARIANTS] produced${PUBLISH_PREFIX:+ and published under $PUBLISH_PREFIX/}."
# teardown runs via the EXIT trap (rc = 0)
