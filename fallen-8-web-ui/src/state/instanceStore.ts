// MIT License
//
// instanceStore.ts
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

import { create, type UseBoundStore, type StoreApi } from "zustand";
import { persist } from "zustand/middleware";
import type {
  BinaryOperatorName,
  EdgeREST,
  PathREST,
  PatternSpecification,
  PropertyREST,
  VertexREST,
} from "../api/types";
import { DEFAULT_STYLE_CONFIG, type StyleConfig } from "../canvas/styleConfig";
import {
  DEFAULT_SEMANTIC_DRAFT,
  DEFAULT_SEMANTIC_QUERY_DRAFT,
  type SemanticDraft,
  type SemanticQueryDraft,
  type SlotMode,
} from "../lib/semantic";
import type { TypedValue } from "../lib/literals";
import type { IndexCapability } from "../lib/indexCapabilities";
import { DEFAULT_FEED_FILTER, type FeedFilterDraft } from "./feedFilter";
import { migrateEventFeed, purgeAllEventFeeds, purgeEventFeed } from "./eventFeed";
import { scopeKey } from "./scopeKey";
import { storageKey } from "../app/studioConfig";

/** The one place a workspace scope key becomes a persisted localStorage name. */
function workspaceStorageName(key: string): string {
  return storageKey(`f8.workspace.${key}`);
}

/**
 * Per-instance workspace state (FR-1c), via a memoized store factory. Each instance id
 * owns exactly one store persisted under its own local-storage key, so canvas contents,
 * drafts, and result sets can never mix across instances - mixing is structurally
 * unrepresentable, not merely discouraged.
 */

/** Scalar property snapshot for data-driven styling (studio-canvas-viz FR-11). */
export type CanvasProps = Record<string, string | number | boolean>;

export interface CanvasNode {
  id: number;
  label: string | null;
  props?: CanvasProps;
}

export interface CanvasEdge {
  id: number;
  source: number;
  target: number;
  edgePropertyId: string | null;
  /** The element's actual label (null when unset) - never a copy of edgePropertyId. */
  label: string | null;
  props?: CanvasProps;
}

/**
 * The one display rule for an edge's name: the human-facing label when present, else the
 * always-present type (edgePropertyId). Used by both canvases and the label color scale.
 */
export function edgeDisplayName(
  edge: Pick<CanvasEdge, "label" | "edgePropertyId">,
): string | null {
  return edge.label ?? edge.edgePropertyId ?? null;
}

/** Longest property string kept on the canvas snapshot (FR-11: styling never needs more). */
export const CANVAS_PROP_MAX_STRING = 200;

/** Scalars only — arrays/objects (e.g. embeddings) are style-less and must not bloat local storage. */
export function snapshotProps(properties: PropertyREST[] | null | undefined): CanvasProps {
  const props: CanvasProps = {};
  for (const p of properties ?? []) {
    const v = p.propertyValue;
    if (typeof v === "number" || typeof v === "boolean") {
      props[p.propertyId] = v;
    } else if (typeof v === "string") {
      props[p.propertyId] = v.length > CANVAS_PROP_MAX_STRING ? v.slice(0, CANVAS_PROP_MAX_STRING) : v;
    }
  }
  return props;
}

export interface CanvasModel {
  nodes: Record<number, CanvasNode>;
  edges: Record<number, CanvasEdge>;
}

/**
 * REST elements → canvas model, merge-only over an optional base. An edge can only
 * render when both endpoints are present, so unhydrated endpoints get stub nodes —
 * expand-on-demand and previews stay merge-only. Shared by mergeIntoCanvas and the
 * adjacency preview (feature adjacency-preview).
 */
export function buildCanvasModel(
  vertices: VertexREST[],
  edges: EdgeREST[],
  base?: CanvasModel,
): CanvasModel {
  const nodes = { ...(base?.nodes ?? {}) };
  const edgeMap = { ...(base?.edges ?? {}) };
  for (const v of vertices) {
    nodes[v.id] = {
      id: v.id,
      label: v.label ?? null,
      props: snapshotProps(v.properties),
    };
  }
  for (const e of edges) {
    if (!nodes[e.sourceVertex]) {
      nodes[e.sourceVertex] = { id: e.sourceVertex, label: null };
    }
    if (!nodes[e.targetVertex]) {
      nodes[e.targetVertex] = { id: e.targetVertex, label: null };
    }
    edgeMap[e.id] = {
      id: e.id,
      source: e.sourceVertex,
      target: e.targetVertex,
      edgePropertyId: e.edgePropertyId ?? null,
      label: e.label ?? null,
      props: snapshotProps(e.properties),
    };
  }
  return { nodes, edges: edgeMap };
}

