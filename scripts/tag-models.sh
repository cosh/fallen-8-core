#!/usr/bin/env bash
# MIT License
#
# tag-models.sh
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
# Gives the currently published NL-assist models a VERSION tag matching a repository tag, so
# ":latest" stops being the only name they have. Called by the release workflow on a vX.Y.Z push,
# and runnable by hand.
#
# Why this exists: the fine-tune pipeline republishes over ":latest" every time (run.sh publish is
# invoked as PUBLISH_PREFIX/<variant>, with no tag), so nothing on a running host can tell which
# build it holds without comparing manifest digests. A version tag pinned to a repo tag makes
# "which model does release vX.Y.Z mean" answerable, and lets a deployment pin instead of drift.
#
# It does NOT retrain and does NOT change ":latest" - it adds a second name for the same bytes.
#
# Usage:
#   scripts/tag-models.sh v1.2.3
#   F8_TAG_MODELS="ns/phi4-f8-mini ns/phi4-f8" scripts/tag-models.sh v1.2.3
#
# Env:
#   F8_TAG_MODELS         space-separated repos to tag. Default: both published variants.
#   OLLAMA_SIGNING_KEY    the private key CONTENTS (for CI). Otherwise OLLAMA_KEY_FILE is used.
#   OLLAMA_KEY_FILE       default ~/.ollama/id_ed25519
#   REGISTRY              default https://registry.ollama.ai
#   F8_TAG_DRY_RUN        1 = report what would happen, push nothing
#   F8_TAG_KEEP_LOCAL     1 = keep whatever this run pulled. Default is to remove it again after
#                         pushing, so peak disk is ONE model instead of all of them - which is what
#                         lets a hosted runner tag the 14B alongside the mini. Models that were
#                         already present locally are never touched.
set -euo pipefail

VERSION="${1:-}"
[ -n "$VERSION" ] || { echo "usage: $0 <version-tag>   e.g. $0 v1.2.3" >&2; exit 2; }
case "$VERSION" in
  v[0-9]*.[0-9]*.[0-9]*) ;;
  *) echo "ERROR: '$VERSION' is not a vX.Y.Z tag. The release workflow only fires on those, so a" >&2
     echo "       different shape here would produce a model tag no release refers to." >&2
     exit 2 ;;
esac

MODELS="${F8_TAG_MODELS:-stoic_hellman_728/phi4-f8-mini stoic_hellman_728/phi4-f8}"
REGISTRY="${REGISTRY:-https://registry.ollama.ai}"
OLLAMA_KEY_FILE="${OLLAMA_KEY_FILE:-$HOME/.ollama/id_ed25519}"
DRY="${F8_TAG_DRY_RUN:-0}"

log(){ echo "[tag-models] $*"; }
die(){ echo "[tag-models] ERROR: $*" >&2; exit 1; }

command -v ollama >/dev/null 2>&1 || die "ollama is not installed; this script pulls and re-pushes real model blobs."
command -v sha256sum >/dev/null 2>&1 || die "sha256sum is required to verify the tag points at the same bytes."

