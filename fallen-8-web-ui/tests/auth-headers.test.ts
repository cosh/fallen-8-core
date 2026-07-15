import { describe, expect, it } from "vitest";
import { authHeaders, buildUrl } from "../src/api/client";
import type { InstanceConfig } from "../src/instances/types";

/** Lightweight auth (feature web-ui): bearer by default, named header opt-in. */
describe("instance auth headers", () => {
  const base: Omit<InstanceConfig, "auth"> = { id: "i", name: "i", baseUrl: "" };

  it("sends nothing for auth kind none", () => {
    expect(authHeaders({ ...base, auth: { kind: "none" } })).toEqual({});
  });

  it("sends Authorization: Bearer by default (Cognito-shaped seam)", () => {
    expect(authHeaders({ ...base, auth: { kind: "apiKey", key: "s3cret" } })).toEqual({
      Authorization: "Bearer s3cret",
    });
  });

  it("sends a named header when configured", () => {
    expect(
      authHeaders({ ...base, auth: { kind: "apiKey", key: "s3cret", header: "X-Api-Key" } }),
    ).toEqual({ "X-Api-Key": "s3cret" });
  });
});

describe("url building", () => {
  it("keeps routes root-level against the instance base", () => {
    expect(buildUrl("http://h:1", "/status")).toBe("http://h:1/status");
    expect(buildUrl("", "/graph", { maxElements: 50 })).toBe("/graph?maxElements=50");
  });

  it("drops undefined query values", () => {
    expect(buildUrl("", "/save", { waitForCompletion: true, savePath: undefined })).toBe(
      "/save?waitForCompletion=true",
    );
  });
});
