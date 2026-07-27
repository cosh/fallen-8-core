import { create } from "zustand";
import { persist } from "zustand/middleware";

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
      name: "f8.first-run",
      // Persist only the dismissal memory; the overlay flag is per-session UI state.
      partialize: (s) => ({ dismissed: s.dismissed }),
    },
  ),
);
