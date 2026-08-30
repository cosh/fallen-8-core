// MIT License
//
// modelProvenance.ts
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

import type { ChatProviderStatsREST, EmbeddingProviderStatsREST } from "../api/types";

/**
 * How Studio names the model backend that serves a request (feature model-providers).
 *
 * Two kinds of label, and the distinction is the feature:
 * - AMBIENT ("requests will go to X") reads the polled /status block, which describes the
 *   CURRENT configuration. {@link chatAmbientLabel} is named so a call site cannot use it by
 *   accident where a per-call answer belongs.
 * - PER-CALL reads the `backend` field carried on that call's own response. A draft produced
 *   under one backend must keep saying so after the operator switches, so nothing per-call may
 *   be derived from here.
 *
 * Neither function invents a name it cannot know: an absent field yields a sentence saying the
 * value is absent, never a plausible default.
 */

/**
 * The ambient chat destination: `"{backend} · {model}"`, or the reason there is no pair to show.
 * `undefined` means /status has not answered yet, which is a different thing from chat being off.
 */
export function chatAmbientLabel(chat: ChatProviderStatsREST | null | undefined): string {
  if (!chat) return "checking…";
  if (!chat.enabled) return "chat is off on this instance";
  if (!chat.backend) return "backend not reported by this server";
  return chat.model ? `${chat.backend} · ${chat.model}` : chat.backend;
}

/**
 * The embedding function's identity as a reader recognises it: `modelName[@modelVersion]`.
 * A provider that reports no model name is described by its dimension instead, because that is
 * the one property of the vector space still known to be true.
 */
export function embeddingStamp(provider: EmbeddingProviderStatsREST): string {
  if (!provider.modelName) return `${provider.dimension}d`;
  return provider.modelVersion
    ? `${provider.modelName}@${provider.modelVersion}`
    : provider.modelName;
}
