// MIT License
//
// ErrorBox.tsx
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

import { ApiError, ApiTimeoutError } from "../api/client";

/**
 * Every failed request shows HTTP status + server message (NFR: never a silent console
 * error). A network-level failure renders as the disconnected state with a retry.
 *
 * The fallback title is deliberately NOT about requests. This box also renders throws from the
 * client side of a mutation, where no request was made at all: an integration submit that could
 * not build its body reported "Request failed / Invalid string length", which sent the reader
 * looking for a server fault that did not exist. A title is a claim, so the generic one claims
 * only what is always true.
 */
export function ErrorBox({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  let title = "Cannot continue";
  let detail = "";

  if (error instanceof ApiError) {
    title = `HTTP ${error.status}`;
    detail = error.body || "(no response body)";
  } else if (error instanceof ApiTimeoutError) {
    // Above the TypeError arm on purpose: it extends Error, so without its own arm it would fall
    // to the generic one and lose the one thing worth saying about it.
    title = "No answer from the instance";
    detail = error.message;
  } else if (error instanceof TypeError) {
    title = "Instance unreachable";
    detail = "The endpoint did not respond. Is the server running?";
  } else if (error instanceof Error) {
    detail = error.message;
  }

  return (
    <div
      role="alert"
      className="border-danger/40 bg-danger/5 text-danger rounded border px-3 py-2 text-[12px]"
    >
      <div className="font-semibold">{title}</div>
      {detail && <div className="text-danger/80 mt-1 break-all whitespace-pre-wrap">{detail}</div>}
      {onRetry && (
        <button type="button" className="btn btn-danger mt-2" onClick={onRetry}>
          Retry
        </button>
      )}
    </div>
  );
}
