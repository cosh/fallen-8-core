import { beforeEach, describe, expect, it } from "vitest";
import { useFirstRun } from "../src/firstrun/firstRunStore";

/**
 * The dismissal memory (feature studio-first-run) must not nag a returning user on a graph that
 * stayed empty, yet must re-arm the moment the graph is seen non-empty so a later emptying shows
 * the intro again. The overlay flag is transient and never persisted.
 */
describe("firstRunStore", () => {
  beforeEach(() => {
    useFirstRun.setState({ dismissed: {}, replayOpen: false });
    window.localStorage.clear();
  });

  const KEY = "i-1/default";

  it("dismiss remembers per key; a returning user is not nagged", () => {
    useFirstRun.getState().dismiss(KEY);
    expect(useFirstRun.getState().dismissed[KEY]).toBe(true);
    // A different namespace is unaffected.
    expect(useFirstRun.getState().dismissed["i-1/flights"]).toBeUndefined();
  });

  it("clearIfPopulated re-arms only a previously dismissed key", () => {
    // No-op when not dismissed (no needless state churn).
    const before = useFirstRun.getState().dismissed;
    useFirstRun.getState().clearIfPopulated(KEY);
    expect(useFirstRun.getState().dismissed).toBe(before);

    useFirstRun.getState().dismiss(KEY);
    useFirstRun.getState().clearIfPopulated(KEY);
    expect(useFirstRun.getState().dismissed[KEY]).toBeUndefined();
  });

  it("persists only the dismissal map, not the transient overlay flag", () => {
    useFirstRun.getState().dismiss(KEY);
    useFirstRun.getState().openReplay();
    expect(useFirstRun.getState().replayOpen).toBe(true);

    const persisted = JSON.parse(window.localStorage.getItem("f8.first-run") ?? "{}");
    expect(persisted.state.dismissed[KEY]).toBe(true);
    expect(persisted.state.replayOpen).toBeUndefined();
  });

  it("openReplay/closeReplay toggle the transient flag", () => {
    useFirstRun.getState().openReplay();
    expect(useFirstRun.getState().replayOpen).toBe(true);
    useFirstRun.getState().closeReplay();
    expect(useFirstRun.getState().replayOpen).toBe(false);
  });
});
