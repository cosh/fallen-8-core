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
}

export function normalizeBaseUrl(url: string): string {
  const trimmed = url.trim();
  if (trimmed === "" || trimmed === "/") return "";
  return trimmed.endsWith("/") ? trimmed.slice(0, -1) : trimmed;
}

export function describeEndpoint(instance: InstanceConfig): string {
  return instance.baseUrl === "" ? "same origin" : instance.baseUrl;
}
