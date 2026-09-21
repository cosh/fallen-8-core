// MIT License
//
// sbom-canonicalize.test.ts
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

import { describe, expect, it } from "vitest";
import { canonicalizeSbom, sbomContentEquals } from "../scripts/samples/fallen8Deps";
import { prop } from "../src/lib/jsonlGraph";
import { sbomToGraph, type SpdxSbom } from "../src/lib/sbomGraph";

/**
 * The refresh guard (feature sidecar-shared-options §7). GitHub's dependency-graph endpoint
 * answers differently on every call whether or not a dependency moved: a fresh
 * documentNamespace UUID, a fresh creationInfo timestamp, a creators entry carrying the
 * generator's own build id, and the packages in no stable order. Ten CI commits in one month
 * each rewrote ~11,600 lines of two committed files on that basis; the last of them changed
 * four packages of 1086.
 *
 * So two properties are pinned here. Canonicalizing puts the two unordered collections in a
 * fixed order, and the content comparison ignores exactly the three volatile fields and
 * nothing else, because a comparison that ignored one field too many would stop noticing a
 * real dependency change, which is the failure that actually matters.
 */

/** A document with everything the real one carries, including the fields the type does not declare. */
function sbom(
  packages: Array<{ SPDXID: string; name: string; versionInfo?: string }>,
  relationships: Array<[string, string]> = [],
  metadata: Record<string, unknown> = {},
): SpdxSbom {
  return {
    spdxVersion: "SPDX-2.3",
    dataLicense: "CC0-1.0",
    SPDXID: "SPDXRef-DOCUMENT",
    name: "com.github.cosh/fallen-8-core",
    documentNamespace: "https://spdx.org/spdxdocs/protobom/00000000-0000-0000-0000-000000000000",
    creationInfo: { creators: ["Tool: protobom-v0.0.0-old"], created: "2026-01-01T00:00:00Z" },
    ...metadata,
    packages: packages.map((p) => ({ downloadLocation: "NOASSERTION", filesAnalyzed: false, ...p })),
    relationships: relationships.map(([from, to]) => ({
      spdxElementId: from,
      relatedSpdxElement: to,
      relationshipType: "DEPENDS_ON",
    })),
  } as SpdxSbom;
}

const THREE = [
  { SPDXID: "SPDXRef-npm-zod-3.0.0", name: "zod", versionInfo: "3.0.0" },
  { SPDXID: "SPDXRef-npm-alpha-1.0.0", name: "alpha", versionInfo: "1.0.0" },
  { SPDXID: "SPDXRef-nuget-Middle-2.0.0", name: "Middle", versionInfo: "2.0.0" },
];

