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
