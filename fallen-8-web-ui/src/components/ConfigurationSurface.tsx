// MIT License
//
// ConfigurationSurface.tsx
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

import { useEffect, useMemo, useState } from "react";
import * as Dialog from "@radix-ui/react-dialog";
import { useQuery } from "@tanstack/react-query";
import { usePortalContainer } from "../app/studioConfig";
import { ApiError } from "../api/client";
import { getChatModels } from "../api/endpoints";
import type { InstanceConfig } from "../instances/types";
import type {
  ChatModelREST,
  ChatProviderStatsREST,
  ObservabilityConfigREST,
  PendingRestartREST,
  SettingREST,
} from "../api/types";
import {
  CONFIG_FILTERS,
  CONFIG_GROUPS,
  CONFIG_SECTIONS,
  groupSettings,
  isEnvironmentLocked,
  matchesFilter,
  matchesQuery,
  type ConfigFilterId,
  type SectionGroup,
} from "../lib/configCatalog";
import { ErrorBox } from "./ErrorBox";
import { ObservabilitySection } from "./ObservabilitySection";
import { SettingRow, type SettingSuggestion } from "./SettingRow";

/**
 * The configuration surface (feature configuration-surface): one dialog holding everything this
 * instance binds, reached from the single Configure button on the Connect screen's configuration card.
 *
 * It exists because the card used to render all hundred-odd catalogued settings as ONE flat list,
 * inline, in a twelve-row window. Here they are sectioned, searchable and filterable, and one section
 * shows at a time.
 *
 * It deliberately owns NO server state: the draft, the mutation, the poll suspension and the lock
 * gating all stay on the card, which outlives this dialog. Two observers on the config query would let
 * react-query take the shortest refetch interval, and the card polling every ten seconds would replace
 * a value under a half-typed field here, which is exactly what the poll suspension exists to prevent.
 * What this component does own is where you are looking: the section, the query and the filter. Those
 * live under Dialog.Content, which Radix unmounts on close, so they reset without an effect.
 *
 * The ONE server read it does own is the chat model catalog (feature chat-model-catalog), because it
 * only makes sense once the operator has navigated to one particular section: it sits on its own cache
 * key, is never polled, fetches at most once per visit and unmounts with this subtree. The draft still
 * belongs to the card, so this cannot replace a value under a half-typed field.
 */

export interface ConfigurationSurfaceProps {
  open: boolean;
  onClose: () => void;
  /** Named in the header, because the settings behind it belong to one instance and not the next. */
  instanceName: string;
  /**
   * The instance the catalog read addresses (feature chat-model-catalog). A PROP rather than a
   * registry subscription: everything else here is prop-driven, and subscribing re-rendered this
   * subtree during an instance switch instead of letting it unmount, so it could paint one pass
   * addressing the new instance while every other prop still described the old one.
   */
  instance: InstanceConfig | null;
  settings: readonly SettingREST[];
  pendingRestart: readonly PendingRestartREST[];
  observability: ObservabilityConfigREST;
  draft: Record<string, string | null>;
  dirtyCount: number;
  onChange: (key: string, value: string) => void;
  onClear: (key: string) => void;
  onSave: () => void;
  saving: boolean;
  writeError: ApiError | Error | null;
  /** Whether the server accepts PATCH /config at all (both operator acts are in place). */
  writesAllowed: boolean;
  /** False when an embed host locked the instance, which gates the whole editable region. */
  editable: boolean;
  /** Per key, because the namespace lock narrows to one prefix rather than the whole surface. */
  isRowDisabled: (key: string) => boolean;
  /** A blanked numeric field the server would refuse the whole batch over, if there is one. */
  blankNumericKey?: string;
  /**
   * The chat gateway's RUNNING state (feature chat-model-catalog), absent or null when the instance
   * reports none. Two facts live only here: whether chat is on at all, and which backend is actually
   * serving. Neither is derivable from the inventory - Fallen8:Chat:Enabled is never-writable so its
   * value is withheld, and the Backend descriptor publishes the STORED value, which after a write is
   * the pending one. The picker follows what is running.
   */
  chat?: ChatProviderStatsREST | null;
}

