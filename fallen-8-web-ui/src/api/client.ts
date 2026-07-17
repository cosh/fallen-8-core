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
export class ApiError extends Error {
  readonly status: number;
  readonly url: string;
  readonly body: string;

  constructor(status: number, url: string, body: string) {
    super(`HTTP ${status}${body ? `: ${body}` : ""}`);
    this.name = "ApiError";
    this.status = status;
    this.url = url;
    this.body = body;
  }
}

export interface RequestOptions {
  method?: "GET" | "POST" | "PUT" | "DELETE" | "HEAD";
  body?: unknown;
  query?: Record<string, string | number | boolean | undefined>;
  signal?: AbortSignal;
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
    throw new ApiError(response.status, url, body);
  }
}

export async function apiRequest<T>(
  instance: InstanceConfig,
  path: string,
  options: RequestOptions = {},
): Promise<T | null> {
  const url = buildUrl(instance.baseUrl, path, options.query);
  const headers: Record<string, string> = { ...authHeaders(instance) };
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
