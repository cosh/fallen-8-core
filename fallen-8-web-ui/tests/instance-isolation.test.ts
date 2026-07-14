import { beforeEach, describe, expect, it } from "vitest";
import {
  getInstanceStore,
  resetInstanceStoresForTests,
} from "../src/state/instanceStore";
import type { VertexREST } from "../src/api/types";

/**
 * FR-1c: two registered instances never share or leak canvas/query context. The store
 * factory memoizes one store per instance id, persists under per-id keys, and offers no
 * way to address another instance's state.
 */

const vertex = (id: number): VertexREST => ({
  id,
  creationDate: "",
  modificationDate: "",
  label: "person",
});

describe("per-instance state isolation", () => {
  beforeEach(() => {
    resetInstanceStoresForTests();
    window.localStorage.clear();
  });

  it("memoizes exactly one store per instance id", () => {
    const a1 = getInstanceStore("inst-a");
    const a2 = getInstanceStore("inst-a");
    const b = getInstanceStore("inst-b");
    expect(a1).toBe(a2);
    expect(a1).not.toBe(b);
  });

  it("canvas contents of one instance never appear in another", () => {
    const a = getInstanceStore("inst-a");
    const b = getInstanceStore("inst-b");

    a.getState().mergeIntoCanvas([vertex(1), vertex(2)], []);

    expect(Object.keys(a.getState().canvasNodes)).toHaveLength(2);
    expect(Object.keys(b.getState().canvasNodes)).toHaveLength(0);
  });

  it("drafts are scoped per instance", () => {
    const a = getInstanceStore("inst-a");
    const b = getInstanceStore("inst-b");

    a.getState().setPathDraft({ from: "1", to: "2", vertexFilter: "return (v) => true;" });

    expect(a.getState().pathDraft.vertexFilter).toBe("return (v) => true;");
    expect(b.getState().pathDraft.vertexFilter).toBe("");
  });

  it("persists under distinct local-storage keys", () => {
    getInstanceStore("inst-a").getState().mergeIntoCanvas([vertex(1)], []);
    getInstanceStore("inst-b").getState().setPathDraft({ from: "9" });

    expect(window.localStorage.getItem("f8.workspace.inst-a")).toBeTruthy();
    expect(window.localStorage.getItem("f8.workspace.inst-b")).toBeTruthy();
    expect(window.localStorage.getItem("f8.workspace.inst-a")).not.toContain('"from":"9"');
  });

  it("merging is idempotent and edge endpoints get stub nodes", () => {
    const a = getInstanceStore("inst-a");
    a.getState().mergeIntoCanvas([vertex(1)], [
      {
        id: 10,
        creationDate: "",
        modificationDate: "",
        sourceVertex: 1,
        targetVertex: 2,
        edgePropertyId: "knows",
        label: null,
      },
    ]);
    a.getState().mergeIntoCanvas([vertex(1)], []);

    const state = a.getState();
    expect(Object.keys(state.canvasNodes)).toHaveLength(2); // 1 + stub for 2
    expect(state.canvasNodes[2].label).toBeNull();
    expect(Object.keys(state.canvasEdges)).toHaveLength(1);
  });

  it("removing a node removes its incident edges (view only)", () => {
    const a = getInstanceStore("inst-a");
    a.getState().mergeIntoCanvas(
      [vertex(1), vertex(2)],
      [
        {
          id: 10,
          creationDate: "",
          modificationDate: "",
          sourceVertex: 1,
          targetVertex: 2,
          edgePropertyId: "knows",
          label: null,
        },
      ],
    );
    a.getState().removeFromCanvas("node", 1);
    expect(a.getState().canvasNodes[1]).toBeUndefined();
    expect(Object.keys(a.getState().canvasEdges)).toHaveLength(0);
  });
});
