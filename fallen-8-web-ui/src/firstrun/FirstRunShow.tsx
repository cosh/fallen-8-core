// MIT License
//
// FirstRunShow.tsx
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

import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import { BEATS, BEAT_DURATIONS } from "./beats";
import { MOCK_GRAPH, isPathEdge, isSubgraphEdge, type MockVertex } from "./mockGraph";
import { useReducedMotion } from "./useReducedMotion";
import { useBeatTimeline } from "./useBeatTimeline";

/**
 * The first-run show (feature studio-first-run): a short, mostly-passive, code-driven animated
 * walkthrough of Fallen-8's five core capabilities (graph, path, analytics, subgraph, vectors)
 * on the canned {@link MOCK_GRAPH}, ending on an opt-in handoff.
 *
 * It CREATES NOTHING: it renders a hardcoded mock as SVG and animates it with CSS. It issues no
 * network request of its own. Only the handoff buttons perform a real action, on click, through
 * the injected handlers - so the auto-show (rendered inline by the Dashboard on an empty graph)
 * and the manual replay (rendered by <FirstRunOverlay>) are the SAME component with the same
 * contract; only the entry point differs. Prev/Next/dots let a viewer step through at their pace.
 */
export interface FirstRunShowProps {
  /** "auto" = inline empty-state on the Dashboard; "replay" = manual overlay. Used only for labels. */
  variant: "auto" | "replay";
  /** Dismiss into the app (auto: remember dismissal; replay: close the overlay). */
  onExplore: () => void;
  /** Jump to the Sample gallery (the newcomer's path from empty to a populated, curated graph). */
  onBrowseSamples: () => void;
  /** Point the user at the JSONL import location. */
  onImport: () => void;
}

const CENTER = { x: 410, y: 240 };
/** Node radius used to clip edges so their arrowheads land just outside the target glyph. */
const NODE_R = 30;

// Wide spread so the analytics beat makes "what matters most" pop: the top node is ~1.7x the
// smallest, not a subtle nudge.
function rankScale(rank: number): number {
  return 0.6 + rank * 1.25;
}

/** The rank "halo" radius: a translucent bubble whose size reads the score at a glance. */
function haloRadius(rank: number): number {
  return 16 + rank * 48;
}

/** The single highest-ranked vertex id (the one the analytics beat spotlights). */
const TOP_RANK_ID = MOCK_GRAPH.vertices.reduce((a, b) => (b.rank > a.rank ? b : a)).id;

function vertex(id: number): MockVertex {
  // Small fixed set; a linear find is fine and keeps the data a plain array.
  return MOCK_GRAPH.vertices.find((v) => v.id === id)!;
}

