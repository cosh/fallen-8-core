/**
 * air-routes — the world's busiest airports and the flights between them, from the
 * OpenFlights open dataset (feature sample-graphs). Distance-weighted Dijkstra (route
 * `km` = haversine), hub analysis (PageRank/degree), and semantic search over a one-line
 * airport description. Country flags render as offline-friendly emoji; ✈️ is the fallback.
 */

import { buildJsonlGraph, prop, type JsonlEdge, type JsonlVertex } from "../../src/lib/jsonlGraph";
import {
  boundVectorIndexRecipe,
  embeddingProperties,
  embedTexts,
  haversineKm,
  type BuiltSample,
} from "./shared";
import { COUNTRY_ISO2 } from "./data/countryIso2";

// Pinned to a fixed OpenFlights commit (not a moving branch) so rebuilds are reproducible:
// the top-250 selection and edge set depend on the exact source rows.
const OPENFLIGHTS_REF = "e3bc6dedbcceb8b7b74248a00dcd6207254da6bd";
const AIRPORTS_URL = `https://raw.githubusercontent.com/jpatokal/openflights/${OPENFLIGHTS_REF}/data/airports.dat`;
const ROUTES_URL = `https://raw.githubusercontent.com/jpatokal/openflights/${OPENFLIGHTS_REF}/data/routes.dat`;
const TOP_AIRPORTS = 250;

interface Airport {
  iata: string;
  name: string;
  city: string;
  country: string;
  lat: number;
  lon: number;
}

/** Parses one CSV line honouring the simple quoting OpenFlights uses. */
function parseCsvLine(line: string): string[] {
  const fields: string[] = [];
  let current = "";
  let inQuotes = false;
  for (let i = 0; i < line.length; i++) {
    const ch = line[i];
    if (ch === '"') inQuotes = !inQuotes;
    else if (ch === "," && !inQuotes) {
      fields.push(current);
      current = "";
    } else current += ch;
  }
  fields.push(current);
  return fields;
}

/** Regional-indicator emoji flag for an ISO-3166 alpha-2 code (offline, no hosting). */
function flagEmoji(iso2: string): string {
  return String.fromCodePoint(...[...iso2.toUpperCase()].map((c) => 0x1f1e6 + c.charCodeAt(0) - 65));
}

export async function buildAirRoutes(): Promise<BuiltSample> {
  const airportsText = await (await fetch(AIRPORTS_URL)).text();
  const routesText = await (await fetch(ROUTES_URL)).text();

  // Airports keyed by IATA (rows without a 3-letter IATA are skipped — they cannot be
  // referenced by the routes file).
  const airports = new Map<string, Airport>();
  for (const line of airportsText.split("\n")) {
    if (!line.trim()) continue;
    const f = parseCsvLine(line);
    const iata = f[4]?.replace(/"/g, "");
    const lat = Number(f[6]);
    const lon = Number(f[7]);
    if (!iata || iata.length !== 3 || Number.isNaN(lat) || Number.isNaN(lon)) continue;
    airports.set(iata, {
      iata,
      name: f[1].replace(/"/g, ""),
      city: f[2].replace(/"/g, ""),
      country: f[3].replace(/"/g, ""),
      lat,
      lon,
    });
  }

  // Count route endpoints per airport to rank hubs; collect deduped undirected pairs.
  const degree = new Map<string, number>();
  const pairSeen = new Set<string>();
  const routePairs: Array<[string, string]> = [];
  for (const line of routesText.split("\n")) {
    if (!line.trim()) continue;
    const f = parseCsvLine(line);
    const src = f[2];
    const dst = f[4];
    if (!airports.has(src) || !airports.has(dst) || src === dst) continue;
    degree.set(src, (degree.get(src) ?? 0) + 1);
    degree.set(dst, (degree.get(dst) ?? 0) + 1);
    const key = src < dst ? `${src}|${dst}` : `${dst}|${src}`;
    if (!pairSeen.has(key)) {
      pairSeen.add(key);
      routePairs.push([src, dst]);
    }
  }

  // Top airports by route degree — deterministic (degree desc, then IATA asc).
  const top = [...degree.entries()]
    .sort((a, b) => b[1] - a[1] || (a[0] < b[0] ? -1 : 1))
    .slice(0, TOP_AIRPORTS)
    .map(([iata]) => iata);
  const topSet = new Set(top);
  const idByIata = new Map(top.map((iata, index) => [iata, index]));

  const descriptions = top.map((iata) => {
    const a = airports.get(iata)!;
    return `${a.iata} — ${a.name} in ${a.city}, ${a.country}.`;
  });
  const { vectors, model, dimension } = await embedTexts(descriptions);

  const vertices: JsonlVertex[] = top.map((iata, index) => {
    const a = airports.get(iata)!;
    const iso2 = COUNTRY_ISO2[a.country];
    return {
      id: index,
      label: "airport",
      properties: {
        iata: prop.string(a.iata),
        name: prop.string(a.name),
        city: prop.string(a.city),
        country: prop.string(a.country),
        lat: prop.double(a.lat),
        lon: prop.double(a.lon),
        icon: prop.string(iso2 ? flagEmoji(iso2) : "✈️"),
        ...embeddingProperties(vectors[index], model),
      },
    };
  });

  let edgeId = TOP_AIRPORTS;
  const edges: JsonlEdge[] = [];
  for (const [src, dst] of routePairs) {
    if (!topSet.has(src) || !topSet.has(dst)) continue;
    const a = airports.get(src)!;
    const b = airports.get(dst)!;
    edges.push({
      id: edgeId++,
      source: idByIata.get(src)!,
      target: idByIata.get(dst)!,
      edgePropertyId: "route",
      properties: { km: prop.double(haversineKm(a.lat, a.lon, b.lat, b.lon)) },
    });
  }

  return {
    jsonl: buildJsonlGraph(vertices, edges),
    entry: {
      id: "air-routes",
      title: "World Air Routes",
      emoji: "✈️",
      pitch: `The ${TOP_AIRPORTS} busiest airports and the flights between them (OpenFlights) — shortest paths weighted by real distance.`,
      vertexCount: vertices.length,
      edgeCount: edges.length,
      badges: ["canvas", "path", "analytics", "semantic"],
      trySteps: [
        "Path → cheapest route between two airports with Dijkstra on cost property 'km' (e.g. a small hub to a far-away one); the result is a real minimum-distance itinerary.",
        "Semantic search: 'major airports in Japan' or 'busiest hubs in the Middle East'.",
        "Analytics → PAGERANK or DEGREE to rank the global mega-hubs.",
        "Canvas → country-flag emoji per node ('icon'); size by degree to make the hubs pop.",
      ],
      file: "air-routes.jsonl",
      styleConfig: {
        nodeColorMode: "property",
        nodeColorProperty: "country",
        nodeSizeMode: "degree",
        nodeImageProperty: "icon",
        edgeArrows: false,
      },
      indexRecipes: [boundVectorIndexRecipe(dimension)],
      embedding: { name: "default", model, dimension, metric: "Cosine" },
    },
  };
}
