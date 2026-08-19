// MIT License
//
// F8GraphCanvas.tsx
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

import { useImperativeHandle, useRef, type CSSProperties, type Ref } from "react";
import {
  GraphCanvas,
  type ElementRef,
  type F8GraphCanvasHandle,
} from "../canvas/GraphCanvas";
import { DEFAULT_STYLE_CONFIG, type StyleConfig } from "../canvas/styleConfig";
import type { CanvasEdge, CanvasNode } from "../state/instanceStore";
import type { PathREST } from "../api/types";
import { themeStyle, type ThemeTokens } from "../app/studioConfig";

/**
 * The component-level embed (feature studio-embeddable): Studio's graph canvas as a
 * standalone, data-in/callback-out component for host pages that want an interactive graph
 * without mounting all of Studio. This prop shape is the FROZEN public contract - changing
 * `CanvasNode`/`CanvasEdge`/`StyleConfig`/`ElementRef` in a breaking way breaks hosts, which
 * is why it stays the minimal surface GraphCanvas actually reads (internal-only props such
 * as the adjacency-preview emphasis set are deliberately not part of it).
 */
export interface F8GraphCanvasProps {
  nodes: Record<number, CanvasNode>;
  edges: Record<number, CanvasEdge>;
  /** Style rules + renderer choice; defaults to Studio's canvas defaults. */
  config?: StyleConfig;
  /** A path result to spotlight (members highlighted, the rest dimmed). */
  pathOverlay?: PathREST | null;
  /** Transient node spotlight (the "eclipse" corona). */
  highlight?: ElementRef | null;
  onSelect?: (ref: ElementRef | null) => void;
  /** Token overrides for this embed; anything omitted keeps Studio's dark defaults. */
  theme?: Partial<ThemeTokens>;
  /**
   * Camera handle (feature canvas-host-controls): `fitToView`, `getCameraRatio`,
   * `setCameraRatio`. Optional like everything else here, and attaching it changes no rendering.
   */
  ref?: Ref<F8GraphCanvasHandle>;
}

/**
 * Renders inside its own `.f8-studio` scope root, so the canvas styling works on a page
 * that never mounted the Studio shell. Sized by the host: the wrapper fills its parent.
 */
export function F8GraphCanvas({
  nodes,
  edges,
  config = DEFAULT_STYLE_CONFIG,
  pathOverlay = null,
  highlight = null,
  onSelect,
  theme,
  ref,
}: F8GraphCanvasProps) {
  const handleRef = useRef<F8GraphCanvasHandle>(null);

  /**
   * Delegate to whichever renderer is mounted rather than handing the host the renderer's own handle:
   * switching `config.renderer` swaps 2D for 3D underneath, so a host that had stored the inner
   * handle would be holding a dead object. Delegating also means the methods stay callable after
   * unmount, as no-ops, instead of throwing at a host that kept a reference.
   */
  useImperativeHandle(
    ref,
    () => ({
      fitToView: (durationMs, paddingPx) => handleRef.current?.fitToView(durationMs, paddingPx),
      getCameraRatio: () => handleRef.current?.getCameraRatio() ?? 1,
      setCameraRatio: (ratio) => handleRef.current?.setCameraRatio(ratio),
    }),
    [],
  );

  /*
   * No resize handling lives here on purpose: each renderer owns its own, because only the renderer
   * can tell whether the visitor has taken the camera. This wrapper once observed the box and
   * re-fitted whenever the camera RATIO was still 1, which looked like "nobody has touched it" and
   * was not: sigma's drag handlers write x and y and never touch the ratio, so a visitor who had
   * panned still read as untouched and had their view yanked back on the next host reflow. Canvas2D
   * re-frames by refreshing without moving the camera at all, and Canvas3D re-fits only until its
   * orbit controls report the first interaction.
   */

  return (
    <div className="f8-studio" style={themeStyle(theme) as CSSProperties}>
      <GraphCanvas
        nodes={nodes}
        edges={edges}
        config={config}
        pathOverlay={pathOverlay}
        highlight={highlight}
        onSelect={onSelect ?? (() => {})}
        ref={handleRef}
      />
    </div>
  );
}
