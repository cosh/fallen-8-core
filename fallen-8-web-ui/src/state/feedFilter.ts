// MIT License
//
// feedFilter.ts
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

import type {
  ChangeElementType,
  ChangeEvent,
  ChangeFeedFilter,
} from "../api/changefeed";

/**
 * The Events panel's interest filter (feature studio-event-feed): the configuration
 * vocabulary IS the REST filter grammar - same dimensions, same semantics - applied
 * client-side over the raw event buffer (the panel rides the one shared stream, it
 * never opens a second filtered subscription). Pinned to server parity by tests:
 * AND across dimensions, OR within one, exact case-sensitive matches, an unlabeled
 * element never matches a `labels` filter, and only property events carry a key (so a
 * `keys` filter hides creates/removes). `resync` is exempt - always shown, mirroring
 * "resync is always delivered".
 *
 * The draft persists per instance + namespace inside the workspace store
 * (instanceStore.ts) - a lasting preference, unlike the session-only event buffer.
 */

export const ELEMENT_EVENT_KINDS = [
  "vertexCreated",
  "vertexRemoved",
  "edgeCreated",
  "edgeRemoved",
  "propertySet",
  "propertyRemoved",
] as const;

export type ElementEventKind = (typeof ELEMENT_EVENT_KINDS)[number];

export const ELEMENT_TYPES = ["vertex", "edge"] as const satisfies readonly ChangeElementType[];

/**
 * Checkbox semantics: `kinds`/`elements` hold the ENABLED values (all enabled = the
 * wildcard); `labels`/`keys` hold exact values (empty = the wildcard). An empty
 * `kinds`/`elements` selection matches nothing - expressible here, not over REST
 * (see {@link toChangeFeedFilter}).
 */
export interface FeedFilterDraft {
  kinds: ElementEventKind[];
  elements: ChangeElementType[];
  labels: string[];
  keys: string[];
}

export const DEFAULT_FEED_FILTER: FeedFilterDraft = {
  kinds: [...ELEMENT_EVENT_KINDS],
  elements: [...ELEMENT_TYPES],
  labels: [],
  keys: [],
};

/** Server-parity event matching; see the module doc for the pinned semantics. */
export function matchesFilter(event: ChangeEvent, filter: FeedFilterDraft): boolean {
  if (event.kind === "resync") return true;
  if (!filter.kinds.includes(event.kind as ElementEventKind)) return false;
  if (event.element !== undefined && !filter.elements.includes(event.element)) {
    return false;
  }
  if (
    filter.labels.length > 0 &&
    (event.label === undefined || !filter.labels.includes(event.label))
  ) {
    return false;
  }
  if (
    filter.keys.length > 0 &&
    (event.key === undefined || !filter.keys.includes(event.key))
  ) {
    return false;
  }
  return true;
}

/**
 * Whether the draft is expressible as a `GET /changefeed` query: the REST grammar has
 * wildcards but no "match nothing", so an empty kinds/elements selection is not.
 */
export function isExpressibleAsRest(filter: FeedFilterDraft): boolean {
  return filter.kinds.length > 0 && filter.elements.length > 0;
}

/**
 * The draft as the wire filter, wildcard dimensions omitted and kinds/elements in
 * canonical order - feed it to buildChangeFeedQuery for the copy-as-REST string.
 */
export function toChangeFeedFilter(filter: FeedFilterDraft): ChangeFeedFilter {
  return {
    kinds:
      filter.kinds.length === ELEMENT_EVENT_KINDS.length
        ? undefined
        : ELEMENT_EVENT_KINDS.filter((kind) => filter.kinds.includes(kind)),
    elements:
      filter.elements.length === ELEMENT_TYPES.length
        ? undefined
        : ELEMENT_TYPES.filter((element) => filter.elements.includes(element)),
    labels: filter.labels.length > 0 ? filter.labels : undefined,
    keys: filter.keys.length > 0 ? filter.keys : undefined,
  };
}
