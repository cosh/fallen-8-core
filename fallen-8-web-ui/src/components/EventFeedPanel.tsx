// MIT License
//
// EventFeedPanel.tsx
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

import { useEffect, useState } from "react";
import * as Dialog from "@radix-ui/react-dialog";
import {
  buildChangeFeedQuery,
  type ChangeEventKind,
  type ResyncReason,
} from "../api/changefeed";
import { buildUrl, scopedPath } from "../api/client";
import type { InstanceConfig } from "../instances/types";
import { EVENT_FEED_CAPACITY, getEventFeed, type FeedEntry } from "../state/eventFeed";
import {
  ELEMENT_EVENT_KINDS,
  ELEMENT_TYPES,
  isExpressibleAsRest,
  matchesFilter,
  toChangeFeedFilter,
} from "../state/feedFilter";
import { getInstanceStore } from "../state/instanceStore";
import { shapeSuggestions, useGraphShape } from "../state/graphShape";
import type { LiveFeedStatus } from "../state/liveFeed";
import { Field } from "./Field";
import { help } from "../lib/fieldHelp";
import { InspectLink } from "./InspectLink";
import { usePortalContainer } from "../app/studioConfig";

/**
 * The Events panel (feature studio-event-feed): a right slide-over on the house Radix
 * Dialog pattern showing the newest {@link EVENT_FEED_CAPACITY} raw change-feed events
 * of the ACTIVE namespace, filtered client-side by the persisted interest filter whose
 * vocabulary IS the REST grammar (see feedFilter.ts). Rows are history, not live state:
 * an InspectLink may point at a since-removed element - the Browser answers "not found"
 * honestly. Payloads are metadata-only by design, so rows never show property values;
 * the link is how you reach them.
 */

const KIND_GLYPHS: Record<ChangeEventKind, string> = {
  vertexCreated: "+",
  edgeCreated: "+",
  vertexRemoved: "×",
  edgeRemoved: "×",
  propertySet: "✎",
  propertyRemoved: "⌫",
  resync: "⟳",
};

const KIND_TONES: Record<ChangeEventKind, string> = {
  vertexCreated: "text-accent",
  edgeCreated: "text-accent",
  vertexRemoved: "text-danger",
  edgeRemoved: "text-danger",
  propertySet: "text-fg-dim",
  propertyRemoved: "text-fg-dim",
  resync: "text-danger",
};

/** One honest line per resync reason (change-feed spec §3.3). */
const RESYNC_LINES: Record<ResyncReason, string> = {
  trim: "the graph was compacted; element ids from before this point may be invalid",
  tabulaRasa: "the graph was replaced; element ids from before this point are invalid",
  load: "a save game was loaded; element ids from before this point are invalid",
  delegateWrite: "a compiled delegate wrote directly; its changes were not itemized",
  overflow: "the stream fell behind; events were missed",
  seekOutOfRange: "the catch-up position was no longer buffered; events in between were not observed",
};

function relativeTime(then: number, now: number): string {
  const s = Math.max(0, Math.round((now - then) / 1000));
  if (s < 5) return "just now";
  if (s < 60) return `${s}s ago`;
  const m = Math.round(s / 60);
  if (m < 60) return `${m}m ago`;
  const h = Math.round(m / 60);
  if (h < 24) return `${h}h ago`;
  return `${Math.round(h / 24)}d ago`;
}

/** An entry's display instant: the commit timestamp when present, else client receipt. */
function entryInstant(entry: FeedEntry): number {
  const parsed = entry.event.ts ? Date.parse(entry.event.ts) : Number.NaN;
  return Number.isNaN(parsed) ? entry.receivedAt : parsed;
}

/** Chips + free-text input; Enter or comma adds, suggestions feed a datalist. */
function ChipInput({
  id,
  values,
  onChange,
  suggestions,
  placeholder,
}: {
  id: string;
  values: string[];
  onChange: (values: string[]) => void;
  suggestions: string[];
  placeholder: string;
}) {
  const [text, setText] = useState("");

  const add = (raw: string) => {
    const value = raw.trim();
    if (value && !values.includes(value)) onChange([...values, value]);
    setText("");
  };

  return (
    <div>
      <div className="flex flex-wrap items-center gap-1">
        {values.map((value) => (
          <span
            key={value}
            className="border-line bg-panel-2 text-fg flex items-center gap-1 rounded border px-1.5 py-0.5 text-[11px]"
          >
            {value}
            <button
              type="button"
              aria-label={`Remove ${value}`}
              className="text-fg-faint hover:text-danger cursor-pointer"
              onClick={() => onChange(values.filter((v) => v !== value))}
            >
              ×
            </button>
          </span>
        ))}
        <input
          id={id}
          data-testid={id}
          className="input min-w-24 flex-1"
          list={`${id}-suggestions`}
          value={text}
          placeholder={values.length === 0 ? placeholder : ""}
          onChange={(e) => {
            // A comma commits mid-typing; a datalist pick lands as plain text and
            // commits like typed text does, on Enter or blur.
            if (e.target.value.endsWith(",")) add(e.target.value.slice(0, -1));
            else setText(e.target.value);
          }}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              add(text);
            }
          }}
          onBlur={() => add(text)}
        />
      </div>
      <datalist id={`${id}-suggestions`}>
        {suggestions
          .filter((s) => !values.includes(s))
          .map((s) => (
            <option key={s} value={s} />
          ))}
      </datalist>
    </div>
  );
}

