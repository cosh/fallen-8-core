// MIT License
//
// InstanceHealth.tsx
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
import type { InstanceConfig } from "../instances/types";
import { isCrossOriginInstance } from "../instances/types";
import { getStatus, isAuthorized, listNamespaces } from "../api/endpoints";
import { ApiError, ApiTimeoutError } from "../api/client";
import {
  describeDefaultOnly,
  describeTotals,
  describeWholeGraph,
  summarizeInventory,
} from "../lib/namespaceTotals";

/**
 * The health cell of an Instances row (feature instance-level-health). An instance is a COLLECTION
 * of namespaces, so this cell reports the collection; the per-namespace view is the Namespaces
 * panel one section below.
 *
 * Two requests, each answering the question only it can:
 *
 * - `/status` is the PROBE. It is the one `[AllowAnonymous]` route, so it is the only thing that can
 *   tell "unreachable" from "unauthorized" on a key-secured instance. Its counts, though, are
 *   namespace-scoped, and this row addresses instances it has not activated, so unbound means they
 *   are the reserved `default` alone: reporting them as the instance is the defect this component
 *   was rewritten to remove.
 * - `/ns` is the SIZE: the only route that reports every namespace. It is not anonymous, so it is
 *   fetched only once the probe says the credential is good, and the row never fires a request it
 *   knows will 401.
 *
 * The probe's counts survive as the two degraded readings only, both labelled for what they are
 * (see `describeWholeGraph` / `describeDefaultOnly`).
 */
export function InstanceHealth({ instance }: { instance: InstanceConfig }) {
  const health = useQuery({
    queryKey: [instance.id, "status"],
    queryFn: ({ signal }) => getStatus(instance, signal),
    refetchInterval: 20_000,
    retry: 0,
  });
  const probe = health.data ?? null;
  // Keyed by the RAW instance id, like every other /ns observer (AppShell, NamespacesPanel): the
  // inventory is Fallen-8-level, so the active instance's row rides their cache entry and adds no
  // request of its own.
  const inventory = useQuery({
    queryKey: [instance.id, "namespaces"],
    queryFn: ({ signal }) => listNamespaces(instance, signal),
    enabled: probe !== null && isAuthorized(probe),
    refetchInterval: 15_000,
    retry: 0,
  });

  if (health.isPending) return <span className="text-fg-faint">checking…</span>;
  if (health.isError || probe === null)
    return (
      <span className="text-danger">
        {health.error instanceof ApiTimeoutError ? "no answer" : "unreachable"}
        {/* An address that ACCEPTS the connection and then says nothing is a different fault from
            one that refuses, and it is the one nothing else on this screen can explain: the server
            is usually running and answering somebody else. Naming the address, and the one
            substitution that fixes it on Windows, is the whole difference between a diagnosis and a
            spinner. */}
        {health.error instanceof ApiTimeoutError && (
          <span className="text-fg-faint ml-1 text-[11px]" data-testid="timeout-hint">
            (accepted the connection but sent nothing back within{" "}
            {health.error.timeoutMs / 1000}s - if it is running, try 127.0.0.1 instead of localhost)
          </span>
        )}
        {health.isError && isCrossOriginInstance(instance.baseUrl) && (
          <span className="text-fg-faint ml-1 text-[11px]" data-testid="cors-hint">
            (if the data plane is running, check its AllowedCorsOrigins includes this UI's origin)
          </span>
        )}
      </span>
    );
  if (!isAuthorized(probe))
    return (
      <span className="text-danger">
        {instance.auth.kind === "bearer"
          ? "unauthorized: the host session token was rejected"
          : "unauthorized — check the API key"}
      </span>
    );
  // No default-only number flashes on the way to the instance total.
  if (inventory.isPending) return <span className="text-fg-faint">checking…</span>;

  const notFound = inventory.error instanceof ApiError && inventory.error.status === 404;
  const display = inventory.data
    ? describeTotals(summarizeInventory(inventory.data.namespaces))
    : notFound
      ? // A server predating namespaces: its bare routes are the whole graph, so the probe's
        // counts are instance-level already.
        describeWholeGraph(probe.vertexCount, probe.edgeCount)
      : // Degraded: all we have is the probe, and the label says whose counts those are.
        describeDefaultOnly(
          probe.vertexCount,
          probe.edgeCount,
          inventory.error instanceof ApiError
            ? `HTTP ${inventory.error.status}`
            : "the request failed",
        );

  return (
    <span className="text-accent" title={display.title} data-testid="instance-health">
      {display.label}
    </span>
  );
}