/**
 * What the last "Show whole graph" load fetched vs the namespace's true counts (feature
 * canvas-view-controls FR-3). Persisted alongside the canvas it describes, so the honest
 * truncation notice survives leaving and returning; cleared with the canvas. Only set
 * when something was actually truncated.
 */
export interface WholeGraphTruncation {
  fetchedVertices: number;
  fetchedEdges: number;
  totalVertices: number;
  totalEdges: number;
}

export interface ResultSet {
  id: string;
  title: string;
  createdAt: number;
  elementIds: number[];
}

/** Where a path/subgraph run takes its fragments from (concept spec §5.1). */
export type FilterSource = "inline" | "stored";

/**
 * The tabs of the merged Traverse screen (feature studio-traverse-merge), in strip order.
 * ONE home for the ids: the tab strip renders them, the route validates `?tab=` against them,
 * and the persisted `traverseTab` remembers the last one per instance-and-namespace.
 */
export const TRAVERSE_TABS = ["path", "subgraph", "stored"] as const;

export type TraverseTab = (typeof TRAVERSE_TABS)[number];

/** The tab a fresh workspace opens on: path finding is the more frequent scenario. */
export const DEFAULT_TRAVERSE_TAB: TraverseTab = "path";

/** Guard for the two untrusted sources of a tab id: the `?tab=` search param and storage. */
export const isTraverseTab = (value: unknown): value is TraverseTab =>
  typeof value === "string" && (TRAVERSE_TABS as readonly string[]).includes(value);

/** The scenario tab a stored query of this kind belongs to (its `kind` is `string | null`). */
export const traverseTabForKind = (kind: string | null | undefined): TraverseTab =>
  kind === "SubGraph" ? "subgraph" : "path";

export interface PathDraft {
  from: string;
  to: string;
  // "BLS" / "DIJKSTRA" are the built-ins; any string is accepted so a runtime-registered Path
  // algorithm plugin (feature plugin-registration) is selectable by name.
  algorithm: string;
  maxDepth: number;
  maxResults: number;
  maxPathWeight: number;
  vertexFilter: string;
  edgeFilter: string;
  edgePropertyFilter: string;
  vertexCost: string;
  edgeCost: string;
  filterSource: FilterSource;
  storedQuery: string;
  /** Declarative semantic-traversal block (feature element-embeddings). */
  semantic: SemanticDraft;
}

export const DEFAULT_PATH_DRAFT: PathDraft = {
  from: "",
  to: "",
  algorithm: "BLS",
  maxDepth: 7,
  maxResults: 1,
  maxPathWeight: Number.MAX_VALUE,
  vertexFilter: "",
  edgeFilter: "",
  edgePropertyFilter: "",
  vertexCost: "",
  edgeCost: "",
  filterSource: "inline",
  storedQuery: "",
  semantic: { ...DEFAULT_SEMANTIC_DRAFT },
};

/** The Query screen's three ways of asking (feature index-workspace / semantic-search-onramp). */
export const QUERY_MODES = ["property", "index", "semantic"] as const;

export type QueryMode = (typeof QUERY_MODES)[number];

/**
 * The Query screen's whole input form (feature index-workspace). Persisted per instance
 * so leaving for the Canvas and coming back restores it exactly — results are re-run on
 * demand (kept out of the lean persisted store). Reset via the screen's Clear button.
 */
