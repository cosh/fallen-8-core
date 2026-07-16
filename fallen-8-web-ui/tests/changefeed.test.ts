import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import openapi from "../../features/done/web-ui/openapi-v0.1.json";
import {
  SseParser,
  buildChangeFeedQuery,
  parseChangeEvent,
  streamChanges,
  type ChangeEvent,
  type SseFrame,
  type StreamCloseReason,
} from "../src/api/changefeed";
import type { InstanceConfig } from "../src/instances/types";

/**
 * Change feed client (feature change-feed, spec §3.4/§3.5/§3.7): SSE wire-format
 * parsing, the filter -> query grammar, and the streamChanges loop's contract -
 * reconnect with since=<last id>, exponential backoff, quiet 503 degradation, and
 * NEVER a credential in the query string.
 */

// ---- SSE parser ----

function collect(): { frames: SseFrame[]; parser: SseParser } {
  const frames: SseFrame[] = [];
  const parser = new SseParser((f) => frames.push(f));
  return { frames, parser };
}

const EVENT_FRAME =
  'id: aaaa:42\nevent: vertexCreated\ndata: {"seq":42,"ts":"t","kind":"vertexCreated","element":"vertex","id":7,"label":"person"}\n\n';

describe("SSE parser", () => {
  it("parses id/event/data frames", () => {
    const { frames, parser } = collect();
    parser.push(EVENT_FRAME);
    expect(frames).toHaveLength(1);
    expect(frames[0].id).toBe("aaaa:42");
    expect(frames[0].event).toBe("vertexCreated");
    expect(JSON.parse(frames[0].data)).toMatchObject({ seq: 42, kind: "vertexCreated" });
  });

  it("ignores comment lines (keepalive) and stray blank separators", () => {
    const { frames, parser } = collect();
    parser.push(": keepalive\n\n");
    parser.push("\n\n");
    parser.push(": keepalive\n\n" + EVENT_FRAME + ": keepalive\n\n");
    expect(frames).toHaveLength(1);
    expect(frames[0].event).toBe("vertexCreated");
  });

  it("parses multiple events arriving in one chunk", () => {
    const { frames, parser } = collect();
    parser.push(
      'id: e:1\nevent: vertexCreated\ndata: {"seq":1}\n\n' +
        'id: e:2\nevent: edgeCreated\ndata: {"seq":2}\n\n',
    );
    expect(frames.map((f) => f.id)).toEqual(["e:1", "e:2"]);
    expect(frames.map((f) => f.event)).toEqual(["vertexCreated", "edgeCreated"]);
  });

  it("reassembles frames split at arbitrary chunk boundaries", () => {
    // Feed the exact same bytes one character at a time - the harshest split.
    const { frames, parser } = collect();
    for (const char of EVENT_FRAME + EVENT_FRAME) parser.push(char);
    expect(frames).toHaveLength(2);
    expect(frames[0]).toEqual(frames[1]);
    expect(frames[0].id).toBe("aaaa:42");
    expect(JSON.parse(frames[0].data).id).toBe(7);
  });

  it("joins multiple data lines with newlines per the SSE spec", () => {
    const { frames, parser } = collect();
    parser.push("data: line1\ndata: line2\n\n");
    expect(frames[0].data).toBe("line1\nline2");
  });

  it("handles CRLF line endings, including a CR/LF pair split across chunks", () => {
    const { frames, parser } = collect();
    parser.push("id: e:9\r\nevent: resync\r");
    parser.push('\ndata: {"seq":9}\r\n\r\n');
    expect(frames).toHaveLength(1);
    expect(frames[0]).toEqual({ id: "e:9", event: "resync", data: '{"seq":9}' });
  });

  it("ignores unknown fields and fields without a colon", () => {
    const { frames, parser } = collect();
    parser.push("retry: 3000\nweird\ndata: {}\n\n");
    expect(frames).toHaveLength(1);
    expect(frames[0].data).toBe("{}");
  });
});

describe("change event decoding", () => {
  it("decodes the spec §3.3 JSON shape", () => {
    const event = parseChangeEvent(
      '{"seq":4712,"ts":"2026-07-15T12:34:56.789Z","kind":"propertySet","element":"vertex","id":42,"label":"person","key":"name"}',
    );
    expect(event).toEqual({
      seq: 4712,
      ts: "2026-07-15T12:34:56.789Z",
      kind: "propertySet",
      element: "vertex",
      id: 42,
      label: "person",
      key: "name",
    });
  });

  it("decodes edgeCreated endpoints and resync reasons", () => {
    expect(
      parseChangeEvent('{"seq":1,"ts":"t","kind":"edgeCreated","element":"edge","id":3,"source":7,"target":9}'),
    ).toMatchObject({ source: 7, target: 9 });
    expect(parseChangeEvent('{"seq":2,"ts":"t","kind":"resync","reason":"trim"}')).toMatchObject({
      kind: "resync",
      reason: "trim",
    });
  });

  it("rejects malformed payloads instead of throwing", () => {
    expect(parseChangeEvent("not json")).toBeNull();
    expect(parseChangeEvent('"a string"')).toBeNull();
    expect(parseChangeEvent('{"kind":"vertexCreated"}')).toBeNull(); // no seq
    expect(parseChangeEvent("null")).toBeNull();
  });
});

