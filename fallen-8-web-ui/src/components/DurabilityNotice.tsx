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
 * the person watching. That was the gap: "a client writes into a degraded log and nobody watching a
 * dashboard finds out" was the failure this signal existed to prevent, and it stopped one hop short.
 *
 * Deliberately SILENT when everything is fine. A dashboard that always carries a green durability
 * badge trains people to stop reading it, and the three states worth interrupting for are all
 * exceptional: the log is degraded (commits are landing in memory only), the last recovery was
 * truncated (the graph is a prefix of history, so anything reconciling against it is reasoning from
 * incomplete data), or the last checkpoint dropped indexes (they are gone after the next load).
 */
export function DurabilityNotice({ durability }: { durability: DurabilityREST | null | undefined }) {
  // Absent is not healthy: an older server simply does not report, and saying nothing is honest,
  // whereas rendering "durable" from missing data would be an invention.
  if (!durability) {
    return null;
  }

  const problems: { title: string; detail: string }[] = [];

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
        "They are not in the snapshot, so they will be absent after the next load. Rebuilding one " +
        "from element state is a single call on the Indexes screen.",
    });
  }

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