export interface QueryDraft {
  /**
   * "semantic" is text-in kNN (feature semantic-search-onramp): its own mode rather than a
   * source toggle inside the index mode's vector form, because a capability reachable only
   * after picking the right index is one nobody finds. It shares the kNN parameters below
   * with that form - same question, different query source - but NOT the index, which is
   * drawn from a different set (see semanticIndexId).
   */
  mode: QueryMode;
  /** Property-scan scope: one named "key" (typed operator) or "any" property (contains search). */
  propertyScope: "key" | "any";
  propertyId: string;
  /** All-property search term (propertyScope === "any"): a case-insensitive substring. */
  searchTerm: string;
  /** All-property search label restrictor (propertyScope === "any"); empty scans every label. */
  searchLabel: string;
  indexId: string;
  /**
   * The semantic mode's index, kept apart from `indexId` because the two modes choose from
   * different sets: every registered index there, only the ones that can rank a vector here.
   * Sharing one field meant picking a vector index for a semantic search silently replaced the
   * operator's index-mode selection AND the query form that went with it.
   */
  semanticIndexId: string;
  form: IndexCapability;
  operator: BinaryOperatorName;
  resultType: "Vertices" | "Edges" | "Both";
  literal: TypedValue;
  leftLimit: TypedValue;
  rightLimit: TypedValue;
  includeLeft: boolean;
  includeRight: boolean;
  fulltextQuery: string;
  spatialElementId: string;
  spatialDistance: string;
  /** The pasted query vector (index mode, vector form); the semantic mode never uses it. */
  vectorText: string;
  vectorK: string;
  vectorKind: "any" | "vertex" | "edge";
  vectorLabel: string;
  /** The query text of the semantic mode, embedded once server-side. */
  vectorSearchText: string;
}

export const DEFAULT_QUERY_DRAFT: QueryDraft = {
  mode: "property",
  propertyScope: "key",
  propertyId: "",
  searchTerm: "",
  searchLabel: "",
  indexId: "",
  semanticIndexId: "",
  form: "equality",
  operator: "Equals",
  resultType: "Both",
  literal: { type: "System.String", raw: "" },
  leftLimit: { type: "System.Int32", raw: "0" },
  rightLimit: { type: "System.Int32", raw: "100" },
  includeLeft: true,
  includeRight: true,
  fulltextQuery: "",
  spatialElementId: "",
  spatialDistance: "10",
  vectorText: "",
  vectorK: "10",
  vectorKind: "any",
  vectorLabel: "",
  vectorSearchText: "",
};

/**
 * Rehydrates a persisted Query draft. Text-in kNN used to be a `vectorSource` toggle INSIDE the
 * index mode's vector form; feature semantic-search-onramp made it its own mode, so a draft
 * written by an older build is LIFTED rather than dropped: the text, k, kind and label are the
 * same question asked the same way, and only the route to the form changed. The stale key is
 * stripped instead of spread through, so nothing carries a field this build has no meaning for.
 */
export function migrateQueryDraft(persisted: Partial<QueryDraft> | undefined): QueryDraft {
  const { vectorSource, ...rest } = (persisted ?? {}) as Partial<QueryDraft> & {
    vectorSource?: "vector" | "text";
  };
  const draft: QueryDraft = { ...DEFAULT_QUERY_DRAFT, ...rest };
  // Normalized, not trusted: a mode this build does not know (hand-edited storage) would leave
  // the screen with no form rendered and a Run button that queries whatever the last branch is.
  if (!QUERY_MODES.includes(draft.mode)) draft.mode = DEFAULT_QUERY_DRAFT.mode;
  // Only when that form was the one actually on screen: a stale "text" left behind while the
  // operator moved to a property scan is not a request to reopen a semantic search. The index
  // travels with it, since back then there was only one field holding it.
  return vectorSource === "text" && draft.mode === "index" && draft.form === "vector"
    ? { ...draft, mode: "semantic", semanticIndexId: draft.semanticIndexId || draft.indexId }
    : draft;
}

/**
 * One pattern row of the subgraph builder: a pattern spec plus a stable list key and the
 * step's vertex-slot state (feature subgraph-semantic-thresholds) — the slot MODE and the
 * threshold as editable text (the wire `semanticMinScore` number is derived on build).
 */
export type SubgraphPatternDraft = Omit<PatternSpecification, "semanticMinScore"> & {
  key: string;
  /** Vertex steps only; edge steps ignore it. */
  filterMode: SlotMode;
  /** Threshold text for the semantic mode (parsed on build). */
  semanticMinScore: string;
};

