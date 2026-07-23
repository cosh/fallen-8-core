/**
 * Hover help for every labeled field in the studio (feature studio-mutations-ux).
 * ONE home per explanation: screens reference these keys via <Field helpKey=…> /
 * help(key); no help copy is inlined at call sites. Keys are grouped by concept —
 * shared concepts first, then per-screen one-offs.
 */
export const FIELD_HELP = {
  // ---- shared concepts ----
  elementId:
    "Numeric id of an existing vertex or edge. Ids are assigned by the server and shown in every result table, the canvas, and the browser.",
  propertyId:
    "Name of a property (key), e.g. 'age' or 'name'. Properties are typed key-value pairs on a vertex or edge.",
  typedValue:
    "The value plus its .NET type. The type decides how the server parses, compares, and indexes the value — 42 as Int32 and \"42\" as String are different values.",
  indexId:
    "Unique name of an index on this instance. Registered indices are suggested live from the server; free typing still works.",
  edgePropertyId:
    "Edge container name on the source vertex, e.g. 'knows'. Fallen-8 groups a vertex's outgoing edges into named containers; traversals and degree lookups address edges by this name.",

  // ---- mutations: vertex / edge ----
  mutVertexLabel:
    "Optional label used to categorize the new vertex, e.g. 'person'. Labels drive filtering, statistics, and canvas coloring; leave empty for an unlabeled vertex.",
  mutCreationDate:
    "Optional creation timestamp stored on the element. Enter Unix seconds (e.g. 1713862800) or an ISO date/time, treated as UTC unless it carries an offset; empty stores 0 (epoch).",
  mutProperties:
    "Initial properties written atomically with the element in the same transaction. Add one row per property; each value is typed.",
  mutEdgeSource:
    "Id of the existing vertex the edge starts at (its outgoing side). The edge is stored in this vertex's edge container.",
  mutEdgeTarget: "Id of the existing vertex the edge points to (its incoming side).",
  mutEdgeLabel:
    "Optional label used to categorize the new edge, independent of the edge container name.",
  mutRemoveElement:
    "Id of the vertex or edge to delete. Deleting a vertex also deletes all of its edges. This cannot be undone.",

  // ---- browser ----
  lookupKind:
    "What the id refers to: vertex, edge, or graphelement (either — the server figures out which).",
  maxElements:
    "Upper bound on how many elements the bulk view loads from GET /graph. Keep it modest on big graphs — this is a browser, not an export (use JSONL export for that).",
  bulkFilter:
    "Client-side filter over the loaded elements: matches an exact id or a substring of the label.",

  // ---- query: scans ----
  scanKind:
    "Property scan walks all elements (no index needed); 'ask an index' picks a registered index and offers the query forms its type answers.",
  scanOperator:
    "How each element's property value is compared against the literal, using the literal's type ordering.",
  fulltextQuery: "Search text for the fulltext index; results come back scored with highlights.",
  spatialElementId:
    "Id of the element to search around; the spatial index returns everything within the given distance of it.",
  spatialDistance: "Maximum distance from the reference element, in the spatial index's units.",
  rangeLeftLimit: "Lower bound of the index range scan (typed, like every literal).",
  rangeRightLimit: "Upper bound of the index range scan (typed, like every literal).",
  rangeIncludeLeft: "Whether elements exactly at the lower bound are included (≥ vs >).",
  rangeIncludeRight: "Whether elements exactly at the upper bound are included (≤ vs <).",
  vectorQuery:
    "The query embedding, as a JSON array or comma-separated floats. Its dimension must match the index dimension exactly.",
  vectorK: "How many nearest neighbours to return (top-k).",
  vectorKind: "Restrict matches to vertices, edges, or allow both ('any').",
  vectorLabelConstraint:
    "Optional label filter applied BEFORE top-k — you still get k results, all carrying this label.",
  scanResultType: "Restrict matches to vertices, edges, or return both.",
  scanLiteral: "The typed value each element's property is compared against.",

  // ---- query: index-first querying ----
  indexQueryForm:
    "How to ask this index. The forms offered come from the index's server-reported capabilities (its type decides what it can answer).",

  // ---- indexes screen ----
  indexContentElementId: "Id of the vertex or edge to add to / remove from the index.",
  indexContentKey:
    "The typed key the element is filed under — index lookups compare against exactly this value and type.",
  indexPluginType:
    "Index plugin to instantiate. The list comes from the server's plugin discovery. Only VectorIndex takes creation options (dimension, metric); SpatialIndex cannot be created over REST.",
  vectorDimension:
    "Fixed embedding dimension for the new vector index; every vector added later must have exactly this many components.",
  vectorMetric:
    "Similarity metric the vector index uses. Cosine/DotProduct: higher score is better; L2: lower is better.",
  vectorAddElementId: "Id of the vertex or edge this embedding belongs to.",
  vectorAddPropertyId:
    "Property on the element that holds the embedding; the index reads the vector from there (WAL-recoverable, the honest default).",
  vectorAddVector:
    "The embedding itself, pasted as a JSON array or comma-separated floats — stored only in the index, not on the element.",
  vectorBindEmbeddingName:
    "Optional: bind this vector index to a named element embedding. A bound index maintains itself as a projection of that embedding — you write embeddings on the elements and the index follows; explicit vector-adds are rejected. Leave empty for a raw (bring-your-own-vector) index.",
  vectorModel:
    "Optional model-identity string this index expects its vectors to come from (e.g. 'bge-micro-v2#384#Cosine'). The embedding provider refuses to write mismatched vectors; a search whose provider identity differs answers 409. Diagnostic only for a raw index.",
  embeddingSearchText:
    "Search text. The embedding provider embeds it once (server-side), then runs kNN — you never handle the vector. Needs the provider enabled on this instance.",

  // ---- browser: element embeddings ----
  embeddingName:
    "Name of the embedding to read/write on this element, e.g. 'default'. Letters, digits, underscore, dash; max 64 chars. Different names hold independent vectors (and may differ in dimension).",
  embeddingVectorPaste:
    "The embedding vector, pasted as a JSON array or comma-separated floats. The element is the source of truth; any vector index bound to this name updates automatically on save.",
  embeddingText:
    "Text to embed onto this element via the server's embedding provider (stored with a model-identity stamp). Needs the provider enabled; otherwise paste a vector.",
  embeddingShowReserved:
    "Show the raw reserved properties ($embedding:… and $embeddingModel:…) in the plain property table. They are normally folded into the Embeddings tab.",

  // ---- semantic traversal block (path + subgraph) ----
  semanticQuerySource:
    "Where the query vector comes from: paste a vector, or type text the embedding provider embeds once before the traversal starts (provider-gated).",
  semanticQueryVector:
    "The query vector elements are scored against, pasted as a JSON array or comma-separated floats. Embedded once, up front — the traversal itself runs no model.",
  semanticQueryText:
    "Query text the embedding provider embeds once before the traversal. Mutually exclusive with a pasted vector; needs the provider enabled (else the server 403s).",
  semanticEmbeddingName:
    "Which named element embedding to score against (default 'default'). Elements without it are simply not matched.",
  semanticMetric:
    "Similarity metric: Cosine/DotProduct (higher is closer) or L2 (lower is closer). Must suit the embeddings; bit-identical to the vector index's scoring.",
  semanticMinScore:
    "Declarative filter: keep only elements whose similarity is at least this (Cosine/DotProduct) or at most this (L2). No C# needed — runs with dynamic code execution off.",
  semanticCostBySimilarity:
    "Declarative cost (DIJKSTRA paths only): weight each vertex by similarity so the search prefers close vertices (Cosine cost = 1 − score; L2 = distance). Not available under DotProduct.",

  // ---- path ----
  pathFrom: "Id of the vertex the path search starts from.",
  pathTo: "Id of the vertex the path search must reach.",
  pathAlgorithm: "Path plugin to use, e.g. BLS (breadth-first) or DIJKSTRA (cheapest paths by cost).",
  pathMaxDepth: "Maximum number of hops a path may have; caps the search space.",
  pathMaxResults: "Maximum number of paths returned (for Dijkstra: the K cheapest).",
  pathMaxWeight: "Upper bound on a path's total cost; paths weighing more are discarded.",

  // ---- subgraph ----
  subgraphName:
    "Unique name the subgraph is registered under; used to fetch, recalculate, and delete it later.",
  subgraphFrom:
    "Optional name of an existing subgraph to search WITHIN instead of the whole graph (nesting).",
  patternType:
    "Step kind in the pattern sequence: Vertex matches a vertex, Edge one hop, VariableLengthEdge a min..max hop chain. Steps alternate vertex ↔ edge, starting with a vertex.",
  patternName:
    "Optional name for this pattern step; matched elements are reported under it in the result.",
  patternDirection:
    "Which way the edge step is followed from the previous vertex: outgoing, incoming, or either.",
  patternMinLength: "Minimum number of hops this variable-length step must take.",
  patternMaxLength: "Maximum number of hops this variable-length step may take (capped at 100).",

  // ---- analytics ----
  analyticsAlgorithm:
    "Analytics plugin to run; the description under the picker explains what the selected one computes.",
  analyticsVertexLabel: "Only consider vertices with this label; empty runs on the whole graph.",
  analyticsEdgeProperty:
    "Only traverse edges in this edge container (e.g. 'knows'); empty traverses all edges.",
  analyticsDirection:
    "Edge direction the algorithm follows: in, out, or both. Empty keeps the algorithm's default.",
  analyticsMaxResults:
    "How many top-scored entries the server returns; the computation itself still runs over the whole (filtered) graph.",
  analyticsMaxIterations:
    "Iteration cap for iterative algorithms (e.g. PageRank rounds); empty keeps the algorithm's default.",
  analyticsTimeBudget:
    "Wall-clock budget in seconds; the run stops with partial results when it is exhausted (default 30).",
  analyticsDamping:
    "PageRank damping factor d (probability of following an edge vs. jumping anywhere); 0.85 is the classic choice.",
  analyticsEpsilon:
    "Convergence threshold on the total (L1-summed) score change between rounds; iteration stops once the sum falls below it.",
  analyticsWriteBack:
    "When checked, the run also stores each vertex's score as a property under the key below (asks for confirmation first).",
  analyticsWriteBackKey:
    "Property key the per-vertex scores are written to, e.g. 'analytics.pagerank'.",

  // ---- dashboard ----
  loadPath:
    "Server-side path of the save game to load — resolved on the machine the instance runs on, not in this browser.",
  exportVertexLabel: "Only export vertices with this label (their edges follow); empty exports all.",
  exportEdgeLabel: "Only export edges with this label; empty exports all.",

  // ---- app shell / canvas / save games ----
  instanceSwitcher:
    "The Fallen-8 instance every screen talks to. Register more instances on the Connect screen.",
  canvasRenderer:
    "The canvas projection, both WebGL: 2D (Sigma.js — pan/zoom, crisp labels) or 3D (three.js — orbit with the mouse, labels on hover). Styling, path overlays, and subgraph results work identically in both.",
  canvasLayout:
    "How vertices are placed. 2D: force (ForceAtlas2 physics, clusters emerge), circular ring, circle pack grouped by label, grid, or random. 3D: force (d3-force-3d) or DAG modes that layer the graph top-down / radially (cycles are tolerated, best-effort).",
  canvasNodeColor:
    "What decides a node's color: its label (stable palette hash, the default) or a property you name. Property values hash to the same palette; if every value is numeric the nodes shade along a min→max gradient instead.",
  canvasNodeSize:
    "What decides a node's size: fixed, a numeric property (min-max scaled), or the node's in-/out-/total degree counted over the edges currently on the canvas — expand neighbors and sizes update.",
  canvasNodeImage:
    "Property whose value renders AS the node: an http(s) or data URL loads that image; any other value (e.g. an emoji like 🦊) is drawn by the studio itself — emoticons need no hosting. Nodes without the property stay circles.",
  canvasEdgeColor:
    "What decides an edge's color: its label (default) or a property you name — same palette hashing and numeric gradient rules as node colors.",
  canvasEdgeWidth:
    "What decides an edge's width: fixed, or a numeric property min-max scaled into the width range. Edges without a numeric value keep the default width.",
  canvasNodeLabels:
    "Show vertex labels on the canvas (2D: always visible, 3D: on hover). Past 5,000 rendered elements labels are dropped automatically to protect the frame rate.",
  canvasEdgeLabels:
    "Show edge-container names on edges (2D: always visible, 3D: on hover). Subject to the same 5,000-element degrade threshold as node labels.",
  canvasEdgeArrows:
    "Draw arrowheads pointing at each edge's target vertex, making direction visible in both 2D and 3D.",
  saveGameDeleteFiles:
    "Also delete the checkpoint's files on the server's disk, not just its registry entry. The data is then unrecoverable.",

  // ---- connect ----
  instanceName: "Display name for this connection, shown in the instance switcher.",
  instanceUrl:
    "Base URL of the Fallen-8 REST API, e.g. http://localhost:5000. Leave empty when the studio is served by the instance itself (same origin).",
  instanceApiKey:
    "API key for instances that require one; sent as a Bearer token on every request and kept in this browser only.",

  // ---- stored queries ----
  storedQuery:
    "A named, pre-compiled filter set registered under POST /storedquery. Invoking by name works even when dynamic code execution is disabled on the server.",
  storedQueryName:
    "Unique name the query is registered under and later invoked by. Allowed: letters, digits, underscore, dash; max 128 characters.",
  storedQueryDescription:
    "Optional human-readable note shown next to the query in the library list.",

  // ---- destructive-action confirmation ----
  confirmTyped:
    "Safety catch: type the target instance's name exactly to arm the destructive action. Nothing happens until it matches.",

  // ---- NL assist ----
  nlIntent:
    "Describe the filter in plain language; the assistant drafts a C# filter fragment from it, which you still review and commit yourself.",
  nlBackend: "Where natural-language-to-filter translation runs: a preset endpoint or a custom one.",
  nlPreset: "Pre-configured model endpoint to use.",
  nlEndpoint: "URL of the model API endpoint that translates your prompt into a filter fragment.",
  nlApi: "Wire protocol the endpoint speaks (which request/response shape to use).",
  nlModel: "Model identifier sent to the endpoint, e.g. a model name the API expects.",
  nlTemperature: "Sampling temperature; lower is more deterministic, which suits code generation.",
  nlApiKey:
    "Credential for the model endpoint. Sent only to that endpoint, never to the Fallen-8 instance.",
} as const;

export type FieldHelpKey = keyof typeof FIELD_HELP;

/** Accessor for odd call sites (checkbox labels, aria-labeled inputs, toggles). */
export const help = (key: FieldHelpKey): string => FIELD_HELP[key];
