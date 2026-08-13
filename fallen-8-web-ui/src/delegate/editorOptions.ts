// MIT License
//
// editorOptions.ts
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

import type { editor } from "monaco-editor";

/**
 * The monaco options shared by every Studio editor surface (the delegate fragment editor and
 * the plugin source editor). One home: the two option blocks were byte-identical and must not
 * drift. Module scope also gives them a stable identity, so the react wrapper does not re-run
 * updateOptions on every render.
 *
 * `occurrencesHighlight: "off"` is load-bearing, not cosmetic. monaco 0.52 through 0.55 arm a
 * 50 ms `Delayer` for that feature on every cursor move and DISCARD the promise it returns
 * (wordHighlighter.js:184 and :192); disposing the editor inside that window rejects the
 * orphaned promise with a `CancellationError` whose name and message are both "Canceled"
 * (async.js:219). Nothing outside monaco holds that promise, so nothing can catch it: it
 * escapes as an unhandled rejection into the page - and Studio embeds into someone else's
 * page, whose error reporting then owns our noise. Upstream calls it a bug
 * (microsoft/monaco-editor#4702, #4859) and fixed it in 0.56.0 by attaching the handler those
 * two lines forgot. With the option "off" both trigger sites early-return
 * (wordHighlighter.js:179 and :187), so the promise is never created and disposing the delayer
 * is a no-op. Measured before the change: closing the editor 31-50 ms after a keystroke leaked
 * the rejection in 16 of 20 runs; after it, 0 of 40. It also costs nothing to lose: these
 * editors hold a one-to-three line lambda or a single plugin type, where highlighting the
 * other occurrences of the word under the cursor buys nothing, and it drops a debounce plus a
 * full-model findMatches per cursor move. Keep the option when monaco is upgraded past 0.56.0;
 * delete the upstream half of this note then.
 */
export const F8_EDITOR_OPTIONS: editor.IStandaloneEditorConstructionOptions = {
  minimap: { enabled: false },
  fontSize: 13,
  fontFamily: "JetBrains Mono, monospace",
  lineNumbers: "on",
  scrollBeyondLastLine: false,
  automaticLayout: true,
  fixedOverflowWidgets: true,
  occurrencesHighlight: "off",
};
