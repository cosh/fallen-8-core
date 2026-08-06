// MIT License
//
// firstRunStore.ts
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

import { create } from "zustand";
import { persist } from "zustand/middleware";
import { storageKey } from "../app/studioConfig";

/**
 * First-run show state (feature studio-first-run).
 *
 * `dismissed` remembers, per bound namespace key (`<instanceId>/<ns>`), that the newcomer
 * dismissed the auto-show so a returning user is not nagged on a graph that has simply stayed
 * empty. It is cleared the moment the namespace is observed non-empty, so the show auto-shows
 * again if the graph genuinely empties later. Only this map is persisted.
 *
 * `replayOpen` is the transient manual-replay overlay flag (never persisted): the persistent
 * rail control opens the same <FirstRunShow> from anywhere, ignoring `dismissed`.
 */
export interface FirstRunState {
  dismissed: Record<string, boolean>;
  replayOpen: boolean;
  /** Auto-show path only: remember the newcomer dismissed the show for this namespace. */
  dismiss: (key: string) => void;
  /** Re-arm the auto-show for a namespace once it is seen non-empty (called on populate). */
  clearIfPopulated: (key: string) => void;
  openReplay: () => void;
  closeReplay: () => void;
}

export const useFirstRun = create<FirstRunState>()(
  persist(
    (set) => ({
      dismissed: {},
      replayOpen: false,

      dismiss: (key) =>
        set((s) => (s.dismissed[key] ? s : { dismissed: { ...s.dismissed, [key]: true } })),

      clearIfPopulated: (key) =>
        set((s) => {
          if (!s.dismissed[key]) return s;
          const next = { ...s.dismissed };
          delete next[key];
          return { dismissed: next };
        }),

      openReplay: () => set({ replayOpen: true }),
      closeReplay: () => set({ replayOpen: false }),
    }),
    {
      name: storageKey("f8.first-run"),
      // Persist only the dismissal memory; the overlay flag is per-session UI state.
      partialize: (s) => ({ dismissed: s.dismissed }),
    },
  ),
);
