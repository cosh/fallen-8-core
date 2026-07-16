import { create, type UseBoundStore, type StoreApi } from "zustand";
import { persist } from "zustand/middleware";
import type { CanvasEdgeInput, PathREST, VertexREST } from "../api/types";

/**
 * Per-instance workspace state (FR-1c), via a memoized store factory. Each instance id
 * owns exactly one store persisted under its own local-storage key, so canvas contents,
 * drafts, and result sets can never mix across instances - mixing is structurally
 * unrepresentable, not merely discouraged.
 */

export interface CanvasNode {
  id: number;
  label: string | null;
}

export interface CanvasEdge {
  id: number;
  source: number;
  target: number;
  edgePropertyId: string | null;
  label: string | null;
}

export interface ResultSet {
  id: string;
  title: string;
  createdAt: number;
  elementIds: number[];
}

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
};

/** One-shot navigation intent: "open Query with this scan pre-filled" (cleared on consume). */
export interface ScanPrefill {
  kind: "index";
  indexId: string;
}

export interface WorkspaceState {
  canvasNodes: Record<number, CanvasNode>;
  canvasEdges: Record<number, CanvasEdge>;
  pathOverlay: PathREST | null;
  resultSets: ResultSet[];
  pathDraft: PathDraft;
  scanPrefill: ScanPrefill | null;

  mergeIntoCanvas: (vertices: VertexREST[], edges: CanvasEdgeInput[]) => void;
  removeFromCanvas: (kind: "node" | "edge", id: number) => void;
  clearCanvas: () => void;
  setPathOverlay: (path: PathREST | null) => void;
  addResultSet: (title: string, elementIds: number[]) => void;
  removeResultSet: (id: string) => void;
  setPathDraft: (patch: Partial<PathDraft>) => void;
  setScanPrefill: (prefill: ScanPrefill | null) => void;
}

function createWorkspaceStore(instanceId: string) {
  return create<WorkspaceState>()(
    persist(
      (set) => ({
        canvasNodes: {},
        canvasEdges: {},
        pathOverlay: null,
        resultSets: [],
        pathDraft: { ...DEFAULT_PATH_DRAFT },
        scanPrefill: null,

        mergeIntoCanvas: (vertices, edges) =>
          set((s) => {
            const canvasNodes = { ...s.canvasNodes };
            const canvasEdges = { ...s.canvasEdges };
            for (const v of vertices) {
              canvasNodes[v.id] = { id: v.id, label: v.label ?? null };
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
      }),
      { name: `f8.workspace.${instanceId}` },
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