/** The Subgraph "create" form, persisted per instance so navigation never wipes it. */
export interface SubgraphDraft {
  name: string;
  fromSubGraph: string;
  /** The top-level vertex pre-filter slot's mode (one owner per slot, structural). */
  vertexFilterMode: SlotMode;
  /** The slot's fragment text — kept across mode switches so nothing is lost. */
  vertexFilter: string;
  /** The slot's threshold text for the semantic mode (parsed on build). */
  vertexMinScore: string;
  edgeFilter: string;
  patterns: SubgraphPatternDraft[];
  filterSource: FilterSource;
  storedQuery: string;
  /** The ONE semantic query per request every semantic threshold scores against. */
  semanticQuery: SemanticQueryDraft;
}

export const DEFAULT_SUBGRAPH_DRAFT: SubgraphDraft = {
  name: "",
  fromSubGraph: "",
  vertexFilterMode: "everything",
  vertexFilter: "",
  vertexMinScore: "0.7",
  edgeFilter: "",
  patterns: [],
  filterSource: "inline",
  storedQuery: "",
  semanticQuery: { ...DEFAULT_SEMANTIC_QUERY_DRAFT },
};

/** The Browser screen's lookup + bulk inputs, persisted per instance. */
export interface BrowserDraft {
  idInput: string;
  lookupKind: "graphelement" | "vertex" | "edge";
  maxElements: number;
  bulkFilter: string;
  detailTab: "properties" | "embeddings";
}

export const DEFAULT_BROWSER_DRAFT: BrowserDraft = {
  idInput: "",
  lookupKind: "graphelement",
  maxElements: 1000,
  bulkFilter: "",
  detailTab: "properties",
};

/** The Analytics runner's algorithm pick + tuning inputs, persisted per instance. */
export interface AnalyticsDraft {
  algorithm: string;
  vertexLabel: string;
  edgePropertyId: string;
  direction: string;
  maxResults: string;
  maxIterations: string;
  timeBudget: string;
  damping: string;
  epsilon: string;
  showWriteBack: boolean;
  writeBack: boolean;
  writeBackKey: string;
}

export const DEFAULT_ANALYTICS_DRAFT: AnalyticsDraft = {
  algorithm: "",
  vertexLabel: "",
  edgePropertyId: "",
  direction: "",
  maxResults: "100",
  maxIterations: "",
  timeBudget: "",
  damping: "",
  epsilon: "",
  showWriteBack: false,
  writeBack: false,
  writeBackKey: "",
};

/**
 * The Canvas right-panel tool strip's inputs (feature canvas-find-connect), persisted per
 * instance so leaving the canvas and returning restores the active tab and each tool's form.
 * The Find result list, Connect found paths, and Connect picked-vertex ids are ephemeral session
 * state (re-run on demand), exactly like every other result in the studio - only the inputs here
 * persist.
 */
export interface CanvasToolsDraft {
  /** Which right-panel tab is active: styling, element search, path connecting, or interacting. */
  tab: "style" | "find" | "connect" | "interact";
  /** Find: the all-property contains term (fed to POST /scan/graph/properties). */
  findTerm: string;
  /** Find: optional exact-match label restrictor; empty searches every label. */
  findLabel: string;
  /** Find: restrict matches to vertices, edges, or both. */
  findResultType: "Vertices" | "Edges" | "Both";
  /** Connect: max hops per pairwise path search (maps to the path spec's maxDepth). */
  connectMaxDepth: number;
  /** Connect: use every canvas vertex, or only a picked subset, as the pair endpoints. */
  connectScope: "all" | "pick";
  /**
   * Interact (feature canvas-interact): the filter rows that build the match set the tab's two
   * verbs act on. Every one is INACTIVE when blank - which is why the two numeric thresholds are
   * strings, since 0 is a legitimate degree bound and "" has to mean "not filtering" - and with
   * none of them active the match set is every canvas vertex, i.e. "expand all".
   */
  interactLabel: string;
  interactPropKey: string;
  interactPropTerm: string;
  /** Which edges the degree comparison counts: the database's answer, or the loaded ones. */
  interactDegreeSource: "database" | "canvas";
  interactDegreeDirection: "in" | "out" | "total";
  interactDegreeOp: "over" | "under";
  interactDegreeValue: string;
  interactSemanticText: string;
  interactSemanticIndexId: string;
  interactSemanticDirection: "closer" | "farther";
  /** The threshold in the metric's RAW units, as typed (blank = the filter is off). */
  interactSemanticThreshold: string;
}

