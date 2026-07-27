// MIT License
//
// Truncated.tsx
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

import { truncateChars } from "../lib/truncate";

/**
 * Renders a possibly-unbounded, user-controlled string clipped so it cannot blow out the
 * view, with the FULL value in the native title tooltip so nothing is lost. The one home for
 * "don't let user text destroy the layout" (feature graph-namespaces follow-up).
 *
 * Two modes:
 * - `max` given → a deterministic CHAR cap via {@link truncateChars} (chrome, headings,
 *   table cells without a fixed layout). Pass `middle` for path/URL-shaped values.
 * - `max` omitted → CSS ellipsis (`truncate`): the element must sit in a width-bounded flex
 *   parent (`min-w-0`, and usually `flex-1`), which adapts to the available space.
 */
export function Truncated({
  text,
  max,
  middle = false,
  className = "",
}: {
  text: string;
  max?: number;
  middle?: boolean;
  className?: string;
}) {
  const clipped = max === undefined ? text : truncateChars(text, max, { middle });
  // CSS mode can't know at render time whether the box will clip, so always offer the full
  // value; char mode only when it actually shortened.
  const title = max === undefined || clipped !== text ? text : undefined;
  const classes = max === undefined ? `truncate ${className}`.trim() : className;

  return (
    <span className={classes || undefined} title={title}>
      {clipped}
    </span>
  );
}
