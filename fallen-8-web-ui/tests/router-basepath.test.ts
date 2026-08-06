// MIT License
//
// router-basepath.test.ts
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

import { describe, expect, it, vi } from "vitest";

// The route tree statically imports CanvasScreen -> Canvas2D -> sigma, and sigma needs
// WebGL at import time; the same fakes the canvas tests use keep jsdom collection alive.
vi.mock("sigma", () => ({ default: class {} }));
vi.mock("sigma/rendering", () => ({
  EdgeArrowProgram: class {},
  EdgeRectangleProgram: class {},
  NodeCircleProgram: class {},
}));
vi.mock("@sigma/node-image", () => ({ createNodeImageProgram: () => class {} }));
vi.mock("@sigma/edge-curve", () => ({
  default: class {},
  EdgeCurvedArrowProgram: class {},
  DEFAULT_EDGE_CURVATURE: 0.25,
  indexParallelEdgesIndex: () => {},
}));
vi.mock("graphology-layout-forceatlas2/worker", () => ({
  default: class {
    start() {}
    stop() {}
    kill() {}
  },
}));
vi.mock("graphology-layout-forceatlas2", () => ({
  default: { inferSettings: () => ({}) },
}));
// monacoSetup imports monaco-editor with ?worker specifiers vite-in-vitest cannot resolve.
vi.mock("../src/delegate/monacoSetup", () => ({
  setupMonaco: () => {},
  monaco: {},
}));

import { createStudioRouter, router } from "../src/app/routes";

/**
 * The router seam (feature studio-embeddable): a host basepath prefixes every route while
 * the standalone router keeps building root-relative locations - the default config must
 * stay byte-identical to the pre-seam URLs.
 */
describe("createStudioRouter basepath", () => {
  it("keeps the standalone router at the root", () => {
    expect(router.buildLocation({ to: "/save-games" }).href).toBe("/save-games");
    expect(
      router.buildLocation({ to: "/q/$ns/dashboard", params: { ns: "default" } }).href,
    ).toBe("/q/default/dashboard");
  });

  it("prefixes every route with the host basepath", () => {
    const hosted = createStudioRouter({ basepath: "/studio" });
    expect(hosted.buildLocation({ to: "/save-games" }).href).toBe("/studio/save-games");
    expect(
      hosted.buildLocation({ to: "/q/$ns/dashboard", params: { ns: "ops" } }).href,
    ).toBe("/studio/q/ops/dashboard");
  });
});
