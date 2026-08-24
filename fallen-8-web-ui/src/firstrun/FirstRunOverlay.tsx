// MIT License
//
// FirstRunOverlay.tsx
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

import { useEffect, useState } from "react";
import * as Dialog from "@radix-ui/react-dialog";
import { useNavigate } from "@tanstack/react-router";
import { useActiveNamespace } from "../instances/registry";
import { FirstRunShow } from "./FirstRunShow";
import { useFirstRun } from "./firstRunStore";
import { usePortalContainer } from "../app/studioConfig";
import type { NamespaceSignals } from "../app/namespaceSignals";

/**
 * The first-run show's ONE host (feature studio-first-run), rendered once by the app shell. Radix
 * Dialog gives the focus trap, Escape-to-close, and focus restore for free.
 *
 * It opens on two paths, and the difference is entirely in what closing means:
 *
 * - **auto** - the operator is on a screen ABOUT a graph and that graph was empty WHEN THEY
 *   ARRIVED at the namespace (see the `arrival` latch: emptiness is live, arrival is not), and
 *   they have not dismissed the show for it. Closing by ANY route (Close, Escape, the scrim,
 *   "Explore on my own") records the dismissal; an auto-opening modal that came back on the next
 *   navigation would be unusable. The memory is per namespace and re-armed the moment that
 *   namespace is seen non-empty, so a graph that genuinely empties later shows the intro again the
 *   next time you arrive at it.
 * - **replay** - the rail's Intro button, available at any time regardless of the connection
 *   state, the graph being empty, or the dismissal. Closing it leaves the flag alone UNLESS the
 *   auto path is armed for this namespace, in which case closing counts for both (see `armed`).
 *
 * The show creates NOTHING: its handoff buttons only navigate (to the Sample gallery or the
 * import screen) or dismiss. The unit-test graph endpoint is deliberately never wired in (see
 * CLAUDE.md); newcomers reach a populated graph through the curated Sample gallery.
 */