export function FirstRunShow({ variant, onExplore, onBrowseSamples, onImport }: FirstRunShowProps) {
  const reducedMotion = useReducedMotion();
  const timeline = useBeatTimeline(BEATS.length, BEAT_DURATIONS, reducedMotion);
  // Bump on Replay so the entry (bloom) animation re-runs without a host remount.
  const [playId, setPlayId] = useState(0);

  const phase = timeline.beat; // 0..4 while playing, null once rested
  const resting = timeline.resting;
  // Emphasis is beat-scoped so each step reads cleanly; rank sizing persists (a nice settle) and
  // the graph rests on that clean ranked composition.
  const ranked = resting || (phase !== null && phase >= 2);
  const showPath = phase === 1;
  // The rank "halo" bubbles are a spotlight moment on the analytics beat only; the sizing they
  // introduce persists (a nice settle), but the bubbles do not clutter later beats.
  const showHalo = phase === 2;
  const subgraphOn = phase === 3;
  const semanticOn = phase === 4;

  const stepIndex = resting ? BEATS.length : (phase ?? 0);
  const activeBeat = phase !== null ? BEATS[phase] : null;

  const sectionRef = useRef<HTMLElement>(null);
  const replayRef = useRef<HTMLButtonElement>(null);
  // A user step that lands on the handoff (Skip, or Next on the last beat) disables/removes the
  // control they clicked; move focus to Replay so a keyboard user never drops out to <body>.
  const steppedToRest = useRef(false);

  // Focus the region on mount so Left/Right arrow stepping works for a fresh viewer without a
  // first Tab, in both the inline auto-show and the overlay. preventScroll: the region fills its
  // container, so there is nothing to scroll to.
  useEffect(() => {
    sectionRef.current?.focus({ preventScroll: true });
  }, []);

  useEffect(() => {
    if (resting && steppedToRest.current) {
      steppedToRest.current = false;
      replayRef.current?.focus();
    }
  }, [resting]);

  const replay = () => {
    timeline.replay();
    setPlayId((n) => n + 1);
  };
  const next = () => {
    if (stepIndex === BEATS.length - 1) steppedToRest.current = true;
    timeline.next();
  };
  const skip = () => {
    steppedToRest.current = true;
    timeline.skip();
  };

  const onKeyDown = (e: KeyboardEvent<HTMLElement>) => {
    if (e.key === "ArrowRight" && !resting) {
      e.preventDefault();
      next();
    } else if (e.key === "ArrowLeft" && stepIndex > 0) {
      e.preventDefault();
      timeline.prev();
    }
  };

  const { path, subgraph, semantic } = MOCK_GRAPH;
  const nearest = new Set(semantic.nearest);
  const onPath = new Set(path);
  const inSubgraph = new Set(subgraph);

  // The subgraph "extraction" box: the bounding rect of its member nodes, padded.
  const sgNodes = subgraph.map(vertex);
  const sgPad = 54;
  const sgRect = {
    x: Math.min(...sgNodes.map((v) => v.x)) - sgPad,
    y: Math.min(...sgNodes.map((v) => v.y)) - sgPad,
    w: Math.max(...sgNodes.map((v) => v.x)) - Math.min(...sgNodes.map((v) => v.x)) + sgPad * 2,
    h: Math.max(...sgNodes.map((v) => v.y)) - Math.min(...sgNodes.map((v) => v.y)) + sgPad * 2,
  };

  return (
    <section
      ref={sectionRef}
      className={`f8fr flex h-full min-h-0 flex-col${timeline.paused ? " is-paused" : ""}`}
      data-testid="first-run-show"
      data-variant={variant}
      data-resting={resting ? "true" : "false"}
      // A focusable region so Left/Right arrow-key stepping works after a single Tab, not only
      // once a child control happens to be focused. Not autofocused (that would steal focus).
      tabIndex={0}
      role="region"
      aria-label="Fallen-8 introduction. Use the left and right arrow keys, or the controls below, to step through the features."
      onKeyDown={onKeyDown}
    >
      <div className="f8fr-stagewrap min-h-0 flex-1">
        <svg
          className="f8fr-svg h-full w-full"
          viewBox={MOCK_GRAPH.viewBox}
          preserveAspectRatio="xMidYMid meet"
          role="img"
          aria-label="An animated example graph: a threat actor, a compromised supply-chain tool, its targets, and the defenders."
        >
          <defs>
            {/* One arrowhead, tinted to match each edge's stroke (SVG2 context-stroke). */}
            <marker
              id="f8fr-arrow"
              viewBox="0 0 10 10"
              refX="8"
              refY="5"
              markerWidth="7"
              markerHeight="7"
              orient="auto-start-reverse"
            >
              <path d="M 0 0 L 10 5 L 0 10 z" fill="context-stroke" />
            </marker>
          </defs>

          <g key={playId} className={`f8fr-stage${reducedMotion ? " reduced" : ""}`}>
            {/* One subtle radial ripple on the opening bloom. */}
            <circle className="f8fr-ripple" cx={CENTER.x} cy={CENTER.y} r={40} aria-hidden />

            {/* The subgraph extraction box, only while that beat is active. */}
            {subgraphOn && (
              <rect
                className="f8fr-hull"
                x={sgRect.x}
                y={sgRect.y}
                width={sgRect.w}
                height={sgRect.h}
                rx={20}
                aria-hidden
              />
            )}

            {/* Semantic beat: a query point with kNN links to its nearest neighbors. Behind the
                nodes so the emoji stay legible. */}
            {semanticOn &&
              semantic.nearest.map((id) => {
                const t = vertex(id);
                return (
                  <line
                    key={`knn-${id}`}
                    className="f8fr-knn"
                    x1={semantic.query.x}
                    y1={semantic.query.y}
                    x2={t.x}
                    y2={t.y}
                    aria-hidden
                  />
                );
              })}

            {/* Directed edges, clipped to the node radius so the arrowheads show. */}
            {MOCK_GRAPH.edges.map((edge, i) => {
              const s = vertex(edge.source);
              const t = vertex(edge.target);
              const dx = t.x - s.x;
              const dy = t.y - s.y;
              const len = Math.hypot(dx, dy) || 1;
              const ux = dx / len;
              const uy = dy / len;
              const onPathEdge = showPath && isPathEdge(edge, path);
              const sgEdge = subgraphOn && isSubgraphEdge(edge, subgraph);
              const dimEdge = subgraphOn && !sgEdge;
              const expandEdge =
                semanticOn &&
                ((edge.source === semantic.expandFrom && edge.target === semantic.expandTo) ||
                  (edge.source === semantic.expandTo && edge.target === semantic.expandFrom));
              const cls = [
                "f8fr-edge",
                onPathEdge ? "is-path" : "",
                sgEdge ? "is-subgraph" : "",
                dimEdge ? "is-dim" : "",
                expandEdge ? "is-expand" : "",
              ]
                .filter(Boolean)
                .join(" ");
              return (
                <line
                  key={edge.id}
                  className={cls}
                  style={{ ["--i" as string]: i }}
                  x1={s.x + ux * NODE_R}
                  y1={s.y + uy * NODE_R}
                  x2={t.x - ux * NODE_R}
                  y2={t.y - uy * NODE_R}
                  markerEnd="url(#f8fr-arrow)"
                />
              );
            })}

            {/* Emoji vertices bloom outward; rank sizing, path ring, subgraph highlight, and
                semantic emphasis layer on with their beats. */}
            {MOCK_GRAPH.vertices.map((v, i) => {
              const near = semanticOn && nearest.has(v.id);
              const markScale = (ranked ? rankScale(v.rank) : 1) * (near ? 1.12 : 1);
              const cls = [
                "f8fr-node",
                showPath && onPath.has(v.id) ? "is-onpath" : "",
                subgraphOn && inSubgraph.has(v.id) ? "is-subgraph" : "",
                subgraphOn && !inSubgraph.has(v.id) ? "is-dim" : "",
                near ? "is-near" : "",
                semanticOn && v.id === semantic.expandTo ? "is-expand" : "",
              ]
                .filter(Boolean)
                .join(" ");
              return (
                <g
                  key={v.id}
                  className={`${cls}${showHalo && v.id === TOP_RANK_ID ? " is-top" : ""}`}
                  style={{ ["--i" as string]: i }}
                >
                  {/* Analytics beat: a translucent bubble sized by PageRank, behind the glyph, so
                      the most important entity is unmistakable. The top node's bubble pulses. */}
                  {showHalo && (
                    <circle className="f8fr-halo" cx={v.x} cy={v.y} r={haloRadius(v.rank)} aria-hidden />
                  )}
                  <g className="f8fr-mark" style={{ ["--mark-scale" as string]: markScale }}>
                    <circle className="f8fr-dot" cx={v.x} cy={v.y} r={22} />
                    <text className="f8fr-emoji" x={v.x} y={v.y} textAnchor="middle" dominantBaseline="central">
                      {v.emoji}
                    </text>
                  </g>
                  <text className="f8fr-label" x={v.x} y={v.y + 42} textAnchor="middle">
                    {v.label}
                  </text>
                </g>
              );
            })}

            {/* Semantic beat: the query origin, on top so it reads as "search from here". */}
            {semanticOn && (
              <g className="f8fr-query" aria-hidden>
                <circle className="f8fr-query-dot" cx={semantic.query.x} cy={semantic.query.y} r={17} />
                <text
                  className="f8fr-query-emoji"
                  x={semantic.query.x}
                  y={semantic.query.y}
                  textAnchor="middle"
                  dominantBaseline="central"
                >
                  🔍
                </text>
              </g>
            )}
          </g>
        </svg>
      </div>

      {/* Beat caption in its own row BELOW the stage (never overlapping the graph); announced
          politely to assistive tech. Fixed min-height keeps the layout from jumping per beat. */}
      <div className="f8fr-caption" aria-live="polite" data-testid="first-run-caption">
        {activeBeat && (
          <>
            <p className="f8fr-caption-line">{activeBeat.caption}</p>
            {activeBeat.endpoint && <p className="f8fr-caption-endpoint">{activeBeat.endpoint}</p>}
            {activeBeat.note && <p className="f8fr-caption-note">{activeBeat.note}</p>}
          </>
        )}
      </div>

      {/* Navigation + controls. Prev/Next/dots step through the features; Skip settles on the
          handoff; Replay restarts autoplay. Left/Right arrow keys also step. */}
      <div className="f8fr-controls">
        <div className="f8fr-nav">
          <button
            type="button"
            className="btn f8fr-navbtn"
            data-testid="first-run-prev"
            aria-label="Previous feature"
            disabled={stepIndex === 0}
            onClick={timeline.prev}
          >
            ‹
          </button>
          <div className="f8fr-dots" role="group" aria-label="Walkthrough steps">
            {BEATS.map((b, i) => (
              <button
                key={b.id}
                type="button"
                aria-current={phase === i ? "step" : undefined}
                aria-label={`Step ${i + 1}: ${b.title}`}
                data-testid={`first-run-dot-${i}`}
                className={`f8fr-dot-tick${phase === i ? " is-active" : ""}${
                  phase !== null && phase > i ? " is-done" : ""
                }`}
                onClick={() => timeline.goTo(i)}
              />
            ))}
          </div>
          <button
            type="button"
            className="btn f8fr-navbtn"
            data-testid="first-run-next"
            aria-label="Next feature"
            disabled={resting}
            onClick={next}
          >
            ›
          </button>
        </div>
        <div className="ml-auto flex gap-2">
          {!resting && (
            <button type="button" className="btn" data-testid="first-run-skip" onClick={skip}>
              Skip
            </button>
          )}
          <button
            ref={replayRef}
            type="button"
            className="btn"
            data-testid="first-run-replay"
            onClick={replay}
          >
            Replay
          </button>
        </div>
      </div>

      {/* The calm handoff: the ONLY place a real action can happen, on click. */}
      {resting && (
        <Handoff
          variant={variant}
          onExplore={onExplore}
          onBrowseSamples={onBrowseSamples}
          onImport={onImport}
        />
      )}
    </section>
  );
}

