// MIT License
//
// HostCapabilities.cs
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

namespace NoSQL.GraphDB.Core
{
    /// <summary>
    ///   THE one home for "what can this host actually do", answered once per process.
    ///
    ///   <para>Exactly one capability matters to the engine and everything else follows from it: can the
    ///   host run work on a thread other than the caller's? A single-threaded browser WebAssembly runtime
    ///   cannot - <c>Thread.Start</c> throws <see cref="PlatformNotSupportedException" /> there - and that
    ///   one fact decides three separate designs: transactions run inline on the calling thread rather than
    ///   on a writer thread (<see cref="Transaction.TransactionExecutionMode" />), the change feed's
    ///   teardown does not wait for its dispatch loop, and a checkpoint fans out sequentially instead of
    ///   over pooled tasks. Each of those three used to reason about the host on its own; this is the
    ///   single place the question is asked, so the three cannot disagree.</para>
    ///
    ///   <para>WHY A RUNTIME PROBE, not <c>#if BROWSER</c> and not <c>OperatingSystem.IsBrowser()</c>: one
    ///   assembly ships to every host through the <c>Fallen-8</c> package, so a compile-time switch cannot
    ///   express it; and a browser build WITH threads enabled is perfectly capable, so naming the operating
    ///   system would misclassify it. The probe asks the operation that fails, which is the only question
    ///   whose answer is always right.</para>
    ///
    ///   <para>The probe costs one short-lived background thread, once, on hosts that can start one. It is
    ///   deliberately NOT used by <see cref="Transaction.TransactionManager" />, which needs to start its
    ///   writer thread anyway and so probes by simply doing it and catching - the same answer, arrived at
    ///   without an extra thread.</para>
    /// </summary>
    internal static class HostCapabilities
    {
        /// <summary>
        ///   Whether work can run on a thread other than the caller's, so that blocking the caller on it
        ///   can ever complete. False on a single-threaded WebAssembly runtime, where a blocking wait is a
        ///   wait for work that cannot start until the wait returns.
        /// </summary>
        internal static readonly Boolean SupportsBackgroundWork = ProbeBackgroundWork();

        private static Boolean ProbeBackgroundWork()
        {
            try
            {
                var probe = new Thread(() => { })
                {
                    IsBackground = true,
                    Name = "Fallen8-Capability-Probe"
                };
                probe.Start();
                return true;
            }
            catch (PlatformNotSupportedException)
            {
                return false;
            }
        }
    }
}
