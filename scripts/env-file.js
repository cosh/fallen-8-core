// MIT License
//
// env-file.js
//
// Copyright (c) 2011-2026 Henning Rauch
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

// Reads the root .env file - the SAME file docker compose interpolates ${VAR} references
// from - so the helper scripts and compose resolve a variable identically. Why that matters:
// env-up.js makes Node-side decisions from process.env (the Nahil overlay selector, the
// profile toggles, the GPU force), and Node does not read .env on its own. Without this,
// F8_NAHIL_API_KEY in .env would interpolate fine but never SELECT the overlay, quietly
// starting the local sidecar instead - the file is how a credential is configured once
// rather than per shell (.env is gitignored).
//
// Parsing is the subset of compose's dotenv that matters here: KEY=VALUE, an optional
// `export ` prefix, full-line # comments, a trailing " # comment" on unquoted values,
// matching single/double quotes stripped. Values are literal - no escape or ${VAR}
// expansion. Precedence matches compose too: the shell always wins, so applyDotEnv() only
// fills keys the process environment does not already have (an empty shell value counts
// as set, exactly as compose treats it).

const fs = require('fs');
const path = require('path');

const DEFAULT_PATH = path.join(__dirname, '..', '.env');

function parseDotEnv(file = DEFAULT_PATH) {
  let content;
  try {
    content = fs.readFileSync(file, 'utf8');
  } catch {
    return {};
  }
  // A .env written by PowerShell 5.1 (Set-Content -Encoding utf8) starts with a BOM, which
  // would glue itself to the first line's key and silently drop that variable.
  if (content.charCodeAt(0) === 0xfeff) content = content.slice(1);
  const vars = {};
  for (const rawLine of content.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (line === '' || line.startsWith('#')) continue;
    const eq = line.indexOf('=');
    if (eq <= 0) continue;
    const key = line.slice(0, eq).trim().replace(/^export\s+/, '');
    if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(key)) continue;
    let value = line.slice(eq + 1).trim();
    const quote = value[0];
    if ((quote === '"' || quote === "'") && value.length > 1 && value.endsWith(quote)) {
      value = value.slice(1, -1);
    } else {
      const comment = value.search(/\s#/);
      if (comment !== -1) value = value.slice(0, comment).trim();
    }
    vars[key] = value;
  }
  return vars;
}

function applyDotEnv(file = DEFAULT_PATH) {
  const vars = parseDotEnv(file);
  for (const [key, value] of Object.entries(vars)) {
    if (process.env[key] === undefined) process.env[key] = value;
  }
  return vars;
}

module.exports = { parseDotEnv, applyDotEnv };
