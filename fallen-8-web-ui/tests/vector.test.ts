import { describe, expect, it } from "vitest";
import { parseVector } from "../src/lib/vector";

/**
 * Query-vector parsing (concept spec §6): dimension mismatches and NaNs must be caught
 * client-side, before the server's 400 — vectors are pasted in either JSON-array or
 * separator-delimited form.
 */
describe("parseVector", () => {
  it("parses a JSON array", () => {
    expect(parseVector("[0.12, -0.5, 0.33]")).toEqual({
      ok: true,
      vector: [0.12, -0.5, 0.33],
    });
  });

  it("parses comma/whitespace/semicolon-separated floats", () => {
    expect(parseVector("0.1, 0.2,0.3")).toEqual({ ok: true, vector: [0.1, 0.2, 0.3] });
    expect(parseVector("1 2\n3")).toEqual({ ok: true, vector: [1, 2, 3] });
    expect(parseVector("1;2;3")).toEqual({ ok: true, vector: [1, 2, 3] });
    expect(parseVector("1e-3, -2.5E2")).toEqual({ ok: true, vector: [0.001, -250] });
  });

  it("rejects empty input", () => {
    expect(parseVector("").ok).toBe(false);
    expect(parseVector("   ").ok).toBe(false);
    expect(parseVector("[]").ok).toBe(false);
  });

  it("rejects malformed JSON and non-arrays", () => {
    expect(parseVector("[0.1, 0.2").ok).toBe(false);
    expect(parseVector('{"a": 1}').ok).toBe(false);
  });

  it("rejects non-finite components with a 1-based position", () => {
    const nan = parseVector("[0.1, null, 0.3]");
    expect(nan).toEqual({ ok: false, error: "component 2 is not a finite number" });
    expect(parseVector("0.1, abc").ok).toBe(false);
    expect(parseVector('[1, 2, 1e999]').ok).toBe(false); // Infinity after JSON.parse
    expect(parseVector("[true]").ok).toBe(false); // booleans are not floats
  });
});
