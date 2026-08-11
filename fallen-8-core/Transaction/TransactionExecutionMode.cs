// MIT License
//
// TransactionExecutionMode.cs
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

namespace NoSQL.GraphDB.Core.Transaction
{
    /// <summary>
    ///   How an engine applies the transactions it is handed. THE home for this explanation.
    ///
    ///   <para>Both modes uphold the same single-writer invariant - every transaction body runs
    ///   alone, start to finish - and share one code path for execution, rollback, terminal state,
    ///   the change feed and the write-ahead log. They differ only in WHICH thread runs the body and
    ///   therefore in what a commit group can contain:</para>
    ///
    ///   <list type="bullet">
    ///     <item><description><b>Threaded</b> - a dedicated writer thread owns the graph; enqueuing
    ///     hands the transaction over and returns immediately, and the writer drains whatever is
    ///     ready into ONE commit group with ONE fsync. This is the server design and the default on
    ///     any host that can start a thread.</description></item>
    ///     <item><description><b>Inline</b> - there is no writer thread: the ENQUEUING thread runs
    ///     the body, flushes it as a commit group of one, and returns a
    ///     <see cref="TransactionInformation" /> that is ALREADY terminal, so nothing ever waits.
    ///     This is what lets the engine be constructed and written to on a host that cannot start a
    ///     thread at all - a single-threaded browser WebAssembly runtime, where
    ///     <c>Thread.Start</c> throws <see cref="System.PlatformNotSupportedException" />. The cost is
    ///     the queue: no group commit (one fsync per transaction with a WAL) and no decoupling of the
    ///     caller from the write.</description></item>
    ///   </list>
    /// </summary>
    public enum TransactionExecutionMode
    {
        /// <summary>
        ///   Use <see cref="Threaded" /> where a writer thread can be started and fall back to
        ///   <see cref="Inline" /> where it cannot. The fallback is a RUNTIME capability check - the
        ///   engine attempts the threaded design and treats a
        ///   <see cref="System.PlatformNotSupportedException" /> from <c>Thread.Start</c> as "this host
        ///   is single-threaded" - so ONE assembly serves every host without a compile-time switch.
        ///   The default.
        /// </summary>
        Automatic = 0,

        /// <summary>
        ///   Require the dedicated writer thread. On a host that cannot start one the
        ///   <see cref="System.PlatformNotSupportedException" /> propagates out of the constructor
        ///   rather than being silently downgraded - for a host that would rather fail loudly than
        ///   run without a decoupled writer.
        /// </summary>
        Threaded = 1,

        /// <summary>
        ///   Require inline execution on the calling thread, even where a writer thread could be
        ///   started. Intended for a host that KNOWS it is single-threaded (and for tests). Callers
        ///   are serialized, so this stays correct if such a host turns out to have more than one
        ///   thread after all - but it gives up group commit, so it is the wrong choice for a server.
        /// </summary>
        Inline = 2
    }
}
