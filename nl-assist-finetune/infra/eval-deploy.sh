#!/usr/bin/env bash
# MIT License
#
# eval-deploy.sh
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
#
# Spins up a GPU VM, evaluates the PUBLISHED models on it, brings the results back here, and
# then deletes the resource group. Sibling of deploy.sh (which trains); it reuses that job's
# main.bicep and teardown.sh unchanged, because neither encodes anything fine-tune-specific.
#
# Unlike the fine-tune job, THIS script waits. A fine-tune's artifact escapes to a registry, so
# that VM can self-destruct on success; an eval produces a 15 KB JSON file, so the VM is told to
# stay up (DESTROY_ON_FINISH=0) and the teardown happens HERE, after the results are safely
# copied down. An abandoned run is capped by the f8-teardown.timer backstop on the VM.
#
# Usage:
#   EVAL_PREFIX=<your-ollama-namespace> ./eval-deploy.sh
#   EVAL_ATTACH_RG=rg-f8-eval-xxxxxx ./eval-deploy.sh     # re-attach to a run in progress
#
# Env:
#   EVAL_PREFIX      REQUIRED - registry namespace to pull the variants from (e.g. the same
#                    value used as PUBLISH_PREFIX when they were published).
#   VARIANTS         default "phi4-f8-mini phi4-f8"
#   EVAL_BASELINES   stock model(s) to measure on the same hardware; default "phi4-mini",
#                    set to "" for none.
#   LOCATION         default westeurope        VM_SIZE default Standard_NV36ads_A10_v5
#   OS_DISK_GB       default 128 (no training scratch is needed, unlike the fine-tune job)
#   F8_SPOT          1 = Spot (an eval is short and re-runnable, so Spot is attractive here)
#   EVAL_WAIT_MIN    how long to wait for the run, default 180
#   EVAL_TIMEOUT     per-model cap passed through to eval-run.sh, default 60m
#   RESULTS_DIR      where to put the fetched results; default
#                    nl-assist-finetune/eval/results/cloud-<UTC stamp>/
#   DESTROY_ON_FAILURE  1 = delete the RG even when the eval failed (default 0: keep it so the
#                    VM's log is readable; the VM's own 4h backstop still caps the cost)
#   REPO_URL/REPO_REF, SSH_PUBKEY_FILE, SSH_KEY_FILE, ADMIN_USER, ALLOWED_SSH_CIDR, GIT_TOKEN,
#   F8_DEBUG         as in deploy.sh.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FT="$(cd "$HERE/.." && pwd)"

step(){ echo "[eval-deploy] $*"; }
die(){ echo "[eval-deploy] ERROR: $*" >&2; exit 1; }
_on_err(){ echo "[eval-deploy] ERROR: a command failed (exit $?) around line ${BASH_LINENO[0]} - see above. Re-run with F8_DEBUG=1 for a trace." >&2; }
trap _on_err ERR
[ "${F8_DEBUG:-0}" = "1" ] && set -x

LOCATION="${LOCATION:-westeurope}"
VM_SIZE="${VM_SIZE:-Standard_NV36ads_A10_v5}"
OS_DISK_GB="${OS_DISK_GB:-128}"
VM_NAME=f8-eval
VARIANTS="${VARIANTS:-phi4-f8-mini phi4-f8}"
EVAL_BASELINES="${EVAL_BASELINES-phi4-mini}"
EVAL_TIMEOUT="${EVAL_TIMEOUT:-60m}"

