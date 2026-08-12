// MIT License
//
// lib-scope.mjs
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

// The ONE home for the library artifact's scope constants, imported by both the scoping
// pass (vite.lib.config.ts) and the artifact tripwire (check-lib-artifact.mjs) - two
// independent literals would let the scope rename while the tripwire kept passing on
// every input.

/** The scope root class every selector in the artifact must live under. */
export const SCOPE = ".f8-studio";

/**
 * Only the OUTERMOST scope root re-declares page-level defaults (preflight, theme tokens):
 * a nested root (an F8GraphCanvas inside the Studio tree, or a host's own wrapper) inherits
 * from its ancestor instead, so an ancestor's inline theme overrides are not clobbered by
 * the stylesheet re-applying stock defaults on the inner element.
 */
export const OUTERMOST_SCOPE = `${SCOPE}:not(${SCOPE} ${SCOPE})`;

/**
 * The page-level anchors the scoping pass recognizes. Group 1 is the anchor, group 2 the
 * rest of the compound (`html.dark` -> `.dark`); a rest that BEGINS with a combinator means
 * a page-level ancestor with descendants, which has no faithful in-scope rewrite and must
 * fail the build instead of silently dying.
 */
export const PAGE_LEVEL_ANCHOR = /^(:root|:host|html|body|#root)(?![\w-])([\s\S]*)$/;

/**
 * The tripwire's mirror of the same tokens: a scoped selector where a page-level anchor
 * ended up in DESCENDANT position can never match, so its rule vanished from embeds only.
 */
export const UNMATCHABLE_SCOPED = new RegExp(
  `\\${SCOPE}\\s+(?::root|:host|html\\b|body\\b|#root\\b)`,
);
