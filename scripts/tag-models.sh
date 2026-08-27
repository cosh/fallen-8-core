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
# ONE VERSION TAG PER DISTINCT BUILD, not per release. Publishing (run.sh publish, on a retrain) and
# releasing (a vX.Y.Z tag push) move on unrelated schedules, so most releases carry no new weights,
# and naively snapshotting ":latest" every time mints a fresh name for bytes that already have one.
# That is not harmless: ollama.com badges every version-shaped tag sharing ":latest"'s digest as
# "latest", so N releases without a retrain leave N tags all presenting as current and the model
# page stops answering "which build is this". So before tagging, this compares ":latest" against the
# version tags this repository already knows about, and does nothing when the bytes are already
# named. A release may therefore legitimately have NO model tag - and "one tag per release" never
# held anyway: v0.0.30-v0.0.34 carry none (this mechanism landed 2026-08-23, v0.0.35 was the first
# release to use it), which is why nothing may assume a release version resolves as a model tag.
#
# Usage:
#   scripts/tag-models.sh v1.2.3
#   F8_TAG_MODELS="ns/phi4-f8-mini ns/phi4-f8" scripts/tag-models.sh v1.2.3
#   F8_TAG_FORCE=1 scripts/tag-models.sh v1.2.3   # tag even if those bytes already have a version
#
# Env:
#   F8_TAG_MODELS         space-separated repos to tag. Default: both published variants.
#   F8_TAG_FORCE          1 = mint :VERSION even when an existing version tag already carries
#                         ":latest"'s bytes. The manual "Tag models" workflow needs this:
#                         backfilling a version whose bytes a LATER tag already names is exactly
#                         what the check above otherwise skips.
#   F8_TAG_BASELINE_LIMIT how many known version tags to compare ":latest" against, newest first.
#                         Default 20. Each comparison is one ~700-byte manifest GET per repo.
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
# Anchored regex, not a glob: `v[0-9]*.[0-9]*.[0-9]*` also matched `v1.2.3-rc1`, the exact shape
# the message below claims to reject, and on the workflow_dispatch path this is the ONLY validation.
if [[ ! "$VERSION" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "ERROR: '$VERSION' is not a vX.Y.Z tag. The release workflow only fires on those (stable" >&2
  echo "       versions, no pre-release suffix), so a different shape here would produce a model" >&2
  echo "       tag no release refers to." >&2
  exit 2
fi

MODELS="${F8_TAG_MODELS:-stoic_hellman_728/phi4-f8-mini stoic_hellman_728/phi4-f8}"
REGISTRY="${REGISTRY:-https://registry.ollama.ai}"
OLLAMA_KEY_FILE="${OLLAMA_KEY_FILE:-$HOME/.ollama/id_ed25519}"
DRY="${F8_TAG_DRY_RUN:-0}"
BASELINE_LIMIT="${F8_TAG_BASELINE_LIMIT:-20}"
# A workflow_dispatch boolean reaches an env var as the STRING "true"/"false", which is a documented
# Actions trap: "false" is a non-empty string, so anything treating it as truthy forces every run.
# Normalising here means the workflow can pass the input through verbatim and be right either way.
case "${F8_TAG_FORCE:-0}" in
  1|true|TRUE|True|yes|on) FORCE=1 ;;
  *) FORCE=0 ;;
esac
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

log(){ echo "[tag-models] $*"; }
die(){ echo "[tag-models] ERROR: $*" >&2; exit 1; }
# GitHub renders these in the run summary; locally they are noise, so only under Actions. This is
# what makes "this release minted no model tag" readable in the release log, and tells it apart from
# the OTHER reason this job produces nothing (no signing key, which the workflow reports itself).
notice(){ [ -z "${GITHUB_ACTIONS:-}" ] || echo "::notice title=$1::$2"; }

