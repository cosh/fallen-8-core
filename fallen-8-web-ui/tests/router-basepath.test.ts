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
      router.buildLocation({ to: "/q/$ns/browser", params: { ns: "default" } }).href,
    ).toBe("/q/default/browser");
  });

  it("prefixes every route with the host basepath", () => {
    const hosted = createStudioRouter({ basepath: "/studio" });
    expect(hosted.buildLocation({ to: "/save-games" }).href).toBe("/studio/save-games");
    expect(
      hosted.buildLocation({ to: "/q/$ns/browser", params: { ns: "ops" } }).href,
    ).toBe("/studio/q/ops/browser");
  });

  it("resolves a navigation under the basepath and leaves the host's address bar alone", async () => {
    // Building an href and RESOLVING one are different paths. The prefix is applied at the
    // history layer while router state stays basepath-relative, so a navigation is a full
    // round-trip: prefixed URL out, matched route back. Memory history keeps the host's own
    // address bar untouched throughout.
    const hosted = createStudioRouter({ basepath: "/studio", history: "memory" });
    await hosted.load();
    await hosted.navigate({ to: "/q/$ns/browser", params: { ns: "ops" } });

    expect(hosted.history.location.pathname).toBe("/studio/q/ops/browser");
    expect(hosted.state.location.pathname).toBe("/q/ops/browser");
    expect(hosted.state.matches.at(-1)?.routeId).toBe("/q/$ns/browser");
    expect(window.location.pathname).toBe("/");
  });

  it("keeps a legacy-path redirect inside the basepath", async () => {
    // The pre-namespace bookmarks redirect through `throw redirect(...)`; a redirect that
    // skipped the basepath would send an embedded Studio out of its host mount point.
    const hosted = createStudioRouter({ basepath: "/studio", history: "memory" });
    await hosted.load();
    await hosted.navigate({ to: "/canvas" });

    expect(hosted.history.location.pathname).toMatch(/^\/studio\/q\/[^/]+\/canvas$/);
    expect(hosted.state.matches.at(-1)?.routeId).toBe("/q/$ns/canvas");
  });

  /**
   * A bare `/q/{ns}` is a real route with no index child, so TanStack renders the shell around an
   * empty Outlet there. `sameScopedScreen` therefore must not fall back to it: the button that
   * offers a way OUT of an unreadable namespace would land the operator on a blank screen, which is
   * the exact failure the redirect below exists to prevent.
   */
  it("never resolves the no-leaf case to a route with no screen", async () => {
    const { sameScopedScreen, scopedLeaf } = await import("../src/app/scopedRoute");
    expect(scopedLeaf("/q/ghost")).toBe("");
    expect(sameScopedScreen("/q/ghost")).toBe("/q/$ns/browser");

    const r = createStudioRouter({ history: "memory" });
    await r.load();
    await r.navigate({ to: sameScopedScreen("/q/ghost"), params: { ns: "default" } });
    expect(r.state.matches.at(-1)?.routeId).toBe("/q/$ns/browser");
  });

  /**
   * The Dashboard's two URLs outlive the screen. Dropping the routes would have answered every
   * bookmark with the shell wrapped around an empty <Outlet/> (there is no not-found component),
   * so both forward to the Browser in the namespace they named - the flat one through the scoped
   * one, which is why the second hop is worth pinning as well as the first.
   */
  describe("the removed Dashboard's bookmarks", () => {
    it("forwards the scoped URL to the Browser in the SAME namespace", async () => {
      const scoped = createStudioRouter({ history: "memory" });
      await scoped.load();
      await scoped.navigate({ to: "/q/$ns/dashboard" as "/q/$ns/browser", params: { ns: "ops" } });

      expect(scoped.state.location.pathname).toBe("/q/ops/browser");
      expect(scoped.state.matches.at(-1)?.routeId).toBe("/q/$ns/browser");
    });

    it("forwards the flat pre-namespace URL through it, onto the active namespace", async () => {
      const flat = createStudioRouter({ history: "memory" });
      await flat.load();
      await flat.navigate({ to: "/dashboard" as "/canvas" });

      expect(flat.state.location.pathname).toMatch(/^\/q\/[^/]+\/browser$/);
      expect(flat.state.matches.at(-1)?.routeId).toBe("/q/$ns/browser");
    });
  });

  /**
   * The four URLs the two screens Traverse absorbed leave behind (feature
   * studio-traverse-merge). Landing on the merged screen is not enough: each has to land on the
   * TAB it named, or a three-year-old bookmark to the subgraph builder opens the path finder.
   */
  describe("the absorbed Path and Subgraph bookmarks", () => {
    it.each([
      ["/q/$ns/path", "path"],
      ["/q/$ns/subgraphs", "subgraph"],
    ] as const)("forwards the scoped %s onto its tab, keeping the namespace", async (from, tab) => {
      const scoped = createStudioRouter({ history: "memory" });
      await scoped.load();
      await scoped.navigate({ to: from as "/q/$ns/traverse", params: { ns: "ops" } });

      expect(scoped.state.location.pathname).toBe("/q/ops/traverse");
      expect(scoped.state.location.search).toEqual({ tab });
      expect(scoped.state.matches.at(-1)?.routeId).toBe("/q/$ns/traverse");
    });

    it.each([
      ["/path", "path"],
      ["/subgraphs", "subgraph"],
    ] as const)("forwards the flat %s onto the active namespace's tab in ONE hop", async (from, tab) => {
      const flat = createStudioRouter({ history: "memory" });
      await flat.load();
      await flat.navigate({ to: from as "/canvas" });

      expect(flat.state.location.pathname).toMatch(/^\/q\/[^/]+\/traverse$/);
      expect(flat.state.location.search).toEqual({ tab });
      expect(flat.state.matches.at(-1)?.routeId).toBe("/q/$ns/traverse");
    });
  });
});
