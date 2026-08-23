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
#   F8_SPOT          1 = Spot. NOT recommended: an evicted Spot VM is deallocated, and a
#                    deallocated VM runs no timers, so its own teardown backstop cannot fire.
#   EVAL_WAIT_MIN    how long to wait for the run. Default: DERIVED as models x per-model cap
#                    plus a setup allowance, printed before anything is created.
#   EVAL_TIMEOUT     per-model cap passed through to eval-run.sh, default 60m
#   RESULTS_DIR      where to put the fetched results; default
#                    nl-assist-finetune/eval/results/cloud-<UTC stamp>/
#   DESTROY_ON_FAILURE  1 = delete the RG even when the eval failed (default 0: keep it so the
#                    VM's log is readable). The VM's backstop is DERIVED, not 4h - see the budget
#                    block below - and if this script stopped that timer it re-arms it first.
#   REPO_URL/REPO_REF, SSH_PUBKEY_FILE, SSH_KEY_FILE, ADMIN_USER, ALLOWED_SSH_CIDR, GIT_TOKEN,
#   F8_DEBUG         as in deploy.sh.
set -euo pipefail

# Bash reads a script INCREMENTALLY from a byte offset, so editing this file - or a git pull, or a
# branch switch - WHILE it runs makes the interpreter resume at the wrong place and die on garbage.
# Measured 2026-08-23: an edit mid-run produced "syntax error near unexpected token '('" AFTER the
# evaluation had completed, the results were fetched and the group was deleted, turning a clean run
# into a non-zero exit. This job waits for hours, which is ample time for exactly that. So re-exec
# once from an immutable copy; only the launch-box script needs this, because the VM-side scripts
# run from a clone nobody touches.
if [ "${F8_SELF_COPY:-0}" != "1" ]; then
  F8_SELF_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  _copy="$(mktemp)"
  cat "${BASH_SOURCE[0]}" > "$_copy"
  export F8_SELF_COPY=1 F8_SELF_DIR F8_SELF_TMP="$_copy"
  exec bash "$_copy" "$@"
fi

# From the copy, BASH_SOURCE points at /tmp, so the real directory arrives by env.
HERE="${F8_SELF_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)}"
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
# Validate the digits BEFORE any arithmetic: feeding unvalidated text into $(( )) means bash
# reports its own syntax error and exits, so the friendly message below was unreachable for
# exactly the inputs it was written for.
_to_min(){
  local v="$1" n
  case "$v" in
    *h) n="${v%h}"; case "$n" in ''|*[!0-9]*) echo ""; return;; esac; echo $(( n * 60 )) ;;
    *m) n="${v%m}"; case "$n" in ''|*[!0-9]*) echo ""; return;; esac; echo "$n" ;;
    *s) n="${v%s}"; case "$n" in ''|*[!0-9]*) echo ""; return;; esac; echo $(( (n + 59) / 60 )) ;;
    *)  case "$v" in ''|*[!0-9]*) echo ""; return;; esac; echo "$v" ;;
  esac
}
PER_MODEL_MIN="$(_to_min "$EVAL_TIMEOUT")"
case "$PER_MODEL_MIN" in ''|*[!0-9]*) die "EVAL_TIMEOUT='$EVAL_TIMEOUT' is not a duration I can parse (use e.g. 45m or 2h)." ;; esac
MODEL_COUNT="$(printf '%s %s
' "$VARIANTS" "$EVAL_BASELINES" | wc -w | tr -d ' ')"
[ "$MODEL_COUNT" -gt 0 ] || die "nothing to evaluate: both VARIANTS and EVAL_BASELINES are empty."
# What this has to cover before the first draft is generated: the GRID driver gate (up to 30m by
# its own loop), apt plus install-prereqs.sh, a cold NuGet restore and Release build of the apiApp
# (up to 10m by its own gate), and pulling ~14GB of models. 60m was optimistic for the sum.
SETUP_ALLOWANCE_MIN="${SETUP_ALLOWANCE_MIN:-75}"
WORST_CASE_MIN=$(( MODEL_COUNT * PER_MODEL_MIN + SETUP_ALLOWANCE_MIN ))
EVAL_WAIT_MIN="${EVAL_WAIT_MIN:-$WORST_CASE_MIN}"
case "$EVAL_WAIT_MIN" in ''|*[!0-9]*) die "EVAL_WAIT_MIN='$EVAL_WAIT_MIN' is not a number of minutes." ;; esac
# The reaper is armed from the LARGER of the wait and the run's own worst case. Deriving it from
# the wait alone meant a deliberately short wait (-EvalWaitMin 60 with four models) shortened the
# reaper below the run's own duration, so the timer deleted the group - and the results - while the
# eval was still going, right after telling the operator "Nothing was deleted".
#
# It is armed OnBootSec on the VM, i.e. boot-relative, while the wait below is invocation-relative.
# Those clocks only coincide on a fresh run, which is why wait_and_collect stops the timer once it
# is attached and re-arms it if it gives up. Do NOT claim a nesting invariant here; there is none
# across invocations.
_budget_min=$EVAL_WAIT_MIN
[ "$WORST_CASE_MIN" -gt "$_budget_min" ] && _budget_min=$WORST_CASE_MIN
BACKSTOP_H=$(( (_budget_min + 60 + 59) / 60 ))
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
trap 'rm -f "$KEY_TMP" "${F8_SELF_TMP:-}"' EXIT