# The three time bounds have to nest: per-model cap x models + setup <= how long we wait <= when
# the VM's backstop deletes everything. They were hardcoded independently and did not nest: three
# default models at 60m each already consumed the entire 180m wait, before ~30m of GRID driver
# install, the toolchain, a Release dotnet build and ~14GB of pulls. So derive them.
_to_min(){ case "$1" in *h) echo $(( ${1%h} * 60 ));; *m) echo "${1%m}";; *s) echo $(( (${1%s} + 59) / 60 ));; *) echo "$1";; esac; }
PER_MODEL_MIN="$(_to_min "$EVAL_TIMEOUT")"
case "$PER_MODEL_MIN" in ''|*[!0-9]*) die "EVAL_TIMEOUT='$EVAL_TIMEOUT' is not a duration I can parse (use e.g. 45m or 2h)." ;; esac
MODEL_COUNT="$(printf '%s %s
' "$VARIANTS" "$EVAL_BASELINES" | wc -w | tr -d ' ')"
[ "$MODEL_COUNT" -gt 0 ] || die "nothing to evaluate: both VARIANTS and EVAL_BASELINES are empty."
SETUP_ALLOWANCE_MIN="${SETUP_ALLOWANCE_MIN:-60}"   # GRID gate (<=30m) + toolchain + Release build + pulls
WORST_CASE_MIN=$(( MODEL_COUNT * PER_MODEL_MIN + SETUP_ALLOWANCE_MIN ))
EVAL_WAIT_MIN="${EVAL_WAIT_MIN:-$WORST_CASE_MIN}"
case "$EVAL_WAIT_MIN" in ''|*[!0-9]*) die "EVAL_WAIT_MIN='$EVAL_WAIT_MIN' is not a number of minutes." ;; esac
# The backstop is derived from the wait plus an hour, so it outlives it BY CONSTRUCTION - it can
# never delete the results while we are still fetching them. What that construction cannot do is
# notice an absurd wait, so the ceiling below is the check that actually bites: without it a
# typo'd EVAL_WAIT_MIN=1000 silently provisions an 18h cost ceiling on an A10.
BACKSTOP_H=$(( (EVAL_WAIT_MIN + 60 + 59) / 60 ))
[ "$BACKSTOP_H" -ge 4 ] || BACKSTOP_H=4
MAX_BACKSTOP_H="${MAX_BACKSTOP_H:-8}"   # the fine-tune job's cap; an eval has no business exceeding it
[ "$BACKSTOP_H" -le "$MAX_BACKSTOP_H" ] || die "EVAL_WAIT_MIN=$EVAL_WAIT_MIN derives a ${BACKSTOP_H}h cost ceiling, above the ${MAX_BACKSTOP_H}h limit for this job. Lower EVAL_WAIT_MIN or EVAL_TIMEOUT, drop a model, or raise MAX_BACKSTOP_H deliberately."
USE_SPOT="${F8_SPOT:-0}"
ADMIN_USER="${ADMIN_USER:-azureuser}"
GIT_TOKEN="${GIT_TOKEN:-}"
DESTROY_ON_FAILURE="${DESTROY_ON_FAILURE:-0}"
REGISTRY="${REGISTRY:-https://registry.ollama.ai}"
ATTACH_RG="${EVAL_ATTACH_RG:-}"

b64(){ base64 -w0 2>/dev/null || base64; }

# ---- keys: BOTH halves are needed here, unlike deploy.sh --------------------------------------
# deploy.sh only ever needs the public half (it provisions and walks away). This job has to SSH
# back in to fetch the results, so a missing private key is a hard error before anything is paid
# for, not a surprise an hour later.
SSH_PUBKEY_FILE="${SSH_PUBKEY_FILE:-}"
if [ -z "$SSH_PUBKEY_FILE" ]; then
  for f in "$HOME/.ssh/id_ed25519.pub" "$HOME/.ssh/id_rsa.pub"; do [ -f "$f" ] && SSH_PUBKEY_FILE="$f" && break; done
fi
[ -n "$SSH_PUBKEY_FILE" ] && [ -f "$SSH_PUBKEY_FILE" ] || die "no SSH public key found; set SSH_PUBKEY_FILE."
SSH_KEY_FILE="${SSH_KEY_FILE:-${SSH_PUBKEY_FILE%.pub}}"
[ -f "$SSH_KEY_FILE" ] || die "no SSH PRIVATE key at '$SSH_KEY_FILE' - this job must ssh back in to fetch the results. Set SSH_KEY_FILE."
SSH_PUBKEY="$(cat "$SSH_PUBKEY_FILE")"

