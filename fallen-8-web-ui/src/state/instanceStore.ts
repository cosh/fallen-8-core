import { create, type UseBoundStore, type StoreApi } from "zustand";
import { persist } from "zustand/middleware";
import type { CanvasEdgeInput, PathREST, PropertyREST, VertexREST } from "../api/types";
import { DEFAULT_STYLE_CONFIG, type StyleConfig } from "../canvas/styleConfig";
import { DEFAULT_SEMANTIC_DRAFT, type SemanticDraft } from "../lib/semantic";

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
  label: string | null;
  props?: CanvasProps;
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

export interface ResultSet {
  id: string;
  title: string;
  createdAt: number;
  elementIds: number[];
}

/** Where a path/subgraph run takes its fragments from (concept spec §5.1). */
export type FilterSource = "inline" | "stored";

export interface PathDraft {
  from: string;
  to: string;
  algorithm: "BLS" | "DIJKSTRA";
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

/** One-shot navigation intent: "open Query with this scan pre-filled" (cleared on consume). */
export interface ScanPrefill {
  kind: "index";
  indexId: string;
}

/** One-shot navigation intent: "open Subgraph with this stored query selected". */
export interface SubgraphPrefill {
  storedQuery: string;
}

export interface WorkspaceState {
  canvasNodes: Record<number, CanvasNode>;
  canvasEdges: Record<number, CanvasEdge>;
  styleConfig: StyleConfig;
  pathOverlay: PathREST | null;
  resultSets: ResultSet[];
  pathDraft: PathDraft;
  scanPrefill: ScanPrefill | null;
  subgraphPrefill: SubgraphPrefill | null;

  mergeIntoCanvas: (vertices: VertexREST[], edges: CanvasEdgeInput[]) => void;
  removeFromCanvas: (kind: "node" | "edge", id: number) => void;
  clearCanvas: () => void;
  setStyleConfig: (patch: Partial<StyleConfig>) => void;
  setPathOverlay: (path: PathREST | null) => void;
  addResultSet: (title: string, elementIds: number[]) => void;
  removeResultSet: (id: string) => void;
  setPathDraft: (patch: Partial<PathDraft>) => void;
  setScanPrefill: (prefill: ScanPrefill | null) => void;
  setSubgraphPrefill: (prefill: SubgraphPrefill | null) => void;
}

function createWorkspaceStore(instanceId: string) {
  return create<WorkspaceState>()(
    persist(
      (set) => ({
        canvasNodes: {},
        canvasEdges: {},
        styleConfig: { ...DEFAULT_STYLE_CONFIG },
        pathOverlay: null,
        resultSets: [],
        pathDraft: { ...DEFAULT_PATH_DRAFT },
        scanPrefill: null,
        subgraphPrefill: null,

        mergeIntoCanvas: (vertices, edges) =>
          set((s) => {
            const canvasNodes = { ...s.canvasNodes };
            const canvasEdges = { ...s.canvasEdges };
            for (const v of vertices) {
              canvasNodes[v.id] = {
                id: v.id,
                label: v.label ?? null,
                props: snapshotProps(v.properties),
              };
            }
            for (const e of edges) {
              // An edge can only render when both endpoints are present; add stubs for
              // endpoints we have not hydrated yet so expand-on-demand stays merge-only.
              if (!canvasNodes[e.sourceVertex]) {
                canvasNodes[e.sourceVertex] = { id: e.sourceVertex, label: null };
              }
              if (!canvasNodes[e.targetVertex]) {
                canvasNodes[e.targetVertex] = { id: e.targetVertex, label: null };
              }
              canvasEdges[e.id] = {
                id: e.id,
                source: e.sourceVertex,
                target: e.targetVertex,
                edgePropertyId: e.edgePropertyId ?? null,
                label: e.label ?? e.edgePropertyId ?? null,
                props: snapshotProps(e.properties),
              };
            }
            return { canvasNodes, canvasEdges };
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

        clearCanvas: () => set({ canvasNodes: {}, canvasEdges: {}, pathOverlay: null }),

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

        setScanPrefill: (scanPrefill) => set({ scanPrefill }),

        setSubgraphPrefill: (subgraphPrefill) => set({ subgraphPrefill }),
      }),
      {
        name: `f8.workspace.${instanceId}`,
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
            styleConfig: { ...DEFAULT_STYLE_CONFIG, ...(p.styleConfig ?? {}) },
          };
        },
      },
    ),
  );
}

type WorkspaceStore = UseBoundStore<StoreApi<WorkspaceState>>;

const stores = new Map<string, WorkspaceStore>();

/** Returns the one store belonging to this instance id (memoized). */
export function getInstanceStore(instanceId: string): WorkspaceStore {
  let store = stores.get(instanceId);
  if (!store) {
    store = createWorkspaceStore(instanceId);
    stores.set(instanceId, store);
  }
  return store;
}

/** Test hook: drop all memoized stores (does not clear persisted state). */
export function resetInstanceStoresForTests(): void {
  stores.clear();
}