export const DEFAULT_CANVAS_TOOLS_DRAFT: CanvasToolsDraft = {
  tab: "style",
  findTerm: "",
  findLabel: "",
  findResultType: "Both",
  connectMaxDepth: 3,
  connectScope: "all",
  interactLabel: "",
  interactPropKey: "",
  interactPropTerm: "",
  interactDegreeSource: "database",
  interactDegreeDirection: "total",
  interactDegreeOp: "over",
  interactDegreeValue: "",
  interactSemanticText: "",
  interactSemanticIndexId: "",
  interactSemanticDirection: "closer",
  interactSemanticThreshold: "",
};

/** One-shot navigation intent: "open Query with this index preselected" (cleared on consume). */
export interface ScanPrefill {
  indexId: string;
  /**
   * A query vector to search WITH, in the bracketed form the vector box parses. Set by the
   * "find similar" gesture, which reads it off the source element's own stored embedding: the
   * search surface takes a vector, never an element id, so the element-as-query gesture is
   * composed on this side rather than asked of the server.
   */
  vectorText?: string;
  /**
   * The element the vector came from, so it can be dropped from its own results. There is no
   * self-exclusion anywhere in the engine or the REST contract, so an unfiltered search returns
   * the source element at rank 1 every time.
   */
  sourceElementId?: number;
  /**
   * The source element's label, inherited as the search constraint. Not a convenience: several
   * entity kinds embed as little more than their identifier, so an unconstrained similarity
   * search over such a graph ranks identifier-shaped noise against real matches.
   */
  label?: string;
  kind?: "any" | "vertex" | "edge";
}


export interface WorkspaceState {
  /** The Events panel's interest filter (feature studio-event-feed, see feedFilter.ts). */
  feedFilter: FeedFilterDraft;
  /** One-shot navigation intent: "open the Browser inspecting this element id". */
  inspectPrefill: number | null;
  /**
   * The integration identity whose run this instance is watching, or null.
   *
   * PERSISTED, unlike the one-shot prefills, and that is the point: a run outlives the request that
   * started it and can take hours, so reopening the screen - or reloading the page - has to re-attach
   * to it rather than lose it. The identity is enough to re-find the run, because the runtime keys its
   * slot by exactly that.
   */
  integrationWatch: string | null;
  canvasNodes: Record<number, CanvasNode>;
  canvasEdges: Record<number, CanvasEdge>;
  styleConfig: StyleConfig;
  pathOverlay: PathREST | null;
  wholeGraphTruncation: WholeGraphTruncation | null;
  resultSets: ResultSet[];
  pathDraft: PathDraft;
  queryDraft: QueryDraft;
  subgraphDraft: SubgraphDraft;
  browserDraft: BrowserDraft;
  analyticsDraft: AnalyticsDraft;
  canvasToolsDraft: CanvasToolsDraft;
  /**
   * The Traverse tab last left open (feature studio-traverse-merge). Persisted rather than
   * URL-only because switching namespace or instance rewrites the leaf WITHOUT the search
   * param (see app/scopedRoute.ts), and being dumped on another tab by a context switch is
   * the same "lose what you were looking at" the switchers exist to avoid.
   */
  traverseTab: TraverseTab;
  scanPrefill: ScanPrefill | null;