# OpenSSH ignores a private key whose permissions are group/other-readable. Under WSL a key on
# /mnt/c presents as 0777, so ssh would refuse it - and the first thing that needs it is the
# result FETCH, i.e. after the GPU hour is already paid for. Work from a private copy on the
# local filesystem. (Unverified locally: outbound :22 is blocked here, so this is defensive.)
KEY_TMP="$(mktemp)"; chmod 600 "$KEY_TMP"; cat "$SSH_KEY_FILE" > "$KEY_TMP"
trap 'rm -f "$KEY_TMP"' EXIT

# Ephemeral, single-use hosts: a recycled Azure IP whose old host key is in known_hosts would
# otherwise abort every connection with a spoofing warning.
SSH_OPTS=(-i "$KEY_TMP" -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null
          -o LogLevel=ERROR -o ConnectTimeout=15 -o BatchMode=yes)

command -v az >/dev/null 2>&1 || die "the Azure CLI is not on PATH."
az account show --query id -o tsv >/dev/null 2>&1 || die "not logged in: run 'az login' (and 'az account set -s <sub>')."
SUB="$(az account show --query id -o tsv)"

# ---- repo coordinates, computed HERE where git works -----------------------------------------
# Same reason as the PowerShell launcher: under WSL on /mnt/c a dubious-ownership error makes a
# `git ... || echo main` fallback train/evaluate the wrong ref silently.
origin="$(git -C "$HERE" config --get remote.origin.url 2>/dev/null || echo '')"
origin_https="$(echo "$origin" | sed -E 's#^git@github.com:#https://github.com/#; s#^ssh://git@github.com/#https://github.com/#')"
REPO_URL="${REPO_URL:-${origin_https:-https://github.com/cosh/fallen-8-core.git}}"
REPO_REF="${REPO_REF:-$(git -C "$HERE" rev-parse --abbrev-ref HEAD 2>/dev/null || echo main)}"

RESULTS_DIR="${RESULTS_DIR:-$FT/eval/results/cloud-$(date -u +%Y%m%dT%H%M%SZ)}"

# ---------------------------------------------------------------------------------------------
# Poll / fetch / teardown. Used by a fresh run and by EVAL_ATTACH_RG alike.
# ---------------------------------------------------------------------------------------------

remote_state(){ # -> "DONE" | "FAILED" | "DEAD" | "RUNNING" | "UNREACHABLE" line 1, liveness line 2
  # -n so the loop's stdin is never consumed by ssh. The unit check matters: without it a crashed
  # or SIGKILLed job is indistinguishable from a slow one, and we bill the whole window.
  ssh -n "${SSH_OPTS[@]}" "$ADMIN_USER@$IP" '
    if [ -f /opt/f8/.eval-done ]; then echo DONE
    elif [ -f /opt/f8/.eval-failed ]; then echo FAILED
    elif systemctl is-failed --quiet f8-eval.service; then echo DEAD
    elif ! systemctl is-active --quiet f8-eval.service && [ -f /var/log/f8-eval.log ]; then echo DEAD
    else echo RUNNING; fi
    tail -n 1 /var/log/f8-eval.log 2>/dev/null || true
  ' 2>/dev/null || echo UNREACHABLE
}

fetch_results(){ # copies whatever exists; returns non-zero only if nothing came down
  mkdir -p "$RESULTS_DIR"
  step "fetching results into $RESULTS_DIR ..."
  scp "${SSH_OPTS[@]}" -r "$ADMIN_USER@$IP:/opt/f8/eval-results/." "$RESULTS_DIR/" 2>/dev/null || true
  scp "${SSH_OPTS[@]}" "$ADMIN_USER@$IP:/var/log/f8-eval.log" "$RESULTS_DIR/f8-eval.log" 2>/dev/null || true
  # "Non-empty" is not "got the results": a partial scp that only brought the log down passed.
  # Require the summary AND at least one per-model result file.
  [ -s "$RESULTS_DIR/summary.md" ] && [ -s "$RESULTS_DIR/summary.json" ] || return 1
  ls "$RESULTS_DIR"/baseline-*.json >/dev/null 2>&1 || return 1
  return 0
}

