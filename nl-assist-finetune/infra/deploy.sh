#!/usr/bin/env bash
# MIT License
#
# One-shot deploy of the phi4 fine-tune VM (Azure A10, on-demand by default). Creates a
# DEDICATED resource group, provisions the VM with cloud-init that installs everything, trains
# every variant in VARIANTS (default: phi4-f8-mini + phi4-f8, in ONE session sharing the
# dataset), publishes each to Ollama, then DELETES the whole resource group itself. You watch.
#
# Prereqs on THIS machine: az CLI (logged in: `az login`), an SSH public key, and - for the
# unattended Ollama push - your Ollama signing key at ~/.ollama/id_ed25519 (the one whose
# public half is registered at https://ollama.com/settings/keys).
#
# Usage:
#   PUBLISH_PREFIX=<your-namespace> ./deploy.sh    # publishes <ns>/phi4-f8-mini and <ns>/phi4-f8
# Common overrides (env vars):
#   LOCATION           Azure region (default westeurope). Needs NVadsA10v5 capacity + quota.
#   VM_SIZE            default Standard_NV36ads_A10_v5 (full A10, 24GB)
#   VARIANTS           space-separated (default "phi4-f8-mini phi4-f8"); trained in one session
#   F8_SPOT            0 on-demand (default) | 1 Spot (needs Total Regional Spot vCPU quota)
#   REPO_URL/REPO_REF  git repo + branch to train from (default: this repo's origin + branch)
#   GIT_TOKEN          GitHub token if REPO_URL is private
#   OLLAMA_KEY_FILE    default ~/.ollama/id_ed25519 (omit/absent -> push is skipped)
#   ALLOWED_SSH_CIDR   who may SSH in to watch (default: this machine's public IP /32)
#   DESTROY_ON_FINISH  1 (default) self-destruct after the run; 0 keeps the VM for debugging
#   KEEP_RG            0 (default); set 1 to keep the RG name stable across re-runs
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# base64 with no line wrapping, portable across GNU (-w0) and BSD/macOS base64.
b64(){ base64 -w0 2>/dev/null || base64 | tr -d '\n'; }

# `az deployment ... --template-file *.bicep` runs the Bicep CLI, a .NET single-file binary that
# extracts bundled files to DOTNET_BUNDLE_EXTRACT_BASE_DIR. Pin it to a writable dir under THIS
# user's HOME so a stale/global value (e.g. pointing at another user's home) can't break the
# deploy with "Failure processing application bundle ... Error code: 13".
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="$HOME/.cache/f8-dotnet-bundle"
mkdir -p "$DOTNET_BUNDLE_EXTRACT_BASE_DIR" 2>/dev/null || true

# Loud, debuggable failures - never exit silently. ERR fires on any set -e abort; F8_DEBUG=1 traces.
step(){ echo "[deploy] $*"; }
_on_err(){ echo "[deploy] ERROR: a command failed (exit $?) around line $LINENO - see above. Re-run with F8_DEBUG=1 for a full trace." >&2; }
trap _on_err ERR
[ "${F8_DEBUG:-0}" = "1" ] && set -x

LOCATION="${LOCATION:-westeurope}"
VM_SIZE="${VM_SIZE:-Standard_NV36ads_A10_v5}"
VARIANTS="${VARIANTS:-phi4-f8-mini phi4-f8}"   # space-separated; trained together in one session
PUBLISH_PREFIX="${PUBLISH_PREFIX:-}"           # each variant publishes to $PUBLISH_PREFIX/<variant>
USE_SPOT="${F8_SPOT:-0}"                        # 0 = on-demand (default), 1 = Spot
GIT_TOKEN="${GIT_TOKEN:-}"
DESTROY_ON_FINISH="${DESTROY_ON_FINISH:-1}"
ADMIN_USER="${ADMIN_USER:-azureuser}"

# Repo defaults from this checkout; normalize git@github.com:owner/repo.git -> https URL.
origin="$(git -C "$HERE" config --get remote.origin.url 2>/dev/null || echo '')"
origin_https="$(echo "$origin" | sed -E 's#^git@github.com:#https://github.com/#; s#^ssh://git@github.com/#https://github.com/#')"
REPO_URL="${REPO_URL:-${origin_https:-https://github.com/cosh/fallen-8-core.git}}"
REPO_REF="${REPO_REF:-$(git -C "$HERE" rev-parse --abbrev-ref HEAD 2>/dev/null || echo main)}"

