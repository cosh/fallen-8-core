#!/usr/bin/env bash
# MIT License
#
# One-shot deploy of the phi4 fine-tune VM (Azure A10, Spot). Creates a DEDICATED resource
# group, provisions the VM with cloud-init that installs everything, trains VARIANT (default
# phi4-f8), publishes to Ollama, then DELETES the whole resource group itself. You just watch.
#
# Prereqs on THIS machine: az CLI (logged in: `az login`), an SSH public key, and - for the
# unattended Ollama push - your Ollama signing key at ~/.ollama/id_ed25519 (the one whose
# public half is registered at https://ollama.com/settings/keys).
#
# Usage:
#   PUBLISH_REPO=<your-namespace>/phi4-f8 ./deploy.sh
# Common overrides (env vars):
#   LOCATION           Azure region (default westeurope). Needs NVadsA10v5 Spot capacity.
#   VM_SIZE            default Standard_NV36ads_A10_v5 (full A10, 24GB)
#   REPO_URL/REPO_REF  git repo + branch to train from (default: this repo's origin + branch)
#   GIT_TOKEN          GitHub token if REPO_URL is private
#   VARIANT            phi4-f8 (default) | phi4-f8-mini
#   OLLAMA_KEY_FILE    default ~/.ollama/id_ed25519 (omit/absent -> push is skipped)
#   ALLOWED_SSH_CIDR   who may SSH in to watch (default: this machine's public IP /32)
#   DESTROY_ON_FINISH  1 (default) self-destruct after the run; 0 keeps the VM for debugging
#   KEEP_RG            0 (default); set 1 to keep the RG name stable across re-runs
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# base64 with no line wrapping, portable across GNU (-w0) and BSD/macOS base64.
b64(){ base64 -w0 2>/dev/null || base64 | tr -d '\n'; }

LOCATION="${LOCATION:-westeurope}"
VM_SIZE="${VM_SIZE:-Standard_NV36ads_A10_v5}"
VARIANT="${VARIANT:-phi4-f8}"
PUBLISH_REPO="${PUBLISH_REPO:-}"
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

echo "== plan =="
echo "  resource group : $RG   (self-destructs after the run: DESTROY_ON_FINISH=$DESTROY_ON_FINISH)"
echo "  location/size  : $LOCATION / $VM_SIZE (Spot)"
echo "  repo           : $REPO_URL @ $REPO_REF"
echo "  variant        : $VARIANT"
echo "  publish        : ${PUBLISH_REPO:-<none - push skipped>}"
echo "  ssh from        : $ALLOWED_SSH_CIDR"
echo ""

# ---- preflight: Spot (low-priority) vCPU quota ------------------------------------------
# Turns Azure's cryptic ARM "QuotaExceeded / LowPriorityCores" preflight error into a clear,
# early message with the fix - and avoids leaving an empty RG behind. Best-effort: only blocks
# if it can PROVE a shortfall (skips silently if the az/jq queries don't return numbers).
CORES="$(az vm list-skus -l "$LOCATION" --resource-type virtualMachines -o json 2>/dev/null \
  | jq -r --arg s "$VM_SIZE" '.[]|select(.name==$s)|.capabilities[]|select(.name=="vCPUs")|.value' 2>/dev/null | head -1)"
if [ -n "${CORES:-}" ]; then
  usage="$(az vm list-usage -l "$LOCATION" -o json 2>/dev/null || echo '[]')"
  lim="$(echo "$usage"  | jq -r '(.[]|select((.name.value|ascii_downcase)=="lowprioritycores")|.limit)        // empty')"
  used="$(echo "$usage" | jq -r '(.[]|select((.name.value|ascii_downcase)=="lowprioritycores")|.currentValue) // empty')"
  if [ -n "$lim" ] && [ -n "$used" ] && [ "$((lim - used))" -lt "$CORES" ]; then
    cat >&2 <<MSG
