#!/usr/bin/env bash
# MIT License
#
# eval-run.sh
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

# The FULL evaluation of published models, on whatever box runs this: pull each published
# variant, run eval/baseline.ts --semantic (delegate rows + whole-type plugin rows + the FT-8
# element-set gate, which is one invocation), verify the run was not silently partial, and
# emit a combined summary next to the per-model result files.
#
# The Azure eval runner invokes this on the VM, but it is deliberately standalone: on a box
# that already has a GPU, an ollama daemon and an apiApp, this is the whole job.
#
# Prerequisites it does NOT install: ollama (daemon running, GPU visible), node + npx, and a
# reachable apiApp as the compile authority. It DOES run `npm ci` for fallen-8-web-ui when
# that is missing, because eval/baseline.ts imports the shipping prompt modules from there.
#
# Env:
#   EVAL_PREFIX     registry namespace holding the published variants (mirrors PUBLISH_PREFIX
#                   on the publishing side). Empty means "the variants are already local".
#   VARIANTS        space-separated variants to evaluate (default "phi4-f8-mini phi4-f8") -
#                   the same names the fine-tune job publishes.
#   EVAL_BASELINES  optional space-separated stock models to evaluate for comparison
#                   (e.g. "phi4-mini"); pulled by name, no prefix. Default: none.
#   NL_EVAL_F8      apiApp base URL (default http://localhost:5000)
#   NL_EVAL_ENDPOINT  ollama endpoint (default http://localhost:11434)
#   RESULTS_OUT     where the collected results land (default /opt/f8/eval-results)
#   EVAL_TIMEOUT    per-model wall-clock cap (default 60m)
#   REPO_DIR        repo checkout to run from (default: two levels up from this script)
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="${REPO_DIR:-$(cd "$HERE/../.." && pwd)}"
FT="$REPO_DIR/nl-assist-finetune"

EVAL_PREFIX="${EVAL_PREFIX:-}"
VARIANTS="${VARIANTS:-phi4-f8-mini phi4-f8}"
EVAL_BASELINES="${EVAL_BASELINES:-}"
NL_EVAL_F8="${NL_EVAL_F8:-http://localhost:5000}"
NL_EVAL_ENDPOINT="${NL_EVAL_ENDPOINT:-http://localhost:11434}"
export RESULTS_OUT="${RESULTS_OUT:-/opt/f8/eval-results}"
EVAL_TIMEOUT="${EVAL_TIMEOUT:-60m}"
REGISTRY="${REGISTRY:-https://registry.ollama.ai}"

log() { printf '\n\033[1;36m[eval] %s\033[0m\n' "$*"; }
note() { echo "[eval]   $*"; }
fail() { echo "[eval] ERROR: $*" >&2; exit 1; }

mkdir -p "$RESULTS_OUT"
# Per-model lines from an EARLIER run would otherwise be folded into this run's summary table as
# if they were measured now.
rm -f "$RESULTS_OUT"/line-*.json

# ---------------------------------------------------------------------------------------------
# Preflight. Everything cheap that can invalidate the run happens before any model is pulled.
# ---------------------------------------------------------------------------------------------

command -v ollama >/dev/null 2>&1 || fail "ollama is not installed."
command -v node >/dev/null 2>&1 || fail "node is not installed (eval/baseline.ts runs under tsx)."
command -v npx >/dev/null 2>&1 || fail "npx is not installed."
[ -f "$FT/eval/baseline.ts" ] || fail "no eval harness at $FT/eval/baseline.ts (REPO_DIR=$REPO_DIR wrong?)."

ollama list >/dev/null 2>&1 || fail "the ollama daemon is not answering ('ollama list' failed)."
curl -sf --max-time 15 "$NL_EVAL_F8/status" >/dev/null 2>&1 \
  || fail "no apiApp at $NL_EVAL_F8 - baseline.ts needs it as the compile authority (and it seeds the FT-8 fixture there)."

# GPU is not strictly required, but CPU inference here is ~14 s/token: a run that lands on CPU
# would look like a hang, not a failure. Say so loudly rather than silently burning an hour.
if command -v nvidia-smi >/dev/null 2>&1 && nvidia-smi -L >/dev/null 2>&1; then
  note "GPU: $(nvidia-smi -L | head -1)"
else
  echo "[eval] WARNING: no GPU visible to nvidia-smi. Inference will run on CPU, which is" >&2
  echo "[eval]          unusably slow for these models (measured ~14 s/token). Continuing," >&2
  echo "[eval]          but expect the per-model timeout ($EVAL_TIMEOUT) to fire." >&2