export function FirstRunOverlay({
  signals,
  connected,
  onGraphScreen,
}: {
  /** The active namespace's shell-level signals - what decides the auto path. */
  signals: NamespaceSignals;
  /**
   * Whether the active instance answered AND authorized. /status reports real counts to an
   * unauthorized caller too, so without this an instance that rejected the credential would greet
   * the operator with a walkthrough on top of the "rejected the credential" guard.
   */
  connected: boolean;
  /**
   * Whether the current route is a namespace-scoped screen (`/q/{ns}/…`).
   *
   * The auto path is deliberately silent on the Fallen-8-level screens - Connect, Save games,
   * Integrations. The walkthrough is about a GRAPH, and those three are where you wire one up:
   * registering an instance, naming namespaces, restoring a checkpoint, pointing an integration at
   * a system. Interrupting that with a modal is not onboarding, it is an ambush, and the very
   * first thing a newcomer does after connecting is click a rail entry - which lands them on a
   * scoped screen, where the show opens with nothing half-finished behind it.
   */
  onGraphScreen: boolean;
}) {
  const replayOpen = useFirstRun((s) => s.replayOpen);
  const closeReplay = useFirstRun((s) => s.closeReplay);
  const dismiss = useFirstRun((s) => s.dismiss);
  const clearIfPopulated = useFirstRun((s) => s.clearIfPopulated);
  const namespace = useActiveNamespace();
  const navigate = useNavigate();
  const portalContainer = usePortalContainer();

  const key = signals.key;
  // No key means no instance, and an unknown namespace counts as dismissed rather than as fresh.
  const dismissed = useFirstRun((s) => (key === null ? true : (s.dismissed[key] ?? false)));

  // Re-arm the auto-show once the namespace is seen non-empty, so a graph that genuinely empties
  // later shows the intro again on the next arrival.
  useEffect(() => {
    if (key !== null && signals.populated) clearIfPopulated(key);
  }, [key, signals.populated, clearIfPopulated]);

  /**
   * Was this namespace empty when we ARRIVED at it? Latched once per namespace, on the first
   * /status answer for it, and never re-derived.
   *
   * `signals.empty` is live - it follows the change feed within ~300ms - and deriving `open` from
   * it directly made the modal fire in the middle of deliberate work that passes through zero. The
   * worst case is a primary path: loading a sample runs a tabula rasa and THEN an import, so an
   * operator who clicks Load on a populated graph got the walkthrough thrown over the Samples
   * screen for the whole duration of the import (minutes, for a sample that ingests documents),
   * with the loader's own progress behind its scrim. Deleting the last element in the Browser, or
   * another client wiping the graph while you work, did the same. A graph emptying under you is
   * not a first run.
   *
   * It is latched ON arrival and cleared FOR GOOD once the namespace is seen to hold data. The
   * second half is not symmetry, it is required: `clearIfPopulated` re-arms the dismissal when a
   * namespace becomes non-empty (so a graph that genuinely empties later greets you again), and a
   * latch that still said "arrived empty" would then re-open the show the moment the operator
   * populated the graph. Scenario 13 of the e2e suite caught exactly that - generate a graph on a
   * fresh namespace and the walkthrough came back over the Benchmark screen mid-run.
   *
   * The render-phase updates are React's documented way to reset state when an input changes; the
   * `key` guard makes the first run once per namespace rather than every render, and the second
   * only fires on the transition into "populated".
   */
  const [arrival, setArrival] = useState<{ key: string; empty: boolean } | null>(null);
  if (key !== null && signals.known && arrival?.key !== key) {
    setArrival({ key, empty: signals.empty });
  } else if (arrival !== null && arrival.key === key && arrival.empty && signals.populated) {
    setArrival({ key: arrival.key, empty: false });
  }
  const arrivedEmpty = arrival?.key === key && arrival.empty;

  /**
   * The auto path's preconditions EXCEPT the route gate. Split out because closing the overlay has
   * to consult it: `open` has two independent reasons, and clearing only the one that fired hands
   * straight over to the other. On a fresh install that was reachable in two clicks - Intro on the
   * Connect screen, then "Browse sample graphs", which closed the replay and navigated onto a
   * scoped screen where the auto path immediately re-opened the same dialog the user had just
   * dismissed. So a close records the dismissal whenever the auto path is armed, not only when it
   * is what opened the thing.
   *
   * The durability warning WINS over the welcome: a truncated recovery is a leading reason a
   * namespace you expected to hold data is empty, and the shell banner that says so must not sit
   * behind this modal's scrim. The rail's Intro button still plays the show on demand.
   */
  const armed = connected && arrivedEmpty && !signals.durabilityUnhealthy && !dismissed;
  const auto = armed && onGraphScreen;
  const open = replayOpen || auto;
  const variant = replayOpen ? "replay" : "auto";

  const close = () => {
    if (replayOpen) closeReplay();
    if (key !== null && armed) dismiss(key);
  };

  const onBrowseSamples = () => {
    close();
    void navigate({ to: "/q/$ns/samples", params: { ns: namespace } });
  };

  const onImport = () => {
    close();
    void navigate({ to: "/save-games" });
  };

  return (
    <Dialog.Root open={open} onOpenChange={(o) => !o && close()}>
      <Dialog.Portal container={portalContainer}>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-black/70" />
        <Dialog.Content
          data-testid="first-run-overlay"
          className="panel modal-center flex h-[min(680px,88vh)] w-[min(1000px,92vw)] flex-col p-4"
        >
          <div className="mb-2 flex items-center gap-2">
            <Dialog.Title className="text-fg-dim text-[11px] font-semibold tracking-widest uppercase">
              Fallen-8 intro
            </Dialog.Title>
            <Dialog.Close asChild>
              <button
                type="button"
                className="btn ml-auto"
                data-testid="first-run-overlay-close"
                aria-label="Close the intro"
              >
                Close
              </button>
            </Dialog.Close>
          </div>
          <div className="min-h-0 flex-1">
            <FirstRunShow
              variant={variant}
              onExplore={close}
              onBrowseSamples={onBrowseSamples}
              onImport={onImport}
            />
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
