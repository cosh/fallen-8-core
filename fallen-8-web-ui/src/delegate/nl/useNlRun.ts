// MIT License
//
// useNlRun.ts
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

import { useEffect, useRef, useState } from "react";
import { probeEndpoint } from "./generate";
import type { NlAssistConfig } from "./config";

/**
 * Run lifecycle shared by both NL panels (the delegate editor's and the plugin editor's).
 * It exists because the two panels had the same two defects in duplicate:
 *
 * - The panel held an AbortController but nothing aborted it on unmount, so closing the editor
 *   mid-draft left the model call running. On a slow backend a second generation then queued
 *   behind the orphan instead of starting.
 * - Nothing aborted a previous run when a new one began.
 *
 * Both are lifecycle concerns rather than presentation, so they live here once.
 */
export function useNlRun(): {
  begin: () => AbortController;
  cancel: () => void;
} {
  const abortRef = useRef<AbortController | null>(null);

  // The cleanup is the whole point: an in-flight model call must not outlive the panel.
  useEffect(() => () => abortRef.current?.abort(), []);

  return {
    begin: () => {
      // The generate button is disabled while busy, so in the UI this is a no-op. It matters for
      // any other caller: it guarantees at most ONE controller is reachable, so cancel() and the
      // unmount cleanup can never be left holding a stale one while a newer run is in flight.
      // (It does not stop an already-started request's own continuation, which owns its signal.)
      abortRef.current?.abort();
      const controller = new AbortController();
      abortRef.current = controller;
      return controller;
    },
    cancel: () => abortRef.current?.abort(),
  };
}

/**
 * Whole seconds since `active` last became true, and 0 whenever it is false.
 *
 * This is the panels' only progress signal, and it is load-bearing rather than decorative: a
 * local model on CPU can need minutes for one draft (see the troubleshooting page), so without
 * an advancing number a working call is indistinguishable from a hung one, which is exactly how
 * the original hang was reported.
 */
export function useElapsedSeconds(active: boolean): number {
  const [elapsed, setElapsed] = useState(0);

  useEffect(() => {
    if (!active) {
      setElapsed(0);
      return;
    }
    const startedAt = Date.now();
    setElapsed(0);
    const id = setInterval(() => setElapsed(Math.floor((Date.now() - startedAt) / 1000)), 1000);
    return () => clearInterval(id);
  }, [active]);

  return elapsed;
}

/**
 * Informational-only reachability read (nl-assist-ux FR-2), shared by both NL panels: custom mode
 * probes the configured endpoint, instance mode reports nothing (its reachability is the instance
 * connection itself, shown on Connect). Never gates generation.
 */
export function useReachabilityProbe(
  configured: boolean,
  isInstance: boolean,
  effective: NlAssistConfig,
): boolean | null {
  const [reachable, setReachable] = useState<boolean | null>(null);

  useEffect(() => {
    if (!configured || isInstance) {
      setReachable(null);
      return;
    }
    const controller = new AbortController();
    setReachable(null);
    void probeEndpoint(effective, controller.signal).then((ok) => {
      if (!controller.signal.aborted) setReachable(ok);
    });
    return () => controller.abort();
    // Deps are the effective backend's primitives - `effective` itself is a new object every
    // render and would re-probe in a loop.
  }, [configured, isInstance, effective.endpoint, effective.apiKind, effective.model, effective.apiKey]);

  return reachable;
}
