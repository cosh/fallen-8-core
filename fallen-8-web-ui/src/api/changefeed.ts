import { ApiError, authHeaders, buildUrl, scopedPath } from "./client";
import type { InstanceConfig } from "../instances/types";

/**
 * Change feed consumption (feature change-feed, spec §3.7): fetch-based SSE reading of
 * GET /changefeed with the instance's auth headers - the browser-native EventSource
 * cannot set headers, and the API key NEVER goes into a query string (URLs leak into
 * logs and proxies).
 *
 * The client recipe encoded here: fetch, then stream; on resync, re-fetch. Delivery is
 * in-order, at-most-once per connection; whenever continuity is lost the server says so
 * in-band with a `resync` event. Reconnects carry the last seen `id:` value as `since`
 * (the catch-up contract), with exponential backoff. A 503 means the feed is disabled
 * (or full) - the stream stops quietly instead of hammering the server.
 */

// ---- event model (spec §3.3 JSON; fields ABSENT when not applicable) ----

export type ChangeEventKind =
  | "vertexCreated"
  | "vertexRemoved"
  | "edgeCreated"
  | "edgeRemoved"
  | "propertySet"
  | "propertyRemoved"
  | "resync";

export type ChangeElementType = "vertex" | "edge";

export type ResyncReason =
  | "trim"
  | "tabulaRasa"
  | "load"
  | "delegateWrite"
  | "overflow"
  | "seekOutOfRange";

export interface ChangeEvent {
  /** Monotonic per process epoch, commit order. */
  seq: number;
  /** Commit timestamp (UTC, ISO-8601), shared by a transaction's events. */
  ts: string;
  kind: ChangeEventKind;
  /** Element events only. */
  element?: ChangeElementType;
  /** Element id - element events only. */
  id?: number;
  /** Omitted when the element has no label. */
  label?: string;
  /** Property key - propertySet/propertyRemoved only (never the value). */
  key?: string;
  /** Source vertex id - edgeCreated only. */
  source?: number;
  /** Target vertex id - edgeCreated only. */
  target?: number;
  /** resync only. */
  reason?: ResyncReason;
}

/** Declarative server-side filter (spec §3.4): AND across dimensions, OR within one. */
export interface ChangeFeedFilter {
  kinds?: ChangeEventKind[];
  elements?: ChangeElementType[];
  labels?: string[];
  keys?: string[];
}

/** Maps a filter + catch-up position onto the comma-separated query grammar. */
export function buildChangeFeedQuery(
  filter?: ChangeFeedFilter,
  since?: string,
): Record<string, string | undefined> {
  const csv = (values?: readonly string[]) =>
    values && values.length > 0 ? values.join(",") : undefined;
  return {
    kinds: csv(filter?.kinds),
    elements: csv(filter?.elements),
    labels: csv(filter?.labels),
    keys: csv(filter?.keys),
    since,
  };
}

// ---- SSE wire-format parser ----

/** One dispatched SSE frame; `data` joins multiple data lines with \n per the SSE spec. */
export interface SseFrame {
  id?: string;
  event?: string;
  data: string;
}

/**
 * Incremental SSE parser: feed it chunks (split at ANY boundary), it dispatches complete
 * frames. Comment lines (": keepalive") and unknown fields are ignored; a blank line
 * dispatches; \n, \r\n and \r all terminate a line.
 */
export class SseParser {
  private buffer = "";
  private id: string | undefined;
  private event: string | undefined;
  private dataLines: string[] = [];

  constructor(private readonly onFrame: (frame: SseFrame) => void) {}

  push(chunk: string): void {
    this.buffer += chunk;
    for (;;) {
      const lineEnd = this.buffer.search(/[\r\n]/);
      if (lineEnd === -1) return;
      // A trailing \r might be half of a \r\n split across chunks - wait for more input.
      if (this.buffer[lineEnd] === "\r" && lineEnd === this.buffer.length - 1) return;
      const line = this.buffer.slice(0, lineEnd);
      const skip =
        this.buffer[lineEnd] === "\r" && this.buffer[lineEnd + 1] === "\n" ? 2 : 1;
      this.buffer = this.buffer.slice(lineEnd + skip);
      this.handleLine(line);
    }
  }

  private handleLine(line: string): void {
    if (line === "") {
      this.dispatch();
      return;
    }
    if (line.startsWith(":")) return; // comment (the server's keepalive heartbeat)

    const colon = line.indexOf(":");
    const field = colon === -1 ? line : line.slice(0, colon);
    let value = colon === -1 ? "" : line.slice(colon + 1);
    if (value.startsWith(" ")) value = value.slice(1);

    switch (field) {
      case "id":
        this.id = value;
        break;
      case "event":
        this.event = value;
        break;
      case "data":
        this.dataLines.push(value);
        break;
      default:
        break; // unknown fields are ignored per the SSE spec
    }
  }

  private dispatch(): void {
    if (this.id === undefined && this.event === undefined && this.dataLines.length === 0) {
      return; // stray blank line (e.g. after a keepalive comment)
    }
    this.onFrame({ id: this.id, event: this.event, data: this.dataLines.join("\n") });
    this.id = undefined;
    this.event = undefined;
    this.dataLines = [];
  }
}

