// MIT License
//
// ShortestPathDefinition.cs
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
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace NoSQL.GraphDB.Core.Algorithms.Path
{
    /// <summary>
    /// Defines the parameters for shortest path calculation
    /// </summary>
    public sealed class ShortestPathDefinition
    {
        /// <summary>
        /// Gets or sets the source vertex identifier
        /// </summary>
        public Int32 SourceVertexId
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the destination vertex identifier
        /// </summary>
        public Int32 DestinationVertexId
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the maximum depth
        /// </summary>
        public Int32 MaxDepth { get; set; } = 1;

        /// <summary>
        /// Gets or sets the maximum path weight
        /// </summary>
        public Double MaxPathWeight { get; set; } = Double.MaxValue;

        /// <summary>
        /// Gets or sets the maximum number of results
        /// </summary>
        public Int32 MaxResults { get; set; } = 1;

        /// <summary>
        /// Gets or sets the edge property filter
        /// </summary>
        public Delegates.EdgePropertyFilter EdgePropertyFilter
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the vertex filter
        /// </summary>
        public Delegates.VertexFilter VertexFilter
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the edge filter
        /// </summary>
        public Delegates.EdgeFilter EdgeFilter
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the edge cost delegate
        /// </summary>
        public Delegates.EdgeCost EdgeCost
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the vertex cost delegate
        /// </summary>
        public Delegates.VertexCost VertexCost
        {
            get; set;
        }

        #region cooperative execution budget

        /// <summary>How often (in traversal steps - examined edges, dequeued search states) the
        /// deadline and the cancellation token are sampled inside a hot loop. A power of two, so
        /// <see cref="PathBudget"/> masks instead of divides (the same sampling idiom as
        /// <c>GraphAnalyticsDefinition.BudgetCheckInterval</c>).</summary>
        public const Int32 BudgetCheckInterval = 1_024;

        /// <summary>
        ///   Wall-clock budget for the whole calculation. Zero or negative - the DEFAULT - means
        ///   unbounded, so a definition that names no budget runs exactly as it always did.
        ///   Exhaustion is not a partial result: the calculation reports no paths and sets
        ///   <see cref="BudgetExhausted"/> (the REST layer answers 408).
        /// </summary>
        /// <remarks>
        ///   HONEST LIMIT: the budget is COOPERATIVE - it is sampled between traversal steps, i.e.
        ///   between invocations of the filter/cost delegates. It bounds an algorithmic blow-up and
        ///   lets a caller that gave up stop the work; it CANNOT interrupt a single delegate call
        ///   that never returns (a compiled fragment runs in-process with full trust, so a
        ///   never-returning fragment still holds the thread). Only real isolation would close
        ///   that, see features/done/dynamic-code-resource-limits.
        /// </remarks>
        public TimeSpan TimeBudget
        {
            get; set;
        }

        /// <summary>
        ///   Cap on the traversal steps the calculation may take (an examined edge in the
        ///   frontier walk, a dequeued Dijkstra state). Zero or negative - the DEFAULT - means
        ///   unbounded. Unlike <see cref="TimeBudget"/> it trips exactly, which makes a bound
        ///   reproducible across machines; the same
        ///   <see cref="BudgetExhausted"/>/no-partial-result rule applies.
        /// </summary>
        public Int32 StepBudget
        {
            get; set;
        }

        /// <summary>
        ///   Cooperative cancellation, checked together with <see cref="TimeBudget"/>. The REST
        ///   layer links this to the request abort so a client that gives up stops the traversal.
        ///   The same honest limit as <see cref="TimeBudget"/> applies (checked BETWEEN delegate
        ///   calls).
        /// </summary>
        public CancellationToken CancellationToken
        {
            get; set;
        }

        /// <summary>
        ///   Set by the algorithm when the budget (deadline, step cap or cancellation) stopped the
        ///   run; reset at the start of every calculation, so a definition instance describes ONE
        ///   run. When it is true the calculation reported no paths even though the graph may hold
        ///   some - that is a timeout, not a "no path exists" answer, and callers must distinguish
        ///   the two (the REST surface answers 408 instead of an empty 200).
        /// </summary>
        public Boolean BudgetExhausted
        {
            get; internal set;
        }

        #endregion
    }

    /// <summary>
    ///   The cooperative run budget of one path calculation: wall-clock deadline, step cap and
    ///   cancellation, sampled between traversal steps. It mirrors the analytics budget idiom
    ///   (<c>AGraphAnalyticsAlgorithm.BudgetGuard</c>) with one deliberate difference - it counts
    ///   the steps itself, because a path search has no single vertex loop to count in - and it
    ///   publishes its verdict through <see cref="ShortestPathDefinition.BudgetExhausted"/> so the
    ///   <c>IShortestPathAlgorithm</c> signature stays unchanged.
    ///
    ///   <para>What it can and cannot do is documented once, on
    ///   <see cref="ShortestPathDefinition.TimeBudget"/>.</para>
    /// </summary>
    internal sealed class PathBudget
    {
        private readonly ShortestPathDefinition _definition;
        private readonly Stopwatch _stopwatch;
        private readonly TimeSpan _timeBudget;
        private readonly Int32 _stepBudget;
        private readonly CancellationToken _cancellationToken;

        /// <summary>No deadline, no step cap and a token that can never be cancelled: the
        /// historical unbounded run, which must keep the loops it always had.</summary>
        private readonly Boolean _unbounded;

        private Int32 _steps;

        private PathBudget(ShortestPathDefinition definition)
        {
            _definition = definition;
            _timeBudget = definition.TimeBudget;
            _stepBudget = definition.StepBudget;
            _cancellationToken = definition.CancellationToken;
            _unbounded = _timeBudget <= TimeSpan.Zero && _stepBudget <= 0 && !_cancellationToken.CanBeCanceled;
            //one stopwatch per calculation (never per step), so an unbounded run pays nothing measurable
            _stopwatch = Stopwatch.StartNew();
        }

        /// <summary>Starts the budget of one calculation and clears the previous run's verdict.</summary>
        internal static PathBudget Create(ShortestPathDefinition definition)
        {
            definition.BudgetExhausted = false;
            return new PathBudget(definition);
        }

        /// <summary>The sticky verdict: whether a checkpoint has already observed exhaustion. A
        /// plain field read (no clock, no counting), for the sites that only need to unwind.</summary>
        internal Boolean IsExhausted => _definition.BudgetExhausted;

        /// <summary>Checks the deadline and the token NOW, unconditionally. For the coarse sites -
        /// the start of a calculation, a BLS frontier level, a Yen iteration - where one clock read
        /// per call is free and a deterministic stop is worth more than sampling.</summary>
        internal Boolean IsExhaustedNow()
        {
            if (_definition.BudgetExhausted)
            {
                return true;
            }

            if (_unbounded)
            {
                return false;
            }

            if (_cancellationToken.IsCancellationRequested ||
                (_timeBudget > TimeSpan.Zero && _stopwatch.Elapsed >= _timeBudget))
            {
                _definition.BudgetExhausted = true;
                return true;
            }

            return false;
        }

        /// <summary>
        ///   Counts one traversal step and reports whether the run must stop: the step cap trips
        ///   exactly, the deadline and the token are sampled every
        ///   <see cref="ShortestPathDefinition.BudgetCheckInterval"/> steps. Inlined and guarded by
        ///   the unbounded fast path, so an unbudgeted traversal keeps its old hot loop.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Boolean IsExhaustedAtStep()
        {
            if (_unbounded)
            {
                return false;
            }

            if (_definition.BudgetExhausted)
            {
                return true;
            }

            var steps = ++_steps;

            if (_stepBudget > 0 && steps > _stepBudget)
            {
                _definition.BudgetExhausted = true;
                return true;
            }

            return (steps & (ShortestPathDefinition.BudgetCheckInterval - 1)) == 0 && IsExhaustedNow();
        }
    }
}