  mergeIntoCanvas: (vertices: VertexREST[], edges: EdgeREST[]) => void;
  removeFromCanvas: (kind: "node" | "edge", id: number) => void;
  clearCanvas: () => void;
  setWholeGraphTruncation: (truncation: WholeGraphTruncation | null) => void;
  setStyleConfig: (patch: Partial<StyleConfig>) => void;
  setPathOverlay: (path: PathREST | null) => void;
  addResultSet: (title: string, elementIds: number[]) => void;
  removeResultSet: (id: string) => void;
  setPathDraft: (patch: Partial<PathDraft>) => void;
  resetPathDraft: () => void;
  setQueryDraft: (patch: Partial<QueryDraft>) => void;
  resetQueryDraft: () => void;
  setSubgraphDraft: (patch: Partial<SubgraphDraft>) => void;
  resetSubgraphDraft: () => void;
  setBrowserDraft: (patch: Partial<BrowserDraft>) => void;
  resetBrowserDraft: () => void;
  setAnalyticsDraft: (patch: Partial<AnalyticsDraft>) => void;
  resetAnalyticsDraft: () => void;
  setCanvasToolsDraft: (patch: Partial<CanvasToolsDraft>) => void;
  setTraverseTab: (tab: TraverseTab) => void;
  setScanPrefill: (prefill: ScanPrefill | null) => void;
  setFeedFilter: (patch: Partial<FeedFilterDraft>) => void;
  setInspectPrefill: (id: number | null) => void;
  setIntegrationWatch: (instanceId: string | null) => void;
}