delete_rg(){
  step "deleting resource group '$RG' ..."
  az group delete --name "$RG" --yes --no-wait || {
    echo "[eval-deploy] WARNING: the delete request failed. Delete it yourself:" >&2
    echo "  az group delete --name $RG --yes" >&2
    return 0
  }
  step "deletion accepted (async). Verify with: az group exists -n $RG"
}

wait_and_collect(){
  local deadline state live last_live=""
  deadline=$(( $(date +%s) + EVAL_WAIT_MIN * 60 ))
  step "waiting for the run (up to ${EVAL_WAIT_MIN}m). Watch it live with:"
  step "  ssh ${ADMIN_USER}@${IP} 'tail -f /var/log/f8-eval.log'"
  while [ "$(date +%s)" -lt "$deadline" ]; do
    local out; out="$(remote_state)"
    state="$(printf '%s' "$out" | head -n 1)"
    live="$(printf '%s' "$out" | tail -n +2 | tail -n 1)"
    case "$state" in
      DONE)
        step "the VM reports DONE."
        fetch_results || die "the run finished but NOTHING could be fetched from $IP. The VM is still up - copy /opt/f8/eval-results by hand before deleting '$RG'."
        [ -s "$RESULTS_DIR/summary.md" ] || die "fetched files but no summary.md - treating this as a failed run. The VM is still up; inspect $RESULTS_DIR and the VM."
        echo ""
        cat "$RESULTS_DIR/summary.md"
        echo ""
        step "results are in $RESULTS_DIR (a per-run directory, so your existing eval/results/baseline-*.json ledger is untouched)."
        delete_rg
        return 0
        ;;
      FAILED)
        step "the VM reports FAILED. Reason:"
        ssh "${SSH_OPTS[@]}" "$ADMIN_USER@$IP" 'cat /opt/f8/.eval-failed 2>/dev/null; echo; tail -n 40 /var/log/f8-eval.log 2>/dev/null' 2>/dev/null || true
        fetch_results || step "(nothing to fetch)"
        if [ "$DESTROY_ON_FAILURE" = "1" ]; then delete_rg; else
          step "keeping '$RG' so you can investigate. The VM's own backstop deletes it ~4h after boot."
          step "  ssh ${ADMIN_USER}@${IP} 'less /var/log/f8-eval.log'"
          step "  az group delete --name $RG --yes"
        fi
        return 33
        ;;
      DEAD)
        step "the f8-eval unit stopped without writing a marker - it died (OOM, kill, or a crash)."
        ssh -n "${SSH_OPTS[@]}" "$ADMIN_USER@$IP" 'tail -n 40 /var/log/f8-eval.log 2>/dev/null; systemctl status --no-pager f8-eval.service 2>/dev/null | head -n 15' 2>/dev/null || true
        fetch_results || step "(nothing to fetch)"
        if [ "$DESTROY_ON_FAILURE" = "1" ]; then delete_rg; else
          step "keeping '$RG' to investigate; the VM's backstop deletes it ~${BACKSTOP_H}h after boot."
        fi
        return 33
        ;;
      UNREACHABLE)
        [ "$last_live" = "" ] && step "waiting for SSH to come up ..." ;;
      *)
        if [ "$live" != "$last_live" ] && [ -n "$live" ]; then step "$live"; last_live="$live"; fi
        ;;
    esac
    sleep 30
  done
  echo "" >&2
  step "TIMEOUT after ${EVAL_WAIT_MIN}m. Nothing was deleted."
  step "Re-attach (this is also how you recover if this box slept):"
  step "  EVAL_ATTACH_RG=$RG $HERE/eval-deploy.sh"
  step "If that cannot connect, your public IP likely changed since the NSG was pinned to it:"
  step "  az network nsg rule update -g $RG --nsg-name ${VM_NAME}-nsg -n AllowSSH --source-address-prefixes \$(curl -fsS https://api.ipify.org)/32"
  return 1
}

# ---------------------------------------------------------------------------------------------
# Attach to an existing run and stop.
# ---------------------------------------------------------------------------------------------

