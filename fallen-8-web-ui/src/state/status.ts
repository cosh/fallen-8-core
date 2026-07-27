// MIT License
//
// status.ts
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
import { getConfig, getStatus } from "../api/endpoints";
import type { InstanceConfig } from "../instances/types";

/**
 * The shared /status cache entry. Same query key as the AppShell health probe, so every
 * consumer reads one cache row and rides its periodic refresh; /status is the cheap
 * discovery surface (available plugins + live index inventory — feature
 * studio-index-discovery), unlike the budgeted Graph-shape pass in graphShape.ts.
 */
export function useStatus(instance: InstanceConfig) {
  return useQuery({
    queryKey: [instance.id, "status"],
    queryFn: ({ signal }) => getStatus(instance, signal),
  });
}

/**
 * The instance's read-only configuration (feature instance-config): the semantic providers
 * and observability posture behind the Connect Configuration section. Fallen-8-level, so it
 * is keyed by the RAW instance id (not per namespace) and API-key gated server-side.
 */
export function useConfig(instance: InstanceConfig) {
  return useQuery({
    queryKey: [instance.id, "config"],
    queryFn: ({ signal }) => getConfig(instance, signal),
    retry: 0,
    // Re-check periodically so model residency (a model loads/unloads in the sidecar over time)
    // updates on its own; the panel also offers a manual Refresh. GET /config's probe is bounded.
    refetchInterval: 10_000,
  });
}
