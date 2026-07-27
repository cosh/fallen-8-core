// MIT License
//
// vector.ts
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
