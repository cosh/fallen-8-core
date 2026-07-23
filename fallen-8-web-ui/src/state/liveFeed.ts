import { useEffect, useState } from "react";
import { useQueryClient, type QueryClient } from "@tanstack/react-query";
import type { InstanceConfig } from "../instances/types";
import { getInstanceStore } from "./instanceStore";
import { getEdge } from "../api/endpoints";
import { streamChanges, type ChangeEvent } from "../api/changefeed";

/**
 * Live mode (feature change-feed, spec §3.7): ONE stream per ACTIVE instance, torn down
 * on instance switch (FR-1c isolation), turning committed mutations into UI updates.
 *
 * Integration approach - targeted query invalidation, not hand-rolled cache surgery:
 * every screen already reads through react-query keys scoped as [instance.id, ...], so
 * invalidating the relevant keys (debounced against event bursts) makes every screen
 * live without per-element cache merging. The canvas is the exception (it is a curated
 * zustand store, not a query), so it gets the direct minimum: removed elements are
 * dropped, and a created edge between two on-screen vertices is fetched and merged.
 *
 * Resync handling is mandatory, not optional: on ANY resync every query of the instance
 * is invalidated (the same fetches the screens issued to render initially); for reason
 * trim/tabulaRasa/load every held element id is invalid, so the canvas state is cleared.
 */

export type LiveFeedStatus = "off" | "connecting" | "live" | "unavailable";

export interface LiveFeedContext {
  instance: InstanceConfig;
  queryClient: QueryClient;
  /** Invalidation debounce window against event bursts (default 300ms). */
  debounceMs?: number;
}

export interface LiveFeedHandlers {
  onEvent: (event: ChangeEvent) => void;
  onResync: (event: ChangeEvent) => void;
  dispose: () => void;
}

/**
 * Builds the event handlers for one instance's stream. Exported separately from the
 * hook so the semantics are unit-testable without a component tree.
 */
export function createLiveFeedHandlers(ctx: LiveFeedContext): LiveFeedHandlers {
  const { instance, queryClient } = ctx;
  const debounceMs = ctx.debounceMs ?? 300;
  const store = getInstanceStore(instance.id);

  // Debounced, deduplicated invalidation: bursts of events (a batch transaction, a
  // generate run) collapse into one round of refetches per affected key.
  const pending = new Map<string, readonly unknown[]>();
  let timer: ReturnType<typeof setTimeout> | null = null;

  const flush = () => {
    timer = null;
    const keys = [...pending.values()];
    pending.clear();
    for (const queryKey of keys) {
      void queryClient.invalidateQueries({ queryKey });
    }
  };

  const schedule = (queryKey: readonly unknown[]) => {
    pending.set(JSON.stringify(queryKey), queryKey);
    timer ??= setTimeout(flush, debounceMs);
  };

  const onEvent = (event: ChangeEvent) => {
    const state = store.getState();

    switch (event.kind) {
      case "vertexRemoved":
        if (event.id !== undefined && state.canvasNodes[event.id]) {
          state.removeFromCanvas("node", event.id);
        }
        break;
      case "edgeRemoved":
        if (event.id !== undefined && state.canvasEdges[event.id]) {
          state.removeFromCanvas("edge", event.id);
        }
        break;
      case "edgeCreated":
        // A new edge between two vertices that are BOTH on screen belongs on screen;
        // the event carries only ids/label, so fetch the element and merge (the same
        // hydration path expand-on-demand uses). Anything else is not visible here.
        if (
          event.id !== undefined &&
          event.source !== undefined &&
          event.target !== undefined &&
          state.canvasNodes[event.source] &&
          state.canvasNodes[event.target] &&
          !state.canvasEdges[event.id]
        ) {
          void getEdge(instance, event.id)
            .then((edge) => {
              if (edge) {
                store.getState().mergeIntoCanvas([], [{ ...edge, edgePropertyId: null }]);
              }
            })
            .catch(() => {
              // The edge may be gone again by the time we fetch - the feed will say so.
            });
        }
        break;
      case "propertySet":
      case "propertyRemoved":
        if (event.id !== undefined) {
          // Element-detail queries (canvas detail panel keys node/edge, browser keys
          // the vertex adjacency views) - re-fetch the displayed element's detail.
          schedule([
            instance.id,
            "element",
            event.element === "edge" ? "edge" : "node",
            event.id,
          ]);
          if (event.element === "vertex") {
            schedule([instance.id, "vertex", event.id]);
          }
        }
        break;
      default:
        break;
    }

    // Dashboard/status counters follow the feed while the stream is up.
    schedule([instance.id, "status"]);
    if (event.kind !== "propertySet" && event.kind !== "propertyRemoved") {
      schedule([instance.id, "graph"]);
    }
  };

  const onResync = (event: ChangeEvent) => {
    // Continuity lost: re-fetch the visible state - every query this instance owns.
    if (timer !== null) {
      clearTimeout(timer);
      timer = null;
      pending.clear();
    }
    if (event.reason === "trim" || event.reason === "tabulaRasa" || event.reason === "load") {
      // Ids were reassigned or the graph replaced wholesale: every held id is invalid.
      store.getState().clearCanvas();
    }
    void queryClient.invalidateQueries({ queryKey: [instance.id] });
  };

  const dispose = () => {
    if (timer !== null) {
      clearTimeout(timer);
      timer = null;
    }
    pending.clear();
  };

  return { onEvent, onResync, dispose };
}