# SSH public key.
SSH_PUBKEY_FILE="${SSH_PUBKEY_FILE:-}"
if [ -z "$SSH_PUBKEY_FILE" ]; then
  for f in "$HOME/.ssh/id_ed25519.pub" "$HOME/.ssh/id_rsa.pub"; do [ -f "$f" ] && SSH_PUBKEY_FILE="$f" && break; done
fi
[ -n "$SSH_PUBKEY_FILE" ] && [ -f "$SSH_PUBKEY_FILE" ] || { echo "ERROR: no SSH public key found; set SSH_PUBKEY_FILE=..." >&2; exit 1; }
SSH_PUBKEY="$(cat "$SSH_PUBKEY_FILE")"

# Ollama signing key (optional; enables the unattended push).
OLLAMA_KEY_FILE="${OLLAMA_KEY_FILE:-$HOME/.ollama/id_ed25519}"

# Restrict SSH to this machine's public IP unless overridden.
ALLOWED_SSH_CIDR="${ALLOWED_SSH_CIDR:-$(curl -fsS https://api.ipify.org 2>/dev/null | sed 's#$#/32#')}"
ALLOWED_SSH_CIDR="${ALLOWED_SSH_CIDR:-*}"
[ "$ALLOWED_SSH_CIDR" = "*" ] && echo "WARNING: could not detect your public IP; opening SSH to 0.0.0.0/0 (key-only auth). Set ALLOWED_SSH_CIDR to restrict." >&2

SUB="$(az account show --query id -o tsv)"
if [ "${KEEP_RG:-0}" = "1" ]; then RG="${F8_RG:-rg-f8-finetune}"; else RG="${F8_RG:-rg-f8-finetune-$(openssl rand -hex 3)}"; fi

PRICING="$([ "$USE_SPOT" = "1" ] && echo Spot || echo on-demand)"
echo "== plan =="
echo "  resource group : $RG   (self-destructs after the run: DESTROY_ON_FINISH=$DESTROY_ON_FINISH)"
echo "  location/size  : $LOCATION / $VM_SIZE  ($PRICING)"
echo "  repo           : $REPO_URL @ $REPO_REF"
echo "  variants       : $VARIANTS"
echo "  publish prefix : ${PUBLISH_PREFIX:-<none - push skipped>}"
echo "  ssh from       : $ALLOWED_SSH_CIDR"
echo ""

# ---- preflight: vCPU quota --------------------------------------------------------------
# Turns Azure's cryptic ARM "QuotaExceeded" preflight error into a clear, early message with
# the fix - and avoids leaving an empty RG behind. Best-effort: only blocks if it can PROVE a
# shortfall (skips silently if az/jq don't return numbers). On-demand needs BOTH "Total Regional
# vCPUs" and the NVadsA10v5 family quota; Spot needs "Total Regional Spot vCPUs".
if command -v jq >/dev/null 2>&1 && command -v az >/dev/null 2>&1; then
trap - ERR; set +e   # best-effort: transient az/jq failures here must NEVER abort or noise-up the deploy
CORES="$(az vm list-skus -l "$LOCATION" --resource-type virtualMachines -o json 2>/dev/null \
  | jq -r --arg s "$VM_SIZE" '.[]|select(.name==$s)|.capabilities[]|select(.name=="vCPUs")|.value' 2>/dev/null | head -1)"
