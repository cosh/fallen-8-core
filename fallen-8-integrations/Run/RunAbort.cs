// MIT License
//
// RunAbort.cs
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
using System.Threading;

namespace NoSQL.GraphDB.Integrations.Run
{
    /// <summary>
    ///   The run's OWN stop signal: "the operator asked for this to stop."
    ///
    ///   <para>THREE SIGNALS CAN END A RUN AND THEY MEAN THREE DIFFERENT THINGS. The CALLER'S token means
    ///   "nobody is listening any more", which a run ignores from the moment it starts writing - closing a
    ///   browser must not stop an import, and <c>JobRunner</c> states that at the point of no return. The
    ///   HOST'S stopping token means "this process is going away", which is an interruption rather than a
    ///   decision. This one is the only signal that ends a run early ON PURPOSE, and the only one whose
    ///   outcome is a run that reports itself cancelled rather than failed or abandoned.</para>
    ///
    ///   <para>It is a type of its own, and not the <see cref="CancellationToken" /> it wraps, because the
    ///   obvious implementation is the dangerous one. A token handed to an in-flight graph write ABORTS
    ///   that write, and a create whose answer never arrives is the one state this runtime cannot heal by
    ///   itself: the elements exist, nothing indexed them, so the next resolve cannot find them and
    ///   duplicates them instead - permanently, because reconciliation withdraws by set difference over an
    ///   index that never named them. So a stop is observed BETWEEN calls, at points where what is left
    ///   behind is a state some later run converges, and this type deliberately exposes no way to do
    ///   anything else with it. The one exception is the source READ, which is cancellable mid-call because
    ///   aborting a read writes nothing; that is why <c>JobRunner</c> hands the provider a real token and
    ///   the applier only ever gets this.</para>
    /// </summary>
    public readonly struct RunAbort
    {
        private readonly CancellationToken _cancel;
        private readonly CancellationToken _shutdown;

        /// <param name="cancel">The operator asked for this run to stop. <c>default</c> never fires.</param>
        /// <param name="shutdown">
        ///   The PROCESS is going away, which stops a run at the same places but means something else
        ///   entirely: the work is not finished with, it is interrupted, and it is picked up again on the
        ///   next start. That difference is why the two arrive as separate tokens rather than one - a
        ///   restart that reported its runs cancelled would tell an operator their import had been stopped
        ///   when it is about to continue, and would drop the spooled entry that lets it.
        /// </param>
        public RunAbort(CancellationToken cancel, CancellationToken shutdown = default)
        {
            _cancel = cancel;
            _shutdown = shutdown;
        }

        /// <summary>Whether a stop of either kind has been asked for. Never blocks and never throws.</summary>
        public Boolean Requested
        {
            get { return _cancel.IsCancellationRequested || _shutdown.IsCancellationRequested; }
        }

        /// <summary>
        ///   Ends the run HERE when a stop has been asked for.
        ///
        ///   <para>Call it only where the state left behind is one a later run converges. There are two
        ///   places it must NOT be called, and both are stated where they are: between a create and the
        ///   index flush that makes what was created findable, and anywhere inside reconciliation.</para>
        ///
        ///   <para>Cancellation WINS over shutdown when both fired, because it is the more specific
        ///   statement: somebody decided this import should not finish, where a shutdown says only that
        ///   this process cannot be the one to finish it.</para>
        /// </summary>
        /// <param name="summariesWritten">What the embedding loop had written when it stopped, for the one
        /// caller that loops. It rides on the exception because only that loop knows it.</param>
        /// <exception cref="RunCancelledException">The operator asked for the run to stop.</exception>
        /// <exception cref="RunInterruptedException">The process is shutting down.</exception>
        public void ThrowIfRequested(Int32 summariesWritten = 0)
        {
            if (_cancel.IsCancellationRequested)
            {
                throw new RunCancelledException { SummariesWritten = summariesWritten };
            }

            if (_shutdown.IsCancellationRequested)
            {
                throw new RunInterruptedException { SummariesWritten = summariesWritten };
            }
        }
    }

    /// <summary>
    ///   The process's own "I am going away" signal, as one injectable thing.
    ///
    ///   <para>A type rather than a raw token so the run machinery does not take a dependency on the
    ///   hosting abstractions to learn one fact, and so a test can drive a shutdown without a host. The
    ///   default never fires, which is what every caller that is not a hosted runtime means.</para>
    /// </summary>
    public sealed class RunShutdown
    {
        /// <summary>A process that is not going anywhere. The default for tests and for the conformance suite.</summary>
        public static readonly RunShutdown Never = new RunShutdown(CancellationToken.None);

        public RunShutdown(CancellationToken token)
        {
            Token = token;
        }

        /// <summary>Fires when this process has begun shutting down.</summary>
        public CancellationToken Token { get; }
    }

    /// <summary>
    ///   A run that stopped at a safe point rather than finishing. Two reasons, two subclasses, because
    ///   what happens next differs completely: a CANCELLED run is over, and an INTERRUPTED one is resumed.
    /// </summary>
    public abstract class RunStoppedException : Exception
    {
        protected RunStoppedException(String message)
            : base(message)
        {
        }

        /// <summary>
        ///   How many entity summaries were embedded before the stop, for the one phase that loops inside
        ///   the target. Zero everywhere else, which is also the truth everywhere else. It rides on the
        ///   exception for the reason <see cref="Graph.GraphTargetException.SummariesWritten" /> does: the
        ///   chunks that landed put real vectors on real elements, and a report claiming zero would be
        ///   false about state a bound index answers searches over.
        /// </summary>
        public Int32 SummariesWritten { get; set; }
    }

    /// <summary>
    ///   Raised at the safe point where a run stopped because it was CANCELLED.
    ///
    ///   <para>Its own type rather than an <see cref="OperationCanceledException" />, because that type
    ///   already means something else here and the two are not distinguishable at a catch site: a
    ///   client-side timeout on a graph write surfaces as one, and so does the caller walking away. Only
    ///   this type means "somebody asked, and the run stopped where stopping was safe".</para>
    /// </summary>
    public sealed class RunCancelledException : RunStoppedException
    {
        public RunCancelledException()
            : base("The run was cancelled. What it had already written stands, and it deliberately did " +
                   "NOT reconcile: a cancelled run's claimed set is missing everything it never reached, " +
                   "so withdrawing by set difference over it would delete healthy elements the source " +
                   "still describes. The next completed run of this identity converges the graph.")
        {
        }
    }

    /// <summary>
    ///   Raised at the safe point where a run stopped because THIS PROCESS is going away.
    ///
    ///   <para>Not a cancellation and not a failure: the work is unfinished rather than abandoned. What it
    ///   had written stands, its spooled entry is deliberately KEPT, and the next start picks the run up
    ///   from the embedding cursor. That is what turns a container restart from hours lost into seconds
    ///   lost, and it is why this is a type of its own rather than a flag on the cancellation.</para>
    /// </summary>
    public sealed class RunInterruptedException : RunStoppedException
    {
        public RunInterruptedException()
            : base("The run was interrupted because this process is shutting down. What it had written " +
                   "stands, it did not reconcile, and its spooled entry is kept: the next start resumes it " +
                   "from where the embedding got to.")
        {
        }
    }
}