function EventRow({
  entry,
  now,
  onInspect,
}: {
  entry: FeedEntry;
  now: number;
  onInspect: (id: number) => void;
}) {
  const { event } = entry;
  const instant = entryInstant(entry);
  const time = (
    <span
      className="text-fg-faint shrink-0 text-[10px]"
      title={event.ts ?? new Date(entry.receivedAt).toISOString()}
    >
      {relativeTime(instant, now)}
    </span>
  );
  const seq = (
    <span className="text-fg-faint ml-auto shrink-0 font-mono text-[10px]">#{event.seq}</span>
  );

  if (event.kind === "resync") {
    const reason = event.reason ?? "overflow";
    return (
      <li
        data-testid="feed-row-resync"
        className="border-danger/40 bg-danger/5 border-b px-3 py-1.5 text-[11px]"
      >
        <div className="flex items-baseline gap-1.5">
          <span aria-hidden className="text-danger">
            {KIND_GLYPHS.resync}
          </span>
          <span className="text-danger">resync ({reason})</span>
          {seq}
          {time}
        </div>
        <div className="text-fg-dim mt-0.5">{RESYNC_LINES[reason]}</div>
      </li>
    );
  }

  return (
    <li data-testid={`feed-row-${event.kind}`} className="border-line/60 border-b px-3 py-1.5 text-[12px]">
      <div className="flex items-baseline gap-1.5">
        <span aria-hidden className={KIND_TONES[event.kind]}>
          {KIND_GLYPHS[event.kind]}
        </span>
        <span className="text-fg">{event.kind}</span>
        {event.element !== undefined && event.id !== undefined && (
          <span className="text-fg-dim">
            {event.element} <InspectLink id={event.id} onInspect={onInspect} />
          </span>
        )}
        {event.label !== undefined && (
          <span className="border-line text-fg-dim rounded border px-1 text-[10px]">
            {event.label}
          </span>
        )}
        {event.key !== undefined && (
          <span className="text-fg-dim font-mono text-[11px]">{event.key}</span>
        )}
        {seq}
        {time}
      </div>
      {event.kind === "edgeCreated" &&
        event.source !== undefined &&
        event.target !== undefined && (
          <div className="text-fg-dim mt-0.5 text-[11px]">
            {event.edgePropertyId !== undefined && (
              <span className="font-mono">{event.edgePropertyId}: </span>
            )}
            <InspectLink id={event.source} onInspect={onInspect} /> →{" "}
            <InspectLink id={event.target} onInspect={onInspect} />
          </div>
        )}
    </li>
  );
}