// ---- filter -> query grammar ----

describe("filter to query-param mapping", () => {
  it("maps each dimension to a comma-separated parameter", () => {
    expect(
      buildChangeFeedQuery(
        {
          kinds: ["vertexCreated", "vertexRemoved"],
          elements: ["vertex"],
          labels: ["person", "company"],
          keys: ["name"],
        },
        "e:42",
      ),
    ).toEqual({
      kinds: "vertexCreated,vertexRemoved",
      elements: "vertex",
      labels: "person,company",
      keys: "name",
      since: "e:42",
    });
  });

  it("omits unset dimensions entirely (unset = wildcard, never an empty parameter)", () => {
    expect(buildChangeFeedQuery(undefined, undefined)).toEqual({
      kinds: undefined,
      elements: undefined,
      labels: undefined,
      keys: undefined,
      since: undefined,
    });
    expect(buildChangeFeedQuery({ labels: [] })).toMatchObject({ labels: undefined });
  });

  it("is a contract route: GET /changefeed exists in the pinned OpenAPI snapshot", () => {
    const paths = (openapi as { paths: Record<string, Record<string, unknown>> }).paths;
    expect(paths["/changefeed"]).toBeDefined();
    expect(Object.keys(paths["/changefeed"])).toContain("get");
  });
});

// ---- streamChanges loop ----

const instance: InstanceConfig = {
  id: "t",
  name: "test",
  baseUrl: "http://f8.test",
  auth: { kind: "apiKey", key: "s3cret", header: "X-Api-Key" },
};

interface MockStream {
  response: Response;
  emit: (text: string) => void;
  end: () => void;
}

/** A hand-controlled SSE response whose stream also honours the fetch abort signal. */
function sseResponse(signal?: AbortSignal | null): MockStream {
  let controller!: ReadableStreamDefaultController<Uint8Array>;
  const stream = new ReadableStream<Uint8Array>({
    start(c) {
      controller = c;
    },
  });
  let open = true;
  const fail = () => {
    if (!open) return;
    open = false;
    controller.error(new DOMException("The operation was aborted.", "AbortError"));
  };
  signal?.addEventListener("abort", fail, { once: true });
  const encoder = new TextEncoder();
  return {
    response: new Response(stream, {
      status: 200,
      headers: { "Content-Type": "text/event-stream" },
    }),
    emit: (text) => controller.enqueue(encoder.encode(text)),
    end: () => {
      if (!open) return;
      open = false;
      controller.close();
    },
  };
}

interface RecordedRequest {
  url: URL;
  headers: Record<string, string>;
}