fi

# baseline.ts imports the shipping prompt modules from the web UI, whose import chain needs its
# runtime deps - absent on a fresh clone (the same reason run.sh's dataset stage installs them).
if [ ! -d "$REPO_DIR/fallen-8-web-ui/node_modules" ]; then
  log "installing fallen-8-web-ui deps (fresh clone)"
  (cd "$REPO_DIR/fallen-8-web-ui" && npm ci)
fi

# The expected row counts, read from the committed eval sets. These are what make a partial run
# detectable: baseline.ts exits 0 even when every row FAILS, and it RESUMES a pre-existing
# results file by skipping row ids it already has - so "no rows generated" is a success-looking
# outcome unless someone counts.
count_rows() { # <json file> -> number of .rows entries, 0 if the file is absent
  [ -f "$1" ] || { echo 0; return; }
  node --input-type=commonjs -e 'const r=require(process.argv[1]).rows;process.stdout.write(String(Array.isArray(r)?r.length:0))' "$1"
}
EXPECT_ROWS="$(count_rows "$FT/eval/eval-set.json")"
EXPECT_PLUGIN_ROWS="$(count_rows "$FT/eval/plugin-eval-set.json")"
[ "$EXPECT_ROWS" -gt 0 ] || fail "eval/eval-set.json holds no rows - nothing to evaluate."
note "expecting $EXPECT_ROWS delegate row(s) and $EXPECT_PLUGIN_ROWS plugin row(s) per model."

# ---------------------------------------------------------------------------------------------
# Model acquisition. A published tag that does not exist is a launch-box-detectable mistake, so
# check it before pulling gigabytes; the same auth-free manifest GET run.sh uses after a push.
# ---------------------------------------------------------------------------------------------

registry_has() { # <namespace/model> -> 0 when the registry serves its "latest" manifest
  local code
  code="$(curl -sSL --connect-timeout 15 --max-time 60 -o /dev/null -w '%{http_code}' \
    "$REGISTRY/v2/${1}/manifests/latest" 2>/dev/null || echo 000)"
  [ "$code" = "200" ]
}

pull_retry() { # <model> - ollama pull with three attempts; a partial pull is not a verdict
  local model="$1" attempt
  for attempt in 1 2 3; do
    if ollama pull "$model"; then return 0; fi
    echo "[eval] pull of '$model' failed (attempt $attempt/3); retrying in 20s..." >&2
    sleep 20
  done
  return 1
}

# Evaluate under the SHORT local name, not the registry path: baseline.ts names its output file
# baseline-<model with [^\w.-] replaced by _>.json, so evaluating "ns/phi4-f8-mini" would write
# baseline-ns_phi4-f8-mini.json and silently stop being comparable with the existing ledger.
acquire() { # <local name> -> ensures the model exists locally under exactly that name
  local local_name="$1" remote
  if [ -n "$EVAL_PREFIX" ]; then
    remote="$EVAL_PREFIX/$local_name"
    registry_has "$remote" || fail "the registry has no '$remote:latest' (has that variant been published?). Nothing was pulled."
    log "pulling $remote (latest)"
    pull_retry "$remote" || fail "could not pull '$remote' after 3 attempts."
    ollama cp "$remote" "$local_name"
    note "tagged '$remote' as '$local_name' (keeps result filenames comparable)."
  else
    ollama show "$local_name" >/dev/null 2>&1 || fail "no local model '$local_name' and EVAL_PREFIX is empty."
    note "using the local '$local_name' as-is (EVAL_PREFIX empty)."
  fi
}

