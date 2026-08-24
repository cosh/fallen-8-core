// MIT License
//
// DurabilityNotice.tsx
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

import type { DurabilityREST } from "../api/types";

/**
 * The durability signal, for a human (feature platform-integrity-audit W5).
 *
 * The engine has published this on /status for a while and a machine already acts on it - the
 * integrations runtime refuses to delete anything while it is unhealthy - but nothing showed it to
 * the person watching. That was the gap the signal exists to close: "a client writes into a
 * degraded log and nobody watching finds out". It is rendered by the app shell, so it reaches that
 * person on whatever screen they are on rather than on one they had to think to open.
 *
 * Deliberately SILENT when everything is fine. A banner that is always there stops being read, and
 * the three states worth interrupting for are all exceptional: the log is degraded (commits are
 * landing in memory only), the last recovery was truncated (the graph is a prefix of history, so
 * anything reconciling against it is reasoning from incomplete data), or the last checkpoint
 * dropped indexes (they are gone after the next load).
 */

/** One thing worth interrupting for: what is wrong, and what it means for this graph. */
export interface DurabilityProblem {
  title: string;
  detail: string;
}

/**
 * The three exceptional states, as prose - and the ONE home for "is durability worth saying
 * something about", which the shell also asks (an empty namespace whose recovery was truncated
 * gets this warning instead of the first-run welcome; see AppShell).
 *
 * Absent is not healthy: an older server simply does not report, and saying nothing is honest,
 * whereas rendering "durable" from missing data would be an invention.
 */
export function durabilityProblems(
  durability: DurabilityREST | null | undefined,
): DurabilityProblem[] {
  if (!durability) {
    return [];
  }

  const problems: DurabilityProblem[] = [];

  if (durability.degraded) {
    problems.push({
      title: "The write-ahead log is degraded",
      detail:
        "Commits are landing in memory but not reaching the log, so a restart loses them. A " +
        "successful save re-establishes a durable baseline.",
    });
  }

  if (durability.lastRecoveryTruncated) {
    problems.push({
      title: "The last recovery was truncated",
      detail:
        `It replayed ${durability.lastRecoveryReplayedEntries.toLocaleString()} transaction(s) and ` +
        "then stopped at the last good entry, so this graph is a prefix of the committed history. " +
        "Anything that reconciles against it - and especially anything that deletes what nothing " +
        "asserts any more - is reasoning from incomplete data.",
    });
  }

  if (durability.lastCheckpointDroppedIndices > 0) {
    problems.push({
      title: `The last checkpoint dropped ${durability.lastCheckpointDroppedIndices.toLocaleString()} index(es)`,
      detail:
        "They are not in the snapshot, so they will be absent after the next load. One REST call " +
        "rebuilds an index from element state: POST /index/backfill/{indexId}. The Indexes screen " +
        "cannot do it yet.",
    });
  }

  return problems;
}

export function DurabilityNotice({ durability }: { durability: DurabilityREST | null | undefined }) {
  const problems = durabilityProblems(durability);

  if (problems.length === 0) {
    return null;
  }

  return (
    <div className="border-warn/40 bg-warn/5 space-y-2 rounded border p-3" data-testid="durability-notice">
      <p className="text-fg text-[12px] font-bold tracking-wider uppercase">Durability</p>
      {problems.map((problem) => (
        <div key={problem.title}>
          <p className="text-fg text-[13px]">{problem.title}</p>
          <p className="text-fg-faint text-[12px]">{problem.detail}</p>
        </div>
      ))}
    </div>
  );
}
