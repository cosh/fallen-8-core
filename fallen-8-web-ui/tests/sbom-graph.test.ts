import { describe, expect, it } from "vitest";
import { sbomToGraph, type SpdxSbom } from "../src/lib/sbomGraph";

/**
 * The SBOM → graph transform (feature sample-graphs) is shared by the build script's
 * fallen8-deps and the Studio's live GitHub card, so its contract is pinned here: node
 * per package (label = ecosystem, emoji icon, license/version/purl props), edge per
 * resolvable DEPENDS_ON, and unresolvable edges dropped so every file imports.
 */
describe("sbomToGraph", () => {
  const sbom: SpdxSbom = {
    packages: [
      {
        name: "root/app",
        SPDXID: "SPDXRef-root",
        versionInfo: "main",
        licenseDeclared: "MIT",
        externalRefs: [{ referenceType: "purl", referenceLocator: "pkg:github/root/app@main" }],
      },
      {
        name: "react",
        SPDXID: "SPDXRef-npm-react",
        versionInfo: "19.0.0",
        licenseConcluded: "MIT",
        externalRefs: [{ referenceType: "purl", referenceLocator: "pkg:npm/react@19.0.0" }],
      },
      {
        name: "numpy",
        SPDXID: "SPDXRef-pypi-numpy",
        externalRefs: [{ referenceType: "purl", referenceLocator: "pkg:pypi/numpy@2.0" }],
      },
    ],
    relationships: [
      { spdxElementId: "SPDXRef-DOCUMENT", relatedSpdxElement: "SPDXRef-root", relationshipType: "DESCRIBES" },
      { spdxElementId: "SPDXRef-root", relatedSpdxElement: "SPDXRef-npm-react", relationshipType: "DEPENDS_ON" },
      { spdxElementId: "SPDXRef-root", relatedSpdxElement: "SPDXRef-pypi-numpy", relationshipType: "DEPENDS_ON" },
      // Dangling: target has no package entry — must be dropped so the file imports.
      { spdxElementId: "SPDXRef-root", relatedSpdxElement: "SPDXRef-missing", relationshipType: "DEPENDS_ON" },
    ],
  };

  it("maps packages to labeled nodes with ecosystem, icon and metadata", () => {
    const { vertices, ecosystemCounts } = sbomToGraph(sbom);
    expect(vertices).toHaveLength(3);

    const react = vertices[1];
    expect(react.label).toBe("npm");
    expect(react.properties?.ecosystem.value).toBe("npm");
    expect(react.properties?.icon.value).toBe("📦");
    expect(react.properties?.version.value).toBe("19.0.0");
    expect(react.properties?.license.value).toBe("MIT");
    expect(react.properties?.purl.value).toBe("pkg:npm/react@19.0.0");

    // numpy has no license → no license property (not an empty string).
    expect(vertices[2].properties?.license).toBeUndefined();
    expect(vertices[2].properties?.icon.value).toBe("🐍");

    expect(ecosystemCounts).toEqual({ github: 1, npm: 1, pypi: 1 });
  });

  it("keeps only resolvable DEPENDS_ON edges", () => {
    const { edges } = sbomToGraph(sbom);
    // 3 DEPENDS_ON in the file, but one targets a missing package → 2 survive; DESCRIBES ignored.
    expect(edges).toHaveLength(2);
    for (const edge of edges) {
      expect(edge.edgePropertyId).toBe("dependsOn");
      expect(edge.source).toBe(0); // the root
    }
    expect(edges.map((e) => e.target).sort()).toEqual([1, 2]);
  });

  it("tolerates an empty SBOM", () => {
    const empty = sbomToGraph({});
    expect(empty.vertices).toHaveLength(0);
    expect(empty.edges).toHaveLength(0);
  });
});
