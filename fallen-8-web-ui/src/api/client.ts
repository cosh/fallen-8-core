// MIT License
//
// client.ts
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

import type { InstanceConfig } from "../instances/types";

/**
 * Thin, instance-bound transport for the Fallen-8 REST surface (design §2.3).
 *
 * Hard rules encoded here:
 * - Routes are root-level ("/graph", "/status", ...) - never under /api/v0.1/.
 * - Missing elements come back as 200-with-null or 204: an empty body resolves to null,
 *   it is NOT an error.
 * - Every failed request throws an ApiError carrying status + the server's message so the
 *   UI can always show both (NFR: no silent failures).
 * - Auth (lightweight, extensible): an instance's API key travels as
 *   `Authorization: Bearer <key>` by default (the shape a future OIDC/JWT scheme reuses),
 *   or in a named header (X-Api-Key style) when configured. Keys never leave the browser
 *   except toward their own instance.
 */
/**
 * The human-readable message from a REST error body. Fallen-8 returns every error as RFC 7807
 * `application/problem+json` (feature api-error-envelope), which carries the message in `detail`
 * (falling back to `title`); any other body - a plain string, an empty body, a pre-envelope
 * server, or a JSON object without a usable `detail`/`title` - is returned unchanged. So for
 * Fallen-8's own responses the Studio error surface always shows the server's message rather than
 * the raw problem+json object.
 */
export function problemDetail(body: string): string {
  if (body.trimStart().startsWith("{")) {
    try {
      const problem = JSON.parse(body) as { detail?: unknown; title?: unknown };
      if (typeof problem.detail === "string" && problem.detail.length > 0) return problem.detail;
      if (typeof problem.title === "string" && problem.title.length > 0) return problem.title;
    } catch {
      // not a JSON object body - fall through to the raw text
    }
  }
  return body;
}

export class ApiError extends Error {
  readonly status: number;
  readonly url: string;
  /**
   * The server's human-readable message - the problem+json `detail`/`title` when the body is an
   * RFC 7807 envelope, otherwise the raw body. For Fallen-8's own errors (always carrying a
   * `detail`/`title`) this is a clean message, never a raw JSON object.
   */
  readonly body: string;

  constructor(status: number, url: string, body: string) {
    const message = problemDetail(body);
    super(`HTTP ${status}${message ? `: ${message}` : ""}`);
    this.name = "ApiError";
    this.status = status;
    this.url = url;
    this.body = message;
  }
}

/**
 * A request that was accepted and never answered. Distinct from {@link ApiError}, which carries a
 * status the server chose: this one exists because there was no answer at all.
 *
 * It has a status of 0 so a caller can still branch on `status`, and it is what turns "the spinner
 * never stops" into a stated failure. That distinction is not theoretical - a broken IPv6 loopback
 * forward on one published port left the Connect screen reading "checking..." indefinitely, with no
 * error anywhere, because a hung fetch never settles and so never becomes an error at all.
 */
export class ApiTimeoutError extends Error {
  readonly status = 0;
  readonly url: string;
  readonly timeoutMs: number;

  constructor(url: string, timeoutMs: number) {
    super(
      `No answer from ${url} within ${timeoutMs} ms. The address accepted the connection but sent ` +
        `nothing back, so the server may be reachable at a different address - on Windows with ` +
        `Docker Desktop, try 127.0.0.1 instead of localhost.`,
    );
    this.name = "ApiTimeoutError";
    this.url = url;
    this.timeoutMs = timeoutMs;
  }
}

export interface RequestOptions {
  method?: "GET" | "POST" | "PUT" | "DELETE" | "HEAD" | "PATCH";
  body?: unknown;
  query?: Record<string, string | number | boolean | undefined>;
  signal?: AbortSignal;
  /**
   * Abort after this long and fail with {@link ApiTimeoutError}.
   *
   * OPT-IN, per call, and deliberately not a default: a job run over a 100 MiB extract legitimately
   * takes half a minute, and a blanket timeout would abort exactly the long operations this API
   * exists for. It belongs on the REACHABILITY probes, whose whole question is "is it there?" and
   * whose answer arrives in milliseconds or not at all.
   */
  timeoutMs?: number;
  /**
   * Namespace scoping (feature graph-namespaces). "namespace" (the default) prefixes the
   * path with /ns/{namespace} when the instance is namespace-bound - the namespace is
   * ALWAYS explicit on the wire, "default" included. "fallen8" pins Fallen-8-level
   * endpoints (save games, delegate validation, the /ns management routes) to their bare
   * form regardless of binding.
   */
  scope?: "namespace" | "fallen8";
}

