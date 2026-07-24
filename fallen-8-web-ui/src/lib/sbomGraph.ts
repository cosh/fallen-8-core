/**
 * SPDX SBOM → Fallen-8 graph (feature sample-graphs). ONE transform, two consumers: the
 * build script bakes the fallen-8-core default (fallen8-deps.jsonl), and the Studio's
 * GitHub card runs it in the browser on any public repo's SBOM fetched just-in-time from
 * api.github.com. Output is the jsonlGraph vertex/edge shape, so both paths hand it to
 * the same importer + loader.
 */

import type { JsonlEdge, JsonlVertex } from "./jsonlGraph";
import { prop } from "./jsonlGraph";

/** The subset of the SPDX SBOM document we read (GitHub's dependency-graph export). */
export interface SpdxSbom {
  packages?: SpdxPackage[];
  relationships?: SpdxRelationship[];
}

interface SpdxPackage {
  name: string;
  SPDXID: string;
  versionInfo?: string;
  licenseConcluded?: string;
  licenseDeclared?: string;
  externalRefs?: Array<{ referenceType?: string; referenceLocator?: string }>;
}

interface SpdxRelationship {
  spdxElementId: string;
  relatedSpdxElement: string;
  relationshipType: string;
}

/** Ecosystem → node emoji; the transform reads the ecosystem off each package's purl. */
const ECOSYSTEM_EMOJI: Record<string, string> = {
  npm: "📦",
  pypi: "🐍",
  nuget: "💠",
  githubactions: "⚙️",
  github: "🏠",
  golang: "🐹",
  maven: "☕",
  cargo: "🦀",
  composer: "🐘",
  rubygems: "💎",
};

const DEFAULT_EMOJI = "📄";

interface ParsedPurl {
  ecosystem: string;
  purl: string;
}

function parsePurl(pkg: SpdxPackage): ParsedPurl {
  const ref = pkg.externalRefs?.find((r) => r.referenceType === "purl");
  const purl = ref?.referenceLocator ?? "";
  // pkg:<ecosystem>/<namespace?>/<name>@<version>
  const match = /^pkg:([^/]+)\//.exec(purl);
  return { ecosystem: match ? match[1].toLowerCase() : "unknown", purl };
}

function licenseOf(pkg: SpdxPackage): string | null {
  const license = pkg.licenseConcluded ?? pkg.licenseDeclared;
  return license && license !== "NOASSERTION" ? license : null;
}

export interface SbomGraphResult {
  vertices: JsonlVertex[];
  edges: JsonlEdge[];
  /** Ecosystem → package count, for the caller's summary/messaging. */
  ecosystemCounts: Record<string, number>;
}

/**
 * Transforms an SPDX SBOM into a dependency graph. Nodes are packages (label =
 * ecosystem); edges are DEPENDS_ON relationships. A package referenced only by a
 * relationship (no package entry) is skipped, so every edge resolves — the importer
 * requires it. Deterministic: packages keep their SBOM order for stable ids.
 */
export function sbomToGraph(sbom: SpdxSbom): SbomGraphResult {
  const packages = sbom.packages ?? [];
  const relationships = sbom.relationships ?? [];

  const idBySpdx = new Map<string, number>();
  const vertices: JsonlVertex[] = [];
  const ecosystemCounts: Record<string, number> = {};

  packages.forEach((pkg, index) => {
    idBySpdx.set(pkg.SPDXID, index);
    const { ecosystem, purl } = parsePurl(pkg);
    ecosystemCounts[ecosystem] = (ecosystemCounts[ecosystem] ?? 0) + 1;

    const properties: NonNullable<JsonlVertex["properties"]> = {
      name: prop.string(pkg.name),
      ecosystem: prop.string(ecosystem),
      icon: prop.string(ECOSYSTEM_EMOJI[ecosystem] ?? DEFAULT_EMOJI),
    };
    if (pkg.versionInfo) properties.version = prop.string(pkg.versionInfo);
    if (purl) properties.purl = prop.string(purl);
    const license = licenseOf(pkg);
    if (license) properties.license = prop.string(license);

    vertices.push({ id: index, label: ecosystem, properties });
  });

  let edgeId = packages.length;
  const edges: JsonlEdge[] = [];
  for (const rel of relationships) {
    if (rel.relationshipType !== "DEPENDS_ON") continue;
    const source = idBySpdx.get(rel.spdxElementId);
    const target = idBySpdx.get(rel.relatedSpdxElement);
    if (source === undefined || target === undefined) continue;
    edges.push({ id: edgeId++, source, target, edgePropertyId: "dependsOn" });
  }

  return { vertices, edges, ecosystemCounts };
}

/** GitHub's rate-limit headers, passed through so the message can name a retry time. */
export interface GithubRateLimit {
  remaining: string | null;
  reset: string | null;
}

/**
 * Turns a non-OK GitHub SBOM response into a SPECIFIC, actionable message (feature
 * sample-graphs). The `GET /repos/{repo}/dependency-graph/sbom` endpoint answers 404 for
 * THREE different causes — the repo is missing, it's private, or its dependency graph is
 * disabled — so the old "not found (or private, or has no dependency graph)" lumped all
 * three and buried the one the user can act on. GitHub's own error body carries a `message`
 * that distinguishes them (notably "Dependency graph is disabled…"); we lead with that.
 * Pure + parameterized on `now` so it is unit-testable.
 */
export function describeGithubSbomFailure(
  repo: string,
  status: number,
  githubMessage: string,
  rateLimit: GithubRateLimit,
  now: number,
): string {
  const detail = githubMessage.trim();

  // Disabled dependency graph is the actionable case, and GitHub may report it as 403 OR
  // 404 — key off the message, not the status.
  if (/dependency graph is disabled/i.test(detail)) {
    return (
      `GitHub's dependency graph is disabled for '${repo}', so it has no SBOM to fetch. ` +
      `The repo owner can enable it under Settings → Advanced Security → Dependency graph, then retry.`
    );
  }

  if ((status === 403 || status === 429) && rateLimit.remaining === "0" && rateLimit.reset) {
    const mins = Math.max(1, Math.ceil((Number(rateLimit.reset) * 1000 - now) / 60_000));
    return `GitHub's anonymous rate limit is exhausted — try again in ~${mins} min.`;
  }

  if (status === 404) {
    return (
      `Repository '${repo}' was not found. Only a PUBLIC repo works here — a private or ` +
      `non-existent repo returns the same 404.` + (detail ? ` (GitHub: ${detail})` : "")
    );
  }

  if (status === 401 || status === 403) {
    return `GitHub refused the request (${status})${detail ? `: ${detail}` : "."}`;
  }

  return `GitHub returned ${status} for '${repo}'${detail ? `: ${detail}` : "."}`;
}
