#!/usr/bin/env bash
# MIT License
#
# bootstrap-eval.sh
#
# Copyright (c) 2011-2026 Henning Rauch
#
# Permission is hereby granted, free of charge, to any person obtaining a copy
# of this software and associated documentation files (the "Software"), to deal
# in the Software without restriction, including without limitation the rights
# to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
# copies of the Software, and to permit persons to whom the Software is
# furnished to do so, subject to the following conditions:
#
# The above copyright notice and this permission notice shall be included in all
# copies or substantial portions of the Software.
#
# THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
# IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
#
# FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
# AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
# LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
# OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
# SOFTWARE.

# Runs UNATTENDED on the Azure GPU VM (started by cloud-init as the systemd unit
# f8-eval.service). Sibling of bootstrap.sh: same shape, different job. It clones the repo,
# installs the toolchain with the SAME shared installer, brings up the GPU and the apiApp, then
# hands the actual work to infra/eval-run.sh from the clone - so the evaluation logic has one
# home and is not re-implemented here.
#
# TWO deliberate differences from bootstrap.sh, both load-bearing:
#
#  1. DESTROY_ON_FINISH defaults to 0. The fine-tune job's artifact escapes to a registry, so
#     that VM can safely delete itself on success. An eval's artifact is a small JSON file, so
#     self-destructing on success would destroy the only copy - the same class of loss as the
#     2026-07-30 incident. The LAUNCH BOX deletes the resource group once it has fetched the
#     results; the f8-teardown.timer backstop caps an abandoned run.
#
#  2. The ollama daemon is restarted AFTER the GPU driver is confirmed, and GPU inference is
#     then PROVEN before any real work. install-prereqs.sh installs ollama before the driver
#     exists (its own modprobe warning is tolerated there), so a daemon inherited from that
#     moment has no CUDA and would run every draft on the CPU at roughly 14 s/token. The
#     fine-tune job never notices, because it only creates and pushes models; an eval would
#     look like a hang and burn the hour.

set -Euo pipefail

LOG=/var/log/f8-eval.log
touch "$LOG" && chmod 644 "$LOG"
exec > >(tee -a "$LOG") 2>&1

set -a; . /etc/f8-eval.env; set +a
: "${REPO_URL:?REPO_URL missing}"; : "${REPO_REF:=main}"
: "${AZ_RESOURCE_GROUP:?}"; : "${AZ_SUBSCRIPTION:?}"
: "${EVAL_PREFIX:=}"                      # registry namespace holding the published variants
: "${VARIANTS:=phi4-f8-mini phi4-f8}"
: "${EVAL_BASELINES=phi4-mini}"           # stock model(s) measured on the SAME hardware.
                                          # Assign-if-UNSET (not if-empty), so passing an
                                          # empty value deliberately means "no baseline".
                                          # The cloud job defaults to the controlled
                                          # three-model run; eval-run.sh on its own does
                                          # exactly what it is told.
: "${EVAL_TIMEOUT:=60m}"
: "${DESTROY_ON_FINISH:=0}"               # see note 1 above - the launch box tears down
: "${DESTROY_ON_FAILURE:=0}"
: "${GIT_TOKEN:=}"
: "${F8_DEBUG:=0}"

WORK=/opt/f8
RESULTS="$WORK/eval-results"
DONE_MARKER="$WORK/.eval-done"
FAILED_MARKER="$WORK/.eval-failed"
mkdir -p "$WORK" "$RESULTS"
export npm_config_yes=true
[ "$F8_DEBUG" = "1" ] && set -x

log(){ echo "[f8-eval $(date -u +%H:%M:%S)] $*"; }
fail(){ log "FATAL: $1"; exit "${2:-40}"; }

# --- teardown: writes the marker the launch box polls, then decides about deletion -----------
teardown(){
  local rc=$1; set +e
  echo ""
  if [ "$rc" -eq 0 ]; then
    log "=== DONE: evaluation succeeded. ==="
    date -u +%FT%TZ > "$DONE_MARKER"
  else
    log "=== FAILED (exit $rc) - see the log above. ==="
    # The reason has to be machine-readable: the launch box prints it instead of making the
    # operator ssh in to find out whether it was the GPU, the pull, or an incomplete row set.
    printf 'exit=%s\nat=%s\n' "$rc" "$(date -u +%FT%TZ)" > "$FAILED_MARKER"
  fi
  if [ "$DESTROY_ON_FINISH" = "1" ] && { [ "$rc" -eq 0 ] || [ "$DESTROY_ON_FAILURE" = "1" ]; }; then
    log "DESTROY_ON_FINISH=1 -> destroying resource group '$AZ_RESOURCE_GROUP' in 60s."
    log "NOTE: the results in $RESULTS die with it unless they were already fetched."
    sleep 60
    bash "$WORK/teardown.sh" || log "teardown.sh failed; the f8-teardown.timer backstop will retry."
  else
    log "leaving the VM UP so the results can be fetched:"
    log "  $RESULTS  (and $LOG)"
    log "The launch box (eval-deploy.sh) deletes the resource group once it has them."
    log "Manual: az group delete --name $AZ_RESOURCE_GROUP --yes"
  fi
  exit "$rc"
}
trap 'teardown $?' EXIT

if [ -f "$DONE_MARKER" ]; then log "marker present -> already completed on a previous boot."; exit 0; fi
rm -f "$FAILED_MARKER"

