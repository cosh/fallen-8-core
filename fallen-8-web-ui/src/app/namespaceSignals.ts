// MIT License
//
// namespaceSignals.ts
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

import type { DurabilityREST } from "../api/types";
import { durabilityProblems } from "../components/DurabilityNotice";
import { useBoundInstance } from "../instances/registry";
import { useStatus } from "../state/status";

/**
 * What the shell knows about the ACTIVE namespace, read from the one /status cache row every
 * screen already shares.
 *
 * Two shell-level surfaces consume this and are COUPLED, which is why one hook answers both: the
 * durability banner (AppShell) and the first-run auto-show (FirstRunOverlay). A namespace that is
 * empty BECAUSE its recovery was truncated must get the warning, not a welcome tour - on the old
 * Dashboard that was ordering (the notice rendered above the show), and a modal cannot be ordered
 * behind its own scrim, so the warning suppresses the show instead.
 */
export interface NamespaceSignals {
  /** The durability block for this namespace; null/undefined from a server that does not report. */
  durability: DurabilityREST | null | undefined;
  /** True when the block names at least one of the three exceptional states. */
  durabilityUnhealthy: boolean;
  /**
   * Strictly 0 vertices, and KNOWN to be 0. /status answers for a namespace the server catalogs
   * but did not load with null counts, and reading that as empty would greet an operator with
   * "get started" over a graph that holds data.
   *
   * LIVE: it follows the change feed within ~300ms of any mutation. Anything that must not fire
   * mid-operation has to latch it rather than derive from it - a sample load is a tabula rasa
   * followed by an import, so this is briefly true in the middle of one.
   */
  empty: boolean;
  /** Non-zero vertex count, i.e. the graph is known to hold data (re-arms the first-run show). */
  populated: boolean;
  /**
   * Whether /status has answered for this namespace at all. `empty` and `populated` are both false
   * while it has not, which is indistinguishable from a not-loaded namespace without this.
   */
  known: boolean;
  /**
   * The per-namespace key both the first-run store and the caches use: `<instanceId>/<ns>`, or the
   * bare instance id on a server that predates namespaces (see boundInstance). Null with no
   * active instance.
   */
  key: string | null;
}

export function useNamespaceSignals(): NamespaceSignals {
  const instance = useBoundInstance();
  // The one polling observer on this row: see useStatus. A warning nobody goes looking for is
  // worth nothing if it only arrives on the next navigation.
  const status = useStatus(instance, { poll: true });

  const vertexCount = status.data?.vertexCount ?? null;
  const durability = status.data?.durability;

  return {
    durability,
    durabilityUnhealthy: durabilityProblems(durability).length > 0,
    empty: vertexCount === 0,
    populated: vertexCount !== null && vertexCount > 0,
    known: status.data !== undefined,
    key: instance?.id ?? null,
  };
}