/**
 * Opens the active instance's change feed stream for the lifetime of the component
 * (AppShell), aborting it on instance switch or unmount. Returns the live status so the
 * shell can surface a quiet "live" / "live updates unavailable" chip - a 503 (feed
 * disabled) degrades to today's polling behaviour without console error spam.
 */
/**
 * Bumped when a namespace is recreated in place (feature graph-namespaces): the stream for
 * that namespace died "fatal" on the 404 and its effect key (the compound instance id) did
 * not change, so a generation counter is what forces the resubscribe.
 */
let feedGeneration = 0;
const feedGenerationListeners = new Set<() => void>();

export function bumpFeedGeneration(): void {
  feedGeneration++;
  for (const listener of feedGenerationListeners) listener();
}

function useFeedGeneration(): number {
  const [generation, setGeneration] = useState(feedGeneration);
  useEffect(() => {
    const listener = () => setGeneration(feedGeneration);
    feedGenerationListeners.add(listener);
    return () => {
      feedGenerationListeners.delete(listener);
    };
  }, []);
  return generation;
}

export function useLiveChangeFeed(instance: InstanceConfig | null): LiveFeedStatus {
  const queryClient = useQueryClient();
  const [status, setStatus] = useState<LiveFeedStatus>("off");
  const instanceId = instance?.id ?? null;
  const generation = useFeedGeneration();

  useEffect(() => {
    if (!instance) {
      setStatus("off");
      return;
    }

    const controller = new AbortController();
    const handlers = createLiveFeedHandlers({ instance, queryClient });
    setStatus("connecting");

    void streamChanges(instance, {
      signal: controller.signal,
      onEvent: handlers.onEvent,
      onResync: handlers.onResync,
      onOpen: () => setStatus("live"),
      onRetry: () => setStatus("connecting"),
      onError: () => {
        // Transient errors are handled by the reconnect loop; stay quiet (no spam).
      },
      onClose: (reason) => {
        if (reason === "unavailable") setStatus("unavailable");
        else if (reason === "fatal") setStatus("off");
        // "aborted" is the teardown path - the next effect run owns the status.
      },
    });

    return () => {
      controller.abort();
      handlers.dispose();
    };
    // Keyed by instance ID, not object identity: one stream per active instance (FR-1c);
    // the generation forces a resubscribe after an in-place namespace recreate.
  }, [instanceId, queryClient, generation]);

  return status;
}
