import { useCallback, useEffect, useState } from "react";

export interface Timeline {
  /** The active beat index while playing, or null once the show has settled on the handoff. */
  beat: number | null;
  /** True once the show has settled: it finished playing, was skipped, or reduced-motion. */
  resting: boolean;
  /** True while the tab is hidden (motion is held so the show never runs past unseen). */
  paused: boolean;
  /** True while auto-advancing; manual navigation turns it off, Replay turns it back on. */
  autoplay: boolean;
  /** Step to the next beat (or the handoff after the last one). Pauses autoplay. */
  next: () => void;
  /** Step to the previous beat. Pauses autoplay. */
  prev: () => void;
  /** Jump directly to a beat by index. Pauses autoplay. */
  goTo: (index: number) => void;
  /** Jump straight to the handoff. Pauses autoplay. */
  skip: () => void;
  /** Restart from beat 0 and resume auto-advancing. */
  replay: () => void;
}

/**
 * Drives the first-run show's beat sequence (feature studio-first-run). Advances one beat at a
 * time on `durations`, settling on the handoff (index === beatCount) after the last beat.
 *
 * - Auto-advances only while `autoplay` is on. Any manual navigation (next/prev/goTo/skip) turns
 *   autoplay off so the timer never yanks the view while the user is exploring; Replay turns it
 *   back on from the top.
 * - Pauses while the tab is hidden (`visibilitychange`) so the show never plays unseen; on return
 *   the current beat's timer restarts from full (pausing, not to-the-millisecond resume).
 * - Under `reducedMotion` it opens already settled on the handoff with autoplay off (no motion),
 *   yet the user can still step through the beats manually (the CSS neutralizes their motion).
 *
 * `durations` must be a stable reference (a module constant) - it is an effect dependency.
 */
export function useBeatTimeline(
  beatCount: number,
  durations: readonly number[],
  reducedMotion: boolean,
): Timeline {
  const [index, setIndex] = useState(() => (reducedMotion ? beatCount : 0));
  const [autoplay, setAutoplay] = useState(() => !reducedMotion);
  const [paused, setPaused] = useState(
    () => typeof document !== "undefined" && document.hidden,
  );

  useEffect(() => {
    if (typeof document === "undefined") return;
    const onVisibility = () => setPaused(document.hidden);
    document.addEventListener("visibilitychange", onVisibility);
    return () => document.removeEventListener("visibilitychange", onVisibility);
  }, []);

  useEffect(() => {
    if (!autoplay) return; // manual navigation is driving
    if (paused) return; // hidden tab: hold the current beat
    if (index >= beatCount) return; // settled on the handoff
    const timer = setTimeout(
      () => setIndex((i) => Math.min(i + 1, beatCount)),
      durations[index],
    );
    return () => clearTimeout(timer);
  }, [index, paused, autoplay, beatCount, durations]);

  const clamp = useCallback((i: number) => Math.max(0, Math.min(i, beatCount)), [beatCount]);
  const next = useCallback(() => {
    setAutoplay(false);
    setIndex((i) => clamp(i + 1));
  }, [clamp]);
  const prev = useCallback(() => {
    setAutoplay(false);
    setIndex((i) => clamp(i - 1));
  }, [clamp]);
  const goTo = useCallback(
    (i: number) => {
      setAutoplay(false);
      setIndex(clamp(i));
    },
    [clamp],
  );
  const skip = useCallback(() => {
    setAutoplay(false);
    setIndex(beatCount);
  }, [beatCount]);
  const replay = useCallback(() => {
    setAutoplay(true);
    setIndex(0);
  }, []);

  const resting = index >= beatCount;
  return { beat: resting ? null : index, resting, paused, autoplay, next, prev, goTo, skip, replay };
}
