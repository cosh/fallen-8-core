// MIT License
//
// TraverseScreen.tsx
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

import { useLayoutEffect, useRef } from "react";
import { useQuery } from "@tanstack/react-query";
import { useNavigate, useParams, useRouterState } from "@tanstack/react-router";
import { useInstanceStore } from "../instances/registry";
import { listStoredQueries } from "../api/endpoints";
import { selectStoredQuery } from "../lib/storedQueries";
import {
  isTraverseTab,
  TRAVERSE_TABS,
  traverseTabForKind,
  type TraverseTab,
} from "../state/instanceStore";
import { StoredQueriesPanel } from "../components/StoredQueriesPanel";
import { PathScreen } from "./PathScreen";
import { SubgraphScreen } from "./SubgraphScreen";

/**
 * Traverse (feature studio-traverse-merge): path finding and the subgraph builder under one
 * rail entry, plus the stored-query library they share.
 *
 * The two scenarios were near-twins: same filter-source toggle, same delegate slots, same
 * editor with its NL assist, and one `/storedquery` library per namespace that the Studio
 * nevertheless rendered as two kind-scoped panels, one per screen. Tabs keep the two dense
 * forms from ever stacking while making the third, unified library view possible.
 *
 * Panels stay MOUNTED and are hidden instead: a tab switch is not a screen change, so a path
 * result, an open advanced tier, or a builder message must survive it. (Instance/namespace
 * switches still reset everything, via the shell's remount key.)
 */

const TAB_LABELS: Record<TraverseTab, string> = {
  path: "Path finding",
  subgraph: "Subgraph builder",
  stored: "Stored queries",
};

export function TraverseScreen() {
  const { instance, store } = useInstanceStore();
  const tab = store((s) => s.traverseTab);
  const setTraverseTab = store((s) => s.setTraverseTab);
  const setPathDraft = store((s) => s.setPathDraft);
  const setSubgraphDraft = store((s) => s.setSubgraphDraft);
  const navigate = useNavigate();
  const { ns } = useParams({ strict: false }) as { ns?: string };
  // The LIVE location, not this match's validated search: a namespace or instance switch
  // remounts this screen (the shell keys the content on instance+namespace) before the router
  // commits the new match, so the match would still be carrying the PREVIOUS scope's ?tab= and
  // the effect below would adopt it into the new scope's store. `location` is already the new,
  // tab-less URL at that point. isTraverseTab repeats what validateSearch does, so reading the
  // raw value costs nothing.
  const searchTab = useRouterState({
    select: (s) => (s.location.search as { tab?: unknown }).tab,
  });

  /*
   * ONE render source - the persisted tab - with the URL as an input to it, not a rival:
   *
   * - Rendering off the store makes a click instant and independent of when the router lands.
   *   Deriving it from the URL instead would show the OLD tab until the navigation resolved,
   *   which is also what makes a mocked router untestable.
   * - The store is per instance-and-namespace, so it is what survives a context switch: those
   *   switchers rewrite the leaf and carry no search param (app/scopedRoute.ts), and the shell
   *   remounts this screen, so the new namespace opens on ITS remembered tab.
   * - `useLayoutEffect`, not `useEffect`: a deep link must not paint the default tab first.
   * - Adopting the URL only when it CHANGES (not whenever it differs) is what keeps a click
   *   from being reverted by the still-stale search param one render later.
   */
  const adopted = useRef<TraverseTab | undefined>(undefined);
  useLayoutEffect(() => {
    if (isTraverseTab(searchTab) && searchTab !== adopted.current) {
      adopted.current = searchTab;
      setTraverseTab(searchTab);
    }
  }, [searchTab, setTraverseTab]);

  // Shares the key (and therefore the fetch) with the library panel and both pickers; here it
  // only feeds the count in the tab label.
  const stored = useQuery({
    queryKey: [instance.id, "storedqueries"],
    queryFn: ({ signal }) => listStoredQueries(instance, signal),
  });

  const show = (next: TraverseTab) => {
    setTraverseTab(next);
    // Deep-linkable and back-button-free: the tab rides the URL, but as a REPLACE - flipping
    // tabs is not navigation history.
    if (ns) {
      void navigate({
        to: "/q/$ns/traverse",
        params: { ns },
        search: { tab: next },
        replace: true,
      });
    }
  };

  return (
    // Boxed to the widest tab panel (the subgraph builder's max-w-5xl) so the strip's rule ends
    // where the content does. Full-bleed, it ran a long way past the centered path form.
    <div className="mx-auto max-w-5xl space-y-4">
      <div className="border-line flex border-b" role="tablist">
        {TRAVERSE_TABS.map((id) => (
          <button
            key={id}
            type="button"
            role="tab"
            aria-selected={tab === id}
            data-testid={`traverse-tab-${id}`}
            className={`px-4 py-2 text-[11px] font-semibold tracking-wide uppercase ${
              tab === id ? "text-accent border-accent border-b-2" : "text-fg-dim hover:text-fg"
            }`}
            onClick={() => show(id)}
          >
            {TAB_LABELS[id]}
            {/* Absent while loading and on an error: a count is a fact, and "0" is a
                different claim than "not known yet". */}
            {id === "stored" && stored.data != null && (
              <span className="text-fg-faint ml-1.5 font-normal">{stored.data.length}</span>
            )}
          </button>
        ))}
      </div>

      <div role="tabpanel" aria-label={TAB_LABELS.path} hidden={tab !== "path"}>
        <PathScreen />
      </div>
      <div role="tabpanel" aria-label={TAB_LABELS.subgraph} hidden={tab !== "subgraph"}>
        <SubgraphScreen />
      </div>
      <div role="tabpanel" aria-label={TAB_LABELS.stored} hidden={tab !== "stored"}>
        {/* One library, one table (concept spec §5.3). "Use" selects the entry into its OWN
            scenario's picker and shows that tab - the entry names the scenario, so the
            operator never has to. */}
        <StoredQueriesPanel
          onUse={(entry) => {
            const target = traverseTabForKind(entry.kind);
            const patch = selectStoredQuery(entry.name!);
            if (target === "subgraph") setSubgraphDraft(patch);
            else setPathDraft(patch);
            show(target);
          }}
        />
      </div>
    </div>
  );
}