describe("canonicalizeSbom", () => {
  it("puts packages in SPDXID order", () => {
    const canonical = canonicalizeSbom(sbom(THREE));

    expect(canonical.packages?.map((p) => p.SPDXID)).toEqual([
      "SPDXRef-npm-alpha-1.0.0",
      "SPDXRef-npm-zod-3.0.0",
      "SPDXRef-nuget-Middle-2.0.0",
    ]);
  });

  it("orders by code point, not by locale, so two machines agree", () => {
    // The uppercase M sorts BEFORE lowercase letters ordinally and after them in most
    // locale-aware collations. A locale-sensitive sort here would reintroduce exactly the
    // per-machine churn the canonicalization exists to remove.
    const canonical = canonicalizeSbom(
      sbom([
        { SPDXID: "SPDXRef-a-lower", name: "a" },
        { SPDXID: "SPDXRef-B-upper", name: "B" },
      ]),
    );

    expect(canonical.packages?.map((p) => p.SPDXID)).toEqual(["SPDXRef-B-upper", "SPDXRef-a-lower"]);
  });

  it("is idempotent and independent of the input order", () => {
    const once = canonicalizeSbom(sbom(THREE));
    const shuffled = canonicalizeSbom(sbom([THREE[2], THREE[0], THREE[1]]));

    expect(JSON.stringify(canonicalizeSbom(once))).toEqual(JSON.stringify(once));
    expect(JSON.stringify(shuffled)).toEqual(JSON.stringify(once));
  });

  it("puts relationships in a total order", () => {
    const canonical = canonicalizeSbom(
      sbom(THREE, [
        ["SPDXRef-npm-zod-3.0.0", "SPDXRef-npm-alpha-1.0.0"],
        ["SPDXRef-npm-alpha-1.0.0", "SPDXRef-nuget-Middle-2.0.0"],
        ["SPDXRef-npm-alpha-1.0.0", "SPDXRef-npm-zod-3.0.0"],
      ]),
    );

    expect(canonical.relationships?.map((r) => `${r.spdxElementId}>${r.relatedSpdxElement}`)).toEqual([
      "SPDXRef-npm-alpha-1.0.0>SPDXRef-npm-zod-3.0.0",
      "SPDXRef-npm-alpha-1.0.0>SPDXRef-nuget-Middle-2.0.0",
      "SPDXRef-npm-zod-3.0.0>SPDXRef-npm-alpha-1.0.0",
    ]);
  });

  it("breaks an SPDXID tie without falling back to input order", () => {
    // SPDXID is meant to be unique. If it ever is not, a tie left to sort stability would put
    // the rows in whatever order the endpoint sent them, which is the churn again.
    const forward = canonicalizeSbom(
      sbom([
        { SPDXID: "SPDXRef-dup", name: "second", versionInfo: "2" },
        { SPDXID: "SPDXRef-dup", name: "first", versionInfo: "1" },
      ]),
    );
    const backward = canonicalizeSbom(
      sbom([
        { SPDXID: "SPDXRef-dup", name: "first", versionInfo: "1" },
        { SPDXID: "SPDXRef-dup", name: "second", versionInfo: "2" },
      ]),
    );

    expect(forward.packages?.map((p) => p.name)).toEqual(["first", "second"]);
    expect(JSON.stringify(backward)).toEqual(JSON.stringify(forward));
  });

  it("keeps every document field the file has to carry", () => {
    const canonical = canonicalizeSbom(sbom(THREE)) as SpdxSbom & Record<string, unknown>;

    // The written file is the whole SPDX document, not just the two collections: dropping any
    // of these would produce an invalid SBOM that still passed a content comparison.
    expect(canonical.spdxVersion).toBe("SPDX-2.3");
    expect(canonical.dataLicense).toBe("CC0-1.0");
    expect(canonical.SPDXID).toBe("SPDXRef-DOCUMENT");
    expect(canonical.name).toBe("com.github.cosh/fallen-8-core");
    expect(canonical.documentNamespace).toContain("spdx.org");
    expect(canonical.creationInfo).toBeTruthy();
  });

  it("leaves a document with neither collection alone", () => {
    const empty = canonicalizeSbom({} as SpdxSbom);

    expect(empty.packages).toBeUndefined();
    expect(empty.relationships).toBeUndefined();
  });
});

