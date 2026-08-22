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

import { useMemo, useState } from "react";
import * as Dialog from "@radix-ui/react-dialog";
import { usePortalContainer } from "../app/studioConfig";
import type { ApiError } from "../api/client";
import type { ObservabilityConfigREST, PendingRestartREST, SettingREST } from "../api/types";
import {
  CONFIG_FILTERS,
  CONFIG_GROUPS,
  CONFIG_SECTIONS,
  groupSettings,
  matchesFilter,
  matchesQuery,
  type ConfigFilterId,
  type SectionGroup,
} from "../lib/configCatalog";
import { ErrorBox } from "./ErrorBox";
import { ObservabilitySection } from "./ObservabilitySection";
import { SettingRow } from "./SettingRow";

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
 */

export interface ConfigurationSurfaceProps {
  open: boolean;
  onClose: () => void;
  /** Named in the header, because the settings behind it belong to one instance and not the next. */
  instanceName: string;
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
          className="panel modal-center flex h-[min(48rem,90vh)] w-[min(74rem,94vw)] flex-col overflow-hidden"
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

  return (
    <>
      <div className="panel-title shrink-0">
        <Dialog.Title asChild>
          <span>Configuration</span>
        </Dialog.Title>
        <span className="text-fg-faint min-w-0 truncate normal-case" data-testid="config-surface-instance">
          {instanceName}
        </span>
        <button type="button" className="btn ml-auto shrink-0 normal-case" onClick={onClose}>
          Close
        </button>
      </div>

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
                    count={counts.get(entry.section.id) ?? 0}
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
              This instance publishes no settings inventory, so nothing here can be changed. What it is
              exporting is still readable below.
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
                />
              ))}
            </div>
          )}
        </div>
      </div>

      <div className="border-line flex shrink-0 items-center gap-3 border-t p-2">
        {!writesAllowed && (
          <span className="text-fg-faint text-[11px]">
            read-only: writes need an API key and Fallen8:Security:EnableConfigurationWrite
          </span>
        )}
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
}: {
  entry: SectionGroup;
  query: string;
  filter: ConfigFilterId;
  observability: ObservabilityConfigREST;
  draft: Record<string, string | null>;
  onChange: (key: string, value: string) => void;
  onClear: (key: string) => void;
  isRowDisabled: (key: string) => boolean;
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
  const custom = entry.section.id === "observability" && !searching && filter === "all";

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
              />
            ))}
          </div>
        ))
      )}
    </section>
  );
}