if [ -n "$ATTACH_RG" ]; then
  RG="$ATTACH_RG"
  az group exists --name "$RG" | grep -qx true || die "resource group '$RG' does not exist (already torn down?)."
  IP="$(az network public-ip show -g "$RG" -n "${VM_NAME}-pip" --query ipAddress -o tsv)" \
    || die "could not read the public IP of '$RG'."
  step "re-attaching to $RG at $IP"
  wait_and_collect
  exit $?
fi

# ---------------------------------------------------------------------------------------------
# A fresh run.
# ---------------------------------------------------------------------------------------------

[ -n "${EVAL_PREFIX:-}" ] || die "EVAL_PREFIX is required: the VM has no local models, so it must pull them from a registry namespace (the same value you published with)."

# Nothing costs money until this passes: a variant that was never published is the cheapest
# possible failure to detect, and the same auth-free manifest GET run.sh uses after a push.
step "checking the published tags exist before creating anything..."
for v in $VARIANTS; do
  code="$(curl -sSL --connect-timeout 15 --max-time 60 -o /dev/null -w '%{http_code}' \
    "$REGISTRY/v2/$EVAL_PREFIX/$v/manifests/latest" 2>/dev/null || echo 000)"
  [ "$code" = "200" ] || die "$REGISTRY/v2/$EVAL_PREFIX/$v/manifests/latest returned HTTP $code - '$EVAL_PREFIX/$v' is not published. Publish it first, or drop it from VARIANTS."
  step "  $EVAL_PREFIX/$v: present"
done

detected_ip="$(curl -fsS https://api.ipify.org 2>/dev/null || true)"
ALLOWED_SSH_CIDR="${ALLOWED_SSH_CIDR:-${detected_ip:+$detected_ip/32}}"
ALLOWED_SSH_CIDR="${ALLOWED_SSH_CIDR:-*}"
[ "$ALLOWED_SSH_CIDR" = "*" ] && echo "[eval-deploy] WARNING: could not detect your public IP; opening SSH to 0.0.0.0/0 (key-only auth)." >&2

RAND="$(od -An -N3 -tx1 /dev/urandom | tr -d ' \n')"
RG="rg-f8-eval-$RAND"

echo ""
echo "  job            : evaluate published models on a GPU VM, fetch results, tear down"
echo "  resource group : $RG  (in $LOCATION, VM $VM_SIZE, spot=$USE_SPOT)"
echo "  repo           : $REPO_URL @ $REPO_REF"
echo "  pulling        : $EVAL_PREFIX/{$(echo "$VARIANTS" | tr ' ' ',')}"
echo "  baselines      : ${EVAL_BASELINES:-<none>}"
echo "  results        : $RESULTS_DIR"
echo "  time budget    : ${MODEL_COUNT} model(s) x ${PER_MODEL_MIN}m + ${SETUP_ALLOWANCE_MIN}m setup = ${WORST_CASE_MIN}m worst case;"
echo "                   waiting ${EVAL_WAIT_MIN}m, the VM self-reaps at ${BACKSTOP_H}h"
echo ""

BOOTSTRAP_B64="$(b64 < "$HERE/bootstrap-eval.sh")"
TEARDOWN_B64="$(b64 < "$HERE/teardown.sh")"

