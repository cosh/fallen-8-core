// MIT License
//
// feed-filter.test.ts
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

import { describe, expect, it } from "vitest";
import { buildChangeFeedQuery, type ChangeEvent } from "../src/api/changefeed";
import {
  DEFAULT_FEED_FILTER,
  ELEMENT_EVENT_KINDS,
  isExpressibleAsRest,
  matchesFilter,
  toChangeFeedFilter,
  type FeedFilterDraft,
} from "../src/state/feedFilter";

/**
 * Server-parity pinning (feature studio-event-feed): the interest filter applies the
 * EXACT semantics of the GET /changefeed grammar (change-feed spec §3.4) client-side -
 * AND across dimensions, OR within one, exact case-sensitive matches, an unlabeled
 * element never matches `labels`, only property events carry a key, and `resync` is
 * always delivered. Divergence here would teach users the wrong grammar.
 */

const ev = (partial: Partial<ChangeEvent> & Pick<ChangeEvent, "kind">): ChangeEvent => ({
  seq: 1,
  ts: "2026-08-01T12:00:00.000Z",
  ...partial,
});

const filter = (partial: Partial<FeedFilterDraft>): FeedFilterDraft => ({
  ...DEFAULT_FEED_FILTER,
  ...partial,
});

describe("matchesFilter: server-parity semantics", () => {
  it("the default filter is the wildcard: every event matches, labeled or not", () => {
    for (const kind of ELEMENT_EVENT_KINDS) {
      expect(matchesFilter(ev({ kind, element: "vertex", id: 1 }), DEFAULT_FEED_FILTER)).toBe(true);
    }
    expect(matchesFilter(ev({ kind: "vertexCreated", element: "vertex", id: 1 }), DEFAULT_FEED_FILTER)).toBe(true);
  });

  it("kinds: only enabled kinds match (OR within the dimension)", () => {
    const f = filter({ kinds: ["vertexCreated", "edgeCreated"] });
    expect(matchesFilter(ev({ kind: "vertexCreated", element: "vertex", id: 1 }), f)).toBe(true);
    expect(matchesFilter(ev({ kind: "edgeCreated", element: "edge", id: 2 }), f)).toBe(true);
    expect(matchesFilter(ev({ kind: "vertexRemoved", element: "vertex", id: 1 }), f)).toBe(false);
    expect(matchesFilter(ev({ kind: "propertySet", element: "vertex", id: 1, key: "k" }), f)).toBe(false);
  });

  it("kinds: an empty selection matches no element event", () => {
    const f = filter({ kinds: [] });
    for (const kind of ELEMENT_EVENT_KINDS) {
      expect(matchesFilter(ev({ kind, element: "vertex", id: 1 }), f)).toBe(false);
    }
  });

  it("elements: restricting to vertex excludes edge events and vice versa", () => {
    const vertexOnly = filter({ elements: ["vertex"] });
    expect(matchesFilter(ev({ kind: "vertexCreated", element: "vertex", id: 1 }), vertexOnly)).toBe(true);
    expect(matchesFilter(ev({ kind: "edgeCreated", element: "edge", id: 2 }), vertexOnly)).toBe(false);

    const edgeOnly = filter({ elements: ["edge"] });
    expect(matchesFilter(ev({ kind: "propertySet", element: "edge", id: 2, key: "k" }), edgeOnly)).toBe(true);
    expect(matchesFilter(ev({ kind: "propertySet", element: "vertex", id: 1, key: "k" }), edgeOnly)).toBe(false);
  });

  it("labels: exact and case-sensitive; an unlabeled element never matches", () => {
    const f = filter({ labels: ["person"] });
    expect(matchesFilter(ev({ kind: "vertexCreated", element: "vertex", id: 1, label: "person" }), f)).toBe(true);
    expect(matchesFilter(ev({ kind: "vertexCreated", element: "vertex", id: 1, label: "Person" }), f)).toBe(false);
    expect(matchesFilter(ev({ kind: "vertexCreated", element: "vertex", id: 1 }), f)).toBe(false);
  });

  it("labels: OR within the dimension", () => {
    const f = filter({ labels: ["person", "city"] });
    expect(matchesFilter(ev({ kind: "vertexCreated", element: "vertex", id: 1, label: "city" }), f)).toBe(true);
    expect(matchesFilter(ev({ kind: "vertexCreated", element: "vertex", id: 1, label: "country" }), f)).toBe(false);
  });

  it("keys: only property events carry a key, so a keys filter hides creates/removes", () => {
    const f = filter({ keys: ["name"] });
    expect(matchesFilter(ev({ kind: "propertySet", element: "vertex", id: 1, key: "name" }), f)).toBe(true);
    expect(matchesFilter(ev({ kind: "propertyRemoved", element: "vertex", id: 1, key: "name" }), f)).toBe(true);
    expect(matchesFilter(ev({ kind: "propertySet", element: "vertex", id: 1, key: "age" }), f)).toBe(false);
    // The REST caveat, verbatim: creates and removes have no key and never match.
    expect(matchesFilter(ev({ kind: "vertexCreated", element: "vertex", id: 1 }), f)).toBe(false);
    expect(matchesFilter(ev({ kind: "edgeRemoved", element: "edge", id: 2 }), f)).toBe(false);
  });

  it("dimensions combine with AND", () => {
    const f = filter({ kinds: ["propertySet"], labels: ["person"], keys: ["name"] });
    expect(
      matchesFilter(ev({ kind: "propertySet", element: "vertex", id: 1, label: "person", key: "name" }), f),
    ).toBe(true);
    // Right kind + key, wrong (absent) label.
    expect(matchesFilter(ev({ kind: "propertySet", element: "vertex", id: 1, key: "name" }), f)).toBe(false);
    // Right kind + label, wrong key.
    expect(
      matchesFilter(ev({ kind: "propertySet", element: "vertex", id: 1, label: "person", key: "age" }), f),
    ).toBe(false);
  });

  it("resync is exempt: always matches, even an all-empty filter", () => {
    const f = filter({ kinds: [], elements: [], labels: ["x"], keys: ["y"] });
    expect(matchesFilter(ev({ kind: "resync", reason: "overflow" }), f)).toBe(true);
  });
});

