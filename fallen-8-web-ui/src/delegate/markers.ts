// MIT License
//
// markers.ts
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

import type { DelegateDiagnostic } from "../api/types";

/**
 * Converts server diagnostics into Monaco marker data. The server already mapped
 * positions to fragment coordinates (backend DelegateValidationHelper), so lines and
 * columns pass through VERBATIM - re-mapping here is the off-by-N bug FR-24 forbids.
 * Severity constants match monaco.MarkerSeverity (Error=8, Warning=4, Info=2).
 */

export const MARKER_SEVERITY = { error: 8, warning: 4, info: 2 } as const;

export interface MarkerData {
  startLineNumber: number;
  startColumn: number;
  endLineNumber: number;
  endColumn: number;
  message: string;
  severity: number;
}

export function diagnosticsToMarkers(diagnostics: DelegateDiagnostic[]): MarkerData[] {
  return diagnostics.map((d) => ({
    startLineNumber: d.line,
    startColumn: d.column,
    endLineNumber: d.endLine,
    endColumn: d.endColumn,
    message: `${d.id}: ${d.message}`,
    severity: MARKER_SEVERITY[d.severity] ?? MARKER_SEVERITY.info,
  }));
}
