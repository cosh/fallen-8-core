// MIT License
//
// StartupState.cs
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

using System.Threading;

namespace NoSQL.GraphDB.App.Services
{
    /// <summary>
    ///   The readiness flag behind GET /readyz (feature observability):
    ///   <see cref="DurabilityLifecycleService"/> marks it once load-at-startup completes
    ///   (immediately in volatile mode). Honest note: hosted services complete StartAsync
    ///   BEFORE Kestrel accepts connections in the current wiring, so today ready is
    ///   equivalent to live once the server answers - the endpoint still pins the contract,
    ///   serves orchestrator convention, and stays correct if startup load ever becomes
    ///   asynchronous.
    /// </summary>
    public sealed class StartupState
    {
        private int _ready;

        /// <summary>Whether load-at-startup has completed.</summary>
        public bool IsReady => Volatile.Read(ref _ready) == 1;

        /// <summary>Marks startup complete (idempotent).</summary>
        public void MarkReady()
        {
            Volatile.Write(ref _ready, 1);
        }
    }
}