_home="$(getent passwd "$(id -un)" 2>/dev/null | cut -d: -f6)"
export HOME="${_home:-$HOME}"
export DEBIAN_FRONTEND=noninteractive

# --- clone the repo --------------------------------------------------------------------------
log "installing git to clone the repo..."
apt-get -o DPkg::Lock::Timeout=600 update -y || fail "apt update failed" 40
apt-get -o DPkg::Lock::Timeout=600 install -y git ca-certificates || fail "git install failed" 40

CLONE_URL="$REPO_URL"
[ -n "$GIT_TOKEN" ] && CLONE_URL="$(echo "$REPO_URL" | sed -E "s#https://#https://x-access-token:${GIT_TOKEN}@#")"
if [ ! -d "$WORK/repo/.git" ]; then
  log "cloning $REPO_URL @ $REPO_REF ..."
  git clone --depth 1 --branch "$REPO_REF" "$CLONE_URL" "$WORK/repo" || fail "git clone failed" 22
fi

# --- toolchain via the ONE shared installer (dotnet, node, ollama) ---------------------------
log "installing the toolchain (shared install-prereqs.sh)..."
cd "$WORK/repo/nl-assist-finetune"
bash ./install-prereqs.sh || fail "install-prereqs.sh failed" 40
# shellcheck disable=SC1091
. ./.prereqs-env.sh     # DOTNET_ROOT, PATH (dotnet + uv), PY313

# --- GPU: the Azure GRID (vGPU) driver -------------------------------------------------------
# The WHY of this exact driver build lives in ONE home: infra/bootstrap.sh (the block above
# "install the Azure GRID (vGPU) driver"), and in main.bicep's closing comment. Do not
# re-explain it here; if the SKU or driver changes, change it there and mirror it.
if ! nvidia-smi -L >/dev/null 2>&1; then
  log "installing the Azure GRID (vGPU) driver 570.211.01 for the A10..."
  apt-get -o DPkg::Lock::Timeout=600 install -y build-essential dkms "linux-headers-$(uname -r)" \
    || fail "build-essential / kernel headers install failed" 40
  apt-get -o DPkg::Lock::Timeout=600 purge -y 'cuda-drivers*' 'nvidia-driver-*' 'libnvidia-*' >/dev/null 2>&1 || true
  apt-get -o DPkg::Lock::Timeout=600 autoremove -y >/dev/null 2>&1 || true
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

log "confirming the NVIDIA GPU is visible..."
gpu_ready=0
for _ in $(seq 1 120); do
  if nvidia-smi >/dev/null 2>&1; then gpu_ready=1; break; fi
  sleep 15
done
[ "$gpu_ready" = 1 ] || fail "GPU not visible after ~30 min (GRID driver did not bind - see /var/log/nvidia-installer.log)" 20
log "GPU ready:"; nvidia-smi -L

# --- ollama AFTER the driver (see note 2 at the top of this file) ----------------------------
log "restarting the ollama daemon so it discovers CUDA (it was installed before the driver)..."
systemctl enable --now ollama
systemctl restart ollama
for _ in $(seq 1 30); do ollama list >/dev/null 2>&1 && break; sleep 2; done
ollama list >/dev/null 2>&1 || fail "the ollama daemon did not come back after the restart" 23

# --- apiApp (compile authority) --------------------------------------------------------------
log "starting the apiApp (volatile; dynamic code is always on) on http://localhost:5000 ..."
cd "$WORK/repo"
Fallen8__Durability__Volatile=true \
ASPNETCORE_URLS=http://localhost:5000 \
  dotnet run --project fallen-8-core-apiApp -c Release >/var/log/f8-apiapp.log 2>&1 &
# 200 x 3s = 10 min. The fine-tune job gets away with 100 because run.sh has already restored and
# built the solution by then; here this is the FIRST dotnet invocation on the box, so it pays for
# a cold NuGet restore plus a Release build.
for _ in $(seq 1 200); do curl -sf http://localhost:5000/status >/dev/null 2>&1 && break; sleep 3; done
curl -sf http://localhost:5000/status >/dev/null 2>&1 || fail "apiApp did not become healthy within ~10 min (see /var/log/f8-apiapp.log)" 21
log "apiApp healthy."

# --- the job itself: one home for the evaluation logic, taken from the clone ------------------
log "running the evaluation: variants [$VARIANTS] baselines [$EVAL_BASELINES] from '${EVAL_PREFIX:-<local>}'"
EVAL_PREFIX="$EVAL_PREFIX" \
VARIANTS="$VARIANTS" \
EVAL_BASELINES="$EVAL_BASELINES" \
EVAL_TIMEOUT="$EVAL_TIMEOUT" \
RESULTS_OUT="$RESULTS" \
NL_EVAL_F8=http://localhost:5000 \
REPO_DIR="$WORK/repo" \
  bash "$WORK/repo/nl-assist-finetune/infra/eval-run.sh" \
  || fail "eval-run.sh failed (partial results, if any, are in $RESULTS)" 33

[ -s "$RESULTS/summary.md" ] || fail "eval-run.sh reported success but wrote no summary.md" 33

log "SUCCESS. Summary:"
cat "$RESULTS/summary.md"
# teardown runs via the EXIT trap (rc = 0) and writes the .eval-done marker.
