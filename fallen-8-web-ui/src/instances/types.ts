// MIT License
//
// types.ts
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

/**
 * Instance registry model (FR-1a).
 *
 * Auth is intentionally a discriminated union (feature web-ui, lightweight auth): the
 * Fallen-8 apiApp accepts its static API key either in a header (default X-Api-Key) or as
 * an RFC 6750-shaped `Authorization: Bearer <key>`. The bearer form is the seam where a
 * token-based scheme (OIDC/JWT, e.g. AWS Cognito) plugs in later as a new `kind` without
 * touching the client call sites.
 */
export type InstanceAuth =
  | { kind: "none" }
  | { kind: "apiKey"; key: string; useBearer?: boolean; header?: string };

export interface InstanceConfig {
  id: string;
  name: string;
  /** Base URL without a trailing slash; "" means the origin the app is served from. */
  baseUrl: string;
  auth: InstanceAuth;
  /**
   * The ADDRESSED namespace (feature graph-namespaces): when set, every namespace-scoped
   * request goes to /ns/{namespace}/… — explicitly, "default" included. Never persisted on
   * the registry record; useInstanceStore() binds it from the active-namespace state, so a
   * bound view of the instance flows to the screens' API calls.
   */
  namespace?: string;
}

export function normalizeBaseUrl(url: string): string {
  const trimmed = url.trim();
  if (trimmed === "" || trimmed === "/") return "";
  return trimmed.endsWith("/") ? trimmed.slice(0, -1) : trimmed;
}

export function describeEndpoint(instance: InstanceConfig): string {
  return instance.baseUrl === "" ? "same origin" : instance.baseUrl;
}

/**
 * A cross-origin instance is one whose baseUrl resolves to a different origin than the page the
 * Studio is served from. It matters for diagnostics (feature standalone-ui): when such an
 * instance's /status probe fails at the fetch layer, a missing CORS allow-list entry on the data
 * plane is indistinguishable from "server down", so the Connect screen surfaces a CORS hint for
 * exactly these. Same-origin ("") is never cross-origin.
 */
export function isCrossOriginInstance(baseUrl: string): boolean {
  const normalized = normalizeBaseUrl(baseUrl);
  if (normalized === "") return false;
  if (typeof window === "undefined") return false;
  try {
    return new URL(normalized, window.location.href).origin !== window.location.origin;
  } catch {
    return false;
  }
}
