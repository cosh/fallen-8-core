// MIT License
//
// event-feed-panel.test.tsx
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

import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { EventFeedPanel } from "../src/components/EventFeedPanel";
import { getEventFeed, resetEventFeedsForTests } from "../src/state/eventFeed";
import { getInstanceStore, resetInstanceStoresForTests } from "../src/state/instanceStore";
import type { ChangeEvent } from "../src/api/changefeed";
import type { InstanceConfig } from "../src/instances/types";
import type { LiveFeedStatus } from "../src/state/liveFeed";

/**
 * The Events slide-over (feature studio-event-feed): raw buffer in, interest-filtered
 * view out - resync entries exempt from the filter, ids as InspectLinks, the honest
 * footer, and the copy-as-REST bridge to the server-side grammar.
 */

const instance: InstanceConfig = {
  id: "panel-a/ns1",
  name: "panel-a",
  baseUrl: "http://f8.test",
  auth: { kind: "none" },
  namespace: "ns1",
};

const ev = (partial: Partial<ChangeEvent> & Pick<ChangeEvent, "kind">): ChangeEvent => ({
  seq: 1,
  ts: "2026-08-01T12:00:00.000Z",
  ...partial,
});

/** Seeds oldest-to-newest, all as interest-matching (the buffer stores raw anyway). */
function seed(...events: ChangeEvent[]) {
  const feed = getEventFeed(instance.id);
  for (const event of events) feed.getState().record(event, true);
}

function renderPanel(
  props: Partial<{ status: LiveFeedStatus; open: boolean; onClose: () => void; onInspect: (id: number) => void }> = {},
) {
  return render(
    <QueryClientProvider client={new QueryClient()}>
      <EventFeedPanel
        instance={instance}
        status={props.status ?? "live"}
        open={props.open ?? true}
        onClose={props.onClose ?? (() => {})}
        onInspect={props.onInspect ?? (() => {})}
      />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  resetEventFeedsForTests();
  resetInstanceStoresForTests();
});

describe("event rows", () => {
  it("renders every kind with its payload fields, newest on top", () => {
    seed(
      ev({ seq: 1, kind: "vertexCreated", element: "vertex", id: 42, label: "person" }),
      ev({ seq: 2, kind: "propertySet", element: "vertex", id: 42, label: "person", key: "name" }),
      ev({ seq: 3, kind: "edgeCreated", element: "edge", id: 10, edgePropertyId: "knows", source: 42, target: 7 }),
      ev({ seq: 4, kind: "propertyRemoved", element: "edge", id: 10, key: "since" }),
      ev({ seq: 5, kind: "edgeRemoved", element: "edge", id: 10 }),
      ev({ seq: 6, kind: "vertexRemoved", element: "vertex", id: 7 }),
    );
    renderPanel();

    const list = screen.getByTestId("event-feed-list");
    const rows = [...list.querySelectorAll("li")];
    expect(rows).toHaveLength(6);
    // Newest first: the vertexRemoved (seq 6) leads.
    expect(rows[0]).toHaveTextContent("vertexRemoved");
    expect(rows[0]).toHaveTextContent("#6");

    const created = screen.getByTestId("feed-row-vertexCreated");
    expect(created).toHaveTextContent("vertex");
    expect(created).toHaveTextContent("#42");
    expect(created).toHaveTextContent("person");

    const propertySet = screen.getByTestId("feed-row-propertySet");
    expect(propertySet).toHaveTextContent("name"); // the key, never a value

    const edgeCreated = screen.getByTestId("feed-row-edgeCreated");
    expect(edgeCreated).toHaveTextContent("knows:");
    expect(edgeCreated).toHaveTextContent("#42");
    expect(edgeCreated).toHaveTextContent("#7");
  });

  it("renders a resync entry as a gap marker with the per-reason line", () => {
    seed(ev({ seq: 9, kind: "resync", reason: "seekOutOfRange" }));
    renderPanel();

    const row = screen.getByTestId("feed-row-resync");
    expect(row).toHaveTextContent("resync (seekOutOfRange)");
    expect(row).toHaveTextContent("events in between were not observed");
  });

  it("element ids are InspectLinks", () => {
    const onInspect = vi.fn();
    seed(ev({ seq: 1, kind: "vertexCreated", element: "vertex", id: 42 }));
    renderPanel({ onInspect });

    fireEvent.click(screen.getByText("#42"));
    expect(onInspect).toHaveBeenCalledWith(42);
  });
});