describe("sbomContentEquals", () => {
  it("ignores the three fields the endpoint regenerates on every call", () => {
    const committed = sbom(THREE, [["SPDXRef-npm-zod-3.0.0", "SPDXRef-npm-alpha-1.0.0"]]);
    const refetched = sbom(THREE, [["SPDXRef-npm-zod-3.0.0", "SPDXRef-npm-alpha-1.0.0"]], {
      documentNamespace: "https://spdx.org/spdxdocs/protobom/ffffffff-ffff-ffff-ffff-ffffffffffff",
      creationInfo: { creators: ["Tool: protobom-v0.0.0-brand-new"], created: "2026-09-20T07:45:03Z" },
    });

    expect(sbomContentEquals(committed, refetched)).toBe(true);
  });

  it("ignores a pure reordering", () => {
    const committed = sbom(THREE, [
      ["SPDXRef-npm-zod-3.0.0", "SPDXRef-npm-alpha-1.0.0"],
      ["SPDXRef-npm-alpha-1.0.0", "SPDXRef-npm-zod-3.0.0"],
    ]);
    const refetched = sbom([THREE[1], THREE[2], THREE[0]], [
      ["SPDXRef-npm-alpha-1.0.0", "SPDXRef-npm-zod-3.0.0"],
      ["SPDXRef-npm-zod-3.0.0", "SPDXRef-npm-alpha-1.0.0"],
    ]);

    expect(sbomContentEquals(committed, refetched)).toBe(true);
  });

  it("notices an added package, a removed one and a version bump", () => {
    const committed = sbom(THREE);

    expect(
      sbomContentEquals(
        committed,
        sbom([...THREE, { SPDXID: "SPDXRef-npm-new-1.0.0", name: "new", versionInfo: "1.0.0" }]),
      ),
      "an added dependency is the whole reason the file is refreshed",
    ).toBe(false);

    expect(sbomContentEquals(committed, sbom([THREE[0], THREE[1]])), "a removed dependency").toBe(
      false,
    );

    expect(
      sbomContentEquals(
        committed,
        sbom([{ ...THREE[0], versionInfo: "3.0.1", SPDXID: "SPDXRef-npm-zod-3.0.1" }, THREE[1], THREE[2]]),
      ),
      "a version bump",
    ).toBe(false);
  });

  it("ignores a reordering of the FIELDS inside a package", () => {
    // Measured before the key-stable comparison existed: this returned false, so a generator
    // upgrade that reordered struct fields would have rewritten 11,600 lines to say nothing. The
    // endpoint's own creators entry shows that generator is versioned, so it is a real path.
    const committed = sbom([{ SPDXID: "SPDXRef-x", name: "zod", versionInfo: "3.0.0" }]);
    const reordered = {
      ...committed,
      packages: [
        {
          versionInfo: "3.0.0",
          name: "zod",
          filesAnalyzed: false,
          SPDXID: "SPDXRef-x",
          downloadLocation: "NOASSERTION",
        },
      ],
    } as unknown as SpdxSbom;

    expect(sbomContentEquals(committed, reordered)).toBe(true);
  });

  it("ignores a reordering of the document's own top-level fields", () => {
    const committed = sbom(THREE);
    const reordered = Object.fromEntries(
      Object.entries(committed as unknown as Record<string, unknown>).reverse(),
    ) as unknown as SpdxSbom;

    expect(sbomContentEquals(committed, reordered)).toBe(true);
  });

  it("still notices a renamed field, which a key sort must not hide", () => {
    // The danger of sorting keys is that it stops distinguishing documents it should. A package
    // whose version moved to a differently-named field is a different document.
    const committed = sbom([{ SPDXID: "SPDXRef-x", name: "zod", versionInfo: "3.0.0" }]);
    const renamed = {
      ...committed,
      packages: [{ SPDXID: "SPDXRef-x", name: "zod", version: "3.0.0" }],
    } as unknown as SpdxSbom;

    expect(sbomContentEquals(committed, renamed)).toBe(false);
  });

  it("notices a changed relationship even when the packages are identical", () => {
    const committed = sbom(THREE, [["SPDXRef-npm-zod-3.0.0", "SPDXRef-npm-alpha-1.0.0"]]);
    const rewired = sbom(THREE, [["SPDXRef-npm-zod-3.0.0", "SPDXRef-nuget-Middle-2.0.0"]]);

    expect(sbomContentEquals(committed, rewired)).toBe(false);
  });

  it("notices a changed licence, which is metadata a reader of the sample sees", () => {
    const committed = sbom(THREE);
    const relicensed = canonicalizeSbom(sbom(THREE));
    relicensed.packages![0] = { ...relicensed.packages![0], licenseConcluded: "AGPL-3.0" };

    expect(sbomContentEquals(committed, relicensed)).toBe(false);
  });
});

describe("the canonical order and the derived sample", () => {
  it("renumbers vertices by the sorted position, so the sample is stable too", () => {
    // sbomToGraph assigns each vertex the package's INDEX, and its own comment says packages
    // keep their SBOM order for stable ids - which was true of the transform and false of its
    // input. After canonicalization the id of a package depends on its SPDXID rather than on
    // what order the endpoint happened to answer in.
    const fromOneOrder = sbomToGraph(canonicalizeSbom(sbom(THREE)));
    const fromAnother = sbomToGraph(canonicalizeSbom(sbom([THREE[2], THREE[0], THREE[1]])));

    expect(fromOneOrder.vertices.map((v) => v.properties?.name)).toEqual(
      fromAnother.vertices.map((v) => v.properties?.name),
    );
    expect(fromOneOrder.vertices[0].properties?.name).toEqual(prop.string("alpha"));
  });

  it("keeps edge endpoints pointing at the same packages after the sort", () => {
    const graph = sbomToGraph(
      canonicalizeSbom(sbom(THREE, [["SPDXRef-npm-zod-3.0.0", "SPDXRef-npm-alpha-1.0.0"]])),
    );

    expect(graph.edges).toHaveLength(1);
    const [edge] = graph.edges;
    expect(graph.vertices[edge.source].properties?.name).toEqual(prop.string("zod"));
    expect(graph.vertices[edge.target].properties?.name).toEqual(prop.string("alpha"));
  });
});
