// MIT License
//
// eventFeed.ts
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

import { create, type UseBoundStore, type StoreApi } from "zustand";
import type { ChangeEvent } from "../api/changefeed";
import { scopeKey } from "./scopeKey";

/**
 * Per-namespace event-feed state (feature studio-event-feed): the newest
 * {@link EVENT_FEED_CAPACITY} RAW change-feed events of one namespace, the bell's unread
 * accounting, and the catch-up position (`since`). Deliberately NOT persisted: there is
 * no history read API, so a reload starting empty is honest - replaying a stale buffer
 * would fake a history the server does not serve. The interest FILTER, in contrast, is
 * a lasting preference and persists in the workspace store (see feedFilter.ts).
 *
 * Events are stored unfiltered so a filter change is a pure view re-evaluation: instant,
 * no network, and it can reveal already-observed events the previous filter hid.
 */

export const EVENT_FEED_CAPACITY = 100;

export interface FeedEntry {
  /** Stable list key: `seq` alone can repeat after an epoch change or a replay. */
  key: number;
  event: ChangeEvent;
  /** Client receipt time - the timestamp fallback for events without a usable `ts`. */
  receivedAt: number;
}

export interface EventFeedState {
  /** Newest first, capped at {@link EVENT_FEED_CAPACITY}; includes resync entries. */
  entries: FeedEntry[];
  /** Interest-matching events observed while the panel was closed. */
  unread: number;
  /** A resync arrived while the panel was closed: continuity was lost unseen. */
  resyncSinceOpen: boolean;
  panelOpen: boolean;
  /** Last seen SSE `id:` (`epoch:seq`) - the `since` position for a resubscribe. */
  lastEventId: string | null;

  /**
   * Buffers one event. Unread accrues only while the panel is closed (visible = read):
   * a resync raises the distinct flag instead of counting; element events count when
   * they match the interest filter (the caller evaluates the filter - this store stays
   * filter-agnostic).
   */
  record: (event: ChangeEvent, matchesInterest: boolean) => void;
  setLastEventId: (id: string) => void;
  /** Opening resets unread and the resync flag; closing just flips the gate back on. */
  setPanelOpen: (open: boolean) => void;
  /** Full reset (buffer, unread, flag, catch-up position) - the namespace was recreated. */
  clear: () => void;
}

let entryCounter = 0;

function createEventFeedStore() {
  return create<EventFeedState>()((set) => ({
    entries: [],
    unread: 0,
    resyncSinceOpen: false,
    panelOpen: false,
    lastEventId: null,

    record: (event, matchesInterest) =>
      set((s) => ({
        entries: [
          { key: ++entryCounter, event, receivedAt: Date.now() },
          ...s.entries,
        ].slice(0, EVENT_FEED_CAPACITY),
        unread:
          !s.panelOpen && event.kind !== "resync" && matchesInterest
            ? s.unread + 1
            : s.unread,
        resyncSinceOpen:
          !s.panelOpen && event.kind === "resync" ? true : s.resyncSinceOpen,
      })),

    setLastEventId: (lastEventId) => set({ lastEventId }),

    setPanelOpen: (open) =>
      set(open ? { panelOpen: true, unread: 0, resyncSinceOpen: false } : { panelOpen: false }),

    clear: () =>
      set({ entries: [], unread: 0, resyncSinceOpen: false, lastEventId: null }),
  }));
}

type EventFeedStore = UseBoundStore<StoreApi<EventFeedState>>;

const feeds = new Map<string, EventFeedStore>();

/** Returns the one feed belonging to this instance id + namespace (memoized). */
export function getEventFeed(instanceId: string, namespace?: string): EventFeedStore {
  const key = scopeKey(instanceId, namespace);
  let feed = feeds.get(key);
  if (!feed) {
    feed = createEventFeedStore();
    feeds.set(key, feed);
  }
  return feed;
}

/**
 * Drops a namespace's feed - called alongside instanceStore.purgeInstanceStore when the
 * namespace is dropped or recreated in place: the buffered events describe a dead graph,
 * and a stale `since` against its successor's feed would be meaningless.
 */
export function purgeEventFeed(instanceId: string, namespace?: string): void {
  const key = scopeKey(instanceId, namespace);
  // Clear before dropping the map entry: a mounted component may still hold the store.
  feeds.get(key)?.getState().clear();
  feeds.delete(key);
}

/** Drops EVERY namespace's feed of one instance (the factory-reset blast radius). */
export function purgeAllEventFeeds(instanceId: string): void {
  for (const [key, feed] of [...feeds.entries()]) {
    if (key === instanceId || key.startsWith(`${instanceId}/`)) {
      feed.getState().clear();
      feeds.delete(key);
    }
  }
}

/**
 * Follows a namespace rename: the graph (and its feed epoch/sequence) is unchanged, so
 * the buffer and the catch-up position stay valid under the new address.
 */
export function migrateEventFeed(instanceId: string, from: string, to: string): void {
  const fromKey = scopeKey(instanceId, from);
  const toKey = scopeKey(instanceId, to);
  if (fromKey === toKey) return;
  const feed = feeds.get(fromKey);
  // A displaced destination store (unreachable today: rename targets cannot exist) is
  // cleared like a purge, so a component still holding it never shows stale entries.
  feeds.get(toKey)?.getState().clear();
  feeds.delete(fromKey);
  feeds.delete(toKey);
  if (feed) feeds.set(toKey, feed);
}

/** Test hook: drop all memoized feeds. */
export function resetEventFeedsForTests(): void {
  feeds.clear();
}
