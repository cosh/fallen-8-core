// MIT License
//
// AnalyticsRunGate.cs
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
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;

namespace NoSQL.GraphDB.App.Services
{
    /// <summary>
    ///   The concurrent-run cap for analytics (feature graph-analytics): a run holds a slot for
    ///   its whole duration (including write-back) and the controller answers 429 when none is
    ///   free. Default 1 slot - a whole-graph pass saturates cores and memory bandwidth, so a
    ///   single-operator instance gains nothing from overlapping runs.
    /// </summary>
    public sealed class AnalyticsRunGate
    {
        private readonly SemaphoreSlim _slots;

        public AnalyticsRunGate(IOptions<Fallen8AnalyticsOptions> options)
        {
            _slots = new SemaphoreSlim(options.Value.MaxConcurrentRuns, options.Value.MaxConcurrentRuns);
        }

        /// <summary>Tries to take a slot without waiting; false means 429.</summary>
        public bool TryEnter()
        {
            return _slots.Wait(0);
        }

        /// <summary>Returns the slot taken by <see cref="TryEnter"/>.</summary>
        public void Exit()
        {
            _slots.Release();
        }
    }
}
