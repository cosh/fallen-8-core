import { describe, expect, it } from "vitest";
import { formatFragment } from "../src/delegate/nl/format";

/**
 * Deterministic draft pretty-printing (nl-assist-ux FR-9): long top-level boolean chains
 * break onto operator-prefixed lines; everything the scanner isn't sure about passes
 * through unchanged.
 */

describe("formatFragment", () => {
  it("leaves short fragments on one line", () => {
    expect(formatFragment("return (v) => v.Id < 30;")).toBe("return (v) => v.Id < 30;");
    expect(formatFragment('return (v) => v.Label == "person" && v.Id < 10;')).toBe(
      'return (v) => v.Label == "person" && v.Id < 10;',
    );
  });

  it("breaks a long && chain at top level, one operand per line", () => {
    const oneLiner =
      'return (ge) => ge.Label == "person" && ge.TryGetProperty(out int age, "age") && age > 30 && ge.Id < 10;';
    expect(formatFragment(oneLiner)).toBe(
      [
        "return (ge) =>",
        '    ge.Label == "person"',
        '    && ge.TryGetProperty(out int age, "age")',
        "    && age > 30",
        "    && ge.Id < 10;",
      ].join("\n"),
    );
  });

  it("never splits inside parentheses or string literals", () => {
    const nested =
      'return (v) => v.GetAllNeighbors().Any(n => n.Label == "a" && n.Id > 1) && v.TryGetProperty(out string s, "x && y") && s.Length > 0;';
    const formatted = formatFragment(nested);
    // The inner && (inside Any(...)) and the one inside the string stay put.
    expect(formatted).toContain('Any(n => n.Label == "a" && n.Id > 1)');
    expect(formatted).toContain('"x && y"');
    expect(formatted.split("\n")).toHaveLength(4);
  });

  it("splits the outer lambda, not a nested one", () => {
    const formatted = formatFragment(
      'return (v) => v.OutEdges.Count > 0 && v.GetAllNeighbors().All(n => n.Label != null) && v.Label == "hub";',
    );
    expect(formatted.split("\n")[0]).toBe("return (v) =>");
  });

  it("normalizes model-emitted line breaks before reflowing", () => {
    const multiLine =
      'return (v) =>\n  v.Label == "person"\n     &&   v.TryGetProperty(out int age, "age")\n && age > 30 && v.GetOutDegree() >= 2;';
    expect(formatFragment(multiLine)).toBe(
      [
        "return (v) =>",
        '    v.Label == "person"',
        '    && v.TryGetProperty(out int age, "age")',
        "    && age > 30",
        "    && v.GetOutDegree() >= 2;",
      ].join("\n"),
    );
  });

  it("passes through shapes it does not understand", () => {
    const block =
      'return (v) => { var ok = v.TryGetProperty(out int age, "age"); return ok && age > 30 && v.Id < 10 && v.Label != null; };';
    expect(formatFragment(block)).toBe(block);

    const ternary =
      'return (e) => e.TryGetProperty(out double weight, "weight") ? weight * 2.5 + 1.0 : 1.0 + 2.0 + 3.0 + 4.0;';
    // No top-level && / || - single long operand stays as-is.
    expect(formatFragment(ternary)).toBe(ternary);
  });
});
