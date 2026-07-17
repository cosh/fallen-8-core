import { useEffect, useRef } from "react";
import ForceGraph3D, { type ForceGraph3DInstance } from "3d-force-graph";
import * as THREE from "three";
import type { CanvasEdge, CanvasNode } from "../state/instanceStore";
import type { StyleConfig } from "./styleConfig";
import type { ResolvedStyles } from "./styleEngine";
import { imageUrlFor } from "./imageAssets";
import type { ElementRef } from "./GraphCanvas";

/**
 * three.js (WebGL) 3D projection with feature parity to Canvas2D: resolved styles,
 * path-overlay dim/highlight, node/edge/background selection, image & emoji sprites.
 * Placement is d3-force-3d, optionally constrained to a DAG (FR-6); labels are hover
 * tooltips — the 3D labeling idiom (FR-7).
 */

interface FgNode {
  id: number;
  name: string;
  color: string;
  val: number;
  image: string | null;
  spriteScale: number;
}

interface FgLink {
  id: number;
  source: number | FgNode;
  target: number | FgNode;
  name: string;
  color: string;
  width: number;
}

// Texture cache shared across remounts; emoji data URLs and http URLs load the same way.
const textureCache = new Map<string, THREE.Texture>();

function textureFor(url: string): THREE.Texture {
  let texture = textureCache.get(url);
  if (!texture) {
    texture = new THREE.TextureLoader().load(url);
    texture.colorSpace = THREE.SRGBColorSpace;
    textureCache.set(url, texture);
  }
  return texture;
}

// nodeLabel/linkLabel render as HTML tooltips — labels and property values must not inject.
function escapeHtml(text: string): string {
  return text
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
}

// Sigma sizes are radii in px; nodeVal is proportional to sphere volume. Cubing the
// ratio keeps 3D radii proportional to their 2D counterparts (default size → val 1).
function sizeToVal(size: number): number {
  return Math.pow(size / 5, 3);
}

export function Canvas3D({
  nodes,
  edges,
  styles,
  config,
  onSelect,
}: {
  nodes: Record<number, CanvasNode>;
  edges: Record<number, CanvasEdge>;
  styles: ResolvedStyles;
  config: StyleConfig;
  onSelect: (ref: ElementRef | null) => void;
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const fgRef = useRef<ForceGraph3DInstance<FgNode, FgLink> | null>(null);
  const nodesByIdRef = useRef(new Map<number, FgNode>());

  // Mount the force graph once.
  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    const fg = new ForceGraph3D(container, { controlType: "orbit" }) as unknown as
      ForceGraph3DInstance<FgNode, FgLink>;
    fgRef.current = fg;

    fg.backgroundColor("#0b0e12")
      .showNavInfo(false)
      .width(container.clientWidth)
      .height(container.clientHeight)
      .nodeColor((n) => n.color)
      .nodeVal((n) => n.val)
      .nodeOpacity(0.9)
      .nodeThreeObject((n) => {
        if (!n.image) return null as unknown as THREE.Object3D;
        const sprite = new THREE.Sprite(
          new THREE.SpriteMaterial({ map: textureFor(n.image), transparent: true }),
        );
        sprite.scale.set(n.spriteScale, n.spriteScale, 1);
        return sprite;
      })
      .linkColor((l) => l.color)
      .linkWidth((l) => l.width)
      .linkOpacity(0.55)
      .onNodeClick((n) => onSelect({ kind: "node", id: n.id }))
      .onLinkClick((l) => onSelect({ kind: "edge", id: l.id }))
      .onBackgroundClick(() => onSelect(null))
      // Cyclic graphs are the norm here; DAG layouts just do their best (FR-6).
      .onDagError(() => undefined);

    const resize = new ResizeObserver(() => {
      fg.width(container.clientWidth).height(container.clientHeight);
    });
    resize.observe(container);

    return () => {
      resize.disconnect();
      fg._destructor();
      fgRef.current = null;
      nodesByIdRef.current = new Map();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Sync the store model + resolved styles. Node objects are reused by id so the
  // simulation keeps positions across merges (expand-on-demand must not reshuffle).
  useEffect(() => {
    const fg = fgRef.current;
    if (!fg) return;

    const previous = nodesByIdRef.current;
    const next = new Map<number, FgNode>();
    const fgNodes: FgNode[] = [];
    for (const node of Object.values(nodes)) {
      const style = styles.nodes[node.id];
      const fgNode = previous.get(node.id) ?? ({ id: node.id } as FgNode);
      fgNode.name = escapeHtml(node.label ? `${node.label} #${node.id}` : `#${node.id}`);
      fgNode.color = style.color;
      fgNode.val = sizeToVal(style.size);
      fgNode.image = style.image ? imageUrlFor(style.image) : null;
      fgNode.spriteScale = style.size * 1.6;
      next.set(node.id, fgNode);
      fgNodes.push(fgNode);
    }
    nodesByIdRef.current = next;

    const fgLinks: FgLink[] = [];
    for (const edge of Object.values(edges)) {
      if (!next.has(edge.source) || !next.has(edge.target)) continue;
      const style = styles.edges[edge.id];
      fgLinks.push({
        id: edge.id,
        source: edge.source,
        target: edge.target,
        name: escapeHtml(edge.edgePropertyId ?? edge.label ?? `#${edge.id}`),
        color: style.color,
        width: style.width,
      });
    }

    fg.graphData({ nodes: fgNodes, links: fgLinks });
    if (previous.size === 0 && next.size > 0) {
      window.setTimeout(() => fgRef.current?.zoomToFit(600, 60), 600);
    }
  }, [nodes, edges, styles]);

  // Render options (FR-6/FR-7): DAG constraint, hover labels, arrowheads.
  useEffect(() => {
    const fg = fgRef.current;
    if (!fg) return;
    fg.dagMode(
      (config.layout3d === "dag-td" ? "td" : config.layout3d === "dag-radial" ? "radialout" : null)!,
    ).dagLevelDistance(45);
    fg.nodeLabel(config.showNodeLabels ? (n) => n.name : () => "");
    fg.linkLabel(config.showEdgeLabels ? (l) => l.name : () => "");
    fg.linkDirectionalArrowLength(config.edgeArrows ? 3.5 : 0).linkDirectionalArrowRelPos(1);
  }, [config.layout3d, config.showNodeLabels, config.showEdgeLabels, config.edgeArrows]);

  return (
    <div
      ref={containerRef}
      data-testid="graph-canvas"
      className="bg-ink h-full w-full overflow-hidden"
      aria-label="graph canvas"
    />
  );
}