ERROR: not enough Spot (low-priority) vCPU quota in $LOCATION for $VM_SIZE.
  Need $CORES cores; your low-priority quota is $used/$lim (used/limit). 14B QLoRA needs the
  FULL A10 (24GB) = $VM_SIZE = $CORES cores, so a smaller SKU won't fit.
  Fix (then re-run this script):
    Portal -> Quotas -> Compute -> region "$LOCATION" -> "Total Regional Low-priority vCPUs"
    -> request a limit >= $CORES.  https://portal.azure.com/#view/Microsoft_Azure_Capacity/QuotaMenuBlade/~/myQuotas
  Or: run on-demand instead of Spot (edit main.bicep: priority=Regular + drop billingProfile;
  needs the dedicated "Standard NVadsA10v5 Family vCPUs" quota), or set LOCATION=<region with quota>.
MSG
    exit 1
  fi
fi

# ---- assemble cloud-init ----------------------------------------------------------------
BOOTSTRAP_B64="$(b64 < "$HERE/bootstrap.sh")"
TEARDOWN_B64="$(b64 < "$HERE/teardown.sh")"

ollama_files=""
if [ -n "$PUBLISH_REPO" ] && [ -f "$OLLAMA_KEY_FILE" ]; then
  KEY_B64="$(b64 < "$OLLAMA_KEY_FILE")"
  PUB_B64="$( [ -f "${OLLAMA_KEY_FILE}.pub" ] && b64 < "${OLLAMA_KEY_FILE}.pub" || echo '')"
  ollama_files="  - path: /root/.ollama/id_ed25519
    permissions: '0600'
    encoding: b64
    content: ${KEY_B64}
"
  [ -n "$PUB_B64" ] && ollama_files="${ollama_files}  - path: /root/.ollama/id_ed25519.pub
    permissions: '0644'
    encoding: b64
    content: ${PUB_B64}
"
elif [ -n "$PUBLISH_REPO" ]; then
  echo "WARNING: PUBLISH_REPO set but no key at $OLLAMA_KEY_FILE - the push will be skipped on the VM." >&2
fi

CLOUD_INIT="$(cat <<EOF
#cloud-config
write_files:
  - path: /etc/f8-finetune.env
    permissions: '0600'
    content: |
      REPO_URL=${REPO_URL}
      REPO_REF=${REPO_REF}
      VARIANT=${VARIANT}
      PUBLISH_REPO=${PUBLISH_REPO}
      GIT_TOKEN=${GIT_TOKEN}
      AZ_RESOURCE_GROUP=${RG}
      AZ_SUBSCRIPTION=${SUB}
      DESTROY_ON_FINISH=${DESTROY_ON_FINISH}
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
      Description=Fire the F8 teardown backstop 8h after boot (survives a hung/SIGKILLed bootstrap)
      [Timer]
      OnBootSec=8h
      Unit=f8-teardown.service
      [Install]
      WantedBy=timers.target
runcmd:
  - mkdir -p /opt/f8 /root/.ollama
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
az group create --name "$RG" --location "$LOCATION" -o none
echo "deploying VM (this returns once the VM is created; the run then proceeds on the VM)..."
if ! az deployment group create \
  --resource-group "$RG" \
  --template-file "$HERE/main.bicep" \
  --parameters \
      location="$LOCATION" \
      vmSize="$VM_SIZE" \
      adminUsername="$ADMIN_USER" \
      adminSshPublicKey="$SSH_PUBKEY" \
      customData="$CUSTOM_DATA" \
      allowedSshCidr="$ALLOWED_SSH_CIDR" \
  -o none; then
  echo "" >&2
  echo "deployment failed - deleting the empty resource group '$RG' so nothing lingers." >&2
  az group delete --name "$RG" --yes --no-wait 2>/dev/null || true
  exit 1
fi

IP="$(az network public-ip show -g "$RG" -n f8-finetune-pip --query ipAddress -o tsv)"
echo ""
echo "== deployed. the VM is now installing + training + publishing, then it deletes RG '$RG'. =="
echo "watch progress (wait ~1-2 min for cloud-init to start the log):"
echo "  ssh ${ADMIN_USER}@${IP} 'tail -f /var/log/f8-finetune.log'"
echo "if you need to stop early / clean up yourself:"
echo "  az group delete --name $RG --yes"
