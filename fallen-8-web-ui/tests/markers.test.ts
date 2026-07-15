import { describe, expect, it } from "vitest";
import { diagnosticsToMarkers, MARKER_SEVERITY } from "../src/delegate/markers";

/**
 * FR-24 / spec §10 "diagnostic-position mapping": the server returns fragment
 * coordinates; the client must render them VERBATIM. Any offset here is the off-by-N
 * squiggle bug (double mapping).
 */
describe("diagnostic markers", () => {
  it("passes positions through unchanged - no client-side re-mapping", () => {
    const markers = diagnosticsToMarkers([
      {
        line: 2,
        column: 26,
        endLine: 2,
        endColumn: 29,
        id: "CS0103",
        message: "The name 'zzz' does not exist in the current context",
        severity: "error",
      },
    ]);
    expect(markers).toHaveLength(1);
    expect(markers[0].startLineNumber).toBe(2);
    expect(markers[0].startColumn).toBe(26);
    expect(markers[0].endLineNumber).toBe(2);
    expect(markers[0].endColumn).toBe(29);
    expect(markers[0].severity).toBe(MARKER_SEVERITY.error);
    expect(markers[0].message).toContain("CS0103");
  });

  it("maps severities to monaco constants", () => {
    const [error, warning, info] = diagnosticsToMarkers([
      { line: 1, column: 1, endLine: 1, endColumn: 2, id: "A", message: "", severity: "error" },
      { line: 1, column: 1, endLine: 1, endColumn: 2, id: "B", message: "", severity: "warning" },
      { line: 1, column: 1, endLine: 1, endColumn: 2, id: "C", message: "", severity: "info" },
    ]);
    expect(error.severity).toBe(8);
    expect(warning.severity).toBe(4);
    expect(info.severity).toBe(2);
  });
});