# Checked, not assumed: a non-numeric bound makes every `[ n -ge $BASELINE_LIMIT ]` fail, which
# reads as "not reached yet" and removes the bound instead of reporting a bad setting.
[[ "$BASELINE_LIMIT" =~ ^[0-9]+$ ]] || die "F8_TAG_BASELINE_LIMIT must be a whole number, got '$BASELINE_LIMIT'."

# A dry run reads the registry and hashes manifests, it never pulls or pushes, so it must not demand
# the daemon on a box that only wants to know what a release WOULD do. Same reasoning as the signing
# key further down.
[ "$DRY" = "1" ] || command -v ollama >/dev/null 2>&1 || die "ollama is not installed; this script pulls and re-pushes real model blobs."
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
    # Prompting is fine when a person is watching; -n exists only so an UNATTENDED run cannot hang
    # forever on a password prompt (measured: it does). So probe once and pick accordingly.
    local SUDO="sudo -n"
    if ! sudo -n true 2>/dev/null; then
      if [ -t 0 ]; then
        SUDO="sudo"
        log "sudo will prompt for your password (the key must go into $home/.ollama, which the daemon owns)."
      else
        die "installing the key into the daemon's home ($home/.ollama) needs sudo, and this session cannot prompt (no terminal). Run it from an interactive shell, or grant passwordless sudo as CI runners have."
      fi
    fi
    $SUDO mkdir -p "$home/.ollama"
    $SUDO install -m 600 "$OLLAMA_KEY_FILE" "$home/.ollama/id_ed25519"
    # The public half too: derived, so it always matches, and some versions read it.
    ssh-keygen -y -f "$OLLAMA_KEY_FILE" 2>/dev/null > "$OLLAMA_KEY_FILE.pub" || true
    [ -s "$OLLAMA_KEY_FILE.pub" ] && $SUDO install -m 644 "$OLLAMA_KEY_FILE.pub" "$home/.ollama/id_ed25519.pub"
    $SUDO chown -R ollama:ollama "$home/.ollama" || die "could not give the daemon ownership of its key; it would read nothing and report 'no key found'."
    # A root-owned 0600 key is invisible to the daemon and fails with the SAME message as a
    # malformed one, so assert readability as the user that actually signs.
    $SUDO -u ollama test -r "$home/.ollama/id_ed25519" || die "the daemon user cannot read the installed key."
    $SUDO systemctl restart ollama 2>/dev/null || true
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
#
# Deliberately not `curl | sha256sum`: a failed fetch pipes NOTHING, and the sha256 of nothing is a
# perfectly valid-looking digest (e3b0c44298fc...) that every missing tag produces. That made the
# ":latest is missing" guard below unreachable, and it would make two nonexistent tags compare
# EQUAL - a repo with nothing published would report that its bytes already carry a version. So the
# fetch goes to a file and only a non-empty one is hashed, which keeps a miss reportable as a miss.
# It stays a file rather than a variable because a command substitution eats the trailing newline,
# and the digest has to be over exactly the bytes the registry sent or it matches nothing ollama
# ever shows.
remote_digest() { # <repo> <tag> -> 12 hex, or EMPTY when the tag does not exist
  local body status=0
  body="$(mktemp)"
  curl -sf --connect-timeout 15 --max-time 120 "$REGISTRY/v2/$1/manifests/$2" -o "$body" 2>/dev/null || status=$?
  if [ "$status" -eq 0 ] && [ -s "$body" ]; then
    sha256sum < "$body" | cut -c1-12
  fi
  rm -f "$body"
}
remote_exists() { # <repo> <tag>
  [ "$(curl -sSL --connect-timeout 15 --max-time 60 -o /dev/null -w '%{http_code}' "$REGISTRY/v2/$1/manifests/$2" 2>/dev/null || echo 000)" = "200" ]
}

