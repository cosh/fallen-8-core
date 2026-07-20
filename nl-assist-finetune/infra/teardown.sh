#!/usr/bin/env bash
# MIT License
#
# Deletes THIS VM's own resource group using the VM's system-assigned managed identity, via
# the instance-metadata token endpoint + the ARM REST API. Deliberately depends on nothing but
# curl + jq (installed by cloud-init before anything else) - NOT the Azure CLI - so teardown
# still works even if the main bootstrap failed to install its toolchain. Invoked both by
# bootstrap.sh's exit trap and by the f8-teardown.timer backstop (fires even if bootstrap hangs
# and is SIGKILLed, where a bash trap would not run).
set -uo pipefail

. /etc/f8-finetune.env 2>/dev/null || true
: "${AZ_SUBSCRIPTION:=}"; : "${AZ_RESOURCE_GROUP:=}"; : "${DESTROY_ON_FINISH:=1}"
# Honor debug mode so the backstop timer doesn't delete a VM you deliberately kept.
[ "$DESTROY_ON_FINISH" = "1" ] || { echo "[teardown] DESTROY_ON_FINISH != 1 -> keeping resources (debug mode)."; exit 0; }
[ -n "$AZ_SUBSCRIPTION" ] && [ -n "$AZ_RESOURCE_GROUP" ] || { echo "[teardown] missing AZ_SUBSCRIPTION/AZ_RESOURCE_GROUP"; exit 1; }

tok="$(curl -s --max-time 30 -H 'Metadata:true' \
  'http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource=https%3A%2F%2Fmanagement.azure.com%2F' \
  | jq -r '.access_token // empty')"
[ -n "$tok" ] || { echo "[teardown] no managed-identity token; delete manually: az group delete -n $AZ_RESOURCE_GROUP --yes"; exit 1; }

echo "[teardown] DELETE resource group '$AZ_RESOURCE_GROUP' via ARM REST"
curl -s --max-time 60 -X DELETE \
  -H "Authorization: Bearer $tok" \
  "https://management.azure.com/subscriptions/${AZ_SUBSCRIPTION}/resourcegroups/${AZ_RESOURCE_GROUP}?api-version=2021-04-01&forceDeletionTypes=Microsoft.Compute/virtualMachines" \
  -o /dev/null -w "[teardown] ARM returned HTTP %{http_code} (202 = accepted, deletion proceeds async)\n"