function createWorkspaceStore(instanceId: string) {
  return create<WorkspaceState>()(
    persist(
      (set) => ({
        canvasNodes: {},
        canvasEdges: {},
        styleConfig: { ...DEFAULT_STYLE_CONFIG },
        pathOverlay: null,
        wholeGraphTruncation: null,
        resultSets: [],
        pathDraft: { ...DEFAULT_PATH_DRAFT },
        queryDraft: { ...DEFAULT_QUERY_DRAFT },
        subgraphDraft: { ...DEFAULT_SUBGRAPH_DRAFT },
        browserDraft: { ...DEFAULT_BROWSER_DRAFT },
        analyticsDraft: { ...DEFAULT_ANALYTICS_DRAFT },
        canvasToolsDraft: { ...DEFAULT_CANVAS_TOOLS_DRAFT },
        traverseTab: DEFAULT_TRAVERSE_TAB,
        scanPrefill: null,
        feedFilter: { ...DEFAULT_FEED_FILTER },
        inspectPrefill: null,
        integrationWatch: null,

        mergeIntoCanvas: (vertices, edges) =>
          set((s) => {
            const model = buildCanvasModel(vertices, edges, {
              nodes: s.canvasNodes,
              edges: s.canvasEdges,
            });
            return { canvasNodes: model.nodes, canvasEdges: model.edges };
          }),

        removeFromCanvas: (kind, id) =>
          set((s) => {
            if (kind === "edge") {
              const canvasEdges = { ...s.canvasEdges };
              delete canvasEdges[id];
              return { canvasEdges };
            }
            const canvasNodes = { ...s.canvasNodes };
            delete canvasNodes[id];
            const canvasEdges = Object.fromEntries(
              Object.entries(s.canvasEdges).filter(
                ([, e]) => e.source !== id && e.target !== id,
              ),
            );
            return { canvasNodes, canvasEdges };
          }),

        clearCanvas: () =>
          set({
            canvasNodes: {},
            canvasEdges: {},
            pathOverlay: null,
            wholeGraphTruncation: null,
          }),

        setWholeGraphTruncation: (wholeGraphTruncation) => set({ wholeGraphTruncation }),

        setStyleConfig: (patch) =>
          set((s) => ({ styleConfig: { ...s.styleConfig, ...patch } })),

        setPathOverlay: (pathOverlay) => set({ pathOverlay }),

        addResultSet: (title, elementIds) =>
          set((s) => ({
            resultSets: [
              {
                id: `r-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`,
                title,
                createdAt: Date.now(),
                elementIds,
              },
              ...s.resultSets,
            ].slice(0, 20),
          })),

        removeResultSet: (id) =>
          set((s) => ({ resultSets: s.resultSets.filter((r) => r.id !== id) })),

        setPathDraft: (patch) =>
          set((s) => ({ pathDraft: { ...s.pathDraft, ...patch } })),

        resetPathDraft: () => set({ pathDraft: { ...DEFAULT_PATH_DRAFT } }),

        setQueryDraft: (patch) =>
          set((s) => ({ queryDraft: { ...s.queryDraft, ...patch } })),

        resetQueryDraft: () => set({ queryDraft: { ...DEFAULT_QUERY_DRAFT } }),

        setSubgraphDraft: (patch) =>
          set((s) => ({ subgraphDraft: { ...s.subgraphDraft, ...patch } })),

        resetSubgraphDraft: () => set({ subgraphDraft: { ...DEFAULT_SUBGRAPH_DRAFT } }),

        setBrowserDraft: (patch) =>
          set((s) => ({ browserDraft: { ...s.browserDraft, ...patch } })),

        resetBrowserDraft: () => set({ browserDraft: { ...DEFAULT_BROWSER_DRAFT } }),

        setAnalyticsDraft: (patch) =>
          set((s) => ({ analyticsDraft: { ...s.analyticsDraft, ...patch } })),

        resetAnalyticsDraft: () => set({ analyticsDraft: { ...DEFAULT_ANALYTICS_DRAFT } }),

        setCanvasToolsDraft: (patch) =>
          set((s) => ({ canvasToolsDraft: { ...s.canvasToolsDraft, ...patch } })),

        setTraverseTab: (traverseTab) => set({ traverseTab }),

        setScanPrefill: (scanPrefill) => set({ scanPrefill }),

        setFeedFilter: (patch) =>
          set((s) => ({ feedFilter: { ...s.feedFilter, ...patch } })),

        setInspectPrefill: (inspectPrefill) => set({ inspectPrefill }),
        setIntegrationWatch: (integrationWatch) => set({ integrationWatch }),
      }),
      {
        name: workspaceStorageName(instanceId),
        // One-shot navigation intents are session state: persisted, a never-consumed
        // one would fire a surprise lookup on a later session's first visit.
        partialize: ({ scanPrefill: _scan, inspectPrefill: _inspect, ...rest }) => rest,
        // Deep-merge drafts/config so state persisted before a field existed picks
        // up its default instead of rehydrating as undefined.
        merge: (persisted, current) => {
          const p = (persisted ?? {}) as Partial<WorkspaceState>;
          return {
            ...current,
            ...p,
            pathDraft: {
              ...DEFAULT_PATH_DRAFT,
              ...(p.pathDraft ?? {}),
              // Nested draft added after some state was persisted: deep-default it too, so
              // an older pathDraft picks up every semantic field instead of a partial.
              semantic: { ...DEFAULT_SEMANTIC_DRAFT, ...(p.pathDraft?.semantic ?? {}) },
            },
            queryDraft: migrateQueryDraft(p.queryDraft),
            // A subgraph draft persisted before the slot-mode restructure (feature
            // subgraph-semantic-thresholds) carried a block-local `semantic` object and no
            // slot modes; it is RESET rather than migrated - a draft is a session
            // convenience, not data.
            subgraphDraft:
              p.subgraphDraft && !("semanticQuery" in p.subgraphDraft)
                ? { ...DEFAULT_SUBGRAPH_DRAFT }
                : {
                    ...DEFAULT_SUBGRAPH_DRAFT,
                    ...(p.subgraphDraft ?? {}),
                    semanticQuery: {
                      ...DEFAULT_SEMANTIC_QUERY_DRAFT,
                      ...(p.subgraphDraft?.semanticQuery ?? {}),
                    },
                  },
            browserDraft: { ...DEFAULT_BROWSER_DRAFT, ...(p.browserDraft ?? {}) },
            analyticsDraft: { ...DEFAULT_ANALYTICS_DRAFT, ...(p.analyticsDraft ?? {}) },
            canvasToolsDraft: { ...DEFAULT_CANVAS_TOOLS_DRAFT, ...(p.canvasToolsDraft ?? {}) },
            // Normalized, not trusted: a tab id this build does not know (hand-edited storage,
            // a renamed tab) would leave the Traverse screen with every panel hidden.
            traverseTab: isTraverseTab(p.traverseTab) ? p.traverseTab : DEFAULT_TRAVERSE_TAB,
            styleConfig: { ...DEFAULT_STYLE_CONFIG, ...(p.styleConfig ?? {}) },
            feedFilter: { ...DEFAULT_FEED_FILTER, ...(p.feedFilter ?? {}) },
            // One-shots never rehydrate (see partialize); this also drops values
            // persisted before the partialize existed.
            scanPrefill: null,
            inspectPrefill: null,
            // integrationWatch is deliberately NOT reset here. It is not a one-shot: a run outlives the
            // request that started it and can take hours, so surviving a reload is the whole point of
            // persisting it. Nulling it here silently defeated that - the field was written to storage by
            // partialize and thrown away on the way back in.
          };
        },
      },
    ),
  );
}