# The version tags to compare ":latest" against. registry.ollama.ai implements no
# /v2/<repo>/tags/list (it 404s with the Go router's plain-text body, not the registry's own JSON
# error), so what is already published cannot be READ back; it has to come from this repository.
# Two sources, both cheap and both offline:
#   - `git tag`, which IS the release history, newest version first;
#   - the pinned default in docker-compose.yml, which names a version that certainly got published.
# The pin is appended rather than kept as a fallback because a SHALLOW checkout carries only the tag
# being released, and a git list that quietly comes back holding just $VERSION would defeat this
# check without saying so. Both workflows therefore ask for fetch-depth: 0, and the pin is the belt
# to that braces. Pre-release shapes are filtered out: they never fire a release, so they can never
# have produced a model tag.
KNOWN_VERSIONS="$(
  {
    git -C "$REPO_ROOT" tag --list 'v[0-9]*' --sort=-v:refname 2>/dev/null || true
    grep -hoE '\$\{(F8_DELEGATE_REPO|F8_PHI4F8_REPO):-[^}]+\}' "$REPO_ROOT/docker-compose.yml" 2>/dev/null |
      grep -oE 'v[0-9]+\.[0-9]+\.[0-9]+' || true
  } | grep -xE 'v[0-9]+\.[0-9]+\.[0-9]+' | grep -vxF "$VERSION" | awk '!seen[$0]++' || true
)"

# Does some existing version tag already name these bytes? Prints the first one that does.
# Logs to stderr, because the caller reads its stdout.
already_named() { # <repo> <12-hex digest of :latest> -> prints a version tag, or fails
  local repo="$1" digest="$2" cand probed=0 d
  for cand in $KNOWN_VERSIONS; do
    if [ "$probed" -ge "$BASELINE_LIMIT" ]; then
      log "  compared the $probed newest version tags and stopped there; raise F8_TAG_BASELINE_LIMIT to look further back." >&2
      break
    fi
    probed=$((probed + 1))
    d="$(remote_digest "$repo" "$cand" || true)"
    # A tag that does not exist yields empty, and the caller's digest is never empty (it is guarded
    # above), so a miss cannot match. Checked anyway: this is the comparison the whole gate rests on.
    [ -n "$d" ] || continue
    [ "$d" = "$digest" ] || continue
    printf '%s
' "$cand"
    return 0
  done
  return 1
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
unchanged=""
for repo in $MODELS; do
  log "=== $repo ==="
  # `|| true`: pipefail would otherwise abort the script on curl's exit code and never reach the
  # message below, which is the one an operator actually needs.
  latest="$(remote_digest "$repo" latest || true)"
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

  # One tag per distinct BUILD (the header explains why). Deliberately AFTER the check above, so a
  # :$VERSION that exists pointing somewhere else still fails loudly instead of being skipped.
  if [ "$FORCE" != "1" ] && dup="$(already_named "$repo" "$latest")"; then
    log "  :latest ($latest) is already published as :$dup - not adding :$VERSION as a second name for the same bytes."
    log "  A release with no retrain since :$dup is expected to mint nothing. F8_TAG_FORCE=1 overrides."
    notice "No model tag for $VERSION" "$repo:latest already carries the version :$dup (digest $latest), so $VERSION would be a duplicate name for bytes that already have one. Nothing was pushed. Retrain and publish to move :latest, or re-run the Tag models workflow with force."
    unchanged="$unchanged $repo:$dup"
    continue
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
if [ -n "$unchanged" ]; then
  log "not tagged, the bytes already have a version name:$unchanged"
  log "Nothing is wrong with that: the pin keeps naming the build it names, and the model page keeps"
  log "one current version instead of collecting synonyms."
fi
if [ -n "$skipped" ]; then
  log "DRY RUN, nothing was pushed for:$skipped"
fi
if [ "$count" -gt 0 ]; then
  log "Consumers can now pin a build instead of tracking :latest, e.g."
  log "  F8_DELEGATE_REPO=<ns>/phi4-f8-mini:$VERSION"
fi
