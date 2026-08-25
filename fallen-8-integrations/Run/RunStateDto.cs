// MIT License
//
// RunStateDto.cs
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
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.Integrations.Run
{
    /// <summary>
    ///   One identity's run as a reader sees it: what phase it is in now, or what it ended as.
    ///
    ///   <para>A snapshot, taken under the tracker's lock, so a reader never observes a half-updated
    ///   run. Both a progress view and an outcome view, because the two are the same question asked at
    ///   different times and splitting them would make a poller guess which endpoint to call.</para>
    /// </summary>
    public sealed class RunStateDto
    {
        /// <summary>The id the job route answered with, so a caller can tell its own run from a later one.</summary>
        public String RunId { get; set; } = String.Empty;

        public String ProviderId { get; set; } = String.Empty;

        public String IntegrationInstanceId { get; set; } = String.Empty;

        public String? Namespace { get; set; }

        /// <summary>ISO-8601 UTC. Reported, never compared: ordering uses the sequence.</summary>
        public String StartedAt { get; set; } = String.Empty;

        /// <summary>ISO-8601 UTC, or null while the run is in flight.</summary>
        public String? FinishedAt { get; set; }

        /// <summary>
        ///   Whether the run is still going. The one field a poller needs to decide whether to keep
        ///   asking, so it is explicit rather than inferred from a null timestamp.
        /// </summary>
        public Boolean Running { get; set; }

        /// <summary>Wall time so far, or the total once finished.</summary>
        public Int64 ElapsedMilliseconds { get; set; }

        /// <summary>The current phase from <see cref="RunPhases" />, or null once the run has ended.</summary>
        public String? Phase { get; set; }

        /// <summary>
        ///   The phase a run STOPPED in, when it did not finish cleanly. Null for a clean run and while one
        ///   is in flight. Without it a failed run's last phase reads as one that never happened.
        /// </summary>
        public String? StoppedInPhase { get; set; }

        /// <summary>
        ///   Whether the JOB asked for summary embedding. It lives here rather than in the client, because
        ///   it is a fact about the run and a client holding it in component state reports it wrongly after
        ///   any remount - claiming nobody asked for embedding that actually happened.
        /// </summary>
        public Boolean EmbedRequested { get; set; }

        /// <summary>How far through the current phase, where it counts. Zero when it does not.</summary>
        public Int32 PhaseDone { get; set; }

        /// <summary>The total for the current phase, or zero when the phase has no countable unit.</summary>
        public Int32 PhaseTotal { get; set; }

        /// <summary>Phases already finished, in the order they completed.</summary>
        public String[] CompletedPhases { get; set; } = Array.Empty<String>();

        /// <summary>
        ///   The report, once there is one. Present for a run that FAILED as well as one that
        ///   succeeded: a failed run's report carries its own errorKind and its counts, and those counts
        ///   are the difference between "nothing happened" and "the graph landed, the embedding did not".
        /// </summary>
        public JobReport? Report { get; set; }

        /// <summary>
        ///   Set only when the run produced no report at all because it threw. A report and an error are
        ///   mutually exclusive, and a run with neither is still in flight.
        /// </summary>
        public String? Error { get; set; }

        /// <summary>
        ///   Monotonic start order, for sorting. Not a timestamp: two runs starting in the same tick must
        ///   still be orderable.
        /// </summary>
        [JsonIgnore]
        public Int64 Sequence { get; set; }
    }

    /// <summary>What the job route answers when it ACCEPTS a run rather than waiting for it.</summary>
    public sealed class RunAcceptedDto
    {
        public String RunId { get; set; } = String.Empty;

        public String ProviderId { get; set; } = String.Empty;

        public String IntegrationInstanceId { get; set; } = String.Empty;

        /// <summary>
        ///   Where to watch it. Spelled out because the alternative is every client hard-coding a route
        ///   shape, and this one is new enough that a reader needs telling.
        /// </summary>
        public String Progress { get; set; } = String.Empty;
    }
}