describe("the interest filter as a view", () => {
  it("unchecking a kind hides its rows instantly and the footer says so", () => {
    seed(
      ev({ seq: 1, kind: "vertexCreated", element: "vertex", id: 1 }),
      ev({ seq: 2, kind: "vertexRemoved", element: "vertex", id: 1 }),
    );
    renderPanel();
    expect(screen.getByTestId("feed-row-vertexCreated")).toBeInTheDocument();

    fireEvent.click(screen.getByTestId("feed-kind-vertexCreated"));

    expect(screen.queryByTestId("feed-row-vertexCreated")).toBeNull();
    expect(screen.getByTestId("feed-row-vertexRemoved")).toBeInTheDocument();
    expect(screen.getByText(/1 hidden by the filter/)).toBeInTheDocument();
  });

  it("resync entries stay visible under any filter (resync is always delivered)", () => {
    seed(
      ev({ seq: 1, kind: "vertexCreated", element: "vertex", id: 1 }),
      ev({ seq: 2, kind: "resync", reason: "overflow" }),
    );
    getInstanceStore(instance.id).getState().setFeedFilter({ kinds: [], elements: [] });
    renderPanel();

    expect(screen.queryByTestId("feed-row-vertexCreated")).toBeNull();
    expect(screen.getByTestId("feed-row-resync")).toBeInTheDocument();
  });

  it("a label chip narrows the view with exact matching", () => {
    seed(
      ev({ seq: 1, kind: "vertexCreated", element: "vertex", id: 1, label: "person" }),
      ev({ seq: 2, kind: "vertexCreated", element: "vertex", id: 2, label: "city" }),
      ev({ seq: 3, kind: "vertexCreated", element: "vertex", id: 3 }), // unlabeled
    );
    renderPanel();

    const input = screen.getByTestId("feed-labels");
    fireEvent.change(input, { target: { value: "person" } });
    fireEvent.keyDown(input, { key: "Enter" });

    const list = screen.getByTestId("event-feed-list");
    expect([...list.querySelectorAll("li")]).toHaveLength(1);
    expect(screen.getByText(/2 hidden by the filter/)).toBeInTheDocument();
    // The chip persists into the workspace store (survives a remount).
    expect(getInstanceStore(instance.id).getState().feedFilter.labels).toEqual(["person"]);
  });
});

describe("panel chrome", () => {
  it("shows the namespace, resets unread on open, and un-gates on unmount", () => {
    const feed = getEventFeed(instance.id);
    seed(ev({ seq: 1, kind: "vertexCreated", element: "vertex", id: 1 }));
    expect(feed.getState().unread).toBe(1);

    const { unmount } = renderPanel();
    expect(screen.getByTestId("event-feed-namespace")).toHaveTextContent("ns1");
    expect(feed.getState().unread).toBe(0); // visible = read
    expect(feed.getState().panelOpen).toBe(true);

    unmount();
    expect(feed.getState().panelOpen).toBe(false);
  });

  it("empty states distinguish 'no events yet' from 'nothing matches'", () => {
    const { unmount } = renderPanel();
    expect(screen.getByTestId("event-feed-empty")).toHaveTextContent(/No events yet/);
    unmount();

    seed(ev({ seq: 1, kind: "vertexCreated", element: "vertex", id: 1 }));
    getInstanceStore(instance.id).getState().setFeedFilter({ kinds: [] });
    renderPanel();
    expect(screen.getByTestId("event-feed-empty")).toHaveTextContent(/No buffered event matches/);
  });

  it("explains the disabled feed instead of showing a dead list", () => {
    renderPanel({ status: "unavailable" });
    expect(screen.getByTestId("event-feed-state")).toHaveTextContent(/disabled on this instance/);
  });

  it("the Close button closes", () => {
    const onClose = vi.fn();
    renderPanel({ onClose });
    fireEvent.click(screen.getByTestId("event-feed-close"));
    expect(onClose).toHaveBeenCalled();
  });
});

describe("copy as REST", () => {
  it("copies the filter as the equivalent /changefeed query, no credentials", async () => {
    const writeText = vi.fn<(text: string) => Promise<void>>(async () => {});
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });

    getInstanceStore(instance.id)
      .getState()
      .setFeedFilter({ kinds: ["vertexCreated", "propertySet"], labels: ["person"] });
    renderPanel();

    fireEvent.click(screen.getByTestId("event-feed-copy-rest"));

    // findBy waits inside act, covering the post-clipboard "copied" state flip.
    await screen.findByText("copied");
    expect(writeText).toHaveBeenCalledTimes(1);
    const url = writeText.mock.calls[0][0];
    expect(url).toBe(
      "http://f8.test/ns/ns1/changefeed?kinds=vertexCreated%2CpropertySet&labels=person",
    );
  });

  it("is disabled when the selection matches nothing (inexpressible over REST)", () => {
    getInstanceStore(instance.id).getState().setFeedFilter({ kinds: [] });
    renderPanel();
    expect(screen.getByTestId("event-feed-copy-rest")).toBeDisabled();
  });
});
