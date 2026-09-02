// MIT License
//
// integrations.ts
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

import { useQuery } from "@tanstack/react-query";
import { ApiError } from "../api/client";
import { getIntegrationLimits, listIntegrationProviders } from "../api/endpoints";
import type { InstanceConfig } from "../instances/types";
import type { FileLimits, IntegrationProvider } from "../api/types";

/**
 * The integrations capability of one instance (feature integrations), read from the one route that
 * answers it: the provider catalog.
 *
 * There is no `/status` block to read, deliberately - the runtime is a separate deployable and the
 * instance publishes nothing about it - so the catalog IS the probe. The interesting states are
 * three: it answered (the screen works), it refused the capability (the screen and its rail entry
 * are ABSENT), or the instance itself is unwell (that is the shell's business, not this screen's).
 *
 * A refusal is 403 on an instance with an API key configured and 401 on an open one, because the
 * standing capability policy challenges before it forbids. Both mean the same thing here, and
 * keying on one status code would hide the screen on secured instances only.
 */
export type IntegrationsCapability = "checking" | "available" | "absent" | "unreachable";

export function useIntegrationProviders(instance: InstanceConfig | null) {
  return useQuery<IntegrationProvider[] | null, unknown>({
    queryKey: [instance?.id, "integration-providers"],
    queryFn: ({ signal }) => listIntegrationProviders(instance!, signal),
    enabled: instance !== null,
    retry: 0,
    staleTime: 30_000,
  });
}

/**
 * What a job may carry on this instance (feature integration-file-transport). Read once per
 * instance and held for the session: the ceilings come from configuration, so they change when the
 * instance is redeployed and never while a screen is open.
 *
 * It fails softly on purpose. An instance too old to serve the route answers 404, and a form that
 * cannot read the ceilings must check NOTHING rather than fall back to a number of its own - see
 * `lib/fileLimits.ts`, which is the only place allowed to interpret the absence.
 */
export function useIntegrationLimits(instance: InstanceConfig | null) {
  return useQuery<FileLimits | null, unknown>({
    queryKey: [instance?.id, "integration-limits"],
    queryFn: ({ signal }) => getIntegrationLimits(instance!, signal),
    enabled: instance !== null,
    retry: 0,
    staleTime: Infinity,
  });
}

/** Whether a failure is the capability being off rather than the instance being unwell. */
export function isCapabilityRefusal(error: unknown): boolean {
  return error instanceof ApiError && (error.status === 403 || error.status === 401);
}

/**
 * The href for a provider's documentation link, or null when there is nothing safe to link.
 *
 * The runtime refuses anything but an absolute http(s) URL when it builds its catalog, so this is
 * the second half of that check rather than the only one: the descriptor arrives over the network
 * from a deployable Studio does not ship with, and a `javascript:` href here would run in the
 * operator's browser. Anything else is dropped silently - the row simply carries no link, which is
 * the same state as a provider that declares no documentation.
 */
export function docsHref(provider: Pick<IntegrationProvider, "docsUrl">): string | null {
  const raw = provider.docsUrl?.trim();
  if (!raw) return null;

  try {
    const url = new URL(raw);
    return url.protocol === "http:" || url.protocol === "https:" ? url.href : null;
  } catch {
    // Not a URL at all, including every relative value: `new URL` needs an origin to resolve one,
    // and resolving against Studio's own would send a click to a path Studio does not serve.
    return null;
  }
}

/** The capability verdict of a providers query, for the rail and for a deep link. */
export function capabilityOf(query: {
  isError: boolean;
  isSuccess: boolean;
  error: unknown;
}): IntegrationsCapability {
  if (query.isError) return isCapabilityRefusal(query.error) ? "absent" : "unreachable";
  return query.isSuccess ? "available" : "checking";
}
