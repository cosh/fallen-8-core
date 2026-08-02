// MIT License
//
// event-feed.test.ts
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

import { beforeEach, describe, expect, it } from "vitest";
import type { ChangeEvent } from "../src/api/changefeed";
import {
  EVENT_FEED_CAPACITY,
  getEventFeed,
  resetEventFeedsForTests,
} from "../src/state/eventFeed";
import {
  migrateInstanceStore,
  purgeAllInstanceStores,
  purgeInstanceStore,
  resetInstanceStoresForTests,
} from "../src/state/instanceStore";

/**
 * The Events panel's buffer semantics (feature studio-event-feed): a raw ring of the
 * newest 100 events per instance + namespace, unread accounting gated on the panel
 * being closed, and a session lifecycle that follows the workspace store's blast
 * radius (purge on drop/recreate, move on rename) - never localStorage.
 */

const ev = (seq: number, partial: Partial<ChangeEvent> = {}): ChangeEvent => ({
  seq,
  ts: "2026-08-01T12:00:00.000Z",
  kind: "vertexCreated",
  element: "vertex",
  id: seq,
  ...partial,
});

beforeEach(() => {
  resetEventFeedsForTests();
  resetInstanceStoresForTests();
});

describe("event feed buffer", () => {
  it("stores raw events newest first", () => {
    const feed = getEventFeed("a");
    feed.getState().record(ev(1), true);
    feed.getState().record(ev(2), false); // buffered even when not matching interest
    const { entries } = feed.getState();
    expect(entries.map((e) => e.event.seq)).toEqual([2, 1]);
  });

  it("caps at the ring capacity, dropping the oldest", () => {
    const feed = getEventFeed("a");
    for (let seq = 1; seq <= EVENT_FEED_CAPACITY + 5; seq++) {
      feed.getState().record(ev(seq), true);
    }
    const { entries } = feed.getState();
    expect(entries).toHaveLength(EVENT_FEED_CAPACITY);
    expect(entries[0].event.seq).toBe(EVENT_FEED_CAPACITY + 5);
    expect(entries[entries.length - 1].event.seq).toBe(6);
  });

  it("entry keys are stable and unique even when seq repeats (epoch change)", () => {
    const feed = getEventFeed("a");
    feed.getState().record(ev(1), true);
    feed.getState().record(ev(1), true);
    const { entries } = feed.getState();
    expect(entries[0].key).not.toBe(entries[1].key);
  });

  it("unread counts only interest-matching element events, only while closed", () => {
    const feed = getEventFeed("a");
    feed.getState().record(ev(1), true);
    feed.getState().record(ev(2), false);
    expect(feed.getState().unread).toBe(1);

    feed.getState().setPanelOpen(true);
    feed.getState().record(ev(3), true); // visible = read: no accrual while open
    expect(feed.getState().unread).toBe(0);

    feed.getState().setPanelOpen(false);
    feed.getState().record(ev(4), true);
    expect(feed.getState().unread).toBe(1);
  });

  it("a resync raises the distinct flag instead of counting", () => {
    const feed = getEventFeed("a");
    feed.getState().record(ev(1, { kind: "resync", reason: "overflow", element: undefined, id: undefined }), false);
    expect(feed.getState().unread).toBe(0);
    expect(feed.getState().resyncSinceOpen).toBe(true);
    // The entry itself is buffered - the panel lists it as a gap marker.
    expect(feed.getState().entries[0].event.kind).toBe("resync");
  });

  it("a resync while the panel is open does not flag: the user saw it", () => {
    const feed = getEventFeed("a");
    feed.getState().setPanelOpen(true);
    feed.getState().record(ev(1, { kind: "resync", reason: "trim", element: undefined, id: undefined }), false);
    expect(feed.getState().resyncSinceOpen).toBe(false);
  });

  it("opening the panel resets unread and the resync flag; closing does not", () => {
    const feed = getEventFeed("a");
    feed.getState().record(ev(1), true);
    feed.getState().record(ev(2, { kind: "resync", reason: "overflow" }), false);
    expect(feed.getState().unread).toBe(1);
    expect(feed.getState().resyncSinceOpen).toBe(true);

    feed.getState().setPanelOpen(true);
    expect(feed.getState().unread).toBe(0);
    expect(feed.getState().resyncSinceOpen).toBe(false);
    expect(feed.getState().entries).toHaveLength(2); // the buffer itself stays

    feed.getState().setPanelOpen(false);
    expect(feed.getState().unread).toBe(0);
  });

  it("clear resets buffer, unread, flag AND the catch-up position", () => {
    const feed = getEventFeed("a");
    feed.getState().record(ev(1), true);
    feed.getState().setLastEventId("epoch:1");
    feed.getState().clear();
    expect(feed.getState().entries).toEqual([]);
    expect(feed.getState().unread).toBe(0);
    expect(feed.getState().lastEventId).toBeNull();
  });
});

describe("event feed scoping and lifecycle", () => {
  it("scopes per instance + namespace, with 'default' collapsing onto the bare id", () => {
    expect(getEventFeed("a/default")).toBe(getEventFeed("a"));
    expect(getEventFeed("a", "default")).toBe(getEventFeed("a"));
    expect(getEventFeed("a", "other")).not.toBe(getEventFeed("a"));
    expect(getEventFeed("a", "other")).toBe(getEventFeed("a/other"));
    expect(getEventFeed("a", "other")).not.toBe(getEventFeed("b", "other"));
  });

  it("purgeInstanceStore clears the namespace's feed (drop/recreate blast radius)", () => {
    const feed = getEventFeed("a", "x");
    feed.getState().record(ev(1), true);
    feed.getState().setLastEventId("epoch:1");

    purgeInstanceStore("a", "x");

    // A component still holding the old store sees it cleared, and a fresh lookup
    // starts empty - a stale `since` against the successor would be meaningless.
    expect(feed.getState().entries).toEqual([]);
    expect(feed.getState().lastEventId).toBeNull();
    expect(getEventFeed("a", "x").getState().entries).toEqual([]);
  });

  it("purgeAllInstanceStores clears every namespace's feed of the instance", () => {
    getEventFeed("a").getState().record(ev(1), true);
    getEventFeed("a", "x").getState().record(ev(2), true);
    getEventFeed("b").getState().record(ev(3), true);

    purgeAllInstanceStores("a");

    expect(getEventFeed("a").getState().entries).toEqual([]);
    expect(getEventFeed("a", "x").getState().entries).toEqual([]);
    expect(getEventFeed("b").getState().entries).toHaveLength(1); // untouched
  });

  it("migrateInstanceStore moves the feed to the renamed namespace", () => {
    const feed = getEventFeed("a", "from");
    feed.getState().record(ev(7), true);
    feed.getState().setLastEventId("epoch:7");

    migrateInstanceStore("a", "from", "to");

    // Rename keeps the graph and its feed epoch/sequence: buffer + position follow.
    expect(getEventFeed("a", "to").getState().entries[0].event.seq).toBe(7);
    expect(getEventFeed("a", "to").getState().lastEventId).toBe("epoch:7");
    expect(getEventFeed("a", "from").getState().entries).toEqual([]);
  });
});