describe("toChangeFeedFilter: the wire mapping for copy-as-REST", () => {
  it("the default (wildcard) filter maps to no parameters at all", () => {
    const wire = toChangeFeedFilter(DEFAULT_FEED_FILTER);
    expect(wire).toEqual({ kinds: undefined, elements: undefined, labels: undefined, keys: undefined });
    expect(buildChangeFeedQuery(wire)).toEqual({
      kinds: undefined,
      elements: undefined,
      labels: undefined,
      keys: undefined,
      since: undefined,
    });
  });

  it("a subset renders in canonical order regardless of toggle order", () => {
    const wire = toChangeFeedFilter(filter({ kinds: ["propertySet", "vertexCreated"] }));
    expect(wire.kinds).toEqual(["vertexCreated", "propertySet"]);
    expect(buildChangeFeedQuery(wire).kinds).toBe("vertexCreated,propertySet");
  });

  it("labels and keys pass through exactly; empty means wildcard (omitted)", () => {
    const wire = toChangeFeedFilter(filter({ labels: ["person", "city"], keys: [] }));
    expect(buildChangeFeedQuery(wire).labels).toBe("person,city");
    expect(buildChangeFeedQuery(wire).keys).toBeUndefined();
  });

  it("a single element type renders; both is the wildcard", () => {
    expect(toChangeFeedFilter(filter({ elements: ["edge"] })).elements).toEqual(["edge"]);
    expect(toChangeFeedFilter(filter({ elements: ["edge", "vertex"] })).elements).toBeUndefined();
  });
});

describe("isExpressibleAsRest", () => {
  it("the REST grammar has wildcards but no match-nothing", () => {
    expect(isExpressibleAsRest(DEFAULT_FEED_FILTER)).toBe(true);
    expect(isExpressibleAsRest(filter({ kinds: ["vertexCreated"] }))).toBe(true);
    expect(isExpressibleAsRest(filter({ kinds: [] }))).toBe(false);
    expect(isExpressibleAsRest(filter({ elements: [] }))).toBe(false);
  });
});
