// MIT License
//
// sbom-refresh-guard.test.ts
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

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { buildFallen8Deps, type SbomFile } from "../scripts/samples/fallen8Deps";
import type { SpdxSbom } from "../src/lib/sbomGraph";

/**
 * The half of the refresh guard that `sbom-canonicalize.test.ts` cannot reach. That file pins the
 * two pure functions; this one drives the decision they exist for, which lives in `loadSbom` and is
 * reachable only through `buildFallen8Deps`: a refetch whose content is unchanged must leave the
 * committed file ALONE, so its timestamp goes on meaning "when the dependencies last actually
 * changed" and the refresh workflow's porcelain guard finds nothing to commit. Ten CI commits in
 * one month each rewrote about 11,600 lines before that branch existed, and nothing covered it.
 *
 * Both halves are here on purpose. "Does not write when unchanged" proves nothing on its own: a
 * seam that swallowed every write would pass it, which is why the changed case sits beside it.
 *
 * The file is INJECTED, not mocked, and that was learned the hard way rather than chosen. Mocking
 * `node:fs` does not reach the module under test: the unchanged assertion went green while the real
 * write went through, so the test passed AND had replaced the committed 26,000-line document with
 * the fixture below. The seam makes that impossible, and `assertNeverTheRealFile` keeps it so.
 */

/** A document carrying the fields the real one does, in the shape the endpoint returns. */
function sbom(
  packages: Array<{ SPDXID: string; name: string; versionInfo: string }>,
  volatileSuffix: string,
): SpdxSbom {
  return {
    spdxVersion: "SPDX-2.3",
    dataLicense: "CC0-1.0",
    SPDXID: "SPDXRef-DOCUMENT",
    name: "com.github.cosh/fallen-8-core",
    documentNamespace: `https://spdx.org/spdxdocs/protobom/${volatileSuffix}`,
    creationInfo: {
      creators: [`Tool: protobom-${volatileSuffix}`],
      created: `2026-0${volatileSuffix.length}-01T00:00:00Z`,
    },
    packages: packages.map((p) => ({
      downloadLocation: "NOASSERTION",
      filesAnalyzed: false,
      ...p,
    })),
    relationships: packages.map((p) => ({
      spdxElementId: "SPDXRef-DOCUMENT",
      relatedSpdxElement: p.SPDXID,
      relationshipType: "DEPENDS_ON",
    })),
  } as SpdxSbom;
}

const TWO = [
  { SPDXID: "SPDXRef-npm-zod-3.0.0", name: "zod", versionInfo: "3.0.0" },
  { SPDXID: "SPDXRef-nuget-Serilog-4.0.0", name: "Serilog", versionInfo: "4.0.0" },
];

const THREE = [...TWO, { SPDXID: "SPDXRef-npm-new-1.0.0", name: "new", versionInfo: "1.0.0" }];

/** The committed copy the refetch is compared against, served in place of the real file. */
const COMMITTED = sbom(TWO, "aa");

/** Records what the module asked of the file, and touches no filesystem at all. */
function spyFile(committed: SpdxSbom): SbomFile & { written: string[]; reads: number } {
  const state = {
    written: [] as string[],
    reads: 0,
    read() {
      state.reads += 1;
      return JSON.stringify(committed);
    },
    write(contents: string) {
      state.written.push(contents);
    },
  };
  return state;
}

function answerWith(fetched: SpdxSbom) {
  vi.stubGlobal(
    "fetch",
    vi.fn(async () => ({ ok: true, json: async () => ({ sbom: fetched }) })),
  );
}

/**
 * The seam is the only thing this suite may write through. Asserting the module ASKED it is what
 * distinguishes "the write branch did not run" from "the seam was bypassed", and only the first of
 * those is a pass.
 */
function assertNeverTheRealFile(file: { reads: number }) {
  expect(
    file.reads,
    "the module never read through the injected seam, so it is reading the real committed file",
  ).toBeGreaterThan(0);
}

describe("the SBOM refresh guard", () => {
  beforeEach(() => {
    process.env.F8_DEPS_REFETCH = "1";
  });

  afterEach(() => {
    delete process.env.F8_DEPS_REFETCH;
    vi.unstubAllGlobals();
  });

  it("refetches, finds the dependencies unchanged, and leaves the file as it is", async () => {
    // Same packages, fresh volatile fields: exactly what the endpoint answers when nothing moved.
    answerWith(sbom(TWO, "bbbb"));
    const file = spyFile(COMMITTED);

    const built = await buildFallen8Deps(file);

    assertNeverTheRealFile(file);
    expect(file.written, "an unchanged refetch must not rewrite the committed document").toEqual([]);
    expect(built.entry.vertexCount).toBeGreaterThan(0);
    expect(built.jsonl.length).toBeGreaterThan(0);
  });

  it("refetches, finds a dependency added, and stores the new document", async () => {
    answerWith(sbom(THREE, "cccc"));
    const file = spyFile(COMMITTED);

    await buildFallen8Deps(file);

    assertNeverTheRealFile(file);
    expect(file.written).toHaveLength(1);
    const stored = JSON.parse(file.written[0]) as SpdxSbom;
    expect(stored.packages).toHaveLength(THREE.length);
    expect(file.written[0].endsWith("\n"), "the stored file keeps its trailing newline").toBe(true);
  });

  it("reads the committed copy and asks nothing of the network when no refetch was requested", async () => {
    delete process.env.F8_DEPS_REFETCH;
    const fetchSpy = vi.fn();
    vi.stubGlobal("fetch", fetchSpy);
    const file = spyFile(COMMITTED);

    await buildFallen8Deps(file);

    assertNeverTheRealFile(file);
    expect(fetchSpy, "an ordinary build must not reach the rate-limited endpoint").not.toHaveBeenCalled();
    expect(file.written).toEqual([]);
  });

  /**
   * The DEFAULT seam, which the three tests above replace and therefore cannot check. Without this
   * the injected parameter could be wired to a default that no longer reads the real document and
   * every test here would still pass, which is the shape a seam most easily rots into.
   *
   * Safe to run against the real file: with no refetch requested this path only reads.
   */
  it("defaults to the real committed document, which parses and derives a sample", async () => {
    delete process.env.F8_DEPS_REFETCH;
    const fetchSpy = vi.fn();
    vi.stubGlobal("fetch", fetchSpy);

    const built = await buildFallen8Deps();

    expect(fetchSpy).not.toHaveBeenCalled();
    expect(built.entry.id).toBe("fallen8-deps");
    // The real document carries a thousand or so packages, so this is the default seam having
    // genuinely read it rather than a fixture that happened to be in scope.
    expect(built.entry.vertexCount).toBeGreaterThan(100);
  });
});
