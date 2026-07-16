/**
 * Query-vector input parsing (concept spec §6): vectors are PASTED (a JSON array or
 * comma/whitespace-separated floats), never typed — this validates client-side so a
 * dimension mismatch or a NaN is caught before the server's 400.
 */
export type ParsedVector =
  | { ok: true; vector: number[] }
  | { ok: false; error: string };

export function parseVector(text: string): ParsedVector {
  const trimmed = text.trim();
  if (!trimmed) return { ok: false, error: "empty" };

  let values: unknown[];
  if (trimmed.startsWith("[")) {
    try {
      const parsed = JSON.parse(trimmed) as unknown;
      if (!Array.isArray(parsed)) return { ok: false, error: "not an array" };
      values = parsed;
    } catch {
      return { ok: false, error: "invalid JSON array" };
    }
  } else {
    values = trimmed.split(/[\s,;]+/).filter(Boolean);
  }

  if (values.length === 0) return { ok: false, error: "empty" };
  const vector: number[] = [];
  for (const value of values) {
    // Only numbers and numeric strings count: Number(null) is 0, so anything else
    // (null, booleans, nested arrays) must be rejected, not coerced.
    const n =
      typeof value === "number"
        ? value
        : typeof value === "string"
          ? Number(value)
          : NaN;
    if (!Number.isFinite(n)) {
      return { ok: false, error: `component ${vector.length + 1} is not a finite number` };
    }
    vector.push(n);
  }
  return { ok: true, vector };
}