export function ConfigurationSurface(props: ConfigurationSurfaceProps) {
  const portalContainer = usePortalContainer();
  return (
    <Dialog.Root open={props.open} onOpenChange={(open) => !open && props.onClose()}>
      <Dialog.Portal container={portalContainer}>
        <Dialog.Overlay className="modal-overlay" />
        {/* No Dialog.Description: the surface is a form, and Radix wants the opt-out stated rather
            than inferred (the shape EventFeedPanel already uses). */}
        <Dialog.Content
          aria-describedby={undefined}
          data-testid="config-surface"
          // 56rem rather than 48: at 48 a six-row section with group hints did not fit, which is not
          // just a screenshot problem - it is rows an operator has to scroll for on a screen that had
          // the space. 90vh still wins on a short window.
          className="panel modal-center flex h-[min(56rem,90vh)] w-[min(74rem,94vw)] flex-col overflow-hidden"
        >
          {/* Mounted UNDER Dialog.Content on purpose: Radix unmounts this subtree on close, which is
              what resets the section, the query and the filter. */}
          <SurfaceBody {...props} />
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

function SurfaceBody({
  onClose,
  instanceName,
  instance,
  settings,
  pendingRestart,
  observability,
  draft,
  dirtyCount,
  onChange,
  onClear,
  onSave,
  saving,
  writeError,
  writesAllowed,
  editable,
  isRowDisabled,
  blankNumericKey,
  chat,
}: ConfigurationSurfaceProps) {
  const [sectionId, setSectionId] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const [filter, setFilter] = useState<ConfigFilterId>("all");

  const sections = useMemo(() => withObservability(groupSettings(settings)), [settings]);
  const searching = query.trim().length > 0;

  // The count each nav entry shows is what its pane WOULD show, so a filter can never leave an
  // operator hunting through sections for rows that are not there. Null for a section with no
  // descriptors at all, which is only the injected Observability one: a "0" beside an entry whose
  // pane does have content would be a lie.
  const counts = useMemo(() => {
    const map = new Map<string, number | null>();
    for (const entry of sections) {
      map.set(
        entry.section.id,
        entry.settings.length === 0
          ? null
          : entry.settings.filter((s) => matchesQuery(s, query) && matchesFilter(s, filter)).length,
      );
    }
    return map;
  }, [sections, query, filter]);

  // Nothing is selected until the operator picks, or until the first section with something in it is
  // chosen for them: an empty pane as the landing state would read as a broken surface.
  const selected =
    sections.find((entry) => entry.section.id === sectionId) ??
    sections.find((entry) => (counts.get(entry.section.id) ?? 0) > 0) ??
    sections[0];

  // A search spans every section. One that silently searched only the selected pane would be a trap:
  // an operator who does not already know which section a key is in is exactly who is searching.
  const panes = searching
    ? sections.filter((entry) => (counts.get(entry.section.id) ?? 0) > 0)
    : selected
      ? [selected]
      : [];

  // The chat model picker (feature chat-model-catalog). One read fans out to the backend's own
  // catalog carrying the operator's credential, so it happens only where the answer is actionable:
  // chat is on, this instance accepts writes, and the row is one this operator could actually change
  // and can see. Merely opening this surface fetches nothing.
  const modelKey =
    chat?.enabled === true && chat.backend ? `Fallen8:Chat:${chat.backend}:Model` : null;
  // The row as the pane on screen actually RENDERS it, filter chip included, and not just as the
  // descriptor the instance published: with "not writable" selected the Chat pane shows only rows a
  // rule excludes, and a credentialed read whose answer nothing on screen could consume is precisely
  // the fan-out FR-4 forbids.
  const chatPane = panes.find((entry) => entry.section.id === "chat");
  const modelRow =
    modelKey === null
      ? undefined
      : chatPane?.settings.find((entry) => entry.key === modelKey && matchesFilter(entry, filter));
  // Null when there is no row to offer anything to. Every reason a row cannot be typed into is
  // checked, because a list of names beside a dead control is worse than no list.
  const pickerKey =
    modelRow !== undefined &&
    modelRow.kind === "string" &&
    modelRow.tier !== "notWritable" &&
    !isEnvironmentLocked(modelRow) &&
    writesAllowed &&
    editable &&
    !isRowDisabled(modelRow.key)
      ? modelRow.key
      : null;

  // One refusal per visit, per backend. An errored query holds no data, so react-query counts it
  // stale whatever staleTime says and fetches again the moment `enabled` flips back to true: without
  // this latch, every trip out of the Chat section and back would fan out to the operator's backend
  // again. Closing the surface unmounts this subtree, which is what clears it.
  const catalogKey = `${instance?.id ?? ""}|${chat?.backend ?? ""}`;
  const [refused, setRefused] = useState<string | null>(null);

  const catalog = useQuery({
    // Keyed by the RUNNING backend, so switching it (a restart) cannot offer the previous backend's
    // names out of the cache.
    queryKey: [instance?.id, "chatModels", chat?.backend ?? null],
    queryFn: ({ signal }) => getChatModels(instance!, signal),
    // Not searching, so the pane holding that row is the section the operator NAVIGATED to: a search
    // spans every section and matches a key, its rule and its reason, so one incidental character
    // ("d", typed at the Durability keys) puts the Chat pane on screen with no chat intent behind it.
    // Only the FETCH waits for that; names already in hand stay on offer while a search narrows the
    // pane, because offering them costs nothing.
    enabled: instance !== null && pickerKey !== null && !searching && refused !== catalogKey,
    // At most once per visit: staleTime keeps coming back to the section off the wire, and retry: 0
    // stops a refused read fanning out a second time (the client default retries once).
    retry: 0,
    staleTime: Infinity,
  });

  useEffect(() => {
    if (catalog.isError) {
      setRefused(catalogKey);
    }
  }, [catalog.isError, catalogKey]);

  const picker = useMemo<RowPicker | null>(() => {
    if (pickerKey === null) {
      return null;
    }
    if (catalog.isError) {
      return { key: pickerKey, note: catalogUnavailable(catalog.error) };
    }
    // Reached by SEARCH and never navigated to, so the fetch above was deliberately withheld. Say so
    // rather than rendering a bare field: search is the affordance for an operator who does NOT know
    // which section a key lives in, which is exactly the person hunting for this row, and a list that
    // vanishes with no reason given reads as a broken picker. Names already fetched in this visit are
    // not affected; they survive a search narrowing the pane.
    // `!isFetching` matters: a fetch triggered by NAVIGATING here is still in flight for a moment,
    // and a search typed during that window would otherwise tell an operator who is already in the
    // Chat section to open the Chat section.
    if (catalog.data === undefined && searching && !catalog.isFetching) {
      return { key: pickerKey, note: "Open the Chat section to load catalogued names; or type one." };
    }
    // Studio filters, the route does not (decision 8): an embedding model written here is a refusal
    // at the first completion. An UNKNOWN capability stays, because "the backend did not say" is not
    // "the backend said no", and the name may still be the right one.
    const offered = (catalog.data?.models ?? [])
      .filter((model) => model.capability !== "embedding")
      .map((model) => ({ value: model.name, label: modelOptionLabel(model) }));
    return offered.length > 0 ? { key: pickerKey, suggestions: offered } : null;
  }, [pickerKey, catalog.data, catalog.isError, catalog.error, catalog.isFetching, searching]);

  return (
    <>
      <div className="panel-title shrink-0">
        <Dialog.Title asChild>
          <span>Configuration</span>
        </Dialog.Title>
        <span className="text-fg-faint min-w-0 truncate normal-case" data-testid="config-surface-instance">
          {instanceName}
        </span>
        {/* In the HEADER, which never scrolls, and not in the footer where this used to be: every row
            below is disabled when the instance refuses writes, and a disabled row is a control that
            does nothing when clicked. The reason has to be where the rows are, not under them. */}
        {!writesAllowed && (
          <span className="border-warn/50 text-warn shrink-0 rounded border px-1.5 py-0.5 text-[10px] tracking-wider uppercase">
            read-only
          </span>
        )}
        <button type="button" className="btn ml-auto shrink-0 normal-case" onClick={onClose}>
          Close
        </button>
      </div>

      {!writesAllowed && (
        <div
          className="border-line text-fg-faint shrink-0 border-b px-3 py-1.5 text-[11px]"
          data-testid="config-read-only-note"
        >
          Every setting below is read-only: writes need an API key and
          Fallen8:Security:EnableConfigurationWrite.
        </div>
      )}

      {pendingRestart.length > 0 && (
        <div
          className="border-warn/50 text-warn m-2 max-h-24 shrink-0 overflow-y-auto rounded border p-2 text-[11px]"
          data-testid="config-pending-restart-detail"
        >
          {/* The count sentence is the card's job; this is the disclosure the card cannot fit. */}
          <div className="font-medium">waiting for a restart</div>
          <ul className="text-fg-dim mt-1 space-y-0.5">
            {pendingRestart.map((entry) => (
              <li key={entry.key}>
                <code className="text-[10px]">{entry.key}</code>: running{" "}
                <span className="text-fg">{entry.runningValue ?? "unset"}</span>, pending{" "}
                <span className="text-fg">{entry.pendingValue ?? "unset"}</span>
              </li>
            ))}
          </ul>
        </div>
      )}

      {/* No inventory means nothing to narrow, and a filter here could only ever subtract the one
          section that still has something to say. Hiding the controls is what keeps `filter` at "all"
          and `query` empty on such an instance. */}
      {settings.length > 0 && (
        <div className="border-line flex shrink-0 flex-wrap items-center gap-1.5 border-b p-2">
          <input
            className="input w-56 shrink-0"
            data-testid="config-search"
            type="search"
            placeholder="search keys and reasons"
            aria-label="search settings"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
          />
          {CONFIG_FILTERS.map((entry) => (
            <button
              key={entry.id}
              type="button"
              title={entry.title}
              aria-pressed={filter === entry.id}
              data-testid={`config-filter-${entry.id}`}
              className={filter === entry.id ? "btn btn-accent normal-case" : "btn normal-case"}
              onClick={() => setFilter(entry.id)}
            >
              {entry.label}
            </button>
          ))}
        </div>
      )}

      <div className="flex min-h-0 flex-1">
        {/* w-60 is the width at which the longest section label ("Chat and language model") fits
            beside its count without truncating. Narrower and the nav lies about what is in it. */}
        <nav
          className="border-line w-60 shrink-0 space-y-2 overflow-y-auto border-r p-2"
          data-testid="config-section-nav"
          aria-label="configuration sections"
        >
          {CONFIG_GROUPS.map((group) => {
            const entries = sections.filter((entry) => entry.section.group === group.id);
            if (entries.length === 0) {
              return null;
            }
            return (
              <div key={group.id}>
                <div className="text-fg-faint px-2 py-1 text-[10px] tracking-widest uppercase">
                  {group.label}
                </div>
                {entries.map((entry) => (
                  <NavEntry
                    key={entry.section.id}
                    entry={entry}
                    count={counts.get(entry.section.id) ?? null}
                    active={!searching && entry.section.id === selected?.section.id}
                    onSelect={() => {
                      setSectionId(entry.section.id);
                      setQuery("");
                    }}
                  />
                ))}
              </div>
            );
          })}
        </nav>

        <div className="min-w-0 flex-1 overflow-y-auto p-3" data-testid="config-section-pane">
          {settings.length === 0 && (
            <p className="text-fg-faint mb-3 text-[11px]" data-testid="config-no-inventory">
              This instance publishes no settings inventory, so nothing here can be changed.
            </p>
          )}
          {panes.length === 0 ? (
            <p className="text-fg-faint text-[12px]" data-testid="config-no-matches">
              Nothing matches. The search covers a setting's key, its exclusion rule and the reason it
              is excluded; it cannot match what a key does, because the instance publishes no
              description for one.
            </p>
          ) : (
            <div className="space-y-6">
              {panes.map((entry) => (
                <SectionPane
                  key={entry.section.id}
                  entry={entry}
                  query={query}
                  filter={filter}
                  observability={observability}
                  draft={draft}
                  onChange={onChange}
                  onClear={onClear}
                  isRowDisabled={isRowDisabled}
                  picker={picker}
                />
              ))}
            </div>
          )}
        </div>
      </div>

      <div className="border-line flex shrink-0 items-center gap-3 border-t p-2">
        {writeError && (
          <div className="min-w-0 flex-1" data-testid="config-settings-error">
            <ErrorBox error={writeError} />
          </div>
        )}
        {editable && dirtyCount > 0 && (
          <button
            type="button"
            className="btn-accent btn ml-auto shrink-0 normal-case"
            data-testid="config-save"
            disabled={saving || blankNumericKey !== undefined}
            title={
              blankNumericKey !== undefined
                ? `${blankNumericKey} is empty: type a number, or discard the edit`
                : undefined
            }
            onClick={onSave}
          >
            {saving ? "saving…" : `Save ${dirtyCount}`}
          </button>
        )}
      </div>
    </>
  );
}

/**
 * What the model picker adds to exactly ONE row: the names to offer, or the one line saying why there
 * are none. ONE object, because SettingRow is memoised and both halves have to be stable references
 * across a keystroke in any other row.
 */
type RowPicker = { key: string; suggestions?: readonly SettingSuggestion[]; note?: string };

/**
 * What is known about a catalogued model, shown beside its name: the backend's own class string
 * (verbatim, since it carries no published legend) and whether a worker can serve it right now.
 * Undefined when the backend said neither, because an empty label reads as a rendering fault.
 */
function modelOptionLabel(model: ChatModelREST): string | undefined {
  const known: string[] = [];
  if (model.class) {
    known.push(model.class);
  }
  if (model.available === true) {
    known.push("warm");
  } else if (model.available === false) {
    known.push("cold start");
  }
  return known.length > 0 ? known.join(" · ") : undefined;
}

/**
 * Why there is no list, in one line. Names the reason the instance gave and nothing else: ApiError.body
 * is already the problem+json detail, which this route is required to keep free of any endpoint value.
 * Typing is never blocked, so the line ends with what to do rather than with the failure.
 */
function catalogUnavailable(error: unknown): string {
  const reason =
    error instanceof ApiError
      ? error.body || `HTTP ${error.status}`
      : error instanceof Error && error.message
        ? error.message
        : "the instance did not answer";
  return `No model list to offer (${reason}); type the name.`;
}

const OBSERVABILITY_SECTION = CONFIG_SECTIONS.find((section) => section.id === "observability")!;

/**
 * Guarantees an Observability entry even when the instance publishes no settings inventory at all.
 * That section reads its values off the observability block rather than off descriptors, so it has
 * something to say on an older server, and it is what this surface absorbed when the standalone
 * observability dialog folded in. Without this, upgrading Studio would take the exporter view away
 * from exactly the instances that cannot show anything else.
 */
function withObservability(sections: SectionGroup[]): SectionGroup[] {
  if (sections.some((entry) => entry.section.id === OBSERVABILITY_SECTION.id)) {
    return sections;
  }
  const injected: SectionGroup = { section: OBSERVABILITY_SECTION, settings: [], groups: [] };
  const at = CONFIG_SECTIONS.indexOf(OBSERVABILITY_SECTION);
  const before = sections.filter((entry) => CONFIG_SECTIONS.indexOf(entry.section) < at);
  return [...before, injected, ...sections.slice(before.length)];
}

function NavEntry({
  entry,
  count,
  active,
  onSelect,
}: {
  entry: SectionGroup;
  /** Null when the section publishes no descriptors, so no number is shown rather than a false zero. */
  count: number | null;
  active: boolean;
  onSelect: () => void;
}) {
  return (
    <button
      type="button"
      aria-current={active ? "true" : undefined}
      title={entry.section.blurb}
      data-testid={`config-section-${entry.section.id}`}
      className={`flex w-full items-center gap-2 rounded px-2 py-1 text-left text-[12px] ${
        active ? "bg-panel-2 text-accent" : "text-fg-dim hover:text-fg"
      }`}
      onClick={onSelect}
    >
      <span className="min-w-0 truncate">{entry.section.label}</span>
      {count !== null && (
        <span className={`ml-auto shrink-0 text-[11px] ${count === 0 ? "text-fg-faint/50" : "text-fg-faint"}`}>
          {count}
        </span>
      )}
    </button>
  );
}

function SectionPane({
  entry,
  query,
  filter,
  observability,
  draft,
  onChange,
  onClear,
  isRowDisabled,
  picker,
}: {
  entry: SectionGroup;
  query: string;
  filter: ConfigFilterId;
  observability: ObservabilityConfigREST;
  draft: Record<string, string | null>;
  onChange: (key: string, value: string) => void;
  onClear: (key: string) => void;
  isRowDisabled: (key: string) => boolean;
  /** For at most one row in the whole surface, whichever pane that row lands in. */
  picker: RowPicker | null;
}) {
  const searching = query.trim().length > 0;
  // A search flattens the sub-groups: their headers are orientation within a section, and under a
  // query the matches are the point. A filter keeps them, because it narrows rather than re-orders.
  const groups = searching
    ? [
        {
          label: "",
          settings: entry.settings.filter((s) => matchesQuery(s, query) && matchesFilter(s, filter)),
        },
      ]
    : entry.groups
        .map((group) => ({ label: group.label, settings: group.settings.filter((s) => matchesFilter(s, filter)) }))
        .filter((group) => group.settings.length > 0);
  const shown = groups.reduce((total, group) => total + group.settings.length, 0);

  // Observability keeps its hand-authored layout, which explains what its keys are for and shows the
  // effective value of the three whose descriptor withholds it. A query or a filter falls back to the
  // generic rows, so searching and filtering behave the same in every section.
  // A section with NO descriptors keeps this layout whatever the filter says, because the generic row
  // path would render nothing at all for it. Reachable with an inventory too: an instance can publish
  // settings and none of them under Fallen8:Observability.
  const custom =
    entry.section.id === "observability" && !searching && (filter === "all" || entry.settings.length === 0);

  return (
    <section>
      <div className="mb-1 flex items-baseline gap-2">
        <h3 className="text-fg text-[12px] font-bold tracking-wide uppercase">{entry.section.label}</h3>
        {entry.settings.length > 0 && (
          <span className="text-fg-faint text-[11px]" data-testid={`config-filter-count-${entry.section.id}`}>
            {shown === entry.settings.length
              ? `${entry.settings.length} settings`
              : `${shown} of ${entry.settings.length} settings`}
          </span>
        )}
      </div>
      <p className="text-fg-dim mb-3 text-[11px]">{entry.section.blurb}</p>

      {custom ? (
        <ObservabilitySection
          observability={observability}
          settings={entry.settings}
          draft={draft}
          onChange={onChange}
          onClear={onClear}
          isRowDisabled={isRowDisabled}
        />
      ) : (
        groups.map((group) => (
          <div key={group.label || "(direct)"} className="mb-3 last:mb-0">
            {group.label && (
              <div className="text-accent mb-1 text-[10px] font-bold tracking-widest uppercase">
                {group.label}
              </div>
            )}
            {group.settings.map((setting) => (
              <SettingRow
                key={setting.key}
                setting={setting}
                draft={draft[setting.key]}
                disabled={isRowDisabled(setting.key)}
                onChange={onChange}
                onClear={onClear}
                suggestions={picker?.key === setting.key ? picker.suggestions : undefined}
                note={picker?.key === setting.key ? picker.note : undefined}
              />
            ))}
          </div>
        ))
      )}
    </section>
  );
}