# A visible GPU does not mean ollama is USING it: a daemon started before the driver existed
# serves from CPU at roughly 14 s/token, which reads as a hang rather than a failure. Prove it
# per model - loading is cheap next to a full eval, and this is the difference between failing
# in two minutes and failing after an hour.
assert_gpu_inference() { # <local model name>
  local model="$1" line
  note "loading '$model' to confirm the daemon serves it from the GPU..."
  curl -sf --max-time 600 "$NL_EVAL_ENDPOINT/api/generate" \
    -H 'Content-Type: application/json' \
    -d "{\"model\":\"$model\",\"prompt\":\"1\",\"stream\":false,\"options\":{\"num_predict\":1}}"     >/dev/null || fail "the ollama daemon could not generate with '$model' at $NL_EVAL_ENDPOINT."
  # Exact match on the NAME column: "phi4-f8" is a prefix of "phi4-f8-mini", so a substring
  # match reports one model's processor as the other's.
  line="$(ollama ps 2>/dev/null | awk -v m="$model" 'NR>1 && ($1==m || $1==m":latest"){print; exit}')"
  if [ -z "$line" ]; then
    note "WARNING: 'ollama ps' does not list '$model' after a load; cannot confirm the processor."
    return 0
  fi
  case "$line" in
    *"100% CPU"*)
      fail "the daemon is serving '$model' 100% on the CPU ($line). At ~14 s/token the eval cannot finish; the GPU driver is present but ollama did not pick it up - restart the daemon AFTER the driver install." ;;
    *CPU*)
      note "WARNING: partial CPU offload for '$model' ($line) - the model may not fit in VRAM. Continuing, slowly." ;;
    *)
      note "GPU-resident: $line" ;;
  esac
}

# ---------------------------------------------------------------------------------------------
# The run itself.
# ---------------------------------------------------------------------------------------------

evaluate() { # <local model name>
  local model="$1"
  local out="$FT/eval/results/baseline-${model//[^[:alnum:]._-]/_}.json"

  assert_gpu_inference "$model"

  # A leftover results file would be RESUMED row-by-row, mixing a previous build's drafts into a
  # run whose whole point is measuring the freshly pulled one. So it must not stay - but it must
  # not be DELETED either: eval/results/ is gitignored, so it can be the only copy of an earlier
  # measurement, and everything from here to the copy at the end of this function can still fail.
  # Move it aside instead. (This runs after the GPU proof for the same reason.)
  if [ -f "$out" ]; then
    local kept="$out.pre-$(date -u +%Y%m%dT%H%M%SZ)"
    mv "$out" "$kept"
    note "kept the previous $(basename "$out") as $(basename "$kept")."
  fi

  log "evaluating '$model' (delegate + plugin rows, --semantic)"
  local started ended rc=0
  started="$(date -u +%s)"
  # No pipe into tee: under pipefail a broken pipe would mask the harness's own exit code.
  set +e
  NL_EVAL_MODEL="$model" \
  NL_EVAL_F8="$NL_EVAL_F8" \
  NL_EVAL_ENDPOINT="$NL_EVAL_ENDPOINT" \
    timeout "$EVAL_TIMEOUT" npx tsx "$FT/eval/baseline.ts" --semantic
  rc=$?
  set -e
  ended="$(date -u +%s)"

  stage_partial(){ [ -f "$out" ] && cp "$out" "$RESULTS_OUT/partial-$(basename "$out")" 2>/dev/null || true; }
  if [ "$rc" = 124 ]; then
    stage_partial
    fail "'$model' hit the $EVAL_TIMEOUT per-model cap. The partial set was staged into $RESULTS_OUT."
  fi
  [ "$rc" = 0 ] || { stage_partial; fail "baseline.ts exited $rc for '$model' (a harness error, not a low score)."; }
  [ -f "$out" ] || fail "baseline.ts reported success for '$model' but wrote no $out."

  # Honest status. baseline.ts exits 0 on a run where every single row FAILED, and a resumed or
  # empty run summarises over zero rows (percent() prints "-"), so the count is the only thing
  # that separates "measured and bad" from "never measured".
  node --input-type=commonjs - "$out" "$EXPECT_ROWS" "$EXPECT_PLUGIN_ROWS" "$model" "$((ended - started))" <<'NODE' || { stage_partial; exit 1; }
// Script comes in on STDIN, so argv is [node, '-', ...args]: args start at 2, NOT at 1 as
// they do for `node -e` (which count_rows above relies on). Getting this wrong shifts every
// argument and the verdict misreads a complete run as an empty one.
const [file, wantRows, wantPlugins, model, secs] = process.argv.slice(2);
const r = require(file);
const rows = Array.isArray(r.rows) ? r.rows : [];
const plugins = Array.isArray(r.pluginRows) ? r.pluginRows : [];
const problems = [];
if (rows.length !== Number(wantRows)) problems.push(`evaluated ${rows.length} delegate rows, expected ${wantRows}`);
if (plugins.length !== Number(wantPlugins)) problems.push(`evaluated ${plugins.length} plugin rows, expected ${wantPlugins}`);
// --semantic is always passed by this script, so no applicable verdicts at all means the FT-8
// element-set gate silently did nothing (a fixture or /subgraph problem), not that it passed.
if (!rows.some((x) => x.semanticApplicable)) problems.push('no FT-8 semantic verdicts were produced at all');
if (problems.length) {
  console.error(`[eval] ERROR: '${model}' produced an INCOMPLETE result set: ${problems.join('; ')}.`);
  console.error('[eval]        Refusing to record it as a run - a partial set reads as a real measurement.');
  process.exit(1);
}
const pct = (n, d) => (d === 0 ? '-' : `${Math.round((100 * n) / d)}%`);
const applicable = rows.filter((x) => x.semanticApplicable);
const line = {
  model,
  rows: rows.length,
  compile: pct(rows.filter((x) => x.compileValid).length, rows.length),
  proxyPass: pct(rows.filter((x) => x.pass).length, rows.length),
  semantic: pct(applicable.filter((x) => x.semanticPass).length, applicable.length),
  semanticN: applicable.length,
  pluginRows: plugins.length,
  pluginCompile: pct(plugins.filter((x) => x.compileValid).length, plugins.length),
  wallSeconds: Number(secs),
  failedIds: rows.filter((x) => !x.pass).map((x) => x.id)
    .concat(plugins.filter((x) => !x.pass).map((x) => x.id)),
};
require('node:fs').writeFileSync(
  process.env.RESULTS_OUT + '/line-' + model.replace(/[^\w.-]/g, '_') + '.json',
  JSON.stringify(line, null, 2),
);
console.log(`[eval] ${model}: ${line.rows} rows, compile ${line.compile}, proxy ${line.proxyPass}, ` +
  `FT-8 ${line.semantic} (n=${line.semanticN}), plugins ${line.pluginCompile} of ${line.pluginRows}`);
NODE

  cp "$out" "$RESULTS_OUT/"
  # Free the VRAM before the next, larger variant loads.
  ollama stop "$model" >/dev/null 2>&1 || true
}

