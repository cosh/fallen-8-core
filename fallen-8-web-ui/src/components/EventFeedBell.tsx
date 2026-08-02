// MIT License
//
// EventFeedBell.tsx
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

import { getEventFeed } from "../state/eventFeed";
import type { LiveFeedStatus } from "../state/liveFeed";

/**
 * The Events bell (feature studio-event-feed): the top-bar presence of the change feed.
 * Signals without being clicked - an interest-filtered unread count (display capped at
 * 99+), and a distinct warning treatment when a resync arrived unseen (continuity was
 * lost, which deserves more than a +1). Muted but clickable while the stream is not
 * live: the panel then explains the state instead of a dead-looking list.
 */

/** Unread numbers above this render as "99+" (the counter itself is uncapped). */
export const FEED_BADGE_MAX = 99;

export function EventFeedBell({
  scopeId,
  status,
  onOpen,
}: {
  /** The bound instance id ("<id>/<namespace>") naming the feed scope. */
  scopeId: string;
  status: LiveFeedStatus;
  onOpen: () => void;
}) {
  const feed = getEventFeed(scopeId);
  const unread = feed((s) => s.unread);
  const resyncSinceOpen = feed((s) => s.resyncSinceOpen);

  const display = unread > FEED_BADGE_MAX ? `${FEED_BADGE_MAX}+` : String(unread);
  const muted = status !== "live";

  const title = resyncSinceOpen
    ? "Events: continuity was lost (resync) - some events may be missing"
    : status === "unavailable"
      ? "Events: the change feed is disabled on this instance"
      : status === "live"
        ? unread > 0
          ? `Events: ${display} new matching your filter`
          : "Events: live, nothing new"
        : "Events: stream not connected";

  const tone = resyncSinceOpen
    ? "border-danger/50 text-danger"
    : unread > 0 && !muted
      ? "border-accent/40 text-accent"
      : "border-line text-fg-dim hover:text-fg";

  return (
    <button
      type="button"
      data-testid="event-feed-bell"
      aria-label={`Events${unread > 0 ? ` (${display} unread)` : ""}${
        resyncSinceOpen ? " (continuity lost)" : ""
      }`}
      title={title}
      onClick={onOpen}
      className={`flex cursor-pointer items-center gap-1 rounded border px-1.5 py-0.5 text-[10px] tracking-wider uppercase transition-colors ${tone} ${
        muted ? "opacity-60" : ""
      }`}
    >
      <span aria-hidden className="text-[12px] leading-none normal-case">
        ≋
      </span>
      {unread > 0 && <span data-testid="event-feed-badge">{display}</span>}
      {resyncSinceOpen && (
        <span aria-hidden data-testid="event-feed-resync-flag">
          !
        </span>
      )}
    </button>
  );
}