# The DAEMON signs pushes, not this shell, and it runs as its own user with its own ~/.ollama. The
# one home for that explanation (and the incident behind it) is
# nl-assist-finetune/infra/bootstrap.sh; do not re-derive it here.
install_signing_key() {
  if [ -n "${OLLAMA_SIGNING_KEY:-}" ]; then
    OLLAMA_KEY_FILE="$(mktemp)"; chmod 600 "$OLLAMA_KEY_FILE"
    # tr -d: a key pasted into a GitHub secret from Windows carries CRLF, which ollama rejects as
    # malformed - surfacing as an UNAUTHENTICATED push, the hardest failure here to read. Strip
    # them rather than let a release die on line endings.
    printf '%s\n' "$OLLAMA_SIGNING_KEY" | tr -d '\r' > "$OLLAMA_KEY_FILE"
    trap 'rm -f "$OLLAMA_KEY_FILE"' EXIT
  fi
  [ -s "$OLLAMA_KEY_FILE" ] || die "no Ollama signing key at '$OLLAMA_KEY_FILE' (and OLLAMA_SIGNING_KEY is unset). A push would authenticate as nobody and upload nothing."

  # Validate BEFORE installing. ollama signs every registry request, pulls included, so replacing
  # the daemon's working key with an unparseable one breaks even reads - which is how this first
  # surfaced: "Error: pull model manifest: ssh: no key found", Go's message for a PEM block it
  # could not decode, on a PULL. Reporting the SHAPE turns that into an actionable message.
  # Deliberately prints no key material: only counts and booleans, because CI masks the exact
  # secret but not a substring of it.
  # A 0600 copy: ssh-keygen refuses a key with loose permissions (one on a Windows mount presents
  # as 0777), so validating the original would reject a perfectly good key for the wrong reason.
  local probe
  probe="$(mktemp)"; chmod 600 "$probe"; cat "$OLLAMA_KEY_FILE" > "$probe"
  if ! ssh-keygen -y -P '' -f "$probe" >/dev/null 2>&1; then
    echo "[tag-models] the key material is not a usable OpenSSH PRIVATE key:" >&2
    echo "[tag-models]   lines: $(wc -l < "$OLLAMA_KEY_FILE" | tr -d ' ')" >&2
    if head -1 "$OLLAMA_KEY_FILE" | grep -q -- "-----BEGIN"; then
      echo "[tag-models]   starts with a BEGIN header: yes" >&2
    else
      echo "[tag-models]   starts with a BEGIN header: NO" >&2
    fi
    if head -1 "$OLLAMA_KEY_FILE" | grep -q "^ssh-"; then
      echo "[tag-models]   looks like a PUBLIC key - this needs the private half" >&2
    fi
    if grep -qF '\n' "$OLLAMA_KEY_FILE"; then
      echo "[tag-models]   contains LITERAL backslash-n: its newlines were escaped, not real" >&2
    fi
    if [ "$(wc -l < "$OLLAMA_KEY_FILE" | tr -d ' ')" -le 1 ]; then
      echo "[tag-models]   it is a single line: the line breaks were lost when it was stored" >&2
    fi
    rm -f "$probe"
    die "store the key with its real line breaks intact - the whole file, BEGIN and END lines included. The daemon's existing key was left untouched."
  fi

  rm -f "$probe"

  local home
  home="$(getent passwd ollama 2>/dev/null | cut -d: -f6 || true)"
  if [ -n "$home" ]; then
    # -n on every sudo below: without it, a box without passwordless sudo PROMPTS, and in a
    # non-interactive context that hangs until something kills it rather than failing. Check once,
    # with a message that says what to do.
    sudo -n true 2>/dev/null || die "installing the key into the daemon's home ($home/.ollama) needs passwordless sudo. CI runners have it; on a workstation either run this from a sudo-capable session or copy the key there yourself, then re-run."
    sudo -n mkdir -p "$home/.ollama"
    sudo -n install -m 600 "$OLLAMA_KEY_FILE" "$home/.ollama/id_ed25519"
    # The public half too: derived, so it always matches, and some versions read it.
    ssh-keygen -y -f "$OLLAMA_KEY_FILE" 2>/dev/null > "$OLLAMA_KEY_FILE.pub" || true
    [ -s "$OLLAMA_KEY_FILE.pub" ] && sudo -n install -m 644 "$OLLAMA_KEY_FILE.pub" "$home/.ollama/id_ed25519.pub"
    sudo -n chown -R ollama:ollama "$home/.ollama" || die "could not give the daemon ownership of its key; it would read nothing and report 'no key found'."
    # A root-owned 0600 key is invisible to the daemon and fails with the SAME message as a
    # malformed one, so assert readability as the user that actually signs.
    sudo -n -u ollama test -r "$home/.ollama/id_ed25519" || die "the daemon user cannot read the installed key."
    sudo -n systemctl restart ollama 2>/dev/null || true
    for _ in $(seq 1 30); do ollama list >/dev/null 2>&1 && break; sleep 2; done
    log "installed the signing key into the daemon's home ($home/.ollama)."
  else
    mkdir -p "$HOME/.ollama" && chmod 700 "$HOME/.ollama"
    install -m 600 "$OLLAMA_KEY_FILE" "$HOME/.ollama/id_ed25519"
    log "installed the signing key into $HOME/.ollama (no separate daemon user found)."
  fi
}

