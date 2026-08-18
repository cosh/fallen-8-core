// MIT License
//
// NlDraftList.tsx
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

import type { ReactNode } from "react";
import type { Verdict } from "./feedback";

/**
 * The single home for the NL-assist draft list, shared by the delegate editor
 * (NlAssistPanel) and the plugin authoring editor (PluginNlAssistPanel). It renders the
 * small-model's drafts (the "results") with three review affordances:
 *
 *  - **newest on top** — drafts arrive in generation order but display reversed, so the draft
 *    just produced (and loaded into the editor) is where the eye lands; "draft N" numbering
 *    stays in generation order and callbacks carry the original index, so nothing else shifts.
 *  - **its own scrollbar** — a fixed max height keeps a long run of drafts from pushing the
 *    export affordance (and, in the delegate sidebar, the rest of the panel) off screen.
 *  - **unrated drafts stand out** — a draft with no 👍/👎 verdict gets a warn-toned left bar and
 *    full-strength thumbs (a call to judge it, which feeds the fine-tune corpus); the highlight
 *    clears the instant it is rated, and returns if the rating is cleared.
 *
 * The panel-specific bits (label suffix, load-button title, per-attempt stats/detail slots, and
 * the data-testid prefixes) are passed in so both panels behave identically.
 */

/** One drafted candidate, as the hosting panel wants it shown. */
export interface NlDraftView {
  /** Server validation outcome: true = valid, false = invalid, null = not (yet) known. */
  valid: boolean | null;
  /** The user's 👍/👎 on this draft, or null until judged. */
  verdict: Verdict | null;
  /** True when this draft's body is the text currently loaded in the editor. */
  active: boolean;
  /** Hover title for the load button (e.g. the fragment text, or a generic hint). */
  loadTitle: string;
  /** Text appended after "draft N" — e.g. " (2 error(s))" or " (invalid)". Omit when valid. */
  labelSuffix?: string;
  /** Content shown below the row (the delegate panel's collapsible raw stats). */
  below?: ReactNode;
}

export function NlDraftList({
  testid,
  verdictTestidPrefix,
  drafts,
  onLoad,
  onRate,
}: {
  testid: string;
  verdictTestidPrefix: string;
  drafts: NlDraftView[];
  /** Called with the draft's original (generation-order) index, not its display position. */
  onLoad: (index: number) => void;
  onRate: (index: number, verdict: Verdict) => void;
}) {
  return (
    <ol className="max-h-64 space-y-1 overflow-y-auto pr-1" data-testid={testid}>
      {drafts
        // Preserve the original index for key/label/testid/callbacks, then flip for display so
        // the newest draft renders first.
        .map((draft, index) => ({ draft, index }))
        .reverse()
        .map(({ draft, index }) => {
          const unjudged = draft.verdict === null;
          return (
            <li
              key={index}
              data-unjudged={unjudged ? "true" : undefined}
              className={`rounded border-l-2 py-1 pr-1 pl-2 ${
                unjudged ? "border-warn/60 bg-warn/5" : "border-transparent"
              }`}
            >
              <div className="flex items-center gap-1">
                <span
                  className={
                    draft.valid
                      ? "text-accent"
                      : draft.valid === false
                        ? "text-danger"
                        : "text-fg-faint"
                  }
                >
                  {draft.valid ? "✓" : draft.valid === false ? "✗" : "?"}
                </span>
                <button
                  type="button"
                  className={`cursor-pointer truncate hover:underline ${
                    draft.active ? "text-fg font-semibold" : "text-accent-2"
                  }`}
                  title={draft.loadTitle}
                  onClick={() => onLoad(index)}
                >
                  draft {index + 1}
                  {draft.active && " (in editor)"}
                  {draft.labelSuffix}
                </button>
                <span
                  className="ml-auto flex shrink-0 gap-1"
                  data-testid={`${verdictTestidPrefix}-${index}`}
                >
                  <button
                    type="button"
                    title="good draft — mark to save as a training example"
                    className={`cursor-pointer ${
                      draft.verdict === "up"
                        ? "text-accent"
                        : unjudged
                          ? "text-fg hover:text-accent"
                          : "text-fg-faint hover:text-fg"
                    }`}
                    onClick={() => onRate(index, "up")}
                  >
                    👍
                  </button>
                  <button
                    type="button"
                    title="bad draft — mark to save as a training example"
                    className={`cursor-pointer ${
                      draft.verdict === "down"
                        ? "text-danger"
                        : unjudged
                          ? "text-fg hover:text-danger"
                          : "text-fg-faint hover:text-fg"
                    }`}
                    onClick={() => onRate(index, "down")}
                  >
                    👎
                  </button>
                </span>
              </div>
              {draft.below}
            </li>
          );
        })}
    </ol>
  );
}