export function EventFeedPanel({
  instance,
  status,
  open,
  onClose,
  onInspect,
}: {
  /** The namespace-BOUND instance view (compound id) the feed streams for. */
  instance: InstanceConfig;
  status: LiveFeedStatus;
  open: boolean;
  onClose: () => void;
  onInspect: (id: number) => void;
}) {
  const feed = getEventFeed(instance.id);
  const entries = feed((s) => s.entries);
  const store = getInstanceStore(instance.id);
  const filter = store((s) => s.feedFilter);
  const setFeedFilter = store((s) => s.setFeedFilter);

  // Visible = read: opening resets the bell, and nothing accrues while open.
  useEffect(() => {
    feed.getState().setPanelOpen(open);
    return () => feed.getState().setPanelOpen(false);
  }, [feed, open]);

  // Relative times stay honest while the panel sits open on a quiet graph.
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!open) return;
    setNow(Date.now());
    const timer = setInterval(() => setNow(Date.now()), 30_000);
    return () => clearInterval(timer);
  }, [open]);

  const [copyState, setCopyState] = useState<"idle" | "copied" | "failed">("idle");
  useEffect(() => {
    if (copyState === "idle") return;
    const timer = setTimeout(() => setCopyState("idle"), 2_000);
    return () => clearTimeout(timer);
  }, [copyState]);

  // Label/key suggestions ride the Graph shape snapshot when one has been computed;
  // free typing always works (the snapshot is never fetched from here).
  const shape = useGraphShape(instance);
  const suggestions = shapeSuggestions(shape.data);
  const labelSuggestions = [
    ...new Set([...suggestions.vertexLabels, ...suggestions.edgeLabels]),
  ];

  const visible = entries.filter((entry) => matchesFilter(entry.event, filter));
  const hidden = entries.length - visible.length;

  const restUrl = buildUrl(
    instance.baseUrl,
    scopedPath(instance, "/changefeed"),
    buildChangeFeedQuery(toChangeFeedFilter(filter)),
  );
  const expressible = isExpressibleAsRest(filter);

  // navigator.clipboard exists only in secure contexts; a plain-HTTP deployment (the
  // documented self-hosted posture) must fail VISIBLY, not throw into the void.
  const copyRestUrl = async () => {
    try {
      if (!navigator.clipboard) throw new Error("clipboard unavailable");
      await navigator.clipboard.writeText(restUrl);
      setCopyState("copied");
    } catch {
      setCopyState("failed");
    }
  };

  const toggle = <T,>(values: T[], value: T): T[] =>
    values.includes(value) ? values.filter((v) => v !== value) : [...values, value];

  const portalContainer = usePortalContainer();
  const stateLine =
    status === "live"
      ? "Streaming committed changes from this namespace."
      : status === "connecting"
        ? "Connecting to the change feed…"
        : status === "unavailable"
          ? "The change feed is disabled on this instance (Fallen8:ChangeFeed:Enabled); live updates fall back to polling."
          : "The change feed stream is not running; check the instance connection.";

  return (
    <Dialog.Root open={open} onOpenChange={(o) => !o && onClose()}>
      <Dialog.Portal container={portalContainer}>
        <Dialog.Overlay className="modal-overlay" />
        <Dialog.Content
          data-testid="event-feed-panel"
          className="panel modal-right flex flex-col"
          aria-describedby={undefined}
        >
          <div className="border-line flex items-center gap-2 border-b px-3 py-2">
            <Dialog.Title className="text-fg-dim text-[11px] font-semibold tracking-widest uppercase">
              Events
            </Dialog.Title>
            <span data-testid="event-feed-namespace" className="text-fg text-[12px]">
              {instance.namespace ?? "default"}
            </span>
            <Dialog.Close asChild>
              <button
                type="button"
                className="btn ml-auto"
                data-testid="event-feed-close"
                aria-label="Close the events panel"
              >
                Close
              </button>
            </Dialog.Close>
          </div>

          <div data-testid="event-feed-state" className="text-fg-faint px-3 py-1.5 text-[11px]">
            {stateLine}
          </div>

          <div className="border-line space-y-2 border-b px-3 pb-2">
            <div title={help("feedKinds")}>
              <span className="label label-help">kinds</span>
              <div className="grid grid-cols-2 gap-x-2 gap-y-0.5">
                {ELEMENT_EVENT_KINDS.map((kind) => (
                  <label key={kind} className="text-fg-dim flex items-center gap-1 text-[11px]">
                    <input
                      type="checkbox"
                      data-testid={`feed-kind-${kind}`}
                      checked={filter.kinds.includes(kind)}
                      onChange={() => setFeedFilter({ kinds: toggle(filter.kinds, kind) })}
                    />
                    {kind}
                  </label>
                ))}
              </div>
            </div>
            <div title={help("feedElements")}>
              <span className="label label-help">elements</span>
              <div className="flex gap-3">
                {ELEMENT_TYPES.map((element) => (
                  <label key={element} className="text-fg-dim flex items-center gap-1 text-[11px]">
                    <input
                      type="checkbox"
                      data-testid={`feed-element-${element}`}
                      checked={filter.elements.includes(element)}
                      onChange={() =>
                        setFeedFilter({ elements: toggle(filter.elements, element) })
                      }
                    />
                    {element}
                  </label>
                ))}
              </div>
            </div>
            <Field helpKey="feedLabels" label="labels" htmlFor="feed-labels">
              <ChipInput
                id="feed-labels"
                values={filter.labels}
                onChange={(labels) => setFeedFilter({ labels })}
                suggestions={labelSuggestions}
                placeholder="any label"
              />
            </Field>
            <Field helpKey="feedKeys" label="keys" htmlFor="feed-keys">
              <ChipInput
                id="feed-keys"
                values={filter.keys}
                onChange={(keys) => setFeedFilter({ keys })}
                suggestions={suggestions.propertyKeys}
                placeholder="any property key"
              />
            </Field>
          </div>

          <ul data-testid="event-feed-list" className="min-h-0 flex-1 overflow-y-auto">
            {visible.length === 0 && (
              <li data-testid="event-feed-empty" className="text-fg-dim px-3 py-4 text-[12px]">
                {entries.length === 0
                  ? "No events yet: committed changes to this namespace appear here live, from any client (another tab, curl, an MCP agent)."
                  : "No buffered event matches the filter."}
              </li>
            )}
            {visible.map((entry) => (
              <EventRow key={entry.key} entry={entry} now={now} onInspect={onInspect} />
            ))}
          </ul>

          <div className="border-line flex items-center gap-2 border-t px-3 py-1.5">
            <span className="text-fg-faint text-[11px]">
              Newest {EVENT_FEED_CAPACITY} events; older ones fall off.
              {hidden > 0 && ` ${hidden} hidden by the filter.`}
            </span>
            <button
              type="button"
              data-testid="event-feed-copy-rest"
              className="btn ml-auto shrink-0"
              disabled={!expressible}
              title={
                expressible
                  ? help("feedCopyRest")
                  : "An empty kinds/elements selection matches nothing and cannot be expressed as a REST filter."
              }
              onClick={() => void copyRestUrl()}
            >
              {copyState === "copied" ? "copied" : copyState === "failed" ? "copy failed" : "copy as REST"}
            </button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
