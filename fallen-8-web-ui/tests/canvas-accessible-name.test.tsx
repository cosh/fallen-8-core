// MIT License
//
// canvas-accessible-name.test.tsx
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

import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { Canvas2D } from "../src/canvas/Canvas2D";
import { Canvas3D } from "../src/canvas/Canvas3D";
import { DEFAULT_STYLE_CONFIG } from "../src/canvas/styleConfig";
import { EMPTY_OVERLAY, resolveStyles } from "../src/canvas/styleEngine";
import type { CanvasEdge, CanvasNode } from "../src/state/instanceStore";

/**
 * Both canvases carry aria-label="graph canvas", and an accessible name needs a role to attach to -
 * on a bare div there is none, so axe reports aria-prohibited-attr and a Lighthouse accessibility
 * score stops short of 100. These tests query BY ROLE rather than by test id on purpose: getByRole
 * resolves through the same accessible-name computation the audit uses, so dropping role="img" fails
 * here instead of only in a downstream audit of the embedded canvas.
 */

const NODES: Record<number, CanvasNode> = { 1: { id: 1, label: "a" } };
const EDGES: Record<number, CanvasEdge> = {};

// Sigma and three.js both want a real GL context; neither is the subject here, and the container div
// is rendered before either touches it.
vi.mock("sigma", () => import("./fakeSigma").then((m) => ({ default: m.FakeSigma })));
vi.mock("sigma/rendering", () => import("./fakeSigma").then((m) => m.sigmaRenderingModule));
vi.mock("@sigma/node-image", () => import("./fakeSigma").then((m) => m.sigmaNodeImageModule));
vi.mock("@sigma/edge-curve", () => import("./fakeSigma").then((m) => m.sigmaEdgeCurveModule));
vi.mock("graphology-layout-forceatlas2/worker", () =>
  import("./fakeSigma").then((m) => m.fa2WorkerModule));
vi.mock("graphology-layout-forceatlas2", () => import("./fakeSigma").then((m) => m.fa2Module));
vi.mock("3d-force-graph", () =>
  import("./fakeForceGraph").then((m) => ({ default: m.FakeForceGraph })));

describe("canvas accessible name", () => {
  it("names the 2D graph container through a role that legitimately takes one", () => {
    const styles = resolveStyles(NODES, EDGES, EMPTY_OVERLAY, DEFAULT_STYLE_CONFIG);
    render(
      <Canvas2D
        nodes={NODES}
        edges={EDGES}
        styles={styles}
        config={DEFAULT_STYLE_CONFIG}
        onSelect={() => {}}
      />,
    );

    const canvas = screen.getByRole("img", { name: "graph canvas" });
    expect(canvas).toBe(screen.getByTestId("graph-canvas"));
  });

  it("names the 3D graph container the same way", () => {
    const styles = resolveStyles(NODES, EDGES, EMPTY_OVERLAY, DEFAULT_STYLE_CONFIG);
    render(
      <Canvas3D
        nodes={NODES}
        edges={EDGES}
        styles={styles}
        config={DEFAULT_STYLE_CONFIG}
        onSelect={() => {}}
      />,
    );

    const canvas = screen.getByRole("img", { name: "graph canvas" });
    expect(canvas).toBe(screen.getByTestId("graph-canvas"));
  });
});

describe("the 3d-force-graph double has one home", () => {
  it("this file imports fakeForceGraph rather than hand-rolling its own proxy stub", () => {
    const source = readFileSync(fileURLToPath(import.meta.url), "utf8");
    expect(source).toMatch(/import\(["']\.\/fakeForceGraph["']\)/);
    // Built rather than written literally, so this assertion does not itself contain the very
    // string it is checking the file for.
    const oldMockClassName = ["class Fake", "ForceGraph3D"].join("");
    expect(source).not.toContain(oldMockClassName);
  });
});
