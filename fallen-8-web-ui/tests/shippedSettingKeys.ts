// MIT License
//
// shippedSettingKeys.ts
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

import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

/**
 * Every configuration key this repo's server actually publishes, in the order it declares them,
 * read from the catalog itself rather than copied into a list here.
 *
 * A hand-maintained copy would be the wrong shape: the point of the test that consumes this is that
 * Studio's section taxonomy stays exhaustive as the server grows, and a fixture I have to remember to
 * update goes stale in exactly the case that matters. Reading the catalog means adding a key under a
 * section Studio does not map FAILS, which is the coupling this is for.
 *
 * Not a *.test.ts file, so vitest does not collect it (same reason as tests/resizeObserver.ts).
 */

const CATALOG = join(
  dirname(fileURLToPath(import.meta.url)),
  "..",
  "..",
  "fallen-8-core-apiApp",
  "Configuration",
  "Fallen8SettingCatalog.cs",
);

function readShippedKeys(): string[] {
  const source = readFileSync(CATALOG, "utf8");
  const keys = [...source.matchAll(/"(Fallen8:[A-Za-z0-9:]+)"/g)].map((match) => match[1]);
  // A refactor that stops spelling keys as literals would otherwise turn every consumer into a
  // vacuous pass over an empty list. Fail here, where the reason is legible.
  if (keys.length < 90) {
    throw new Error(
      `Only ${keys.length} setting keys were extracted from ${CATALOG}. The catalog no longer spells ` +
        "its keys as string literals, so this extraction needs updating.",
    );
  }
  const unique = new Set(keys);
  if (unique.size !== keys.length) {
    throw new Error(
      "The catalog extraction found a repeated key, so a literal is being matched that is not a key " +
        "declaration. Tighten the pattern before trusting the order.",
    );
  }
  return keys;
}

export const SHIPPED_SETTING_KEYS: readonly string[] = readShippedKeys();
