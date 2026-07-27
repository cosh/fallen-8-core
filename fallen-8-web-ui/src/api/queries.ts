// MIT License
//
// queries.ts
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

import type { QueryClient } from "@tanstack/react-query";

/**
 * Invalidates EVERY query of an instance — the raw-keyed Fallen-8-level ones
 * (`[<id>, ...]`: save games, benchmark, the namespace inventory) AND the per-namespace
 * ones keyed by the bound view's compound id (`[<id>/<ns>, ...]`, feature
 * graph-namespaces). Accepts either id shape.
 */
export function invalidateInstanceQueries(
  queryClient: QueryClient,
  instanceId: string,
): Promise<void> {
  const raw = instanceId.split("/")[0];
  return queryClient.invalidateQueries({
    predicate: (query) => {
      const head = query.queryKey[0];
      return typeof head === "string" && (head === raw || head.startsWith(`${raw}/`));
    },
  });
}
