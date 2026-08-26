// MIT License
//
// RunJournal.cs
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
using System.Collections.Generic;

namespace NoSQL.GraphDB.Integrations.Run
{
    /// <summary>
    ///   What a run has to write down about work it cannot recompute after an interruption.
    ///
    ///   <para>There is exactly one such thing, and it is worth stating why rather than generalising: a
    ///   run's graph writes are recomputable, because re-resolving the same snapshot against the graph
    ///   yields "already there" for everything it created. Its EMBEDDING set is not, because the applier
    ///   embeds only entities whose data CHANGED - so once the writes have landed, the answer to "which
    ///   entities needed a summary" is permanently lost. That single set is the whole of what this
    ///   records.</para>
    /// </summary>
    public interface IRunJournal
    {
        /// <summary>
        ///   Whether this run is being PICKED UP rather than started. It is asked separately from
        ///   <see cref="ResumedEmbeds" /> because the two answers together carry a third case: a resumed run
        ///   with NO journal has LOST it, and recomputing its plan would give an empty one.
        /// </summary>
        Boolean IsResume { get; }

        /// <summary>
        ///   The entities a RESUMED run still has to embed, in journal order, or null when there is no
        ///   journal to go on. Non-null and EMPTY is meaningful and different from null: it says the previous
        ///   attempt finished embedding, so there is nothing left to do.
        /// </summary>
        IReadOnlyList<Int32>? ResumedEmbeds { get; }

        /// <summary>
        ///   Records which entities need a summary, BEFORE the writes that would make the answer
        ///   unknowable. A no-op on a resumed run, whose journal already holds the answer and whose freshly
        ///   computed one would be empty.
        /// </summary>
        void RecordEmbedPlan(IReadOnlyList<Int32> entityIndices);
    }

    /// <summary>
    ///   The journal, spooled, wearing the progress sink's clothes.
    ///
    ///   <para>It is BOTH an <see cref="IRunProgress" /> and an <see cref="IRunJournal" />, and that is a
    ///   deliberate joining of two things rather than laziness. The embedding cursor has to advance per
    ///   CHUNK, and the only thing told about chunks is the progress sink: the chunk loop lives inside the
    ///   graph target, which reports each chunk through <see cref="IRunProgress.Advance" /> and must not
    ///   learn that a spool exists. So the cursor rides on the counter that already exists, and no new seam
    ///   is threaded through the target for it.</para>
    /// </summary>
    public sealed class SpooledRunJournal : IRunProgress, IRunJournal
    {
        private readonly IRunProgress _inner;
        private readonly RunSpool _spool;
        private readonly String _instanceId;

        /// <summary>How many summaries an EARLIER attempt of this run had already embedded.</summary>
        private readonly Int32 _alreadyEmbedded;

        private Int32[] _plan = Array.Empty<Int32>();
        private String? _phase;
        private Int32 _lastRecorded = -1;

        /// <param name="inner">The sink this decorates, which is the tracker's own handle.</param>
        /// <param name="spool">Where the journal is written.</param>
        /// <param name="instanceId">The identity whose entry this is.</param>
        /// <param name="resumed">The journal an earlier attempt left, or null when there is none.</param>
        /// <param name="isResume">Whether this run is being picked up rather than started. Distinct from
        /// <paramref name="resumed" /> being null, which a resumed run whose journal was LOST also is.</param>
        public SpooledRunJournal(IRunProgress inner, RunSpool spool, String instanceId,
            SpooledProgress? resumed, Boolean isResume = false)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _spool = spool ?? throw new ArgumentNullException(nameof(spool));
            _instanceId = instanceId ?? throw new ArgumentNullException(nameof(instanceId));
            IsResume = isResume;

            if (resumed != null)
            {
                _plan = resumed.EmbedEntities;
                _alreadyEmbedded = Math.Min(Math.Max(0, resumed.Embedded), _plan.Length);
                var remaining = resumed.Remaining();
                ResumedEmbeds = remaining;
            }
        }

        /// <inheritdoc />
        public Boolean IsResume { get; }

        /// <inheritdoc />
        public IReadOnlyList<Int32>? ResumedEmbeds { get; }

        /// <inheritdoc />
        public void RecordEmbedPlan(IReadOnlyList<Int32> entityIndices)
        {
            if (IsResume)
            {
                // A resumed run's plan is the one on disk. Recomputing it here would produce an empty set -
                // every element it wrote now compares equal - and writing THAT would strand every summary the
                // interrupted attempt had not reached, permanently.
                return;
            }

            if (entityIndices == null)
            {
                return;
            }

            var plan = new Int32[entityIndices.Count];
            for (var i = 0; i < plan.Length; i++)
            {
                plan[i] = entityIndices[i];
            }

            _plan = plan;
            _lastRecorded = 0;
            _spool.WriteProgress(_instanceId, new SpooledProgress
            {
                EmbedEntities = plan,
                Embedded = 0,
            });
        }

        /// <inheritdoc />
        public void EnterPhase(String phase)
        {
            _phase = phase;
            _inner.EnterPhase(phase);
        }

        /// <inheritdoc />
        public void Advance(Int32 done, Int32 total)
        {
            _inner.Advance(done, total);

            if (!String.Equals(_phase, RunPhases.EmbedSummaries, StringComparison.Ordinal) || _plan.Length == 0)
            {
                return;
            }

            // The counter is per CHUNK and is the only per-chunk signal there is, so this is where the cursor
            // moves. Guarded against writing the same number twice, because the phase is entered with a
            // zero-advance and a stalled target may repeat one.
            var embedded = _alreadyEmbedded + Math.Max(0, done);
            if (embedded <= _lastRecorded || embedded > _plan.Length)
            {
                return;
            }

            _lastRecorded = embedded;
            _spool.WriteProgress(_instanceId, new SpooledProgress
            {
                EmbedEntities = _plan,
                Embedded = embedded,
            });
        }
    }
}