# eval-run.sh is deliberately NOT injected: the VM clones the repo, so it runs the version on
# REPO_REF. That keeps the evaluation logic in one home and keeps customData well under ARM's
# ~64KB cap. teardown.sh IS injected, because the EXIT trap needs it even if the clone fails.
CLOUD_INIT="$(cat <<EOF
#cloud-config
write_files:
  - path: /etc/f8-eval.env
    permissions: '0600'
    content: |
      REPO_URL="${REPO_URL}"
      REPO_REF="${REPO_REF}"
      EVAL_PREFIX="${EVAL_PREFIX}"
      VARIANTS="${VARIANTS}"
      EVAL_BASELINES="${EVAL_BASELINES}"
      EVAL_TIMEOUT="${EVAL_TIMEOUT}"
      REGISTRY="${REGISTRY}"
      GIT_TOKEN="${GIT_TOKEN}"
      AZ_RESOURCE_GROUP="${RG}"
      AZ_SUBSCRIPTION="${SUB}"
      DESTROY_ON_FINISH="0"
      DESTROY_ON_FAILURE="0"
      F8_DEBUG="${F8_DEBUG:-0}"
  - path: /opt/f8/bootstrap-eval.sh
    permissions: '0755'
    encoding: b64
    content: ${BOOTSTRAP_B64}
  - path: /opt/f8/teardown.sh
    permissions: '0755'
    encoding: b64
    content: ${TEARDOWN_B64}
  - path: /etc/systemd/system/f8-eval.service
    permissions: '0644'
    content: |
      [Unit]
      Description=F8 published-model evaluation
      After=network-online.target
      Wants=network-online.target
      [Service]
      Type=oneshot
      RemainAfterExit=yes
      ExecStart=/opt/f8/bootstrap-eval.sh
      TimeoutStartSec=0
      [Install]
      WantedBy=multi-user.target
  - path: /etc/systemd/system/f8-teardown.service
    permissions: '0644'
    content: |
      [Unit]
      Description=F8 cost backstop - delete the resource group unconditionally
      After=network-online.target
      Wants=network-online.target
      [Service]
      Type=oneshot
      EnvironmentFile=-/etc/f8-eval.env
      Environment=F8_BACKSTOP=1
      ExecStart=/opt/f8/teardown.sh
  - path: /etc/systemd/system/f8-teardown.timer
    permissions: '0644'
    content: |
      [Unit]
      Description=Fire the F8 teardown backstop ${BACKSTOP_H}h after boot (caps an abandoned run; derived from the wait)
      [Timer]
      OnBootSec=${BACKSTOP_H}h
      Unit=f8-teardown.service
      [Install]
      WantedBy=timers.target
runcmd:
  - mkdir -p /opt/f8
  - apt-get -o DPkg::Lock::Timeout=600 update -y
  - DEBIAN_FRONTEND=noninteractive apt-get -o DPkg::Lock::Timeout=600 install -y curl jq
  - systemctl daemon-reload
  - systemctl enable --now f8-teardown.timer
  - systemctl enable f8-eval.service
  - systemctl start --no-block f8-eval.service
EOF
)"
CUSTOM_DATA="$(printf '%s' "$CLOUD_INIT" | b64)"

step "creating resource group $RG in $LOCATION..."
az group create --name "$RG" --location "$LOCATION" -o none
step "submitting deployment (VM + network; the GRID driver installs on the VM)..."
if ! az deployment group create \
  --resource-group "$RG" \
  --name main \
  --template-file "$HERE/main.bicep" \
  --parameters \
      location="$LOCATION" \
      vmName="$VM_NAME" \
      vmSize="$VM_SIZE" \
      osDiskSizeGb="$OS_DISK_GB" \
      adminUsername="$ADMIN_USER" \
      adminSshPublicKey="$SSH_PUBKEY" \
      customData="$CUSTOM_DATA" \
      allowedSshCidr="$ALLOWED_SSH_CIDR" \
      useSpot=$([ "$USE_SPOT" = "1" ] && echo true || echo false) \
  -o none; then
  echo "" >&2
  step "deployment FAILED. ARM provisioning errors (a quota problem names itself here):"
  az deployment operation group list -g "$RG" --name main \
    --query "[?properties.provisioningState=='Failed'].{resource:properties.targetResource.resourceType, code:properties.statusCode, message:properties.statusMessage}" \
    -o jsonc 2>/dev/null || step "(could not fetch operation details)"
  step "deleting the empty resource group '$RG' so nothing lingers."
  az group delete --name "$RG" --yes --no-wait 2>/dev/null || true
  exit 1
fi
step "deployment succeeded - VM created."

IP="$(az network public-ip show -g "$RG" -n "${VM_NAME}-pip" --query ipAddress -o tsv)"
step "VM at $IP"
wait_and_collect
