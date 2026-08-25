// MIT License
//
// IRunProgress.cs
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

namespace NoSQL.GraphDB.Integrations.Run
{
    /// <summary>
    ///   Where a run says what it is doing WHILE it does it.
    ///
    ///   <para>It exists because the report is not enough and cannot be: the job route's response is the
    ///   only copy of a report this runtime keeps, and any source worth importing takes longer than the
    ///   proxy in front of it will hold a connection - while the run itself is deliberately built to
    ///   outlive the caller. So without this, a long run is unobservable while it happens and
    ///   unknowable after it ends.</para>
    ///
    ///   <para>Deliberately two methods and no more. A phase is a name an operator recognises, and a
    ///   counter is "how far through that phase"; anything richer would be a second, competing account
    ///   of the run beside the report, and the two would disagree the first time one was updated
    ///   without the other.</para>
    ///
    ///   <para>Implementations must be safe to call from the run's own thread only, must never throw,
    ///   and must never block: a progress sink that can fail is a progress sink that can fail a run
    ///   whose graph writes have already landed.</para>
    /// </summary>
    public interface IRunProgress
    {
        /// <summary>Enters a named phase from <see cref="RunPhases" />, closing the previous one.</summary>
        void EnterPhase(String phase);

        /// <summary>
        ///   How far through the current phase, where the phase has a countable unit. Called as often as
        ///   the run's own batching allows, which is per batch rather than per item.
        /// </summary>
        void Advance(Int32 done, Int32 total);
    }

    /// <summary>
    ///   The phase names, in the order a run passes through them. They live in ONE place because F8
    ///   Studio renders a row per phase and a typo would be a silently missing row rather than a
    ///   failure - and because "the phases" is a contract between the runtime and every reader of it.
    /// </summary>
    public static class RunPhases
    {
        /// <summary>The provider reads the source. No counter: the provider owns that loop.</summary>
        public const String Observe = "observe";

        /// <summary>Envelope and entity validation.</summary>
        public const String Validate = "validate";

        /// <summary>Claim-key lookups against the identity index.</summary>
        public const String Resolve = "resolve";

        /// <summary>Element creation and property writes.</summary>
        public const String WriteElements = "write-elements";

        /// <summary>Edge writes, by derived key.</summary>
        public const String WriteEdges = "write-edges";

        /// <summary>Summary embedding. The long one: it is model inference, not graph work.</summary>
        public const String EmbedSummaries = "embed-summaries";

        /// <summary>Withdrawal by set difference, then deletion on the last claim.</summary>
        public const String Reconcile = "reconcile";

        /// <summary>Every phase, in run order. The Studio renders from this.</summary>
        public static readonly String[] InOrder =
        {
            Observe, Validate, Resolve, WriteElements, WriteEdges, EmbedSummaries, Reconcile,
        };
    }

    /// <summary>
    ///   The sink for every caller that drives a run without watching it - the conformance suite, the
    ///   write-path tests, anything scripted. It is the DEFAULT so that adding progress reporting to
    ///   the run could not change what those callers mean.
    /// </summary>
    public sealed class NoRunProgress : IRunProgress
    {
        /// <summary>The shared instance. It holds nothing, so one is enough.</summary>
        public static readonly NoRunProgress Instance = new NoRunProgress();

        private NoRunProgress()
        {
        }

        /// <inheritdoc />
        public void EnterPhase(String phase)
        {
        }

        /// <inheritdoc />
        public void Advance(Int32 done, Int32 total)
        {
        }
    }
}
