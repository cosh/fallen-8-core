// MIT License
//
// ListCapNote.tsx
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
 * Honest footer for the hard row ceiling: renders "showing N of M" ONLY when a list is actually
 * truncated at LIST_MAX_ROWS (see capList in lib/listCaps.ts) — a rare safety cap, never the
 * everyday scroll threshold — so a list that big never hides the overflow silently. Nothing
 * renders when everything fits (the normal case: the list just scrolls).
 */
export function ListCapNote({ shown, total }: { shown: number; total: number }) {
  if (total <= shown) return null;
  return (
    <div className="text-fg-faint px-3 py-1.5 text-[11px]" data-testid="list-cap-note">
      Showing the first {shown.toLocaleString()} of {total.toLocaleString()}.
    </div>
  );
}
