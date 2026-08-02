// MIT License
//
// scopeKey.ts
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
 * The ONE per-instance-per-namespace scope rule, shared by the workspace store
 * (instanceStore) and the event feed (eventFeed): accepts a pre-bound compound
 * "<id>/<namespace>" or an (id, namespace) pair, and collapses the reserved "default"
 * namespace onto the bare instance id - so a pre-namespace workspace is adopted as
 * default's with no migration (feature graph-namespaces). Registry ids never contain "/".
 */
export function scopeKey(instanceId: string, namespace?: string): string {
  const compound = namespace === undefined ? instanceId : `${instanceId}/${namespace}`;
  return compound.endsWith("/default") ? compound.slice(0, -"/default".length) : compound;
}