/**
 * The /ns/{namespace} prefix for a namespace-bound instance; the bare path for an unbound
 * one (pre-namespace servers and Fallen-8-level callers).
 */
export function scopedPath(instance: InstanceConfig, path: string): string {
  return instance.namespace
    ? `/ns/${encodeURIComponent(instance.namespace)}${path}`
    : path;
}

export function buildUrl(
  baseUrl: string,
  path: string,
  query?: RequestOptions["query"],
): string {
  let url = `${baseUrl}${path}`;
  if (query) {
    const params = new URLSearchParams();
    for (const [key, value] of Object.entries(query)) {
      if (value !== undefined) params.set(key, String(value));
    }
    const qs = params.toString();
    if (qs) url += `?${qs}`;
  }
  return url;
}

/**
 * Credential headers for the SYNCHRONOUS auth kinds (none/apiKey). A `bearer` instance
 * (feature studio-embeddable: host-supplied async token provider) cannot be served here -
 * its token only resolves in {@link resolveAuthHeaders}, which every transport call site
 * goes through - so it THROWS rather than returning empty headers: a new sync call site
 * must fail loudly instead of silently sending the request unauthenticated.
 */
export function authHeaders(instance: InstanceConfig): Record<string, string> {
  const auth = instance.auth;
  if (auth.kind === "bearer") {
    throw new Error(
      "authHeaders cannot resolve a host-supplied bearer token; use resolveAuthHeaders.",
    );
  }
  if (auth.kind === "apiKey" && auth.key) {
    if (auth.header && auth.useBearer !== true) {
      return { [auth.header]: auth.key };
    }
    return { Authorization: `Bearer ${auth.key}` };
  }
  return {};
}

/**
 * Credential headers for ALL auth kinds - the one function transport call sites use. For
 * `bearer` it awaits the host's token provider per request (the host caches/refreshes as it
 * sees fit); the standalone kinds pass through {@link authHeaders} synchronously.
 */
export async function resolveAuthHeaders(
  instance: InstanceConfig,
): Promise<Record<string, string>> {
  const auth = instance.auth;
  if (auth.kind === "bearer") {
    return { Authorization: `Bearer ${await auth.getToken()}` };
  }
  return authHeaders(instance);
}

/**
 * The single place a non-ok response becomes an {@link ApiError} (status + the server's body).
 * Shared by {@link apiRequest} and the raw-fetch bulk endpoints so every failure looks the same.
 */
export async function throwIfNotOk(response: Response, url: string): Promise<void> {
  if (!response.ok) {
    let body = "";
    try {
      body = await response.text();
    } catch {
      // keep the status-only error
    }

    raiseApiError(response.status, url, body);
  }
}

/**
 * Turns one non-ok answer into an {@link ApiError}, whatever transport read it. Separate from
 * {@link throwIfNotOk} because {@link apiUpload} goes through XMLHttpRequest and has no
 * `Response`, and a second copy of this would be a second place for the namespace announcement
 * below to go stale.
 */
export function raiseApiError(status: number, url: string, body: string): never {
  // The server marks a missing namespace with a "namespace" extension on its 404
  // problem+json (feature graph-namespaces); announce it so the recover state renders
  // immediately instead of waiting for the next inventory poll.
  if (status === 404 && typeof window !== "undefined") {
    try {
      const problem = JSON.parse(body) as { namespace?: unknown };
      if (typeof problem.namespace === "string") {
        window.dispatchEvent(
          new CustomEvent("f8:namespace-missing", { detail: { namespace: problem.namespace } }),
        );
      }
    } catch {
      // not a problem+json body
    }
  }

  throw new ApiError(status, url, body);
}

