import type { PatternSpecification } from "../api/types";

/**
 * Client-side pattern-sequence rules (FR-16), mirrored from the subgraph spec: the
 * sequence alternates vertex ↔ edge starting with a vertex, a level-0 edge (sequence
 * starting with an edge step) is also legal, VariableLengthEdge needs min ≤ max and the
 * API caps max length at 100. The server's 400 is still surfaced if it disagrees.
 */

export const MAX_VARIABLE_EDGE_LENGTH = 100;

function isEdgeStep(type: PatternSpecification["type"]): boolean {
  return type === "Edge" || type === "VariableLengthEdge";
}

/** Structural subset so both wire specs and builder drafts validate with one function. */
type PatternStep = Pick<PatternSpecification, "type" | "minLength" | "maxLength">;

export function validatePatternSequence(patterns: PatternStep[]): string | null {
  for (let i = 0; i < patterns.length; i++) {
    const step = patterns[i];

    if (i > 0) {
      const previous = patterns[i - 1];
      if (isEdgeStep(step.type) === isEdgeStep(previous.type)) {
        return `Step ${i + 1} (${step.type}) must alternate: a ${
          isEdgeStep(step.type) ? "vertex" : "edge"
        } step has to come between two ${isEdgeStep(step.type) ? "edge" : "vertex"} steps.`;
      }
    }

    if (step.type === "VariableLengthEdge") {
      const min = step.minLength ?? 0;
      const max = step.maxLength ?? 0;
      if (min > max) {
        return `Step ${i + 1}: minLength (${min}) exceeds maxLength (${max}).`;
      }
      if (max > MAX_VARIABLE_EDGE_LENGTH) {
        return `Step ${i + 1}: maxLength (${max}) exceeds the API cap of ${MAX_VARIABLE_EDGE_LENGTH}.`;
      }
    }
  }
  return null;
}
