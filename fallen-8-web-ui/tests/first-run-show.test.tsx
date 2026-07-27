import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import { FirstRunShow } from "../src/firstrun/FirstRunShow";
import { BEATS } from "../src/firstrun/beats";
import { MOCK_GRAPH } from "../src/firstrun/mockGraph";

/**
 * The show (feature studio-first-run) creates nothing: it renders a hardcoded mock and animates
 * it, issuing no network request of its own. Only the handoff buttons act, through the injected
 * handlers. It steps through five beats (graph, path, analytics, subgraph, vectors) with
 * Prev/Next/dots, and under reduced motion rests immediately on the composed state + handoff.
 */
function mockMatchMedia(reduced: boolean) {
  window.matchMedia = vi.fn().mockImplementation((query: string) => ({
    matches: query.includes("reduce") ? reduced : false,
    media: query,
    onchange: null,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    addListener: vi.fn(),
    removeListener: vi.fn(),
    dispatchEvent: vi.fn(),
  }));
}

const handlers = () => ({
  onExplore: vi.fn(),
  onImport: vi.fn(),
  onBrowseSamples: vi.fn(),
});

describe("FirstRunShow", () => {
  let fetchSpy: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    vi.useFakeTimers();
    fetchSpy = vi.fn();
    vi.stubGlobal("fetch", fetchSpy);
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("issues no network request of its own and draws every mock entity", () => {
    mockMatchMedia(false);
    render(<FirstRunShow variant="auto" {...handlers()} />);

    for (const v of MOCK_GRAPH.vertices) {
      expect(screen.getByText(v.label)).toBeInTheDocument();
      expect(screen.getByText(v.emoji)).toBeInTheDocument();
    }
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it("autoplays from beat 1's caption with Prev disabled and Skip/Replay available", () => {
    mockMatchMedia(false);
    render(<FirstRunShow variant="auto" {...handlers()} />);

    expect(screen.getByTestId("first-run-caption")).toHaveTextContent(BEATS[0].caption);
    expect(screen.getByTestId("first-run-prev")).toBeDisabled();
    expect(screen.getByTestId("first-run-skip")).toBeInTheDocument();
    expect(screen.getByTestId("first-run-replay")).toBeInTheDocument();
    expect(screen.queryByTestId("first-run-handoff")).not.toBeInTheDocument();
  });

  it("steps through the features with Next and Prev (manual navigation)", () => {
    mockMatchMedia(false);
    render(<FirstRunShow variant="auto" {...handlers()} />);

    fireEvent.click(screen.getByTestId("first-run-next"));
    expect(screen.getByTestId("first-run-caption")).toHaveTextContent(BEATS[1].caption);
    fireEvent.click(screen.getByTestId("first-run-next"));
    expect(screen.getByTestId("first-run-caption")).toHaveTextContent(BEATS[2].caption);
    fireEvent.click(screen.getByTestId("first-run-prev"));
    expect(screen.getByTestId("first-run-caption")).toHaveTextContent(BEATS[1].caption);
  });

  it("jumps straight to the subgraph beat via its dot and draws the extraction box", () => {
    mockMatchMedia(false);
    render(<FirstRunShow variant="auto" {...handlers()} />);

    const subgraphIndex = BEATS.findIndex((b) => b.id === "subgraph");
    fireEvent.click(screen.getByTestId(`first-run-dot-${subgraphIndex}`));
    expect(screen.getByTestId("first-run-caption")).toHaveTextContent("PUT /subgraph");
    expect(document.querySelector(".f8fr-hull")).not.toBeNull();
  });

  it("Skip jumps to the handoff", () => {
    mockMatchMedia(false);
    render(<FirstRunShow variant="auto" {...handlers()} />);

    fireEvent.click(screen.getByTestId("first-run-skip"));
    expect(screen.getByTestId("first-run-handoff")).toBeInTheDocument();
    expect(screen.queryByTestId("first-run-skip")).not.toBeInTheDocument();
    expect(screen.getByTestId("first-run-replay")).toBeInTheDocument();
  });

  it("under reduced motion, rests on the handoff immediately without autoplay", () => {
    mockMatchMedia(true);
    render(<FirstRunShow variant="auto" {...handlers()} />);

    expect(screen.getByTestId("first-run-show")).toHaveAttribute("data-resting", "true");
    expect(screen.getByTestId("first-run-handoff")).toBeInTheDocument();
    expect(screen.queryByTestId("first-run-skip")).not.toBeInTheDocument();
    expect(document.querySelector(".f8fr-stage.reduced")).not.toBeNull();
  });

  it("the handoff buttons act only through the injected handlers, on click", async () => {
    mockMatchMedia(true);
    const h = handlers();
    render(<FirstRunShow variant="auto" {...h} />);

    fireEvent.click(screen.getByTestId("first-run-import"));
    expect(h.onImport).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByTestId("first-run-explore"));
    expect(h.onExplore).toHaveBeenCalledTimes(1);

    // "Browse sample graphs" jumps to the Sample gallery (the host navigates); it never writes,
    // and the unit-test graph is deliberately never wired in.
    fireEvent.click(screen.getByTestId("first-run-browse-samples"));
    expect(h.onBrowseSamples).toHaveBeenCalledTimes(1);

    expect(fetchSpy).not.toHaveBeenCalled();
  });
});