/**
 * One request's deadline: a signal that aborts when the caller's does OR when the timeout expires,
 * and a flag saying which of the two happened.
 *
 * Built by hand rather than with `AbortSignal.any`, which jsdom does not implement - a test suite
 * that cannot run the code is not covering it.
 *
 * `timeoutMs` undefined or 0 means no deadline at all: the caller's own signal passes through
 * unchanged and `expired` never becomes true. Exported so a caller with its own timeout budget
 * (nl-assist's model-call ceiling, generate.ts) reuses this rather than re-deriving it.
 */
export function startDeadline(
  caller: AbortSignal | undefined,
  timeoutMs: number | undefined,
): { signal: AbortSignal | undefined; expired: boolean; done: () => void } {
  if (!timeoutMs) return { signal: caller, expired: false, done: () => {} };

  const controller = new AbortController();
  const state = { signal: controller.signal, expired: false, done: () => {} };

  const timer = setTimeout(() => {
    state.expired = true;
    controller.abort();
  }, timeoutMs);

  const relay = () => controller.abort();
  if (caller) {
    if (caller.aborted) controller.abort();
    else caller.addEventListener("abort", relay, { once: true });
  }

  state.done = () => {
    clearTimeout(timer);
    caller?.removeEventListener("abort", relay);
  };

  return state;
}

export async function apiRequest<T>(
  instance: InstanceConfig,
  path: string,
  options: RequestOptions = {},
): Promise<T | null> {
  const effectivePath = options.scope === "fallen8" ? path : scopedPath(instance, path);
  const url = buildUrl(instance.baseUrl, effectivePath, options.query);
  const headers: Record<string, string> = { ...(await resolveAuthHeaders(instance)) };
  const deadline = startDeadline(options.signal, options.timeoutMs);
  const init: RequestInit = {
    method: options.method ?? "GET",
    headers,
    signal: deadline.signal,
  };
  if (options.body !== undefined) {
    headers["Content-Type"] = "application/json";
    init.body = JSON.stringify(options.body);
  }

  let response: Response;
  try {
    response = await fetch(url, init);
  } catch (error) {
    // Only OUR deadline becomes a timeout. An abort from the caller's own signal (react-query
    // cancelling on unmount, a superseded query) is rethrown untouched, or every navigation would
    // report a server that did not answer.
    if (deadline.expired) throw new ApiTimeoutError(url, options.timeoutMs!);
    throw error;
  } finally {
    deadline.done();
  }

  await throwIfNotOk(response, url);

  // 204 / empty 200 bodies mean "not found" or "accepted, nothing to say" - never an error.
  if (response.status === 204) return null;
  const text = await response.text();
  if (text === "" || text === "null") return null;
  return JSON.parse(text) as T;
}

/**
 * Multipart POST (feature unstructured-ingestion: the document upload). Same URL scoping,
 * auth and error mapping as {@link apiRequest}; the browser sets the multipart boundary
 * itself, so no Content-Type is written here.
 */
export async function apiForm<T>(
  instance: InstanceConfig,
  path: string,
  form: FormData,
  options: Pick<RequestOptions, "signal" | "scope"> = {},
): Promise<T | null> {
  const effectivePath = options.scope === "fallen8" ? path : scopedPath(instance, path);
  const url = buildUrl(instance.baseUrl, effectivePath);
  const response = await fetch(url, {
    method: "POST",
    headers: { ...(await resolveAuthHeaders(instance)) },
    body: form,
    signal: options.signal,
  });

  await throwIfNotOk(response, url);

  if (response.status === 204) return null;
  const text = await response.text();
  if (text === "" || text === "null") return null;
  return JSON.parse(text) as T;
}

/**
 * Whether a failure is somebody having cancelled rather than something having gone wrong.
 *
 * It exists because react-query does NOT treat an aborted MUTATION as a cancellation the way it
 * treats an aborted query: the mutation moves to `isError` with the abort as its error, so a call
 * site that renders `isError` puts a red box on the screen of the person who just pressed cancel.
 *
 * Matched on the NAME alone, deliberately. Neither `instanceof DOMException` nor `instanceof Error`
 * can be relied on here: jsdom's `DOMException` does not extend `Error`, so an `instanceof Error`
 * check passes in a browser and fails in the test suite - which is the worst of both, a guard that
 * is only absent where it is being verified. Every abort reason carries this name, whatever its
 * class.
 */
