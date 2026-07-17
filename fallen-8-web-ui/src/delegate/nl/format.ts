/**
 * Deterministic pretty-printer for generated fragments (nl-assist-ux FR-9): models emit
 * one-line lambdas; long boolean chains are broken at TOP-LEVEL && / || so the draft is
 * readable in the editor. No model round-trip - a second generation pass would be slow
 * (CPU inference) and could change semantics; this never does.
 */

/** One-liners up to this width stay one line. */
const MAX_SINGLE_LINE = 72;

export function formatFragment(fragment: string): string {
  const collapsed = collapseWhitespace(fragment.trim());
  if (collapsed.length <= MAX_SINGLE_LINE) return collapsed;
  if (!collapsed.startsWith("return") || !collapsed.endsWith(";")) return collapsed;

  const arrow = topLevelArrowIndex(collapsed);
  if (arrow < 0) return collapsed;
  const header = collapsed.slice(0, arrow + 2).trimEnd();
  const body = collapsed.slice(arrow + 2, -1).trim();
  // Only the expression-lambda shape is formatted; anything else passes through.
  if (body.startsWith("{")) return collapsed;

  const operands = splitTopLevel(body);
  if (operands.length < 2) return collapsed;

  const lines = [header, `    ${operands[0].text}`];
  for (const operand of operands.slice(1)) {
    lines.push(`    ${operand.op} ${operand.text}`);
  }
  return `${lines.join("\n")};`;
}

interface Operand {
  /** The operator preceding this operand ("" for the first). */
  op: "" | "&&" | "||";
  text: string;
}

/**
 * Minimal scanner shared by the helpers: walks the fragment tracking bracket depth and
 * string/char literals, invoking `visit` only at top level outside literals.
 */
function scan(
  text: string,
  visit: (index: number) => number | void,
): void {
  let depth = 0;
  let quote: '"' | "'" | null = null;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (quote) {
      if (ch === "\\") i++;
      else if (ch === quote) quote = null;
      continue;
    }
    if (ch === '"' || ch === "'") {
      quote = ch;
      continue;
    }
    if (ch === "(" || ch === "[" || ch === "{") depth++;
    else if (ch === ")" || ch === "]" || ch === "}") depth--;
    else if (depth === 0) {
      const skip = visit(i);
      if (skip !== undefined) i = skip;
    }
  }
}

/** Collapses whitespace runs to single spaces, preserving string/char literals. */
function collapseWhitespace(text: string): string {
  let result = "";
  let quote: '"' | "'" | null = null;
  let pendingSpace = false;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (quote) {
      result += ch;
      if (ch === "\\" && i + 1 < text.length) result += text[++i];
      else if (ch === quote) quote = null;
      continue;
    }
    if (/\s/.test(ch)) {
      pendingSpace = result.length > 0;
      continue;
    }
    if (pendingSpace) {
      result += " ";
      pendingSpace = false;
    }
    if (ch === '"' || ch === "'") quote = ch;
    result += ch;
  }
  return result;
}

/** Index of the outer lambda's `=>` (top level, outside literals), or -1. */
function topLevelArrowIndex(text: string): number {
  let found = -1;
  scan(text, (i) => {
    if (found < 0 && text[i] === "=" && text[i + 1] === ">") found = i;
  });
  return found;
}

/** Splits a boolean expression at top-level && / || into operator-prefixed operands. */
function splitTopLevel(body: string): Operand[] {
  const operands: Operand[] = [];
  let start = 0;
  let op: Operand["op"] = "";
  scan(body, (i) => {
    const pair = body.slice(i, i + 2);
    if (pair === "&&" || pair === "||") {
      operands.push({ op, text: body.slice(start, i).trim() });
      op = pair;
      start = i + 2;
      return i + 1;
    }
  });
  operands.push({ op, text: body.slice(start).trim() });
  return operands.filter((operand) => operand.text !== "");
}
