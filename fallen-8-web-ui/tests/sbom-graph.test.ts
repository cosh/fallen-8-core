import { describe, expect, it } from "vitest";
import { describeGithubSbomFailure, sbomToGraph, type SpdxSbom } from "../src/lib/sbomGraph";

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

describe("describeGithubSbomFailure", () => {
  const noLimit = { remaining: null, reset: null };
  const now = 1_700_000_000_000;

  it("leads with the actionable 'dependency graph is disabled' case, regardless of status", () => {
    const msg404 = describeGithubSbomFailure("o/r", 404, "Dependency graph is disabled for this repository.", noLimit, now);
    const msg403 = describeGithubSbomFailure("o/r", 403, "Dependency graph is disabled for this repository.", noLimit, now);
    for (const m of [msg404, msg403]) {
      expect(m).toContain("dependency graph is disabled for 'o/r'");
      expect(m).toMatch(/enable it/i);
    }
  });

  it("distinguishes a plain 404 (missing or private) and echoes GitHub's message", () => {
    const m = describeGithubSbomFailure("o/r", 404, "Not Found", noLimit, now);
    expect(m).toContain("was not found");
    expect(m).toContain("PUBLIC");
    expect(m).toContain("(GitHub: Not Found)");
    expect(m).not.toMatch(/disabled/i);
  });

  it("names a retry time when the anonymous rate limit is exhausted", () => {
    const reset = String(Math.floor(now / 1000) + 3 * 60); // 3 minutes out
    const m = describeGithubSbomFailure("o/r", 403, "API rate limit exceeded", { remaining: "0", reset }, now);
    expect(m).toMatch(/rate limit is exhausted/);
    expect(m).toContain("~3 min");
  });

  it("reports a bare 403 refusal (not rate limited) with GitHub's message", () => {
    const m = describeGithubSbomFailure("o/r", 403, "Must have push access", noLimit, now);
    expect(m).toContain("refused the request (403)");
    expect(m).toContain("Must have push access");
  });

  it("falls back to the status for anything else", () => {
    const m = describeGithubSbomFailure("o/r", 500, "", noLimit, now);
    expect(m).toContain("GitHub returned 500 for 'o/r'");
  });
});