export function wasCancelled(error: unknown): boolean {
  return (
    typeof error === "object" &&
    error !== null &&
    (error as { name?: unknown }).name === "AbortError"
  );
}

/** How far a send has got. `total` is null when the browser will not say how much there is. */
export interface UploadProgress {
  sent: number;
  total: number | null;
}

/**
 * Multipart POST that reports how far the SEND has got (feature integration-file-transport).
 *
 * XMLHttpRequest and not `fetch`, for one reason: fetch cannot report upload progress. A
 * `ReadableStream` request body can, but it requires HTTP/2 and `duplex: "half"`, is unsupported in
 * Safari, and would make progress depend on the transport an instance happens to be reached over.
 * XHR reports it everywhere, so this is a deliberate exception rather than legacy code, and it is
 * the ONLY one: everything without a progress bar goes through {@link apiForm}.
 *
 * The progress matters because of what this carries. An integration job may be gigabytes of
 * extracts, and a send with no feedback is indistinguishable from a hang - which is exactly what
 * the operator who prompted this feature reported: they pressed run, saw nothing for minutes, and
 * then got an error.
 *
 * Failures are shaped like every other call's: an {@link ApiError} for a status, an abort rethrown
 * as an abort so react-query reads it as a cancellation, and a `TypeError` for a transport failure,
 * which is the same class `fetch` throws and which `ErrorBox` renders as an unreachable instance.
 */
export function apiUpload<T>(
  instance: InstanceConfig,
  path: string,
  form: FormData,
  options: {
    signal?: AbortSignal;
    scope?: "namespace" | "fallen8";
    onProgress?: (progress: UploadProgress) => void;
  } = {},
): Promise<T | null> {
  const effectivePath = options.scope === "fallen8" ? path : scopedPath(instance, path);
  const url = buildUrl(instance.baseUrl, effectivePath);

  return resolveAuthHeaders(instance).then(
    (headers) =>
      new Promise<T | null>((resolve, reject) => {
        const request = new XMLHttpRequest();
        request.open("POST", url);
        for (const [name, value] of Object.entries(headers)) {
          request.setRequestHeader(name, value);
        }

        // No Content-Type is set here on purpose: the browser writes it, boundary and all. Setting
        // one would send a boundary nothing matches, and the server would find no parts at all.
        if (options.onProgress) {
          request.upload.addEventListener("progress", (event) => {
            options.onProgress!({
              sent: event.loaded,
              total: event.lengthComputable ? event.total : null,
            });
          });
        }

        const abort = () => request.abort();
        options.signal?.addEventListener("abort", abort, { once: true });
        const finish = () => options.signal?.removeEventListener("abort", abort);

        request.addEventListener("load", () => {
          finish();
          if (request.status < 200 || request.status >= 300) {
            try {
              raiseApiError(request.status, url, request.responseText ?? "");
            } catch (error) {
              reject(error);
            }
            return;
          }

          const text = request.responseText ?? "";
          if (request.status === 204 || text === "" || text === "null") {
            resolve(null);
            return;
          }

          try {
            resolve(JSON.parse(text) as T);
          } catch (error) {
            reject(error);
          }
        });

        request.addEventListener("error", () => {
          finish();
          // The same class fetch throws for a failed connection, so one error taxonomy covers both
          // transports and the caller needs no special case.
          reject(new TypeError(`Failed to reach ${url}`));
        });

        request.addEventListener("abort", () => {
          finish();
          // Rethrown as an ABORT rather than an error: react-query treats an aborted mutation as
          // cancelled, and reporting it as a failure would put an error box on the screen of
          // somebody who pressed cancel themselves.
          reject(
            options.signal?.reason instanceof Error
              ? options.signal.reason
              : new DOMException("The upload was cancelled.", "AbortError"),
          );
        });

        request.send(form);
      }),
  );
}