# Every ssh/scp below runs with BatchMode=yes, and the launcher starts this script in a
# non-interactive shell with no agent, so a PASSPHRASE-PROTECTED key can never authenticate. That
# failure would surface only at the fetch - after the GPU hour is paid - as an indistinguishable
# "no SSH yet". So prove the key is usable now, locally, before anything is created.
# ssh-keygen only derives the public half; nothing is transmitted and no key material is printed.
if command -v ssh-keygen >/dev/null 2>&1; then
  if ! ssh-keygen -y -P '' -f "$KEY_TMP" >/dev/null 2>&1; then
    die "the private key '$SSH_KEY_FILE' cannot be used without a passphrase, and every ssh/scp here runs with BatchMode=yes (no agent, non-interactive). The results could never be fetched. Point SSH_KEY_FILE at a passphrase-free key, or create one: ssh-keygen -t ed25519 -N '' -f ~/.ssh/f8_eval"
  fi
else
  step "WARNING: no ssh-keygen, so the private key's usability could not be checked up front."
fi

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

# The VM's reaper is boot-relative and this wait is not, so while we are attached WE own teardown:
# stop the timer, and re-arm a late one if we give up. If sudo is unavailable the clamp below is the
# fallback - we at least stop over-waiting past a deadline we cannot see.
disarm_backstop(){
  if ssh -n "${SSH_OPTS[@]}" "$ADMIN_USER@$IP" 'sudo -n systemctl stop f8-teardown.timer' 2>/dev/null; then
    step "stopped the VM's teardown timer for the duration of this wait (we tear down instead)."
    return 0
  fi
  step "WARNING: could not stop the VM's teardown timer; clamping this wait to its own deadline."
  return 1
}

rearm_backstop(){ # <minutes from now> - hand the reaper back before we stop watching
  local mins="$1" out rc=0
  # DOUBLE quotes: this was single-quoted, so $1 never expanded and the VM received
  # "--on-active=m", which systemd rejects. Combined with the stop above, that left the box with
  # no reaper at all - the one outcome this whole design exists to prevent. No --unit either: a
  # fixed name collides on a second re-arm, and we never need to address it again.
  out="$(ssh -n "${SSH_OPTS[@]}" "$ADMIN_USER@$IP" \
    "sudo -n systemd-run --on-active=${mins}m --setenv=F8_BACKSTOP=1 /opt/f8/teardown.sh" 2>&1)" || rc=$?
  if [ "$rc" = 0 ]; then
    step "re-armed a teardown backstop on the VM for ~${mins}m from now."
    return 0
  fi
  step "WARNING: could not re-arm the VM backstop (${out:-no output}). DELETE IT YOURSELF:"
  step "  az group delete -n $RG --yes"
  return 1
}

remote_backstop_remaining(){ # -> seconds until the VM reaps itself, or empty
  # RELATIVE seconds, computed on the VM. The timer is armed OnBootSec, i.e. MONOTONIC, so
  # NextElapseUSecRealtime is 0 for it and an earlier version of this always returned empty -
  # silently disabling the clamp it was written to provide. Monotonic also means no clock-skew
  # arithmetic between the two machines.
  local secs
  secs="$(ssh -n "${SSH_OPTS[@]}" "$ADMIN_USER@$IP" '
    n="$(systemctl show -p NextElapseUSecMonotonic --value f8-teardown.timer 2>/dev/null)"
    case "$n" in ""|*[!0-9]*) exit 0;; esac
    [ "$n" = 0 ] && exit 0
    u="$(awk "{print int(\$1)}" /proc/uptime)"
    echo $(( n / 1000000 - u ))' 2>/dev/null || true)"
  case "$secs" in ''|*[!0-9-]*) echo ""; return;; esac
  echo "$secs"
}

