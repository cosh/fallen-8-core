#!/usr/bin/env bash
# MIT License
#
# registry-probe.sh
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
# FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
# AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
# LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
# OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
# SOFTWARE.
#
# Sourced, not executed. One home for "does the registry serve this tag?", shared by the launch
# box (eval-deploy.sh, before it creates anything) and the VM (eval-run.sh, before it pulls
# gigabytes). The auth-free manifest GET is the same one run.sh uses after a push.
#
# The distinction this file exists for: curl reports a failed TRANSFER as http_code 000, which is
# "we could not ask", NOT "the tag is missing". Measured 2026-08-23: a single 000 from the launch
# box aborted an eval with "'<prefix>/phi4-f8' is not published" while that tag was in fact
# published and served a 200 manifest seconds later. So only a 404 - the registry answering "no
# such tag" - is allowed to accuse the operator of not publishing; everything else retries and is
# then reported as what it is.

REGISTRY="${REGISTRY:-https://registry.ollama.ai}"

# registry_probe <namespace/model>
#   0 = present (REGISTRY_PROBE_DETAIL: "manifest 200")
#   3 = definitively absent: the registry answered 404
#   4 = could not be established after 3 attempts (REGISTRY_PROBE_DETAIL says why)
registry_probe() {
  local ref="$1" attempt code errfile detail=''
  errfile="$(mktemp)"
  for attempt in 1 2 3; do
    code="$(curl -sSL --connect-timeout 15 --max-time 60 -o /dev/null -w '%{http_code}' \
      "$REGISTRY/v2/$ref/manifests/latest" 2>"$errfile" || true)"
    case "${code:-000}" in
      200)
        rm -f "$errfile"; REGISTRY_PROBE_DETAIL='manifest 200'; return 0 ;;
      404)
        rm -f "$errfile"; REGISTRY_PROBE_DETAIL='HTTP 404'; return 3 ;;
      000)
        # curl's own diagnosis (DNS, TLS, timeout) is the only useful part; -sS puts it on stderr.
        detail="$( { tr -d '\r' < "$errfile" | grep -v '^[[:space:]]*$' | tail -n1; } || true)"
        detail="${detail:-no HTTP response (connection failed or timed out)}" ;;
      *)
        detail="HTTP $code" ;;
    esac
    if [ "$attempt" -lt 3 ]; then sleep 5; fi
  done
  rm -f "$errfile"
  REGISTRY_PROBE_DETAIL="$detail (3 attempts)"
  return 4
}
