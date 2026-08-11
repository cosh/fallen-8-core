// MIT License
//
// DurabilityState.cs
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

using System;

namespace NoSQL.GraphDB.Core
{
    /// <summary>
    ///   A point-in-time read of whether this engine's writes are actually durable, and of whether
    ///   the state it is serving is the complete history or a prefix of it (feature
    ///   platform-integrity-audit W5).
    ///
    ///   <para><b>Why this type exists.</b> The engine already computes every fact below, and every
    ///   one of them died at the engine boundary. <see cref="Transaction.TransactionInformation.Durable" />
    ///   is set by the writer and was read nowhere outside the engine; the degraded-WAL state existed
    ///   only as an OpenTelemetry gauge, so it existed only if the operator had wired a collector; a
    ///   truncated recovery logged one error and became an activity tag. So a client could write into
    ///   a degraded log and receive success for every write, then lose all of them on the next kill,
    ///   and after a truncated replay it would reconcile against a silent prefix of history without
    ///   any way to know.</para>
    ///
    ///   <para><b>Why a client should care rather than just an operator.</b> Any reconciling writer
    ///   that deletes state because "nothing asserts it any more" is reading a conclusion out of graph
    ///   content. If that content is a post-truncation prefix, the conclusion is wrong and the
    ///   deletion is the one mutation re-syncing cannot undo. Such a client is expected to check
    ///   <see cref="Degraded" /> and <see cref="LastRecoveryTruncated" /> before deleting, and to
    ///   defer instead: deferring a deletion is recoverable, performing a wrong one is not.</para>
    ///
    ///   <para>Every field is a cheap read of already-published state. Nothing here forces a flush, a
    ///   scan or a probe.</para>
    /// </summary>
    public sealed class DurabilityState
    {
        /// <summary>
        ///   Whether a write-ahead log is configured at all. When <c>false</c> a committed transaction
        ///   is durable only as far as the last checkpoint, which is the documented volatile/no-WAL
        ///   posture rather than a fault - so <see cref="Degraded" /> stays <c>false</c> and this flag
        ///   is what tells the two apart.
        /// </summary>
        public Boolean WalEnabled
        {
            get; internal set;
        }

        /// <summary>
        ///   Whether write durability is currently DEGRADED: the sticky failure fence has tripped (a
        ///   log append or flush failed) or an anchored log is waiting for its paired snapshot load.
        ///   Transactions still commit in memory and still report success, so this is the only signal
        ///   that they are not reaching disk. A successful save re-establishes a durable baseline and
        ///   clears it.
        /// </summary>
        public Boolean Degraded
        {
            get; internal set;
        }

        /// <summary>
        ///   Whether a write-ahead-log recovery has run in this engine's lifetime. When <c>false</c>
        ///   the recovery fields below carry no information.
        /// </summary>
        public Boolean RecoveryRan
        {
            get; internal set;
        }

        /// <summary>
        ///   Whether the last recovery stopped BEFORE the end of the log. Replay is fail-stop for
        ///   core-data entries: a decode failure or a re-execution fault halts at the last good entry,
        ///   because continuing would misapply every later entry against a diverged id space. The
        ///   graph is then internally consistent but is a PREFIX of the committed history, and this is
        ///   the only in-band way to know that.
        /// </summary>
        public Boolean LastRecoveryTruncated
        {
            get; internal set;
        }

        /// <summary>How many log entries the last recovery replayed.</summary>
        public Int32 LastRecoveryReplayedEntries
        {
            get; internal set;
        }

        /// <summary>
        ///   How many indices the last checkpoint could NOT persist and therefore dropped from its
        ///   manifest. Dropping is deliberate - one index that fails to serialize must not cost the
        ///   whole checkpoint - but it means the next load comes up with every element intact and
        ///   those indices GONE. Index content is derived state, so the repair is to repopulate it
        ///   from element state; this number is what tells a client it needs to.
        /// </summary>
        public Int32 LastCheckpointDroppedIndices
        {
            get; internal set;
        }
    }
}