remote_state(){ # -> "DONE" | "FAILED" | "DEAD" | "RUNNING" | "UNREACHABLE" line 1, liveness line 2
  # -n so the loop's stdin is never consumed by ssh. The unit check matters: without it a crashed
  # or SIGKILLed job is indistinguishable from a slow one, and we bill the whole window.
  # ActiveState explicitly, NOT `is-active`: the unit is Type=oneshot, so it sits in "activating"
  # for the entire run, and `is-active --quiet` exits non-zero for that. An earlier version of this
  # check therefore declared a perfectly healthy job DEAD on the first successful poll.
  # "inactive" additionally needs evidence the job ever ran, because the unit is inactive in the
  # window between sshd coming up and cloud-init starting it.
  ssh -n "${SSH_OPTS[@]}" "$ADMIN_USER@$IP" '
    st="$(systemctl show -p ActiveState --value f8-eval.service 2>/dev/null || echo unknown)"
    if [ -f /opt/f8/.eval-done ]; then echo DONE
    elif [ -f /opt/f8/.eval-failed ]; then echo FAILED
    elif [ "$st" = failed ]; then echo DEAD
    elif [ "$st" = inactive ] && [ -f /var/log/f8-eval.log ]; then echo DEAD
    else echo RUNNING; fi
    tail -n 1 /var/log/f8-eval.log 2>/dev/null || true
  ' 2>/dev/null || echo UNREACHABLE
}

fetch_results(){ # copies whatever exists; non-zero unless the real artifacts arrived
  mkdir -p "$RESULTS_DIR"
  step "fetching results into $RESULTS_DIR ..."
  # Keep scp's own diagnosis. Discarding it made a partial transfer indistinguishable from a clean
  # one, and left the operator nothing to act on.
  local msg rc
  rc=0; msg="$(scp "${SSH_OPTS[@]}" -r "$ADMIN_USER@$IP:/opt/f8/eval-results/." "$RESULTS_DIR/" 2>&1)" || rc=$?
  [ "$rc" = 0 ] || step "scp of the results exited $rc: ${msg:-<no output>}"
  rc=0; msg="$(scp "${SSH_OPTS[@]}" "$ADMIN_USER@$IP:/var/log/f8-eval.log" "$RESULTS_DIR/f8-eval.log" 2>&1)" || rc=$?
  [ "$rc" = 0 ] || step "scp of the run log exited $rc: ${msg:-<no output>}"
  # Three outcomes, not two: 0 = the complete set, 2 = something arrived but not a finished run
  # (e.g. only partial-*.json and the log, which is exactly what a FAILED run leaves), 1 = nothing.
  # Collapsing 2 into 1 made the failure path report "(nothing to fetch)" over real artifacts.
  if [ -s "$RESULTS_DIR/summary.md" ] && [ -s "$RESULTS_DIR/summary.json" ] \
     && ls "$RESULTS_DIR"/baseline-*.json >/dev/null 2>&1; then
    return 0
  fi
  [ -n "$(ls -A "$RESULTS_DIR" 2>/dev/null)" ] && return 2
  return 1
}

