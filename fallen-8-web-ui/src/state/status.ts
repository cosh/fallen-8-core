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
import { STATUS_POLL_MS } from "../lib/pollIntervals";

/**
 * The shared /status cache entry. Same query key as the AppShell health probe, so every
 * consumer reads one cache row and rides its periodic refresh; /status is the cheap
 * discovery surface (available plugins + live index inventory — feature
 * studio-index-discovery), unlike the budgeted Graph-shape pass in graphShape.ts.
 *
 * Accepts null and then asks nothing: the shell-level readers (the durability banner, the
 * first-run auto-show) render outside the connection gate, so "no instance yet" is a state they
 * pass through rather than an assertion they can make.
 *
 * Pass poll: true to drive the shared row on a timer. Exactly one caller does - the app shell,
 * on the same 15s cadence as its instance health probe - because the durability banner is a
 * warning nobody goes looking for: it has to arrive while the operator is on some other screen.
 * Every other observer rides that refresh through the shared cache row and asks for nothing.
 *
 * Polling implies `retry: 0`, matching the health probe, and that pairing is load-bearing rather
 * than tidy: on a server that predates namespaces the bound view collapses to the raw instance, so
 * this row and the probe become the SAME query key. react-query takes `retry` from whichever
 * observer initiated the current fetch, so without this the probe's deliberate fail-fast became a
 * coin flip against a default of three retries - and for the ~7s of backoff that won, a dead
 * instance still read "online" with the nav unlocked. There is no point retrying a 15s poll anyway;
 * the next tick is the retry.
 */
export function useStatus(instance: InstanceConfig | null, options?: { poll?: boolean }) {
  return useQuery({
    queryKey: [instance?.id, "status"],
    queryFn: ({ signal }) => getStatus(instance!, signal),
    enabled: instance !== null,
    refetchInterval: options?.poll ? STATUS_POLL_MS : undefined,
    ...(options?.poll ? { retry: 0 } : {}),
  });
}

/**
 * The instance's configuration (features instance-config and writable-instance-config): the setting
 * inventory, the semantic providers and the observability posture behind the Connect Configuration
 * section. Fallen-8-level, so it is keyed by the RAW instance id (not per namespace) and API-key
 * gated server-side.
 *
 * Pass poll: false while the operator has unsaved edits. The panel is an editor now, and a ten second
 * refetch would otherwise replace the value under a half-typed field: the poll exists so model
 * residency updates on its own, which is never worth losing someone's input over.
 */
export function useConfig(instance: InstanceConfig, options?: { poll?: boolean }) {
  const poll = options?.poll ?? true;
  return useQuery({
    queryKey: [instance.id, "config"],
    queryFn: ({ signal }) => getConfig(instance, signal),
    retry: 0,
    refetchInterval: poll ? 10_000 : false,
  });
}