if [ -n "${CORES:-}" ]; then
  step "preflight: $VM_SIZE = $CORES vCPUs; checking $LOCATION quota (spot=$USE_SPOT)..."
  usage="$(az vm list-usage -l "$LOCATION" -o json 2>/dev/null || echo '[]')"
  short=""
  q_check(){ # $1 = jq object-selector  $2 = human label
    local u l
    l="$(echo "$usage" | jq -r "($1 | .limit)        // empty" 2>/dev/null | head -1)"
    u="$(echo "$usage" | jq -r "($1 | .currentValue) // empty" 2>/dev/null | head -1)"
    if [ -n "$l" ] && [ -n "$u" ] && [ "$((l - u))" -lt "$CORES" ]; then
      short="${short}
  - $2: $u/$l used/limit ($((l - u)) free, need $CORES)"
    fi
  }
  if [ "$USE_SPOT" = "1" ]; then
    q_check '.[]|select((.name.value|ascii_downcase)=="lowprioritycores")' 'Total Regional Spot vCPUs'
  else
    q_check '.[]|select(.name.value=="cores")' 'Total Regional vCPUs'
    q_check '.[]|select(.name.localizedValue|test("NVadsA10v5";"i"))' 'Standard NVadsA10v5 Family vCPUs'
  fi
  if [ -n "$short" ]; then
    cat >&2 <<MSG
ERROR: not enough vCPU quota in $LOCATION for $VM_SIZE ($CORES cores; 14B QLoRA needs the full A10).
  Short:$short
  Request an increase, then re-run this script -
  https://portal.azure.com/#view/Microsoft_Azure_Capacity/QuotaMenuBlade/~/myQuotas
    - on-demand (default): raise BOTH "Total Regional vCPUs" and "Standard NVadsA10v5 Family vCPUs" to >= $CORES
    - Spot (F8_SPOT=1):     raise "Total Regional Spot vCPUs" to >= $CORES
MSG
    exit 1
  fi
  step "preflight: quota sufficient ($CORES vCPUs needed)."
else
  step "preflight: skipped (couldn't read the SKU's vCPU count from az - deploy proceeds anyway)."
fi
set -e; trap _on_err ERR   # re-enable errexit + error trap after the best-effort preflight
else
  step "preflight: skipped (jq or the az CLI not found on this box - quota not checked; deploy proceeds)."
fi

# ---- assemble cloud-init ----------------------------------------------------------------
BOOTSTRAP_B64="$(b64 < "$HERE/bootstrap.sh")"
TEARDOWN_B64="$(b64 < "$HERE/teardown.sh")"

ollama_files=""
if [ -n "$PUBLISH_PREFIX" ]; then
  # Publishing is intended (PUBLISH_PREFIX set) -> a missing/empty key is a HARD error here, before
  # we spend on a VM: without an injected key the VM's ollama would sign a push with a fresh,
  # UNREGISTERED key and upload nothing (the incident that lost a trained model). Unset
  # PUBLISH_PREFIX to run train-only.
  if [ ! -s "$OLLAMA_KEY_FILE" ]; then
    echo "ERROR: PUBLISH_PREFIX='$PUBLISH_PREFIX' is set but there is no non-empty Ollama signing key at" >&2
    echo "OLLAMA_KEY_FILE=$OLLAMA_KEY_FILE. Point it at your registered key (public half at" >&2
    echo "https://ollama.com/settings/keys), or unset PUBLISH_PREFIX to run train-only." >&2
    exit 1
  fi
  KEY_B64="$(b64 < "$OLLAMA_KEY_FILE")"
  PUB_B64="$( [ -f "${OLLAMA_KEY_FILE}.pub" ] && b64 < "${OLLAMA_KEY_FILE}.pub" || echo '')"
  ollama_files="  - path: /opt/f8/ollama_id_ed25519
    permissions: '0600'
    encoding: b64
    content: ${KEY_B64}
"
  [ -n "$PUB_B64" ] && ollama_files="${ollama_files}  - path: /opt/f8/ollama_id_ed25519.pub
    permissions: '0644'
    encoding: b64
    content: ${PUB_B64}
"
  [ -n "$PUB_B64" ] || step "NOTE: no ${OLLAMA_KEY_FILE}.pub found; the VM's key-identity preflight will be skipped (push still verified post-hoc)."
  step "using Ollama key $OLLAMA_KEY_FILE for the unattended push to '$PUBLISH_PREFIX/*' - its public"
  step "half MUST be registered at https://ollama.com/settings/keys (the VM preflight enforces this)."
fi