report_fetched(){ # after a non-success outcome, say what actually came down
  local rc=$1
  case "$rc" in
    0|2) step "fetched into $RESULTS_DIR:"; ls -1 "$RESULTS_DIR" | sed 's/^/    /' ;;
    *)   step "(nothing could be fetched)" ;;
  esac
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
  local deadline state live last_live="" unreachable=0 nsg_repinned=0 now_ip="" diag=""
  local fetch_rc=0 frc=0 quiet=0
  local disarmed=0 attached=0 reaper=""
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
        fetch_rc=0; fetch_results || fetch_rc=$?
        case "$fetch_rc" in
          0) : ;;
          2) report_fetched 2
             die "the run finished but the fetched set is INCOMPLETE (no summary or no per-model results). The VM is still up - copy /opt/f8/eval-results by hand before deleting '$RG'." ;;
          *) die "the run finished but NOTHING could be fetched from $IP. The VM is still up - copy /opt/f8/eval-results by hand before deleting '$RG'." ;;
        esac
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
        frc=0; fetch_results || frc=$?
        report_fetched "$frc"
        if [ "$DESTROY_ON_FAILURE" = "1" ]; then delete_rg; else
          # If we stopped its reaper, hand it back before walking away. This MUST run: the first
          # real run died before reaching it and left a running A10 with no teardown at all.
          if [ "$disarmed" = 1 ]; then rearm_backstop 60; fi
          step "keeping '$RG' so you can investigate."
          step "  ssh ${ADMIN_USER}@${IP} 'less /var/log/f8-eval.log'"
          step "  az group delete --name $RG --yes"
        fi
        return 33
        ;;
      DEAD)
        step "the f8-eval unit stopped without writing a marker - it died (OOM, kill, or a crash)."
        ssh -n "${SSH_OPTS[@]}" "$ADMIN_USER@$IP" 'tail -n 40 /var/log/f8-eval.log 2>/dev/null; systemctl status --no-pager f8-eval.service 2>/dev/null | head -n 15' 2>/dev/null || true
        frc=0; fetch_results || frc=$?
        report_fetched "$frc"
        if [ "$DESTROY_ON_FAILURE" = "1" ]; then delete_rg; else
          if [ "$disarmed" = 1 ]; then rearm_backstop 60; fi
          step "keeping '$RG' to investigate."
        fi
        return 33
        ;;
      UNREACHABLE)
        unreachable=$(( unreachable + 1 ))
        if [ "$unreachable" = 1 ]; then step "no SSH yet; waiting ..."; fi
        # remote_state discards stderr, so authentication failures and a booting VM look identical.
        # Once, early, run a diagnostic that KEEPS stderr, so "Permission denied (publickey)" is
        # seen in the first minute instead of after the entire wait.
        if [ "$unreachable" = 2 ]; then
          diag="$(ssh -n "${SSH_OPTS[@]}" -o ConnectTimeout=10 "$ADMIN_USER@$IP" true 2>&1)" || true
          [ -n "$diag" ] && step "ssh says: $diag"
        fi
        # Six misses (~3m) is long enough to be worth one authoritative check: if the group is
        # gone, every further poll and the NSG re-pin below would be aimed at nothing.
        if [ $(( unreachable % 6 )) = 0 ]; then
          if ! az group exists --name "$RG" 2>/dev/null | grep -qx true; then
            step "the resource group '$RG' no longer exists - something deleted it (the VM's own"
            step "backstop, or a hand-run az group delete). Anything not already in"
            step "$RESULTS_DIR is gone."
            return 34
          fi
        fi
        # The NSG was pinned to the launch box's public IP when the group was created. On a
        # re-attach from another network - or after a DHCP change - that rule names somebody
        # else's address and NOTHING will ever connect. Re-pin it once.
        if [ "$unreachable" = 4 ] && [ "$nsg_repinned" = 0 ]; then
          nsg_repinned=1
          now_ip="$(curl -fsS https://api.ipify.org 2>/dev/null || true)"
          # If the rule was deliberately left open (no IP could be detected at create time),
          # re-pinning it to a /32 would narrow access rather than restore it.
          # :- because the attach path reaches this before the fresh-run block assigns it;
          # unset means "unknown", which must NOT be treated as "open".
          if [ "${ALLOWED_SSH_CIDR:-}" = "*" ]; then
            step "the AllowSSH rule is open (*), so a re-pin would only restrict it; leaving it."
            now_ip=""
          fi
          if [ -n "$now_ip" ]; then
            step "still unreachable after 2m - re-pinning the NSG to your current IP ($now_ip/32)."
            az network nsg rule update -g "$RG" --nsg-name "${VM_NAME}-nsg" -n AllowSSH \
              --source-address-prefixes "$now_ip/32" -o none 2>/dev/null \
              || step "could not update the NSG rule; do it by hand if this keeps failing."
          fi
        fi
        ;;
      *)
        unreachable=0
        quiet=$(( quiet + 1 ))
        if [ "$attached" = 0 ]; then
          attached=1
          if disarm_backstop; then
            disarmed=1
          else
            reaper="$(remote_backstop_remaining)"
            if [ -n "$reaper" ] && [ "$reaper" -gt 0 ]; then
              local hard=$(( $(date +%s) + reaper - 300 ))
              if [ "$hard" -lt "$deadline" ]; then
                deadline=$hard
                step "clamped this wait to 5m before the VM reaps itself ($(( (reaper - 300) / 60 ))m from now)."
              fi
            fi
          fi
        fi
        if [ "$live" != "$last_live" ] && [ -n "$live" ]; then
          step "$live"; last_live="$live"; quiet=0
        elif [ "$quiet" -ge 6 ]; then
          # A row that stalls (a runaway whole-type generation bounded by the harness's per-call
          # timeout) writes NO new log line for minutes, so printing only on change made a healthy
          # run look hung - measured on the 2026-08-23 run. Heartbeat with elapsed time instead.
          step "still running, no new output for $(( quiet / 2 ))m (a stalled row is normal: the harness caps each draft)"
          quiet=0
        fi
        ;;
    esac
    sleep 30
  done
  echo "" >&2
  if [ "$disarmed" = 1 ]; then rearm_backstop 60; fi
  step "TIMEOUT after ${EVAL_WAIT_MIN}m. Nothing was deleted yet."
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

if [ "$USE_SPOT" = "1" ]; then
  echo "[eval-deploy] WARNING: Spot is requested. An evicted Spot VM is DEALLOCATED, and a" >&2
  echo "[eval-deploy]          deallocated VM runs no systemd timers - so its own teardown" >&2
  echo "[eval-deploy]          backstop cannot fire. If it is evicted, this resource group" >&2
  echo "[eval-deploy]          survives until you or a re-attach delete it." >&2
fi

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