/** Decodes one `data:` payload; null when it is not a well-formed change event. */
export function parseChangeEvent(data: string): ChangeEvent | null {
  try {
    const parsed = JSON.parse(data) as Partial<ChangeEvent> | null;
    if (
      parsed !== null &&
      typeof parsed === "object" &&
      typeof parsed.seq === "number" &&
      typeof parsed.kind === "string"
    ) {
      return parsed as ChangeEvent;
    }
    return null;
  } catch {
    return null;
  }
}

// ---- the streaming loop ----

/**
 * Why the stream stopped for good:
 * - "aborted"     - the caller's AbortSignal fired (instance switch, unmount).
 * - "unavailable" - the server answered 503 (feed disabled or subscriber limit); the
 *                   caller stays in its polling behaviour, no reconnect storm.
 * - "fatal"       - a non-retryable client error (400 bad filter, 401/403 auth).
 */
export type StreamCloseReason = "aborted" | "unavailable" | "fatal";

export interface StreamChangesOptions {
  filter?: ChangeFeedFilter;
  /** Catch-up position for the FIRST request; reconnects carry the last seen id automatically. */
  since?: string;
  signal: AbortSignal;
  /** Element events only - resync events go EXCLUSIVELY to onResync, never here. */
  onEvent: (event: ChangeEvent) => void;
  /** Continuity was lost - re-fetch what you display (mandatory per the contract). */
  onResync?: (event: ChangeEvent) => void;
  /** The stream is connected (also after a successful reconnect). */
  onOpen?: () => void;
  /** A transient error before a reconnect attempt; the loop handles the retry itself. */
  onError?: (error: unknown) => void;
  /** The stream stopped for good and will NOT reconnect. */
  onClose?: (reason: StreamCloseReason) => void;
  /** A reconnect is scheduled (attempt counter and the backoff delay about to be waited). */
  onRetry?: (attempt: number, delayMs: number) => void;
  /** First reconnect delay; doubles per attempt (default 1000). */
  initialBackoffMs?: number;
  /** Backoff ceiling (default 30000). */
  maxBackoffMs?: number;
}

function abortableDelay(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    const onAbort = () => {
      clearTimeout(timer);
      reject(new DOMException("Aborted", "AbortError"));
    };
    const timer = setTimeout(() => {
      signal.removeEventListener("abort", onAbort);
      resolve();
    }, ms);
    signal.addEventListener("abort", onAbort, { once: true });
  });
}

/**
 * Consumes GET /changefeed until the signal aborts, reconnecting with `since=<last id>`
 * and exponential backoff whenever the connection drops. Resolves when the stream has
 * stopped for good (after onClose).
 */
export async function streamChanges(
  instance: InstanceConfig,
  options: StreamChangesOptions,
): Promise<void> {
  const { signal } = options;
  const initialBackoff = options.initialBackoffMs ?? 1_000;
  const maxBackoff = options.maxBackoffMs ?? 30_000;
  let backoff = initialBackoff;
  let attempt = 0;
  let lastId = options.since;

  const close = (reason: StreamCloseReason) => options.onClose?.(reason);

  while (!signal.aborted) {
    const url = buildUrl(
      instance.baseUrl,
      scopedPath(instance, "/changefeed"),
      buildChangeFeedQuery(options.filter, lastId),
    );

    let response: Response | null = null;
    try {
      response = await fetch(url, {
        headers: { ...authHeaders(instance), Accept: "text/event-stream" },
        signal,
      });
    } catch (error) {
      if (signal.aborted) return close("aborted");
      options.onError?.(error); // network failure: retry with backoff below
    }

    if (response) {
      if (response.status === 503) {
        // Feed disabled or subscriber limit: a configuration state, not a transient
        // fault - stop quietly, the UI stays in its polling behaviour.
        await response.body?.cancel().catch(() => {});
        return close("unavailable");
      }
      if (!response.ok) {
        const body = await response.text().catch(() => "");
        options.onError?.(new ApiError(response.status, url, body));
        if (response.status >= 400 && response.status < 500) {
          return close("fatal"); // bad filter / bad credential will not fix itself
        }
        // other 5xx: retry with backoff below
      } else if (!response.body) {
        options.onError?.(new Error("changefeed response has no readable body"));
        return close("fatal");
      } else {
        options.onOpen?.();
        backoff = initialBackoff;
        attempt = 0;

        const parser = new SseParser((frame) => {
          if (frame.id) lastId = frame.id; // the reconnect catch-up position
          if (!frame.data) return;
          const event = parseChangeEvent(frame.data);
          if (!event) return;
          if (event.kind === "resync") {
            options.onResync?.(event);
          } else {
            options.onEvent(event);
          }
        });

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        try {
          for (;;) {
            const { done, value } = await reader.read();
            if (done) break;
            parser.push(decoder.decode(value, { stream: true }));
          }
          parser.push(decoder.decode());
        } catch (error) {
          if (signal.aborted) return close("aborted");
          options.onError?.(error);
        }
        // Stream ended without an abort (server restart, proxy timeout, network drop):
        // fall through to the backoff and reconnect from lastId.
      }
    }

    if (signal.aborted) return close("aborted");
    attempt += 1;
    options.onRetry?.(attempt, backoff);
    try {
      await abortableDelay(backoff, signal);
    } catch {
      return close("aborted");
    }
    backoff = Math.min(backoff * 2, maxBackoff);
  }

  close("aborted");
}
