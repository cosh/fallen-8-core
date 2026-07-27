// MIT License
//
// styling.ts
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
 * Stable label-to-color assignment (FR-19): a label always hashes to the same palette
 * slot, in any instance and any session. Palette tuned for the dark chrome.
 */

export const LABEL_PALETTE = [
  "#4cc38a", // green
  "#58a6ff", // blue
  "#d29922", // amber
  "#f778ba", // pink
  "#a371f7", // violet
  "#ff7b72", // coral
  "#39c5cf", // cyan
  "#9e6a03", // ochre
  "#7ee787", // light green
  "#79c0ff", // light blue
] as const;

export const UNLABELED_COLOR = "#55647a";

export function colorForLabel(label: string | null | undefined): string {
  if (!label) return UNLABELED_COLOR;
  let hash = 0;
  for (let i = 0; i < label.length; i++) {
    hash = (hash * 31 + label.charCodeAt(i)) | 0;
  }
  return LABEL_PALETTE[Math.abs(hash) % LABEL_PALETTE.length];
}

/** Property values color like labels: stringified, then stably hashed (FR-1/FR-2). */
export function colorForValue(value: unknown): string {
  if (value === null || value === undefined || value === "") return UNLABELED_COLOR;
  return colorForLabel(String(value));
}

/** Two-color ramp for all-numeric properties (FR-1): min → cyan, max → pink. */
export const GRADIENT_LOW = "#39c5cf";
export const GRADIENT_HIGH = "#f778ba";

export function gradientColor(t: number): string {
  const clamped = Math.min(1, Math.max(0, t));
  const lo = [0x39, 0xc5, 0xcf];
  const hi = [0xf7, 0x78, 0xba];
  const mix = lo.map((c, i) => Math.round(c + (hi[i] - c) * clamped));
  return `#${mix.map((c) => c.toString(16).padStart(2, "0")).join("")}`;
}

/** FR-20: past this rendered-element count the canvas degrades (drops labels) instead of freezing. */
export const DEGRADE_THRESHOLD = 5_000;
