// MIT License
//
// error-box.test.tsx
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

import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { ApiError, ApiTimeoutError } from "../src/api/client";
import { ErrorBox } from "../src/components/ErrorBox";

/**
 * A title is a claim (feature integration-file-transport FR-9). This box renders failures from
 * both sides of a request, so its generic title may only say what is true of all of them: an
 * integration submit that could not build its body reported "Request failed / Invalid string
 * length" for a request that was never made, and the reader went looking for a server fault.
 */

describe("ErrorBox titles", () => {
  it("names the status for a server refusal", () => {
    render(<ErrorBox error={new ApiError(413, "/integrations/job", "body too large")} />);
    expect(screen.getByRole("alert")).toHaveTextContent("HTTP 413");
    expect(screen.getByRole("alert")).toHaveTextContent("body too large");
  });

  it("says so when a server answers a status with no body", () => {
    render(<ErrorBox error={new ApiError(500, "/status", "")} />);
    expect(screen.getByRole("alert")).toHaveTextContent("HTTP 500");
    expect(screen.getByRole("alert")).toHaveTextContent("(no response body)");
  });

  it("distinguishes an address that accepted the connection and then said nothing", () => {
    // ApiTimeoutError extends Error, so without an arm of its own it falls to the generic one and
    // loses the one thing worth saying: the connection worked and the answer did not come.
    render(<ErrorBox error={new ApiTimeoutError("http://localhost:8080/status", 2000)} />);
    const alert = screen.getByRole("alert");
    expect(alert).toHaveTextContent("No answer from the instance");
    expect(alert).toHaveTextContent(/within 2000 ms/);
  });

  it("reads a fetch rejection as an unreachable endpoint", () => {
    render(<ErrorBox error={new TypeError("Failed to fetch")} />);
    expect(screen.getByRole("alert")).toHaveTextContent("Instance unreachable");
  });

  it("does not claim a request was made when the failure happened before one", () => {
    // The load-bearing case. This is the exact shape the incident produced: a client-side throw
    // inside a mutation, with no request and therefore no status.
    render(<ErrorBox error={new RangeError("Invalid string length")} />);
    const alert = screen.getByRole("alert");
    expect(alert).not.toHaveTextContent("Request failed");
    expect(alert).toHaveTextContent("Cannot continue");
    expect(alert).toHaveTextContent("Invalid string length");
  });

  it("keeps the same restraint for a plain thrown Error", () => {
    render(<ErrorBox error={new Error("staged files come to 5.8 GiB")} />);
    expect(screen.getByRole("alert")).not.toHaveTextContent("Request failed");
    expect(screen.getByRole("alert")).toHaveTextContent("staged files come to 5.8 GiB");
  });

  it("falls back to a bare title for a thrown non-Error, rather than rendering nothing", () => {
    render(<ErrorBox error={"a string nobody should throw"} />);
    expect(screen.getByRole("alert")).toHaveTextContent("Cannot continue");
  });

  it("offers a retry only when one was given, and calls it", async () => {
    const onRetry = vi.fn();
    const { unmount } = render(<ErrorBox error={new Error("nope")} onRetry={onRetry} />);
    screen.getByRole("button", { name: "Retry" }).click();
    expect(onRetry).toHaveBeenCalledTimes(1);
    unmount();

    render(<ErrorBox error={new Error("nope")} />);
    expect(screen.queryByRole("button", { name: "Retry" })).not.toBeInTheDocument();
  });
});
