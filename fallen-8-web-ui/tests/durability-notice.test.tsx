// MIT License
//
// durability-notice.test.tsx
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

import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { DurabilityNotice } from "../src/components/DurabilityNotice";
import type { DurabilityREST } from "../src/api/types";

/**
 * The durability signal reaching a human (feature platform-integrity-audit W5). The engine has
 * published it for a while and the integrations runtime already refuses to delete on it; nothing
 * showed it to the person watching the dashboard, which is the failure it existed to prevent.
 *
 * Silence when healthy is a REQUIREMENT, not an omission: a permanent green badge trains people to
 * stop reading it, and every state worth interrupting for is exceptional.
 */

const healthy: DurabilityREST = {
  walEnabled: true,
  degraded: false,
  recoveryRan: true,
  lastRecoveryTruncated: false,
  lastRecoveryReplayedEntries: 12,
  lastCheckpointDroppedIndices: 0,
};

describe("DurabilityNotice", () => {
  it("says nothing when durability is healthy", () => {
    render(<DurabilityNotice durability={healthy} />);
    expect(screen.queryByTestId("durability-notice")).toBeNull();
  });

  it("says nothing when the server does not report the block", () => {
    // Absence must not be rendered as health: an older server simply does not answer, and inventing
    // "durable" from missing data is the one thing this component must never do.
    render(<DurabilityNotice durability={undefined} />);
    expect(screen.queryByTestId("durability-notice")).toBeNull();
    render(<DurabilityNotice durability={null} />);
    expect(screen.queryByTestId("durability-notice")).toBeNull();
  });

  it("warns that commits are not reaching the log when degraded", () => {
    render(<DurabilityNotice durability={{ ...healthy, degraded: true }} />);
    expect(screen.getByTestId("durability-notice")).toBeInTheDocument();
    expect(screen.getByText(/write-ahead log is degraded/i)).toBeInTheDocument();
  });

  it("warns that the graph is a prefix of history after a truncated recovery, with the count", () => {
    render(
      <DurabilityNotice
        durability={{ ...healthy, lastRecoveryTruncated: true, lastRecoveryReplayedEntries: 1234 }}
      />,
    );
    expect(screen.getByText(/last recovery was truncated/i)).toBeInTheDocument();
    // Separator-agnostic on purpose: the count is rendered with toLocaleString, like every other
    // number in the Studio, so the grouping character is the reader's, not the test's (this host
    // formats 1234 as "1.234").
    expect(screen.getByText(/1[.,\s]?234 transaction/i)).toBeInTheDocument();
  });

  it("warns that dropped indexes will be gone after the next load", () => {
    render(<DurabilityNotice durability={{ ...healthy, lastCheckpointDroppedIndices: 2 }} />);
    expect(screen.getByText(/dropped 2 index/i)).toBeInTheDocument();
  });

  it("reports every unhealthy state at once rather than only the first", () => {
    render(
      <DurabilityNotice
        durability={{
          ...healthy,
          degraded: true,
          lastRecoveryTruncated: true,
          lastCheckpointDroppedIndices: 1,
        }}
      />,
    );
    expect(screen.getByText(/degraded/i)).toBeInTheDocument();
    expect(screen.getByText(/truncated/i)).toBeInTheDocument();
    expect(screen.getByText(/dropped 1 index/i)).toBeInTheDocument();
  });
});