# The 12 hex ollama reports for a model is the leading half of the sha256 of its registry
# manifest, so this compares like with like against a local `ollama list` id.
remote_digest() { # <repo> <tag> -> 12 hex, or empty when the tag does not exist
  curl -sf --connect-timeout 15 --max-time 120 "$REGISTRY/v2/$1/manifests/$2" 2>/dev/null | sha256sum | cut -c1-12
}
remote_exists() { # <repo> <tag>
  [ "$(curl -sSL --connect-timeout 15 --max-time 60 -o /dev/null -w '%{http_code}' "$REGISTRY/v2/$1/manifests/$2" 2>/dev/null || echo 000)" = "200" ]
}

require_disk() { # <gigabytes>
  local free
  free="$(df -BG --output=avail / 2>/dev/null | tail -1 | tr -dc '0-9')"
  [ -n "$free" ] || return 0
  [ "$free" -ge "$1" ] || die "only ${free}G free on / but tagging needs about ${1}G to pull the blobs. Tag fewer models via F8_TAG_MODELS, or run this where there is room."
}

push_verified() { # <repo:tag> - ollama push exits 0 even when it uploaded NOTHING
  local target="$1" out
  if ! out="$(ollama push "$target" 2>&1)"; then
    printf '%s\n' "$out" >&2
    die "'ollama push $target' exited non-zero."
  fi
  printf '%s\n' "$out"
  # Here-string, not a pipe: under pipefail a printf|grep can lose the match to SIGPIPE and pass
  # this guard silently. Same reasoning as run.sh's publish stage.
  if grep -qiE 'signed in|/connect\?|not authorized|unauthorized' <<<"$out"; then
    die "the push was NOT authenticated - the daemon's key is not registered to this namespace, so nothing was uploaded. Register its public half at https://ollama.com/settings/keys."
  fi
}

# A dry run inspects the registry only, so it must not demand a key the operator may not have
# on this box. Everything after this point that actually pushes is gated on DRY as well.
if [ "$DRY" = "1" ]; then
  log "DRY RUN: reading the registry only; the signing key is not checked and nothing is pushed."
else
  install_signing_key
fi

count=0
skipped=""
for repo in $MODELS; do
  log "=== $repo ==="
  latest="$(remote_digest "$repo" latest)"
  [ -n "$latest" ] || die "$repo has no published ':latest' to tag. Publish it first (nl-assist-finetune/run.sh publish)."
  log "  :latest is $latest"

  if remote_exists "$repo" "$VERSION"; then
    existing="$(remote_digest "$repo" "$VERSION")"
    if [ "$existing" = "$latest" ]; then
      log "  :$VERSION already published and identical ($existing) - nothing to do."
      count=$((count + 1))
      continue
    fi
    die ":$VERSION already exists for $repo with a DIFFERENT digest ($existing vs $latest). Refusing to move a released tag; cut a new version instead."
  fi

  if [ "$DRY" = "1" ]; then
    log "  DRY RUN: would pull $repo:latest, tag it :$VERSION, and push."
    skipped="$skipped $repo"
    continue
  fi

  require_disk 12
  had_locally=0
  ollama list | grep -q "^$repo:latest" && had_locally=1
  log "  pulling $repo:latest ..."
  ollama pull "$repo:latest" || die "could not pull $repo:latest"
  ollama cp "$repo:latest" "$repo:$VERSION" >/dev/null || die "could not tag $repo:latest as :$VERSION"
  log "  pushing $repo:$VERSION ..."
  push_verified "$repo:$VERSION"

  after="$(remote_digest "$repo" "$VERSION")"
  [ "$after" = "$latest" ] || die "published :$VERSION but its digest ($after) does not match :latest ($latest) - the tag does not point at the bytes it should."
  log "  :$VERSION published, digest $after (identical to :latest)"
  count=$((count + 1))

  # Give the disk back unless these blobs were already here. Tagging the mini and the 14B together
  # otherwise needs both resident (~12G), which a hosted runner cannot spare.
  if [ "${F8_TAG_KEEP_LOCAL:-0}" != "1" ] && [ "$had_locally" = 0 ]; then
    ollama rm "$repo:$VERSION" >/dev/null 2>&1 || true
    ollama rm "$repo:latest" >/dev/null 2>&1 || true
    log "  removed the local copies again (F8_TAG_KEEP_LOCAL=1 keeps them)"
  fi
done

log "done: $count model(s) carry the tag $VERSION."
[ -n "$skipped" ] && log "DRY RUN, nothing was pushed for:$skipped"
log "Consumers can now pin a build instead of tracking :latest, e.g."
log "  F8_DELEGATE_REPO=<ns>/phi4-f8-mini:$VERSION"
