// MIT License
//
// namespaceName.ts
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
 * Client mirror of the server's namespace-name rule (Fallen8Namespaces.IsValidName). Names
 * are permissive — any case, spaces, punctuation, Unicode — because on-disk storage is keyed
 * by an internal id, not the name; a name is only a display label, a map key, and a URL PATH
 * SEGMENT (/ns/{name}/…, /q/{name}/…). That last role fixes the only hard limits: no "/" or
 * "\" (an encoded slash can't round-trip through the server), no control characters, not
 * "."/".." (path-traversal tokens), no leading/trailing whitespace, and a length cap.
 */
export const NAMESPACE_NAME_MAX = 63;

export function isValidNamespaceName(name: string): boolean {
  if (name.length === 0 || name.length > NAMESPACE_NAME_MAX) return false;
  if (name.trim().length === 0 || name !== name.trim()) return false;
  if (name === "." || name === "..") return false;
  for (const ch of name) {
    const code = ch.codePointAt(0)!;
    if (ch === "/" || ch === "\\" || code < 0x20 || code === 0x7f) return false;
  }
  return true;
}
