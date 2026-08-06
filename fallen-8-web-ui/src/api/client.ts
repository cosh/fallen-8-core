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

export interface RequestOptions {
  method?: "GET" | "POST" | "PUT" | "DELETE" | "HEAD" | "PATCH";
  body?: unknown;
  query?: Record<string, string | number | boolean | undefined>;
  signal?: AbortSignal;
  /**
   * Namespace scoping (feature graph-namespaces). "namespace" (the default) prefixes the
   * path with /ns/{namespace} when the instance is namespace-bound - the namespace is
   * ALWAYS explicit on the wire, "default" included. "fallen8" pins Fallen-8-level
   * endpoints (save games, benchmark, delegate validation, the /ns management routes)
   * to their bare form regardless of binding.
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
 * (feature studio-embeddable: host-supplied async token provider) yields nothing here BY
 * DESIGN - its token only resolves in {@link resolveAuthHeaders}, which every transport
 * call site goes through. Do not fetch with this directly.
 */
export function authHeaders(instance: InstanceConfig): Record<string, string> {
  const auth = instance.auth;
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

    // The server marks a missing namespace with a "namespace" extension on its 404
    // problem+json (feature graph-namespaces); announce it so the recover state renders
    // immediately instead of waiting for the next inventory poll.
    if (response.status === 404 && typeof window !== "undefined") {
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

    throw new ApiError(response.status, url, body);
  }
}

export async function apiRequest<T>(
  instance: InstanceConfig,
  path: string,
  options: RequestOptions = {},
): Promise<T | null> {
  const effectivePath = options.scope === "fallen8" ? path : scopedPath(instance, path);
  const url = buildUrl(instance.baseUrl, effectivePath, options.query);
  const headers: Record<string, string> = { ...(await resolveAuthHeaders(instance)) };
  const init: RequestInit = {
    method: options.method ?? "GET",
    headers,
    signal: options.signal,
  };
  if (options.body !== undefined) {
    headers["Content-Type"] = "application/json";
    init.body = JSON.stringify(options.body);
  }

  const response = await fetch(url, init);

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
