// MIT License
//
// NlDraftStats.tsx
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

import type { NlGenerationStats } from "./generate";

/**
 * One draft's generation stats (nl-assist-ux FR-5), the row UNDER the draft label: the summary
 * line plus the provider's raw fields on demand. Both NL panels render this one component, so
 * the formatter and the markup have a single home.
 *
 * The backend segment is whatever the stats object captured with THIS draft carries, never the
 * live configuration (see lib/modelProvenance.ts). The row sits BELOW the label, not beside it:
 * a same-row block costs the label width the panel's 288px sidebar does not have.
 */
export function NlDraftStats({ stats }: { stats: NlGenerationStats }) {
  return (
    <>
      <div className="text-fg-faint pl-4 text-[10px]">{statsLine(stats)}</div>
      <details className="text-fg-faint pl-4 text-[10px]">
        <summary className="cursor-pointer">raw stats</summary>
        <pre className="overflow-x-auto whitespace-pre-wrap">
          {JSON.stringify(stats.raw, null, 1)}
        </pre>
      </details>
    </>
  );
}

function statsLine(stats: NlGenerationStats): string {
  const parts: string[] = [];
  if (stats.promptTokens !== undefined || stats.completionTokens !== undefined) {
    parts.push(`${stats.promptTokens ?? "?"}→${stats.completionTokens ?? "?"} tok`);
  }
  if (stats.durationMs !== undefined) parts.push(`${(stats.durationMs / 1000).toFixed(1)}s`);
  if (stats.tokensPerSecond !== undefined)
    parts.push(`${stats.tokensPerSecond.toFixed(1)} tok/s`);
  if (stats.backend !== undefined) parts.push(stats.backend);
  return parts.join(" · ");
}