type WorkspaceStore = UseBoundStore<StoreApi<WorkspaceState>>;

const stores = new Map<string, WorkspaceStore>();

/**
 * Returns the one store belonging to this instance id + namespace (memoized). The
 * "default" namespace keeps the pre-namespace key (`f8.workspace.<id>`) so an existing
 * workspace is adopted as default's with no migration; other namespaces persist under
 * `f8.workspace.<id>/<ns>` (feature graph-namespaces). Also accepts a pre-bound
 * "<id>/<namespace>" compound as the first argument (the bound instance view's id, see
 * useInstanceStore) — both call shapes resolve to the same canonical key. Registry ids
 * never contain "/".
 */
export function getInstanceStore(instanceId: string, namespace?: string): WorkspaceStore {
  const key = scopeKey(instanceId, namespace);
  let store = stores.get(key);
  if (!store) {
    store = createWorkspaceStore(key);
    stores.set(key, store);
  }
  return store;
}

/**
 * Drops a namespace's memoized store AND its persisted state (feature graph-namespaces):
 * after a drop, a recreate, or a factory reset the old canvas/results would reference
 * elements that no longer exist (or worse, ids now naming different elements).
 */
export function purgeInstanceStore(instanceId: string, namespace?: string): void {
  const key = scopeKey(instanceId, namespace);
  stores.delete(key);
  localStorage.removeItem(workspaceStorageName(key));
  // The session-only event feed shares the workspace's blast radius: its buffered events
  // (and catch-up position) describe the graph that just went away.
  purgeEventFeed(instanceId, namespace);
}

/** Purges EVERY namespace's workspace of one instance (the factory-reset blast radius). */
export function purgeAllInstanceStores(instanceId: string): void {
  for (const key of [...stores.keys()]) {
    if (key === instanceId || key.startsWith(`${instanceId}/`)) stores.delete(key);
  }
  for (let i = localStorage.length - 1; i >= 0; i--) {
    const name = localStorage.key(i);
    if (
      name === workspaceStorageName(instanceId) ||
      name?.startsWith(workspaceStorageName(`${instanceId}/`))
    ) {
      localStorage.removeItem(name);
    }
  }
  purgeAllEventFeeds(instanceId);
}

/**
 * Moves a namespace's persisted workspace to its new name — rename is a pure address
 * change, so canvas/drafts must follow. Both memoized stores are dropped (a store's
 * persist key is baked in at creation), so the next access rehydrates from the moved state.
 */
export function migrateInstanceStore(instanceId: string, from: string, to: string): void {
  const fromKey = scopeKey(instanceId, from);
  const toKey = scopeKey(instanceId, to);
  if (fromKey === toKey) return;
  const persisted = localStorage.getItem(workspaceStorageName(fromKey));
  if (persisted !== null) {
    localStorage.setItem(workspaceStorageName(toKey), persisted);
    localStorage.removeItem(workspaceStorageName(fromKey));
  }
  stores.delete(fromKey);
  stores.delete(toKey);
  // A rename keeps the graph (and its feed epoch/sequence): the buffer moves along.
  migrateEventFeed(instanceId, from, to);
}

/**
 * Drops every memoized store (persisted state untouched), so the next access recreates it
 * against the CURRENT storage prefix. A store bakes its persist key in at creation
 * (`workspaceStorageName`), and the memo map is keyed by scope alone, so a mount that
 * changes `storageNamespace` (feature studio-embeddable) must clear the map or a colliding
 * instance id + namespace would hand the new mount the previous one's live store - reading
 * its graph data and writing to its key. Called from applyStudioConfig on every mount.
 */
export function dropMemoizedWorkspaceStores(): void {
  stores.clear();
}

/** Test hook: same drop, named for its use at test setup. */
export const resetInstanceStoresForTests = dropMemoizedWorkspaceStores;
