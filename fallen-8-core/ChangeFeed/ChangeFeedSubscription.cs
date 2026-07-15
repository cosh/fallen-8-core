// MIT License
//
// ChangeFeedSubscription.cs
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
using System.Threading.Channels;

namespace NoSQL.GraphDB.Core.ChangeFeed
{
    /// <summary>
    ///   One live subscription (feature change-feed): a bounded event queue the dispatcher fills
    ///   and the consumer drains via <see cref="Reader"/>. Backpressure contract: the dispatcher
    ///   NEVER blocks on a slow consumer - on a full queue it stops enqueueing, marks the
    ///   subscription overflowed, and delivers a single <c>resync(overflow)</c> as soon as space
    ///   frees; the consumer then re-fetches. Dispose unregisters the subscription and completes
    ///   the queue.
    /// </summary>
    public sealed class ChangeFeedSubscription : IDisposable
    {
        private readonly ChangeFeedDispatcher _owner;
        private readonly Channel<ChangeEvent> _queue;

        /// <summary>The subscriber's declarative filter (resync bypasses it).</summary>
        internal ChangeFeedFilter Filter
        {
            get;
        }

        /// <summary>Set when the queue overflowed; the next delivery slot carries resync(overflow).
        /// Touched only on the dispatcher (single-threaded fan-out).</summary>
        private bool _overflowed;

        /// <summary>The consumer's end of the queue.</summary>
        public ChannelReader<ChangeEvent> Reader => _queue.Reader;

        internal ChangeFeedSubscription(ChangeFeedDispatcher owner, ChangeFeedFilter filter, int queueSize)
        {
            _owner = owner;
            Filter = filter ?? ChangeFeedFilter.MatchAll;
            _queue = Channel.CreateBounded<ChangeEvent>(new BoundedChannelOptions(Math.Max(1, queueSize))
            {
                SingleWriter = true,  // the dispatcher (and the subscribe-time replay under the same gate)
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait // TryWrite returns false when full - the drop signal
            });
        }

        /// <summary>
        ///   Delivers one event (dispatcher/replay only, under the dispatcher gate). While
        ///   overflowed, live events are skipped until one <c>resync(overflow)</c> lands; the
        ///   resync carries the current event's seq so the consumer sees where continuity resumed.
        /// </summary>
        internal void TryDeliver(ChangeEvent changeEvent)
        {
            if (_overflowed)
            {
                var resync = ChangeEvent.Resync(changeEvent.Ts, ChangeFeedDispatcher.ResyncReasonOverflow);
                resync.Seq = changeEvent.Seq;
                if (!_queue.Writer.TryWrite(resync))
                {
                    // Still full: the pending resync keeps covering everything missed.
                    return;
                }

                _overflowed = false;
                // Fall through: the event that freed the resync may itself still be deliverable.
            }

            if (!Filter.Matches(changeEvent))
            {
                return;
            }

            if (!_queue.Writer.TryWrite(changeEvent))
            {
                // Dropped: the consumer is slow. It owes exactly one resync (set once; the next
                // delivery attempt places it as soon as the queue has room).
                _overflowed = true;
            }
        }

        /// <summary>Completes the queue (dispatcher shutdown / unsubscribe).</summary>
        internal void Complete()
        {
            _queue.Writer.TryComplete();
        }

        /// <summary>Unregisters the subscription and completes its queue.</summary>
        public void Dispose()
        {
            _owner.Unsubscribe(this);
        }
    }
}
