// MIT License
//
// namespaces.ts
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

import { useQuery } from "@tanstack/react-query";
import { listNamespaces } from "../api/endpoints";
import type { InstanceConfig } from "../instances/types";
import { STATUS_POLL_MS } from "../lib/pollIntervals";

/**
 * The key the shared /ns cache entry lives under, exported because several sites INVALIDATE it and
 * not all of them are in a position to hold the hook. state/liveFeed.ts is a plain factory over a
 * queryClient and debounces by key rather than calling invalidateQueries at all, and
 * NamespaceSwitcher takes its entries as a prop; a hook-returned callback would force both to
 * mount an observer they do not want. So the KEY is the shared thing, and this is its one home.
 *
 * Takes the instance id as a string rather than an InstanceConfig, because liveFeed has already
 * stripped a bound view's /{namespace} suffix down to the raw id. undefined is accepted so the
 * sites holding a nullable instance produce byte-identical keys to the ones they used before.
 */
export const namespacesKey = (instanceId: string | undefined) =>
  [instanceId, "namespaces"] as const;

/**
 * The shared namespace inventory (feature graph-namespaces): one cache row and one poll, read by
 * the app shell, the namespace scope, the Connect panel and each registered instance's health row.
 *
 * The inventory is Fallen-8-level, so it is keyed by the RAW instance id rather than per namespace,
 * and every observer of that key rides one request instead of adding its own. It was four
 * definitions of the same query before, agreeing on the key, the poll and the retry and disagreeing
 * on when to ask.
 *
 * `enabled` NARROWS and never widens: it is ANDed with the instance being present, so no caller can
 * hand listNamespaces a null. Each site's own predicate stays that site's business and is passed
 * in, because react-query takes the fetch decision from whichever observer is mounted, and two of
 * the four are the ONLY observer of this key on the routes they render on. Their gates therefore
 * decide whether a request happens at all, not merely when, which is why they are not defaults:
 * the app shell is alone on the routes that hang off the root rather than off a namespace, and an
 * instance health row renders for every registered instance including the ones nothing else
 * observes.
 */
export function useNamespaces(
  instance: InstanceConfig | null,
  options?: { enabled?: boolean },
) {
  return useQuery({
    queryKey: namespacesKey(instance?.id),
    queryFn: ({ signal }) => listNamespaces(instance!, signal),
    enabled: instance !== null && (options?.enabled ?? true),
    refetchInterval: STATUS_POLL_MS,
    retry: 0,
  });
}
