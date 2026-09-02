// MIT License
//
// InteractPanel.tsx
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

import { useEffect, useMemo, useRef, useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { useInstanceStore } from "../instances/registry";
import { embeddingSearch } from "../api/endpoints";
import { useStatus } from "../state/status";
import { shapeSuggestions, useGraphShape } from "../state/graphShape";
import {
  DEGREE_SWEEP_CAP,
  EXPAND_SWEEP_CAP,
  activeDegree,
  anyCheapActive,
  applyDegree,
  applySemantic,
  degreeSweep,
  expandVertices,
  matchCheap,
  type CheapFilters,
  type ExpandOutcome,
  type SweepProgress,
} from "../lib/canvasInteract";
import { CANVAS_EXPAND_CEILING } from "../lib/canvasCap";
import { EXPAND_EDGE_CAP } from "../lib/neighborhood";
import { MAX_K } from "../lib/vectorSearch";
import { SCROLL_ROWS, capList, scrollRows } from "../lib/listCaps";
import { DISPLAY_CAP } from "../lib/truncate";
import type { ElementRef } from "./GraphCanvas";
import { Field } from "../components/Field";
import { help } from "../lib/fieldHelp";
import { Truncated } from "../components/Truncated";
import { ListCapNote } from "../components/ListCapNote";
import { ErrorBox } from "../components/ErrorBox";

/** What a Preview evaluated: the ids that survived every filter, and what could not be judged. */
interface Evaluated {
  ids: number[];
  /** Candidates the semantic search returned no score for; they matched nothing. */
  unscored: number;
  /** Candidates whose degree the server would not answer; they matched nothing. */
  unreadable: number;
}

/** What the last expand sweep did, kept so the panel can report it after the run. */
type ExpandReport = ExpandOutcome;

/**
 * Canvas "Interact" tab (feature canvas-interact): filters build a MATCH SET over the canvas
 * vertices, and two view-only verbs apply to it - expand every match's neighborhood, or remove
 * every match from the view. With no filter active the match set is every canvas vertex, which is
 * how "expand all" is expressed rather than as a separate button.
 *
 * Label, property and on-canvas degree are cheap, so they evaluate live on every render. Database
 * degree and the semantic threshold cost server round trips, so they run behind Preview, and any
 * filter edit or canvas change INVALIDATES that result: acting on a stale match set is the bug
 * this lifecycle exists to prevent. Nothing here touches the database.
 */
export function InteractPanel({
  selected,
  onSelect,
  onHover,
}: {
  /** What the Detail panel currently shows, so a bulk remove can retire a selection it took. */
  selected: ElementRef | null;
  onSelect: (ref: ElementRef | null) => void;
  /** Spotlight a previewed vertex on the canvas, the Find tab's affordance. */
  onHover?: (ref: ElementRef | null) => void;
}) {
  const { instance, store } = useInstanceStore();
  const draft = store((s) => s.canvasToolsDraft);
  const setDraft = store((s) => s.setCanvasToolsDraft);
  const canvasNodes = store((s) => s.canvasNodes);
  const canvasEdges = store((s) => s.canvasEdges);
  const mergeIntoCanvas = store((s) => s.mergeIntoCanvas);
  const removeManyFromCanvas = store((s) => s.removeManyFromCanvas);

  const status = useStatus(instance).data;
  const suggestions = shapeSuggestions(useGraphShape(instance).data);

  const [evaluated, setEvaluated] = useState<Evaluated | null>(null);
  const [progress, setProgress] = useState<SweepProgress | null>(null);
  const [expandReport, setExpandReport] = useState<ExpandReport | null>(null);
  const [overCap, setOverCap] = useState<string | null>(null);
  /**
   * The two runs get SEPARATE controllers, which is load-bearing rather than tidy: an
   * invalidation aborts an in-flight Preview (it measured a canvas that has since changed), but an
   * expand sweep merges into the canvas on every batch and so invalidates continuously. One shared
   * controller made the sweep abort itself after its first batch and report the rest as skipped.
   */
  const previewControllerRef = useRef<AbortController | null>(null);
  const expandControllerRef = useRef<AbortController | null>(null);
  /** Set while a cancel is deliberate, so the abort is not reported as a failure. */
  const cancelledRef = useRef(false);

  const filters: CheapFilters = useMemo(
    () => ({
      label: draft.interactLabel,
      propKey: draft.interactPropKey,
      propTerm: draft.interactPropTerm,
      degreeSource: draft.interactDegreeSource,
      degreeDirection: draft.interactDegreeDirection,
      degreeOp: draft.interactDegreeOp,
      degreeValue: draft.interactDegreeValue,
    }),
    [
      draft.interactLabel,
      draft.interactPropKey,
      draft.interactPropTerm,
      draft.interactDegreeSource,
      draft.interactDegreeDirection,
      draft.interactDegreeOp,
      draft.interactDegreeValue,
    ],
  );

  const nodeList = useMemo(() => Object.values(canvasNodes), [canvasNodes]);
  const edgeList = useMemo(() => Object.values(canvasEdges), [canvasEdges]);

  // The cheap match set, recomputed whenever the canvas or a cheap filter changes, because being
  // right costs nothing here.
  const cheapMatches = useMemo(
    () => matchCheap(nodeList, edgeList, filters),
    [nodeList, edgeList, filters],
  );

  // A bound vector index is what makes a semantic query answerable: an unbound one holds vectors
  // that exist nowhere else, so it cannot be expected to contain these elements at all.
  const boundIndices = useMemo(
    () =>
      (status?.indices ?? []).filter(
        (index) => index.pluginType === "VectorIndex" && Boolean(index.embeddingName),
      ),
    [status],
  );
  const providerOn = status?.embedding?.enabled === true;
  const semanticAvailable = providerOn && boundIndices.length > 0;
  // Normalized against the LIVE bound set, not trusted: a persisted id naming an index that has
  // since been dropped or renamed would send every Preview to a 404 with no control to fix it,
  // because the picker only appears when there is a choice to make.
  const semanticIndexId =
    boundIndices.find((index) => index.indexId === draft.interactSemanticIndexId)?.indexId ??
    boundIndices[0]?.indexId ??
    "";
  const semanticIndex = boundIndices.find((index) => index.indexId === semanticIndexId);

  const semanticThreshold = draft.interactSemanticThreshold.trim();
  const semanticTextTyped = draft.interactSemanticText.trim() !== "";
  const semanticThresholdTyped = semanticThreshold !== "" && Number.isFinite(Number(semanticThreshold));
  const semanticActive = semanticAvailable && semanticTextTyped && semanticThresholdTyped;
  const databaseDegreeActive =
    draft.interactDegreeSource === "database" && activeDegree(filters) !== null;
  const costly = databaseDegreeActive || semanticActive;

  /**
   * A filter row the user has begun filling in but which cannot yet be applied: a property term
   * with no key, or a semantic half-pair. Load-bearing rather than cosmetic - without it, typing
   * "turbine vibration" and no threshold reads "no filter" and arms `Remove from view` over the
   * WHOLE canvas, and so does the keystroke where a degree box is mid-edit ("1e", or cleared to
   * retype), because a type=number input reports those as empty. So an incomplete filter disables
   * the verbs instead of silently widening them to everything.
   */
  const incomplete: string | null = (() => {
    if (draft.interactPropTerm.trim() !== "" && draft.interactPropKey.trim() === "") {
      return "The property term needs a key to look in.";
    }
    if (semanticAvailable && semanticTextTyped && !semanticThresholdTyped) {
      return "The semantic filter needs a threshold as well as a text.";
    }
    if (semanticAvailable && !semanticTextTyped && semanticThresholdTyped) {
      return "The semantic filter needs a text as well as a threshold.";
    }
    return null;
  })();

  // Any filter edit or canvas change RETIRES an evaluated match set: acting on one taken against
  // a canvas that has since changed is the bug this lifecycle exists to prevent. The canvas deps
  // are the store objects themselves, which zustand replaces on every merge and removal, so a
  // swap of one vertex for another invalidates as surely as a change in count would.
  //
  // Clearing state is only HALF of it: a Preview already in flight would otherwise resolve after
  // this and write its now-stale answer back, re-arming the verbs against filters the user has
  // since changed. Aborting it is what prevents that, since every phase returns without writing
  // once its signal is aborted.
  //
  // Deliberately NOT clearing the expand report: it describes a run that really happened, and the
  // merge that run performed is itself one of the canvas changes landing here.
  useEffect(() => {
    previewControllerRef.current?.abort();
    setEvaluated(null);
    setOverCap(null);
  }, [
    canvasNodes,
    canvasEdges,
    filters,
    draft.interactSemanticText,
    semanticIndexId,
    draft.interactSemanticDirection,
    draft.interactSemanticThreshold,
  ]);

  // Clear a lingering spotlight when this tab unmounts, like the Find tab, and STOP whatever is
  // running: switching tabs unmounts this panel, and a sweep left running would keep issuing
  // requests with its Cancel button gone, while the button on remount invites a second one over
  // the same ids.
  useEffect(
    () => () => {
      onHover?.(null);
      previewControllerRef.current?.abort();
      expandControllerRef.current?.abort();
    },
    [onHover],
  );

  /** The ids the verbs will act on: the evaluated set when costly filters are on, else the live one. */
  const matchIds = incomplete !== null ? null : costly ? (evaluated?.ids ?? null) : cheapMatches.map((n) => n.id);
  const matchCount = matchIds?.length ?? 0;
  const filtersActive = anyCheapActive(filters) || costly || incomplete !== null;

  const preview = useMutation({
    mutationFn: async () => {
      const controller = new AbortController();
      previewControllerRef.current = controller;
      cancelledRef.current = false;
      setEvaluated(null);
      setOverCap(null);

      // Costly filters run over the CHEAP survivors only, so narrowing by label first is what
      // makes a big canvas evaluable at all.
      let ids = cheapMatches.map((n) => n.id);
      let unscored = 0;
      let unreadable = 0;

      if (databaseDegreeActive) {
        if (ids.length > DEGREE_SWEEP_CAP) {
          setOverCap(
            `${ids.length} candidates is over the ${DEGREE_SWEEP_CAP} this evaluates at once. Narrow by label or property first, or count the canvas's own edges instead.`,
          );
          return;
        }
        const degree = activeDegree(filters)!;
        setProgress({ done: 0, total: ids.length });
        const sweep = await degreeSweep(instance, ids, {
          direction: degree.direction,
          signal: controller.signal,
          onProgress: setProgress,
        });
        if (controller.signal.aborted) return;
        const matched = applyDegree(sweep.degrees, degree.op, degree.value);
        ids = ids.filter((id) => matched.has(id));
        unreadable = sweep.unreadable;
      }

      if (semanticActive) {
        // MAX_K, not "as many as the canvas could hold": the engine refuses k over 1024 outright
        // (and only after the provider has embedded the text), so a k taken from any other
        // quantity does not degrade, it 400s. The window this leaves is a real limitation and is
        // reported below rather than hidden - a canvas vertex outside the index's global top-k
        // comes back unscored, and an unscored vertex matches neither direction.
        const result = await embeddingSearch(
          instance,
          {
            indexId: semanticIndexId,
            text: draft.interactSemanticText,
            k: MAX_K,
            kind: "vertex",
          },
          controller.signal,
        );
        if (controller.signal.aborted) return;
        const verdict = applySemantic(
          ids,
          result,
          draft.interactSemanticDirection,
          Number(semanticThreshold),
        );
        ids = ids.filter((id) => verdict.matched.has(id));
        unscored = verdict.unscored;
      }

      setEvaluated({ ids, unscored, unreadable });
    },
    onSettled: () => {
      setProgress(null);
      previewControllerRef.current = null;
    },
  });

  const remove = () => {
    if (!matchIds || matchIds.length === 0) return;
    const dropped = new Set(matchIds);
    // ONE store write for the whole set: a per-vertex loop rebuilds the edge map and re-persists
    // the workspace once per vertex, which froze the tab on a large match set.
    removeManyFromCanvas(matchIds);
    // The selection is content too (the single-remove precedent): if the Detail panel was showing
    // one of these vertices - or an edge that leaves with it - it returns to its empty hint rather
    // than describing something no longer on the canvas.
    if (selected?.kind === "node" && dropped.has(selected.id)) {
      onSelect(null);
    } else if (selected?.kind === "edge") {
      const edge = canvasEdges[selected.id];
      if (edge && (dropped.has(edge.source) || dropped.has(edge.target))) onSelect(null);
    }
  };

  const expand = useMutation({
    mutationFn: async () => {
      if (!matchIds || matchCount > EXPAND_SWEEP_CAP) return;
      const controller = new AbortController();
      expandControllerRef.current = controller;
      cancelledRef.current = false;
      setExpandReport(null);
      setProgress({ done: 0, total: matchIds.length });

      const outcome = await expandVertices(instance, matchIds, {
        skipIds: () => new Set(Object.keys(store.getState().canvasNodes).map(Number)),
        liveElementCount: () => {
          const state = store.getState();
          return Object.keys(state.canvasNodes).length + Object.keys(state.canvasEdges).length;
        },
        elementCeiling: CANVAS_EXPAND_CEILING,
        onMerge: mergeIntoCanvas,
        onProgress: setProgress,
        signal: controller.signal,
      });
      setExpandReport(outcome);
    },
    onSettled: () => {
      setProgress(null);
      expandControllerRef.current = null;
    },
  });

  const expandOverCap = matchCount > EXPAND_SWEEP_CAP;
  const busy = preview.isPending || expand.isPending;
  // react-query does not treat an aborted MUTATION as a cancellation, so without this the person
  // who pressed Cancel gets a red error box for doing exactly what the button offered (the
  // Integrations screen's send-cancel is the precedent).
  const failed = (preview.isError || expand.isError) && !cancelledRef.current;
  const { shown, total } = capList(evaluated?.ids ?? []);
  const labelOptions = [
    ...new Set([
      ...suggestions.vertexLabels,
      ...nodeList.map((n) => n.label).filter((l): l is string => Boolean(l)),
    ]),
  ];

  return (
    <div data-testid="interact-panel" className="space-y-3 p-3 text-[12px]">
      <p className="text-fg-faint text-[11px]">
        Filter the vertices on this canvas, then expand or remove all of them at once. With no
        filter set that is every vertex here. View only: the database is never touched.
      </p>

      <datalist id="interact-labels">
        {labelOptions.map((label) => (
          <option key={label} value={label} />
        ))}
      </datalist>

      <Field helpKey="interactLabel" label="label" htmlFor="interact-label">
        <input
          id="interact-label"
          data-testid="interact-label"
          className="input"
          list="interact-labels"
          value={draft.interactLabel}
          onChange={(e) => setDraft({ interactLabel: e.target.value })}
          placeholder="any label"
        />
      </Field>

      <Field helpKey="interactProperty" label="property">
        <div className="flex gap-2">
          <input
            data-testid="interact-prop-key"
            className="input min-w-0 flex-1"
            list="interact-prop-keys"
            value={draft.interactPropKey}
            onChange={(e) => setDraft({ interactPropKey: e.target.value })}
            placeholder="key"
          />
          <input
            data-testid="interact-prop-term"
            className="input min-w-0 flex-1"
            value={draft.interactPropTerm}
            onChange={(e) => setDraft({ interactPropTerm: e.target.value })}
            placeholder="contains…"
          />
        </div>
      </Field>
      <datalist id="interact-prop-keys">
        {suggestions.propertyKeys.map((key) => (
          <option key={key} value={key} />
        ))}
      </datalist>

      <Field helpKey="interactDegree" label="degree">
        {/* Three controls in a 320px panel, sized by flex-BASIS rather than a width utility:
            `.input` carries w-full at the same specificity, so w-20/w-auto lose to it (every other
            `.input w-*` in the studio renders full width for the same reason) and the row collapsed
            either to one full-width select or to bare chevrons. flex-basis is not in that fight. */}
        <div className="flex gap-1">
          <select
            data-testid="interact-degree-direction"
            className="input flex-[0_0_5.5rem]"
            value={draft.interactDegreeDirection}
            onChange={(e) =>
              setDraft({
                interactDegreeDirection: e.target.value as "in" | "out" | "total",
              })
            }
          >
            <option value="total">total</option>
            <option value="in">in</option>
            <option value="out">out</option>
          </select>
          <select
            data-testid="interact-degree-op"
            className="input flex-[0_0_5.5rem]"
            value={draft.interactDegreeOp}
            onChange={(e) => setDraft({ interactDegreeOp: e.target.value as "over" | "under" })}
          >
            <option value="over">over</option>
            <option value="under">under</option>
          </select>
          <input
            data-testid="interact-degree-value"
            type="number"
            className="input min-w-0 flex-1"
            value={draft.interactDegreeValue}
            onChange={(e) => setDraft({ interactDegreeValue: e.target.value })}
            placeholder="off"
          />
        </div>
      </Field>

      <Field helpKey="interactDegreeSource" label="degree counts" htmlFor="interact-degree-source">
        <div className="border-line flex overflow-hidden rounded border">
          {(["database", "canvas"] as const).map((source) => (
            <button
              key={source}
              type="button"
              data-testid={`interact-degree-source-${source}`}
              className={`px-2 py-1 text-[11px] ${
                draft.interactDegreeSource === source
                  ? "bg-panel-2 text-accent"
                  : "text-fg-dim hover:text-fg"
              }`}
              onClick={() => setDraft({ interactDegreeSource: source })}
            >
              {source === "database" ? "the database" : "edges on canvas"}
            </button>
          ))}
        </div>
      </Field>

      {semanticAvailable ? (
        <Field helpKey="interactSemantic" label="semantic distance">
          <div className="space-y-1">
            <input
              data-testid="interact-semantic-text"
              className="input"
              value={draft.interactSemanticText}
              onChange={(e) => setDraft({ interactSemanticText: e.target.value })}
              placeholder="describe what matters…"
            />
            <div className="flex gap-1">
              <select
                data-testid="interact-semantic-direction"
                className="input w-auto"
                value={draft.interactSemanticDirection}
                onChange={(e) =>
                  setDraft({
                    interactSemanticDirection: e.target.value as "closer" | "farther",
                  })
                }
              >
                <option value="closer">closer than</option>
                <option value="farther">farther than</option>
              </select>
              <input
                data-testid="interact-semantic-threshold"
                type="number"
                step="any"
                className="input min-w-0 flex-1"
                value={draft.interactSemanticThreshold}
                onChange={(e) => setDraft({ interactSemanticThreshold: e.target.value })}
                placeholder="raw score"
              />
            </div>
            {/* Always rendered, even for a single bound index: it is the only place that says
                WHICH index produced a match set, and which metric decides what "closer" means. */}
            <select
              data-testid="interact-semantic-index"
              className="input"
              value={semanticIndexId}
              onChange={(e) => setDraft({ interactSemanticIndexId: e.target.value })}
            >
              {boundIndices.map((index) => (
                <option key={index.indexId} value={index.indexId}>
                  {index.indexId} ({index.embeddingName})
                </option>
              ))}
            </select>
            <p className="text-fg-faint text-[10px]" data-testid="interact-semantic-window">
              {semanticIndex?.model ? `${semanticIndex.model}. ` : ""}
              {status?.embedding?.intendedMetric
                ? `Metric ${status.embedding.intendedMetric}: ${
                    status.embedding.intendedMetric === "L2" ? "lower" : "higher"
                  } is closer. `
                : ""}
              Ranks the index's nearest {MAX_K.toLocaleString()}, so a canvas vertex outside that
              window has no score and matches neither direction.
            </p>
          </div>
        </Field>
      ) : (
        <div className="text-fg-faint" data-testid="interact-semantic-absent">
          {providerOn
            ? "No vector index is bound to an element embedding on this instance, so there is nothing to rank your elements against. Create one on the Indexes screen."
            : "Semantic filtering needs the embedding provider, which is off on this instance."}
        </div>
      )}

      <div className="text-fg-dim" data-testid="interact-count">
        {filtersActive ? "filtered" : "no filter"} ·{" "}
        {incomplete !== null ? (
          <span className="text-warn" data-testid="interact-incomplete">
            {incomplete}
          </span>
        ) : costly && !evaluated ? (
          <span className="text-warn">evaluate to match</span>
        ) : (
          <>
            {matchCount} of {nodeList.length} vertices match
          </>
        )}
        {evaluated && evaluated.unscored > 0 && (
          <span
            className="text-warn"
            data-testid="interact-unscored"
            title={help("interactSemanticUnscored")}
          >
            {" "}
            · {evaluated.unscored} had no score
          </span>
        )}
        {evaluated && evaluated.unreadable > 0 && (
          <span
            className="text-warn"
            data-testid="interact-unreadable"
            title={help("interactDegreeUnreadable")}
          >
            {" "}
            · {evaluated.unreadable} degree{evaluated.unreadable === 1 ? "" : "s"} unreadable
          </span>
        )}
      </div>

      {costly && (
        <div className="flex gap-2">
          <button
            type="button"
            className="btn btn-accent"
            data-testid="interact-preview"
            disabled={busy}
            onClick={() => preview.mutate()}
          >
            {preview.isPending ? "Evaluating…" : "Preview"}
          </button>
          {busy && (
            <button
              type="button"
              className="btn"
              data-testid="interact-cancel"
              onClick={() => {
                cancelledRef.current = true;
                previewControllerRef.current?.abort();
                expandControllerRef.current?.abort();
              }}
            >
              Cancel
            </button>
          )}
        </div>
      )}

      {progress && (
        <div className="text-fg-dim" data-testid="interact-progress">
          vertex {progress.done}/{progress.total}…
        </div>
      )}
      {overCap && (
        <div className="text-warn" data-testid="interact-over-cap">
          {overCap}
        </div>
      )}
      {failed && <ErrorBox error={preview.error ?? expand.error} />}

      {evaluated && evaluated.ids.length > 0 && (
        <div className="scroll-list" style={scrollRows(SCROLL_ROWS.default)}>
          {shown.map((id) => {
            const ref: ElementRef = { kind: "node", id };
            return (
              <div
                key={id}
                data-testid={`interact-match-${id}`}
                className="hover:bg-panel-2 flex items-center gap-1.5 px-1 py-0.5"
                onMouseEnter={() => onHover?.(ref)}
                onMouseLeave={() => onHover?.(null)}
              >
                <button
                  type="button"
                  className="text-accent-2 shrink-0 cursor-pointer hover:underline"
                  title="Show details below"
                  onClick={() => onSelect(ref)}
                  onFocus={() => onHover?.(ref)}
                  onBlur={() => onHover?.(null)}
                >
                  #{id}
                </button>
                <Truncated
                  text={canvasNodes[id]?.label ?? "—"}
                  max={DISPLAY_CAP.label}
                  className="text-fg-dim min-w-0 flex-1"
                />
              </div>
            );
          })}
          <ListCapNote shown={shown.length} total={total} />
        </div>
      )}

      <div className="flex flex-wrap gap-2 pt-1">
        <button
          type="button"
          className="btn btn-accent"
          data-testid="interact-expand"
          disabled={busy || matchCount === 0 || expandOverCap}
          onClick={() => expand.mutate()}
          title="Fetch one hop of edges and neighbors for every matched vertex and merge them into this view."
        >
          {expand.isPending ? "Expanding…" : `Expand (${matchCount})`}
        </button>
        <button
          type="button"
          className="btn btn-danger"
          data-testid="interact-remove"
          disabled={busy || matchCount === 0}
          onClick={remove}
          title="Take every matched vertex off this canvas, with the edges attached to it. Nothing is deleted from the database."
        >
          Remove from view ({matchCount})
        </button>
      </div>

      {expandOverCap && (
        <div className="text-warn" data-testid="interact-expand-over-cap">
          {matchCount} vertices is over the {EXPAND_SWEEP_CAP} one expand sweeps at once, because
          each one costs several requests. Narrow the filters and run it again.
        </div>
      )}

      {expandReport && (
        <div className="text-fg-dim" data-testid="interact-expand-report">
          {/* `expanded`, not `attempted`: a failure is not an expansion, so the two numbers cannot
              be allowed to sum past the total the way "3 of 3 · 1 failed" did. */}
          expanded {expandReport.expanded} of {expandReport.total} vertices
          {expandReport.failed > 0 && ` · ${expandReport.failed} failed`}
          {expandReport.cancelled && " · cancelled"}
          {expandReport.truncated > 0 && (
            <span className="text-warn">
              {" "}
              · {expandReport.truncated} had more than {EXPAND_EDGE_CAP} edges and were cut short
            </span>
          )}
          {expandReport.stoppedAtCeiling && (
            <span className="text-warn">
              {" "}
              · stopped at the {CANVAS_EXPAND_CEILING.toLocaleString()}-element canvas ceiling
            </span>
          )}
        </div>
      )}
    </div>
  );
}