describe("streamChanges", () => {
  let requests: RecordedRequest[];

  beforeEach(() => {
    requests = [];
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function stubFetch(handler: (call: number, init?: RequestInit) => Response | MockStream) {
    vi.stubGlobal(
      "fetch",
      vi.fn(async (url: string, init?: RequestInit) => {
        requests.push({
          url: new URL(url),
          headers: (init?.headers ?? {}) as Record<string, string>,
        });
        if (init?.signal?.aborted) {
          throw new DOMException("The operation was aborted.", "AbortError");
        }
        const result = handler(requests.length, init);
        return result instanceof Response ? result : result.response;
      }),
    );
  }

  it("carries auth in headers and never puts the key in the query string", async () => {
    const abort = new AbortController();
    let stream!: MockStream;
    stubFetch((_, init) => (stream = sseResponse(init?.signal)));

    const done = streamChanges(instance, {
      signal: abort.signal,
      filter: { labels: ["person"] },
      onEvent: () => {},
    });
    await vi.waitFor(() => expect(requests).toHaveLength(1));

    expect(requests[0].url.pathname).toBe("/changefeed");
    expect(requests[0].url.searchParams.get("labels")).toBe("person");
    expect(requests[0].headers["X-Api-Key"]).toBe("s3cret");
    expect(requests[0].url.href).not.toContain("s3cret");

    abort.abort();
    void stream;
    await done;
  });

  it("routes element events to onEvent and resync events exclusively to onResync", async () => {
    const abort = new AbortController();
    let stream!: MockStream;
    stubFetch((_, init) => (stream = sseResponse(init?.signal)));

    const events: ChangeEvent[] = [];
    const resyncs: ChangeEvent[] = [];
    const done = streamChanges(instance, {
      signal: abort.signal,
      onEvent: (e) => events.push(e),
      onResync: (e) => resyncs.push(e),
    });
    await vi.waitFor(() => expect(requests).toHaveLength(1));

    stream.emit('id: e:1\nevent: vertexCreated\ndata: {"seq":1,"ts":"t","kind":"vertexCreated","element":"vertex","id":7}\n\n');
    stream.emit(": keepalive\n\n");
    stream.emit('id: e:2\nevent: resync\ndata: {"seq":2,"ts":"t","kind":"resync","reason":"overflow"}\n\n');

    await vi.waitFor(() => expect(resyncs).toHaveLength(1));
    expect(events).toHaveLength(1);
    expect(events[0]).toMatchObject({ kind: "vertexCreated", id: 7 });
    expect(resyncs[0]).toMatchObject({ kind: "resync", reason: "overflow" });

    abort.abort();
    await done;
  });

  it("reconnects with since=<last seen id> after a dropped stream (catch-up contract)", async () => {
    const abort = new AbortController();
    const streams: MockStream[] = [];
    stubFetch((_, init) => {
      const s = sseResponse(init?.signal);
      streams.push(s);
      return s;
    });

    const opened: number[] = [];
    const done = streamChanges(instance, {
      signal: abort.signal,
      since: "e:100",
      initialBackoffMs: 1,
      onEvent: () => {},
      onOpen: () => opened.push(Date.now()),
    });
    await vi.waitFor(() => expect(requests).toHaveLength(1));
    // The FIRST request carries the caller's since.
    expect(requests[0].url.searchParams.get("since")).toBe("e:100");

    streams[0].emit('id: e:101\nevent: vertexCreated\ndata: {"seq":101,"ts":"t","kind":"vertexCreated","element":"vertex","id":1}\n\n');
    await vi.waitFor(() => expect(opened).toHaveLength(1));
    streams[0].end(); // connection drops without an abort

    await vi.waitFor(() => expect(requests).toHaveLength(2));
    // The reconnect carries the LAST SEEN id, not the original position.
    expect(requests[1].url.searchParams.get("since")).toBe("e:101");
    await vi.waitFor(() => expect(opened).toHaveLength(2));

    abort.abort();
    await done;
  });

  it("backs off exponentially up to the cap while the server is unreachable", async () => {
    const abort = new AbortController();
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => {
        requests.push({ url: new URL("http://f8.test/changefeed"), headers: {} });
        throw new TypeError("network down");
      }),
    );

    const delays: number[] = [];
    const errors: unknown[] = [];
    const done = streamChanges(instance, {
      signal: abort.signal,
      initialBackoffMs: 1,
      maxBackoffMs: 4,
      onEvent: () => {},
      onError: (e) => errors.push(e),
      onRetry: (_attempt, delayMs) => delays.push(delayMs),
    });

    await vi.waitFor(() => expect(delays.length).toBeGreaterThanOrEqual(5));
    abort.abort();
    await done;

    expect(delays.slice(0, 5)).toEqual([1, 2, 4, 4, 4]); // doubles, then capped
    expect(errors.length).toBeGreaterThanOrEqual(5); // surfaced, not swallowed silently
  });

  it("degrades quietly on 503: one request, no reconnect storm, close reason 'unavailable'", async () => {
    const abort = new AbortController();
    stubFetch(() => new Response("feed disabled", { status: 503 }));

    const closes: StreamCloseReason[] = [];
    const errors: unknown[] = [];
    await streamChanges(instance, {
      signal: abort.signal,
      initialBackoffMs: 1,
      onEvent: () => {},
      onError: (e) => errors.push(e),
      onClose: (reason) => closes.push(reason),
    });

    expect(requests).toHaveLength(1); // no retry against a disabled feed
    expect(closes).toEqual(["unavailable"]);
    expect(errors).toEqual([]); // 503 is a state, not an error to spam about
  });

  it("stops fatally on a 400 (bad filter) instead of retrying", async () => {
    const abort = new AbortController();
    stubFetch(() => new Response('{"title":"Invalid change feed filter"}', { status: 400 }));

    const closes: StreamCloseReason[] = [];
    const errors: unknown[] = [];
    await streamChanges(instance, {
      signal: abort.signal,
      onEvent: () => {},
      onError: (e) => errors.push(e),
      onClose: (reason) => closes.push(reason),
    });

    expect(requests).toHaveLength(1);
    expect(closes).toEqual(["fatal"]);
    expect(errors).toHaveLength(1);
  });

  it("stops on abort with close reason 'aborted'", async () => {
    const abort = new AbortController();
    let stream!: MockStream;
    stubFetch((_, init) => (stream = sseResponse(init?.signal)));

    const closes: StreamCloseReason[] = [];
    const done = streamChanges(instance, {
      signal: abort.signal,
      onEvent: () => {},
      onClose: (reason) => closes.push(reason),
    });
    await vi.waitFor(() => expect(requests).toHaveLength(1));

    abort.abort();
    void stream;
    await done;

    expect(closes).toEqual(["aborted"]);
    expect(requests).toHaveLength(1);
  });
});
