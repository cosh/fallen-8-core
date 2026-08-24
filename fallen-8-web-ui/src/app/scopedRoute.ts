// MIT License
//
// scopedRoute.ts
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
 * "Change the namespace, keep the screen" - the one home for that URL rewrite (feature
 * graph-namespaces).
 *
 * Everything that switches context does it: the top bar's namespace and instance switchers, and
 * the two recover states that offer a way out of a namespace this process cannot read. None of
 * them has an overview screen to fall back to, and none of them should: a context switch that
 * also throws you onto a different screen loses the thing you were looking at.
 */

/** The leaf of a `/q/{ns}/…` pathname; "" for the bare namespace route or any flat route. */
export function scopedLeaf(pathname: string): string {
  return pathname.startsWith("/q/") ? pathname.split("/").slice(3).join("/") : "";
}

/**
 * The route id addressing the SAME scoped screen with the namespace left as the `$ns` param, so
 * the caller supplies it. The cast is unavoidable and harmless: the leaf comes from the live
 * pathname, so no literal route id can be inferred from it, and every scoped id types a
 * `navigate` call identically.
 */
export function sameScopedScreen(pathname: string): "/q/$ns/browser" {
  const leaf = scopedLeaf(pathname);
  return (leaf ? `/q/$ns/${leaf}` : "/q/$ns") as "/q/$ns/browser";
}