function Handoff({ variant, onExplore, onBrowseSamples, onImport }: FirstRunShowProps) {
  return (
    <div className="f8fr-handoff panel" data-testid="first-run-handoff">
      <h2 className="f8fr-handoff-title">That is Fallen-8, end to end.</h2>
      <p className="f8fr-handoff-sub">
        {variant === "auto"
          ? "Your graph is empty. Load a curated sample, bring your own data, or explore on your own."
          : "Pick up where you were, or open the sample gallery to load a curated graph."}
      </p>

      <div className="f8fr-handoff-actions">
        <div className="f8fr-handoff-action">
          <button
            type="button"
            className="btn btn-accent"
            data-testid="first-run-browse-samples"
            onClick={onBrowseSamples}
          >
            Browse sample graphs
          </button>
          <p className="f8fr-handoff-hint">Curated, styled demo datasets that load in one click.</p>
        </div>

        <div className="f8fr-handoff-action">
          <button type="button" className="btn" data-testid="first-run-import" onClick={onImport}>
            Import your own data
          </button>
          <p className="f8fr-handoff-hint">
            Stream JSONL to POST /bulk/import, one vertex or edge per line.
          </p>
        </div>

        <div className="f8fr-handoff-action">
          <button type="button" className="btn" data-testid="first-run-explore" onClick={onExplore}>
            Explore on my own
          </button>
          <p className="f8fr-handoff-hint">Dismiss this and start with an empty graph.</p>
        </div>
      </div>
    </div>
  );
}