EVALUATED=""
for v in $VARIANTS; do
  acquire "$v"
  evaluate "$v"
  EVALUATED="$EVALUATED $v"
done

# These are a comparison courtesy, not the measurement. A failure here must never discard a
# completed evaluation of the real variants, so each runs in a subshell that contains fail()'s
# exit, and the summary is still written below.
for b in $EVAL_BASELINES; do
  log "baseline comparison model: $b"
  if ! pull_retry "$b"; then
    note "WARNING: could not pull the comparison model '$b'; skipping it."
    continue
  fi
  if ( evaluate "$b" ); then
    EVALUATED="$EVALUATED $b"
  else
    note "WARNING: the comparison model '$b' failed to evaluate; continuing without it."
  fi
done

# ---------------------------------------------------------------------------------------------
# One combined summary, so the operator reads a verdict rather than five JSON files.
# ---------------------------------------------------------------------------------------------

log "summary"
node --input-type=commonjs - "$RESULTS_OUT" <<'NODE'
const fs = require('node:fs');
const dir = process.argv[2]; // stdin script: see the argv note above
const lines = fs.readdirSync(dir).filter((f) => f.startsWith('line-') && f.endsWith('.json'))
  .map((f) => JSON.parse(fs.readFileSync(dir + '/' + f, 'utf8')));
if (!lines.length) { console.error('[eval] ERROR: no per-model summaries were produced.'); process.exit(1); }
lines.sort((a, b) => a.model.localeCompare(b.model));
const head = ['model', 'rows', 'compile', 'proxyPass', 'semantic', 'semanticN', 'pluginCompile', 'wallSeconds'];
const md = ['# Evaluation run', '',
  '| ' + head.join(' | ') + ' |',
  '| ' + head.map(() => '---').join(' | ') + ' |',
  ...lines.map((l) => '| ' + head.map((h) => String(l[h])).join(' | ') + ' |'),
  '', '## Rows that did not pass', ''];
for (const l of lines) {
  md.push(`- **${l.model}**: ` + (l.failedIds.length ? l.failedIds.join(', ') : '(none)'));
}
fs.writeFileSync(dir + '/summary.md', md.join('\n') + '\n');
fs.writeFileSync(dir + '/summary.json', JSON.stringify({ models: lines }, null, 2));
console.table(lines.map((l) => {
  const { failedIds, ...rest } = l;
  return rest;
}));
for (const l of lines) {
  if (l.failedIds.length) console.log(`${l.model} did not pass: ${l.failedIds.join(', ')}`);
}
NODE

echo
note "collected in $RESULTS_OUT:"
ls -la "$RESULTS_OUT"
log "evaluated:$EVALUATED"
