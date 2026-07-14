// MIT License
//
// DelegateTransaction.cs
//
// Copyright (c) 2025 Henning Rauch
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

namespace NoSQL.GraphDB.Core.Transaction
{
    /// <summary>
    ///   The sanctioned escape hatch that lets a plugin (anything holding <see cref="IFallen8Write"/>)
    ///   run a COMPOSED mutation against the live graph without opening the frozen
    ///   <see cref="ATransaction"/> vocabulary (feature plugin-write-transactions). It is ONE new
    ///   built-in transaction whose body is a plugin-supplied delegate; it is NOT a third-party
    ///   <see cref="ATransaction"/> subclass (<c>TryExecute</c>/<c>Rollback</c> stay internal, the
    ///   built-ins stay sealed).
    ///
    ///   <para>
    ///   Usage: <c>f8.EnqueueTransaction(new DelegateTransaction(ctx =&gt; { ... }))</c> then
    ///   <c>WaitUntilFinished()</c>. Because it is executed by the single <c>TransactionManager</c>
    ///   writer, the body runs ON THE WRITER THREAD by construction, through the
    ///   <see cref="IFallen8WriterContext"/> it is handed - the plugin gets no other way in, and the
    ///   context is invalidated the moment the body returns.
    ///   </para>
    ///
    ///   <para>
    ///   Durability - mode (a), the only mode implemented: a <see cref="DelegateTransaction"/> is NOT
    ///   recognised by the WAL codec, so (exactly like <c>Save</c>/<c>Load</c>) it is not appended to
    ///   the log and its effect - standard vertices/edges/properties - is durable via the next full
    ///   snapshot. It is reported <see cref="TransactionInformation.Durable"/> because there is nothing
    ///   for the log to miss. Mode (b) (an opt-in serialisable descriptor + deterministic replay,
    ///   registered with the codec) is a documented follow-on and is not implemented here.
    ///   </para>
    ///
    ///   <para>
    ///   Atomicity: a body that throws (or whose failure the manager maps to a rollback) has NO
    ///   observable effect for the create/property surface - the context journals every create and
    ///   property change and <see cref="Rollback"/> replays the inverses (honouring
    ///   transaction-atomicity's "RolledBack =&gt; no effect"). A body that also REMOVES graph elements
    ///   has the weaker guarantee documented on <see cref="IFallen8WriterContext.TryRemoveGraphElement"/>.
    ///   </para>
    /// </summary>
    public sealed class DelegateTransaction : ATransaction
    {
        private readonly Action<IFallen8WriterContext> _body;
        private readonly String _name;

        private DelegateWriterContext _context;

        /// <summary>
        ///   Creates a delegate transaction whose <paramref name="body"/> composes the mutation via the
        ///   <see cref="IFallen8WriterContext"/> it receives. <paramref name="name"/> is an optional
        ///   label for diagnostics.
        /// </summary>
        public DelegateTransaction(Action<IFallen8WriterContext> body, String name = null)
        {
            _body = body ?? throw new ArgumentNullException(nameof(body));
            _name = name;
        }

        /// <summary>An optional caller-supplied label for this transaction (diagnostics only).</summary>
        public String Name => _name;

        internal override Boolean TryExecute(Fallen8 f8)
        {
            var context = new DelegateWriterContext(f8);
            _context = context;
            try
            {
                _body(context);
                return true;
            }
            finally
            {
                // The context is only valid for the body's duration; a plugin cannot stash it and mutate
                // off the writer thread later. Invalidated even on a throwing body (which then propagates
                // to the manager -> Error + InternalError + Rollback).
                context.Invalidate();
            }
        }

        internal override void Rollback(Fallen8 f8)
        {
            // Replays the context's journalled inverses (restore properties, remove created elements).
            // A no-op when the body created nothing before it faulted.
            _context?.Compensate();
        }

        internal override void Cleanup()
        {
            // Drop the context (and its journal) once the transaction is no longer observable. The
            // created elements are already owned by the master store; only the journal is released.
            _context = null;
        }
    }
}
