// MIT License
//
// plugin-endpoints.test.ts
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

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import openapi from "../../features/done/web-ui/openapi-v0.1.json";
import * as endpoints from "../src/api/endpoints";
import { scaffoldFor, toClassIdentifier } from "../src/plugin/scaffolds";
import type { InstanceConfig } from "../src/instances/types";

/**
 * Plugin-registration API client (feature plugin-registration): every request the client
 * emits must match a path template + method in the OpenAPI snapshot (routes are root-level,
 * namespace-scoped), bodies serialize as the DTO the server expects, and the per-category
 * scaffolds are correct starter types. Mirrors api-contract.test.ts's contract check.
 */

const instance: InstanceConfig = {
  id: "t",
  name: "test",
  baseUrl: "http://f8.test",
  auth: { kind: "none" },
};

const namespaced: InstanceConfig = { ...instance, namespace: "default" };

interface Recorded {
  method: string;
  path: string;
  body: unknown;
}

let recorded: Recorded[] = [];

beforeEach(() => {
  recorded = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      const parsed = new URL(url);
      recorded.push({
        method: init?.method ?? "GET",
        path: parsed.pathname,
        body: init?.body ? JSON.parse(init.body as string) : undefined,
      });
      return new Response("null", { status: 200 });
    }),
  );
});

afterEach(() => vi.unstubAllGlobals());

const templates = Object.keys((openapi as { paths: Record<string, unknown> }).paths);

function matchesTemplate(path: string, template: string): boolean {
  const pathParts = path.split("/").filter(Boolean);
  const templateParts = template.split("/").filter(Boolean);
  if (pathParts.length !== templateParts.length) return false;
  return templateParts.every((part, i) => part.startsWith("{") || part === pathParts[i]);
}

function assertInContract(call: Recorded) {
  const template = templates.find(
    (t) =>
      matchesTemplate(call.path, t) &&
      Object.keys(
        (openapi as { paths: Record<string, Record<string, unknown>> }).paths[t],
      ).includes(call.method.toLowerCase()),
  );
  expect(template, `${call.method} ${call.path} is not in the OpenAPI contract`).toBeDefined();
}

