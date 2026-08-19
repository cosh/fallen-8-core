// MIT License
//
// Canvas2D.tsx
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

import { useEffect, useImperativeHandle, useRef, type Ref } from "react";
import Graph from "graphology";
import Sigma from "sigma";
import { EdgeArrowProgram, EdgeRectangleProgram, NodeCircleProgram } from "sigma/rendering";
import { createNodeImageProgram } from "@sigma/node-image";
import EdgeCurveProgram, {
  DEFAULT_EDGE_CURVATURE,
  EdgeCurvedArrowProgram,
  indexParallelEdgesIndex,
} from "@sigma/edge-curve";
import { circlepack, circular, random } from "graphology-layout";
import FA2Layout from "graphology-layout-forceatlas2/worker";
import forceAtlas2 from "graphology-layout-forceatlas2";
import { edgeDisplayName, type CanvasEdge, type CanvasNode } from "../state/instanceStore";
import type { StyleConfig } from "./styleConfig";
import type { ResolvedStyles } from "./styleEngine";
import { imageUrlFor } from "./imageAssets";
import { DEGRADE_THRESHOLD } from "./styling";
import {
  eclipseRadius,
  fitCameraRatio,
  fitDurationMs,
  isUsableCameraRatio,
} from "./eclipse";
import { FIT_DURATION_MS, type ElementRef, type F8GraphCanvasHandle } from "./GraphCanvas";

/**
 * Curvature spread for the i-th of a parallel-edge bundle (the sigma.js parallel-edges
 * recipe): amplitude-damped so big bundles stay inside a readable fan.
 */
function parallelCurvature(index: number, maxIndex: number): number {
  if (index < 0) return -parallelCurvature(-index, maxIndex);
  const amplitude = 3.5;
  const maxCurvature = amplitude * (1 - Math.exp(-maxIndex / amplitude)) * DEFAULT_EDGE_CURVATURE;
  return maxIndex === 0 ? 0 : (maxCurvature * index) / maxIndex;
}

/**
 * Sigma.js (WebGL) 2D projection. Renders resolved styles only — the mapping rules
 * live in styleEngine.ts; the layout/label/arrow options come from the style config.
 */