CLOUD_INIT="$(cat <<EOF
#cloud-config
write_files:
  - path: /etc/f8-finetune.env
    permissions: '0600'
    content: |
      REPO_URL="${REPO_URL}"
      REPO_REF="${REPO_REF}"
      VARIANTS="${VARIANTS}"
      PUBLISH_PREFIX="${PUBLISH_PREFIX}"
      GIT_TOKEN="${GIT_TOKEN}"
      AZ_RESOURCE_GROUP="${RG}"
      AZ_SUBSCRIPTION="${SUB}"
      DESTROY_ON_FINISH="${DESTROY_ON_FINISH}"
      DESTROY_ON_FAILURE="${DESTROY_ON_FAILURE:-0}"
      F8_DEBUG="${F8_DEBUG:-0}"
  - path: /opt/f8/bootstrap.sh
    permissions: '0755'
    encoding: b64
    content: ${BOOTSTRAP_B64}
  - path: /opt/f8/teardown.sh
    permissions: '0755'
    encoding: b64
    content: ${TEARDOWN_B64}
${ollama_files}  - path: /etc/systemd/system/f8-finetune.service
    permissions: '0644'
    content: |
      [Unit]
      Description=F8 phi4 finetune, publish, and self-destroy
      After=network-online.target
      Wants=network-online.target
      [Service]
      Type=oneshot
      RemainAfterExit=yes
      ExecStart=/opt/f8/bootstrap.sh
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
      ExecStart=/opt/f8/teardown.sh
  - path: /etc/systemd/system/f8-teardown.timer
    permissions: '0644'
    content: |
      [Unit]
      Description=Fire the F8 teardown backstop 6h after boot (survives a hung/SIGKILLed bootstrap)
      [Timer]
      OnBootSec=6h
      Unit=f8-teardown.service
      [Install]
      WantedBy=timers.target
runcmd:
  - mkdir -p /opt/f8
  - apt-get -o DPkg::Lock::Timeout=600 update -y
  - DEBIAN_FRONTEND=noninteractive apt-get -o DPkg::Lock::Timeout=600 install -y curl jq
  - systemctl daemon-reload
  - systemctl enable --now f8-teardown.timer
  - systemctl enable f8-finetune.service
  - systemctl start --no-block f8-finetune.service
EOF
)"
CUSTOM_DATA="$(printf '%s' "$CLOUD_INIT" | b64)"

# ---- deploy -----------------------------------------------------------------------------
step "creating resource group $RG in $LOCATION..."
az group create --name "$RG" --location "$LOCATION" -o none
step "submitting deployment 'main' (VM + network; the A10 GRID driver installs on the VM in bootstrap); this can take a few minutes..."
if ! az deployment group create \
  --resource-group "$RG" \
  --name main \
  --template-file "$HERE/main.bicep" \
  --parameters \
      location="$LOCATION" \
      vmSize="$VM_SIZE" \
      adminUsername="$ADMIN_USER" \
      adminSshPublicKey="$SSH_PUBKEY" \
      customData="$CUSTOM_DATA" \
      allowedSshCidr="$ALLOWED_SSH_CIDR" \
      useSpot=$([ "$USE_SPOT" = "1" ] && echo true || echo false) \
  -o none; then
  echo "" >&2
  step "deployment FAILED. ARM provisioning errors:"
  az deployment operation group list -g "$RG" --name main \
    --query "[?properties.provisioningState=='Failed'].{resource:properties.targetResource.resourceType, code:properties.statusCode, message:properties.statusMessage}" \
    -o jsonc 2>/dev/null || step "(could not fetch operation details)"
  step "deleting the empty resource group '$RG' so nothing lingers."
  az group delete --name "$RG" --yes --no-wait 2>/dev/null || true
  exit 1
fi
step "deployment succeeded - VM created."

IP="$(az network public-ip show -g "$RG" -n f8-finetune-pip --query ipAddress -o tsv)"
echo ""
echo "== deployed. the VM installs, trains [$VARIANTS], publishes, then deletes RG '$RG'. =="
echo "watch progress (wait ~1-2 min for cloud-init to start the log):"
echo "  ssh ${ADMIN_USER}@${IP} 'tail -f /var/log/f8-finetune.log'"
echo "if you need to stop early / clean up yourself:"
echo "  az group delete --name $RG --yes"
