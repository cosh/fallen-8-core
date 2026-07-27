// MIT License
//
// markers.test.ts
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