describe("plugin endpoints — routes & bodies vs openapi-v0.1.json", () => {
  it("hits only contract routes at root level", async () => {
    await endpoints.listPlugins(instance);
    await endpoints.getPlugin(instance, "MyFunc");
    await endpoints.registerAlgorithmPlugin(instance, {
      name: "MyDijkstra",
      contract: "Path",
      description: "custom",
      sourceCode: "class X {}",
    });
    await endpoints.registerFunctionPlugin(instance, {
      name: "MyFunc",
      sourceCode: "class X {}",
    });
    await endpoints.validatePlugin(instance, "algorithm", {
      name: "MyDijkstra",
      contract: "Path",
      sourceCode: "class X {}",
    });
    await endpoints.validatePlugin(instance, "function", {
      name: "MyFunc",
      sourceCode: "class X {}",
    });
    await endpoints.invokeGraphFunction(instance, "MyFunc", { label: "person" });
    await endpoints.deletePlugin(instance, "MyFunc");

    expect(recorded.length).toBe(8);
    for (const call of recorded) {
      expect(call.path, "routes must be root-level").not.toMatch(/^\/api\//);
      assertInContract(call);
    }
  });

  it("serializes each request with the right method, path and body", async () => {
    await endpoints.listPlugins(instance);
    expect(recorded[0]).toMatchObject({ method: "GET", path: "/plugins" });

    await endpoints.getPlugin(instance, "My Func"); // name is URL-encoded
    expect(recorded[1]).toMatchObject({ method: "GET", path: "/plugins/My%20Func" });

    await endpoints.registerAlgorithmPlugin(instance, {
      name: "MyDijkstra",
      contract: "Path",
      sourceCode: "class X {}",
    });
    expect(recorded[2]).toEqual({
      method: "POST",
      path: "/plugins/algorithm",
      body: { name: "MyDijkstra", contract: "Path", sourceCode: "class X {}" },
    });

    await endpoints.registerFunctionPlugin(instance, {
      name: "MyFunc",
      description: "reads a label",
      sourceCode: "class X {}",
    });
    expect(recorded[3]).toEqual({
      method: "POST",
      path: "/plugins/function",
      body: { name: "MyFunc", description: "reads a label", sourceCode: "class X {}" },
    });

    await endpoints.validatePlugin(instance, "algorithm", {
      name: "MyDijkstra",
      contract: "SubGraph",
      sourceCode: "class X {}",
    });
    expect(recorded[4]).toEqual({
      method: "POST",
      path: "/plugins/algorithm/validate",
      body: { name: "MyDijkstra", contract: "SubGraph", sourceCode: "class X {}" },
    });

    // Function validate omits the (undefined) contract entirely.
    await endpoints.validatePlugin(instance, "function", {
      name: "MyFunc",
      sourceCode: "class X {}",
    });
    expect(recorded[5]).toEqual({
      method: "POST",
      path: "/plugins/function/validate",
      body: { name: "MyFunc", sourceCode: "class X {}" },
    });

    await endpoints.invokeGraphFunction(instance, "MyFunc", { label: "person" });
    expect(recorded[6]).toEqual({
      method: "POST",
      path: "/plugins/function/MyFunc/invoke",
      body: { parameters: { label: "person" } },
    });

    // No parameters → still an (empty) bag, never a missing body key.
    await endpoints.invokeGraphFunction(instance, "MyFunc");
    expect(recorded[7].body).toEqual({ parameters: {} });

    await endpoints.deletePlugin(instance, "MyFunc");
    expect(recorded[8]).toMatchObject({ method: "DELETE", path: "/plugins/MyFunc" });
  });

  it("namespace-scopes every plugin route incl. validate under /ns/{ns}", async () => {
    await endpoints.listPlugins(namespaced);
    await endpoints.validatePlugin(namespaced, "algorithm", {
      name: "X",
      contract: "Path",
      sourceCode: "class X {}",
    });
    await endpoints.invokeGraphFunction(namespaced, "MyFunc");

    expect(recorded[0].path).toBe("/ns/default/plugins");
    expect(recorded[1].path).toBe("/ns/default/plugins/algorithm/validate");
    expect(recorded[2].path).toBe("/ns/default/plugins/function/MyFunc/invoke");
    for (const call of recorded) assertInContract(call);
  });
});

describe("per-category scaffolds", () => {
  it("loads an algorithm scaffold implementing the contract's interface", () => {
    const path = scaffoldFor("algorithm", "Path", "MyDijkstra");
    expect(path).toContain(": IShortestPathAlgorithm");
    expect(path).toContain("TryCalculateShortestPath");
    expect(path).toContain('PluginName    => "MyDijkstra"');
    expect(path).toContain("using NoSQL.GraphDB.Core.Algorithms.Path;");

    const sub = scaffoldFor("algorithm", "SubGraph", "MySub");
    expect(sub).toContain(": ISubGraphAlgorithm");
    expect(sub).toContain("TryCreateSubgraph");

    const analytics = scaffoldFor("algorithm", "Analytics", "MyRank");
    expect(analytics).toContain(": IGraphAnalyticsAlgorithm");
    expect(analytics).toContain("TryRunAnalytics");
  });

  it("loads a function scaffold implementing IGraphFunction (contract ignored)", () => {
    const fn = scaffoldFor("function", "Path", "NeighboursOfLabel");
    expect(fn).toContain(": IGraphFunction");
    expect(fn).toContain("TryInvoke");
    expect(fn).toContain('PluginName    => "NeighboursOfLabel"');
    // The class identifier equals the (already-legal) name here.
    expect(fn).toContain("public sealed class NeighboursOfLabel");
  });

  it("sanitizes a dashed name into a legal C# class identifier, keeping PluginName exact", () => {
    expect(toClassIdentifier("my-plugin")).toBe("my_plugin");
    expect(toClassIdentifier("9lives")).toBe("_9lives");
    expect(toClassIdentifier("")).toBe("MyPlugin");

    const src = scaffoldFor("function", "Path", "my-func");
    expect(src).toContain("public sealed class my_func");
    expect(src).toContain('PluginName    => "my-func"');
  });
});
