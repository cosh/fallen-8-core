// MIT License
//
// beat-timeline.test.ts
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

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, renderHook } from "@testing-library/react";
import { useBeatTimeline } from "../src/firstrun/useBeatTimeline";

/**
 * The beat timeline (feature studio-first-run) advances one beat at a time, settles on the
 * handoff, pauses while the tab is hidden so the show never plays unseen, lets the viewer step
 * through manually (which pauses autoplay), and does not autoplay at all under reduced motion.
 */
const DURATIONS = [100, 100, 100] as const;

function setHidden(hidden: boolean) {
  Object.defineProperty(document, "hidden", { configurable: true, get: () => hidden });
  Object.defineProperty(document, "visibilityState", {
    configurable: true,
    get: () => (hidden ? "hidden" : "visible"),
  });
}

describe("useBeatTimeline", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    setHidden(false);
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it("advances through the beats and settles on the handoff", () => {
    const { result } = renderHook(() => useBeatTimeline(3, DURATIONS, false));
    expect(result.current.beat).toBe(0);
    expect(result.current.resting).toBe(false);
    expect(result.current.autoplay).toBe(true);

    act(() => void vi.advanceTimersByTime(100));
    expect(result.current.beat).toBe(1);
    act(() => void vi.advanceTimersByTime(100));
    expect(result.current.beat).toBe(2);
    act(() => void vi.advanceTimersByTime(100));
    expect(result.current.beat).toBeNull();
    expect(result.current.resting).toBe(true);
  });

  it("holds the current beat while the tab is hidden, then resumes", () => {
    const { result } = renderHook(() => useBeatTimeline(3, DURATIONS, false));
    expect(result.current.beat).toBe(0);

    act(() => {
      setHidden(true);
      document.dispatchEvent(new Event("visibilitychange"));
    });
    expect(result.current.paused).toBe(true);

    // Time passes while hidden: the beat must not advance.
    act(() => void vi.advanceTimersByTime(500));
    expect(result.current.beat).toBe(0);

    act(() => {
      setHidden(false);
      document.dispatchEvent(new Event("visibilitychange"));
    });
    expect(result.current.paused).toBe(false);
    act(() => void vi.advanceTimersByTime(100));
    expect(result.current.beat).toBe(1);
  });

  it("next/prev step through beats and pause autoplay so the timer never yanks the view", () => {
    const { result } = renderHook(() => useBeatTimeline(3, DURATIONS, false));

    act(() => result.current.next());
    expect(result.current.beat).toBe(1);
    expect(result.current.autoplay).toBe(false);

    // Autoplay is off now: advancing the clock must not move the beat.
    act(() => void vi.advanceTimersByTime(1000));
    expect(result.current.beat).toBe(1);

    act(() => result.current.prev());
    expect(result.current.beat).toBe(0);
    // Prev at beat 0 is clamped (no underflow).
    act(() => result.current.prev());
    expect(result.current.beat).toBe(0);
  });

  it("goTo jumps directly to a beat and pauses autoplay", () => {
    const { result } = renderHook(() => useBeatTimeline(3, DURATIONS, false));
    act(() => result.current.goTo(2));
    expect(result.current.beat).toBe(2);
    expect(result.current.autoplay).toBe(false);
  });

  it("Next past the last beat settles on the handoff; Prev from the handoff returns to it", () => {
    const { result } = renderHook(() => useBeatTimeline(3, DURATIONS, false));
    act(() => result.current.goTo(2));
    act(() => result.current.next());
    expect(result.current.resting).toBe(true);
    expect(result.current.beat).toBeNull();

    act(() => result.current.prev());
    expect(result.current.resting).toBe(false);
    expect(result.current.beat).toBe(2);
  });

  it("skip jumps to the handoff; replay restarts at beat 0 and resumes autoplay", () => {
    const { result } = renderHook(() => useBeatTimeline(3, DURATIONS, false));
    act(() => result.current.skip());
    expect(result.current.resting).toBe(true);
    expect(result.current.autoplay).toBe(false);

    act(() => result.current.replay());
    expect(result.current.beat).toBe(0);
    expect(result.current.resting).toBe(false);
    expect(result.current.autoplay).toBe(true);
  });

  it("does not autoplay under reduced motion (opens rested), yet still steps manually", () => {
    const { result } = renderHook(() => useBeatTimeline(3, DURATIONS, true));
    expect(result.current.resting).toBe(true);
    expect(result.current.beat).toBeNull();
    expect(result.current.autoplay).toBe(false);

    // No autoplay even as the clock runs.
    act(() => void vi.advanceTimersByTime(1000));
    expect(result.current.beat).toBeNull();

    // The viewer can still step back into the beats (the CSS neutralizes their motion).
    act(() => result.current.prev());
    expect(result.current.beat).toBe(2);
  });
});