export function Canvas2D({
  nodes,
  edges,
  styles,
  config,
  highlightId,
  onSelect,
  ref,
}: {
  nodes: Record<number, CanvasNode>;
  edges: Record<number, CanvasEdge>;
  styles: ResolvedStyles;
  config: StyleConfig;
  highlightId?: number | null;
  onSelect: (ref: ElementRef | null) => void;
  ref?: Ref<F8GraphCanvasHandle>;
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const sigmaRef = useRef<Sigma | null>(null);
  const fa2Ref = useRef<FA2Layout | null>(null);
  const eclipseRef = useRef<HTMLDivElement>(null);
  const graphRef = useRef<Graph>(new Graph({ multi: true, type: "directed" }));

  // Click handlers live for the Sigma instance's lifetime; they must read the CURRENT
  // onSelect, or navigation guards upstream compare against a closure frozen at mount.
  const onSelectRef = useRef(onSelect);
  useEffect(() => {
    onSelectRef.current = onSelect;
  }, [onSelect]);

  // Mount Sigma once.
  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    const graph = graphRef.current;
    const sigma = new Sigma(graph, container, {
      allowInvalidContainer: true,
      // Off by default in Sigma v3 - without it clickEdge never fires, breaking edge
      // selection on the canvas and edge hops in the adjacency preview.
      enableEdgeEvents: true,
      // Width-1 edges render ~1px and the click hit area equals the rendered geometry -
      // a floor keeps them clickable without touching the resolved style widths.
      minEdgeThickness: 2.5,
      renderEdgeLabels: true,
      labelColor: { color: "#cdd6e4" },
      labelFont: "JetBrains Mono, monospace",
      // Initial values only; the effect below keeps them live when a host changes the config.
      labelSize: styles.magnitudes.labelSize,
      edgeLabelColor: { color: "#55647a" },
      edgeLabelSize: styles.magnitudes.edgeLabelSize,
      defaultEdgeColor: "#232a35",
      zIndex: true,
      nodeProgramClasses: {
        circle: NodeCircleProgram,
        image: createNodeImageProgram(),
      },
      edgeProgramClasses: {
        line: EdgeRectangleProgram,
        arrow: EdgeArrowProgram,
        curved: EdgeCurveProgram,
        curvedArrow: EdgeCurvedArrowProgram,
      },
    });
    sigmaRef.current = sigma;

    sigma.on("clickNode", ({ node }) => onSelectRef.current({ kind: "node", id: Number(node) }));
    sigma.on("clickEdge", ({ edge }) => {
      const id = graph.getEdgeAttribute(edge, "elementId") as number;
      onSelectRef.current({ kind: "edge", id });
    });
    sigma.on("clickStage", () => onSelectRef.current(null));

    // Sigma binds a window "resize" listener and nothing else, so a container-only reflow (a Studio
    // panel collapsing, a host's grid changing) leaves its canvases at the previous pixel size until
    // some unrelated render happens. This is the same call sigma's own window handler makes, for the
    // same reason (it has to rebuild the label grid), and it is not cheap: a full refresh re-indexes
    // every node and edge synchronously, only the paint is deferred to a frame. Matching sigma is
    // still right - doing less risks a stale label grid, and a resize storm costs sigma's own
    // window path exactly the same.
    //
    // Deliberately NOT a fit: a camera reset here would throw away the pan and zoom of a user who
    // merely opened a panel. Under autoRescale the refresh alone re-frames a graph whose camera is
    // untouched, so framing follows the box while the camera stays the user's.
    const resize = new ResizeObserver(() => sigmaRef.current?.scheduleRefresh());
    resize.observe(container);

    return () => {
      resize.disconnect();
      fa2Ref.current?.kill();
      fa2Ref.current = null;
      sigma.kill();
      sigmaRef.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Sync the store model + resolved styles into graphology (merge-only diffing).
  useEffect(() => {
    const graph = graphRef.current;
    const elementCount = Object.keys(nodes).length + Object.keys(edges).length;
    const showLabels = elementCount <= DEGRADE_THRESHOLD;

    const seenNodes = new Set<string>();
    for (const node of Object.values(nodes)) {
      const key = String(node.id);
      seenNodes.add(key);
      const style = styles.nodes[node.id];
      const image = style.image ? imageUrlFor(style.image) : null;
      const attributes = {
        label:
          showLabels && config.showNodeLabels
            ? node.label
              ? `${node.label} #${node.id}`
              : `#${node.id}`
            : null,
        color: style.color,
        size: style.size,
        zIndex: style.zIndex,
        type: image ? "image" : "circle",
        image: image ?? undefined,
        // Grouping key for the circlepack layout (display labels carry "#id" noise).
        group: node.label ?? "",
      };
      if (graph.hasNode(key)) {
        graph.mergeNodeAttributes(key, attributes);
      } else {
        graph.addNode(key, {
          ...attributes,
          x: Math.random() * 10 - 5,
          y: Math.random() * 10 - 5,
        });
      }
    }
    for (const nodeKey of graph.nodes()) {
      if (!seenNodes.has(nodeKey)) graph.dropNode(nodeKey);
    }

    const seenEdges = new Set<string>();
    for (const edge of Object.values(edges)) {
      const key = `e${edge.id}`;
      const source = String(edge.source);
      const target = String(edge.target);
      if (!graph.hasNode(source) || !graph.hasNode(target)) continue;
      seenEdges.add(key);
      const style = styles.edges[edge.id];
      const attributes = {
        elementId: edge.id,
        label: showLabels && config.showEdgeLabels ? (edgeDisplayName(edge) ?? undefined) : undefined,
        color: style.color,
        size: style.width,
        zIndex: style.zIndex,
      };
      if (graph.hasEdge(key)) {
        graph.mergeEdgeAttributes(key, attributes);
      } else {
        graph.addEdgeWithKey(key, source, target, attributes);
      }
    }
    for (const edgeKey of graph.edges()) {
      if (!seenEdges.has(edgeKey)) graph.dropEdge(edgeKey);
    }

    // Parallel edges (same endpoint pair, either direction) would render as coincident
    // straight lines - fan them out with spread curvatures instead.
    indexParallelEdgesIndex(graph);
    graph.forEachEdge((edgeKey, attributes) => {
      const parallelIndex = attributes.parallelIndex as number | null;
      const parallelMaxIndex = attributes.parallelMaxIndex as number | null;
      if (typeof parallelIndex === "number") {
        graph.mergeEdgeAttributes(edgeKey, {
          type: config.edgeArrows ? "curvedArrow" : "curved",
          curvature: parallelCurvature(parallelIndex, parallelMaxIndex ?? 1),
        });
      } else {
        graph.mergeEdgeAttributes(edgeKey, {
          type: config.edgeArrows ? "arrow" : "line",
          curvature: 0,
        });
      }
    });

    sigmaRef.current?.refresh();
  }, [nodes, edges, styles, config.showNodeLabels, config.showEdgeLabels, config.edgeArrows]);

  /**
   * The host-facing camera handle (feature canvas-host-controls). Empty deps: every method reads
   * sigmaRef at call time, so the identity is stable for the component's whole life and a host can
   * hold onto it.
   */
  useImperativeHandle(
    ref,
    () => ({
      fitToView(durationMs, paddingPx) {
        const sigma = sigmaRef.current;
        if (!sigma) return;
        // getDimensions() is a cache that only resize()/render() refresh, so a fit triggered BY a
        // container change would otherwise measure the box the graph just left.
        sigma.resize();
        const stagePadding = sigma.getStagePadding();
        const ratio = fitCameraRatio(sigma.getDimensions(), stagePadding, paddingPx ?? stagePadding);
        // x/y 0.5 is the bounding box centre under autoRescale. angle 0 is load-bearing rather
        // than tidy: matrixFromCamera rotates AFTER dividing by the ratio, so a rotated camera
        // lets the corners of the box back out of frame. The duration floor is load-bearing too -
        // sigma's tween computes elapsed/duration, and 0/0 writes NaN into x, y and ratio for a
        // frame, which blanks the canvas.
        void sigma.getCamera().animate(
          { x: 0.5, y: 0.5, ratio, angle: 0 },
          // Floor of 1: sigma cannot take a zero duration (fitDurationMs says why).
          { duration: fitDurationMs(durationMs, FIT_DURATION_MS, 1) },
        );
        // An unchanged camera state emits nothing, so a fit that asks for the frame already in
        // effect would leave a freshly resized layer unpainted. scheduleRender is rAF-debounced.
        sigma.scheduleRender();
      },
      getCameraRatio: () => sigmaRef.current?.getCamera().ratio ?? 1,
      setCameraRatio: (ratio) => {
        if (!isUsableCameraRatio(ratio)) return;
        // NOT setState: a fitToView tween in flight keeps its own start-state snapshot and an rAF
        // loop that interpolates straight over any setState, so it would swallow this write and land
        // on the fit's ratio instead. animate() is the only public way to cancel a running tween, so
        // every write goes through it; one frame is the shortest duration sigma can divide by.
        void sigmaRef.current?.getCamera().animate({ ratio }, { duration: 1 });
      },
    }),
    [],
  );

  // Label sizes are Sigma SETTINGS, not per-element attributes, so the sync effect above cannot
  // carry them; a host that raises labelSize for a wide container needs it to land without a
  // remount. Skipped while they still match what the constructor was given: setSettings re-validates
  // the settings, rewrites the camera state and schedules a full refresh, and paying for that on
  // mount to write the values Sigma was just built with is a second O(V+E) re-index for nothing.
  const appliedLabelSizesRef = useRef({
    labelSize: styles.magnitudes.labelSize,
    edgeLabelSize: styles.magnitudes.edgeLabelSize,
  });
  useEffect(() => {
    const { labelSize, edgeLabelSize } = styles.magnitudes;
    const applied = appliedLabelSizesRef.current;
    if (applied.labelSize === labelSize && applied.edgeLabelSize === edgeLabelSize) return;
    appliedLabelSizesRef.current = { labelSize, edgeLabelSize };
    sigmaRef.current?.setSettings({ labelSize, edgeLabelSize });
  }, [styles.magnitudes.labelSize, styles.magnitudes.edgeLabelSize]);

  // Layout control (FR-6): FA2 in a worker for "force"; the rest are deterministic.
  useEffect(() => {
    const graph = graphRef.current;
    if (config.layout2d !== "force") {
      fa2Ref.current?.stop();
      if (graph.order > 0) {
        switch (config.layout2d) {
          case "circular":
            circular.assign(graph, { scale: 100 });
            break;
          case "circlepack":
            circlepack.assign(graph, { hierarchyAttributes: ["group"] });
            break;
          case "random":
            random.assign(graph, { scale: 100 });
            break;
          case "grid": {
            const keys = [...graph.nodes()].sort((a, b) => Number(a) - Number(b));
            const cols = Math.max(1, Math.ceil(Math.sqrt(keys.length)));
            keys.forEach((key, i) => {
              graph.setNodeAttribute(key, "x", (i % cols) * 10);
              graph.setNodeAttribute(key, "y", Math.floor(i / cols) * 10);
            });
            break;
          }
        }
      }
      sigmaRef.current?.refresh();
      return;
    }

    if (graph.order === 0) return;
    if (!fa2Ref.current) {
      const settings = forceAtlas2.inferSettings(graph);
      fa2Ref.current = new FA2Layout(graph, { settings });
    }
    fa2Ref.current.start();
    const stopTimer = window.setTimeout(() => fa2Ref.current?.stop(), 5_000);
    return () => window.clearTimeout(stopTimer);
  }, [config.layout2d, nodes, edges]);

  // Hover spotlight (feature canvas-find-connect): while a Find result row is hovered, track its
  // node's live viewport position every frame and park the "eclipse" corona over it. With no
  // highlight (or the node absent from this canvas) the loop never runs and the element stays
  // hidden, so there is zero cost off-hover.
  useEffect(() => {
    const el = eclipseRef.current;
    if (highlightId == null || !el) {
      if (el) el.style.display = "none";
      return;
    }
    let raf = 0;
    const tick = () => {
      const sigma = sigmaRef.current;
      const dd = sigma?.getNodeDisplayData(String(highlightId));
      if (!sigma || !dd) {
        el.style.display = "none";
      } else {
        const { x, y } = sigma.framedGraphToViewport({ x: dd.x, y: dd.y });
        const r = eclipseRadius(sigma.scaleSize(dd.size));
        el.style.display = "block";
        el.style.left = `${x}px`;
        el.style.top = `${y}px`;
        el.style.setProperty("--eclipse-r", `${r}px`);
      }
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => {
      cancelAnimationFrame(raf);
      el.style.display = "none";
    };
  }, [highlightId]);

  return (
    <div className="relative h-full w-full">
      {/*
        role="img" is what makes the aria-label legal: a bare div exposes no role, so there is
        nothing for an accessible name to attach to, and axe rightly flags aria-prohibited-attr.
        Sigma fills this element with WebGL canvas layers that expose nothing to assistive
        technology, so presenting the whole thing as ONE labelled image is not a workaround - it is
        an honest description of what is there. Deliberately not role="application" or "group":
        both would also make the label legal, but they change how a screen reader treats the
        subtree, and there is no keyboard-navigable structure inside here to justify that.
      */}
      <div
        ref={containerRef}
        data-testid="graph-canvas"
        className="bg-ink h-full w-full"
        role="img"
        aria-label="graph canvas"
      />
      <div ref={eclipseRef} className="eclipse-highlight" style={{ display: "none" }} aria-hidden />
    </div>
  );
}
