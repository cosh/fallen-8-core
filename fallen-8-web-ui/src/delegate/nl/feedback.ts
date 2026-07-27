// MIT License
//
// feedback.ts
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

import type { DelegateKind } from "../../api/types";

/**
 * FL-2 of the nl-assist-feedback-loop feature: opt-in, LOCAL capture of labelled
 * (intent -> fragment) pairs for later fine-tuning. Nothing here touches the network - the
 * parent NL-assist spec forbids server-side prompt storage, so capture is a file the operator
 * exports and moves deliberately; the FL-3 consolidation tool ingests these lines and folds
 * the good ones into the training corpus (never the held-out eval set).
 */

export type Verdict = "up" | "down";

export interface TrainingExample {
  delegateKind: DelegateKind;
  /** The plain-language request the draft was generated from. */
  intent: string;
  /** The drafted fragment being labelled (a 👍 marks it a good intent->fragment pair). */
  fragment: string;
  verdict: Verdict | null;
  /** Capture time (ms since epoch) — lets the consolidation tool order/dedupe. */
  ts: number;
}

/**
 * One compact JSON object per line — the JSONL the FL-3 consolidation tool reads. A trailing
 * newline keeps concatenating multiple exports valid; an empty set yields an empty string.
 */
export function toTrainingJsonl<T>(examples: T[]): string {
  if (examples.length === 0) {
    return "";
  }
  return examples.map((example) => JSON.stringify(example)).join("\n") + "\n";
}

/**
 * Trigger a browser download of `text`. Opt-in capture only: it builds an in-memory blob and
 * clicks a link — it never calls an endpoint, so a captured example leaves the machine only
 * when the operator moves the downloaded file.
 */
export function downloadText(filename: string, text: string): void {
  const url = URL.createObjectURL(new Blob([text], { type: "application/jsonl" }));
  try {
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = filename;
    anchor.click();
  } finally {
    URL.revokeObjectURL(url);
  }
}
