#!/bin/sh
# Rewrite config.js from F8_API_URL before nginx starts (feature standalone-ui).
#
# Runs via the official nginx image's /docker-entrypoint.d mechanism (copied in as
# 40-f8-config.sh). F8_API_URL is the browser-reachable Fallen-8 REST origin the standalone Studio
# talks to; empty means same-origin. Trailing-slash / empty normalization is intentionally left to
# the SPA (normalizeBaseUrl, one home); here we only guard the JS string literal so a mangled value
# cannot silently produce a broken config.js that reverts the app to same-origin.
set -eu

CONFIG_FILE="${F8_CONFIG_PATH:-/usr/share/nginx/html/config.js}"
API_URL="${F8_API_URL:-}"

# Refuse a quote or backslash: either would break out of the JS string literal below.
case "$API_URL" in
  *\"* | *\\*)
    echo "f8-config: F8_API_URL contains a quote or backslash; refusing to start" >&2
    exit 1
    ;;
esac

# Refuse an embedded newline/carriage return: it would break the single-line literal.
if [ "$API_URL" != "$(printf '%s' "$API_URL" | tr -d '\n\r')" ]; then
  echo "f8-config: F8_API_URL contains a newline; refusing to start" >&2
  exit 1
fi

printf 'window.__F8_CONFIG__ = { apiUrl: "%s" };\n' "$API_URL" > "$CONFIG_FILE"
echo "f8-config: wrote ${CONFIG_FILE} with apiUrl=\"${API_URL}\""
