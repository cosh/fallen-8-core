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

# Both jobs' env files, because this script is shared: the fine-tune job writes
# /etc/f8-finetune.env, the eval job writes /etc/f8-eval.env. Sourcing only the first one made
# the eval job's backstop timer exit 1 on empty AZ_* - i.e. an abandoned GPU VM was never
# reaped, while every doc promised it was.
for _envfile in /etc/f8-finetune.env /etc/f8-eval.env; do
  # shellcheck disable=SC1090
  [ -f "$_envfile" ] && . "$_envfile" 2>/dev/null
done
: "${AZ_SUBSCRIPTION:=}"; : "${AZ_RESOURCE_GROUP:=}"; : "${DESTROY_ON_FINISH:=1}"
: "${F8_BACKSTOP:=0}"
# Honor debug mode so the backstop timer doesn't delete a VM you deliberately kept - EXCEPT when
# invoked as the unconditional cost backstop (F8_BACKSTOP=1, set by that systemd unit only). The
# eval job runs with DESTROY_ON_FINISH=0 as its NORMAL state, because its launch box tears down
# after fetching the results, so for that job "0" must not disarm the cost cap. To keep an eval
# VM past the timer deliberately: systemctl stop f8-teardown.timer.
if [ "$F8_BACKSTOP" = "1" ]; then
  echo "[teardown] invoked as the unconditional cost backstop; DESTROY_ON_FINISH=$DESTROY_ON_FINISH is ignored."
else
  [ "$DESTROY_ON_FINISH" = "1" ] || { echo "[teardown] DESTROY_ON_FINISH != 1 -> keeping resources (debug mode)."; exit 0; }
fi
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
