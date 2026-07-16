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
using System.Threading.Tasks;

namespace NoSQL.GraphDB.Core.ChangeFeed
{
    /// <summary>
    ///   One live subscription (feature change-feed): a bounded event queue the dispatcher fills
    ///   and the consumer drains via <see cref="Reader"/>. Backpressure contract: the dispatcher
    ///   NEVER blocks on a slow consumer - on a full queue it stops enqueueing, marks the
    ///   subscription overflowed, and a waiter delivers a single <c>resync(overflow)</c> AS SOON
    ///   AS the consumer frees queue space (no further commit required - an idle tail after a
    ///   burst may not produce one, and continuity loss must never stay unsignaled). The owed
    ///   resync carries the sequence number of the LAST DROPPED event, so a connection's ids stay
    ///   strictly ascending. Dispose unregisters the subscription and completes the queue.
    /// </summary>
    public sealed class ChangeFeedSubscription : IDisposable
    {
        private readonly ChangeFeedDispatcher _owner;
        private readonly Channel<ChangeEvent> _queue;

        /// <summary>
        ///   Serializes delivery state (<see cref="_overflowed"/>, <see cref="_lastDroppedSeq"/>)
        ///   between the dispatcher's <see cref="TryDeliver"/> (called under the dispatcher gate)
        ///   and the owed-resync waiter (a thread-pool continuation). Never taken on the writer
        ///   thread and never held across an await.
        /// </summary>
        private readonly object _sync = new object();

        /// <summary>The subscriber's declarative filter (resync bypasses it).</summary>
        internal ChangeFeedFilter Filter
        {
            get;
        }

        /// <summary>Set when the queue overflowed; cleared when the owed resync lands.</summary>
        private bool _overflowed;

        /// <summary>The sequence number of the newest event dropped while overflowed - the
        /// position the owed resync reports.</summary>
        private long _lastDroppedSeq;

        /// <summary>Whether an owed-resync waiter is currently running (at most one).</summary>
        private bool _resyncWaiterActive;

        /// <summary>The consumer's end of the queue.</summary>
        public ChannelReader<ChangeEvent> Reader => _queue.Reader;

        internal ChangeFeedSubscription(ChangeFeedDispatcher owner, ChangeFeedFilter filter, int queueSize)
        {
            _owner = owner;
            Filter = filter ?? ChangeFeedFilter.MatchAll;
            _queue = Channel.CreateBounded<ChangeEvent>(new BoundedChannelOptions(Math.Max(1, queueSize))
            {
                // Both the dispatcher (under its gate) and the owed-resync waiter write; reads are
                // the single consumer.
                SingleWriter = false,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait // TryWrite returns false when full - the drop signal
            });
        }

        /// <summary>
        ///   Delivers one event (dispatcher/replay only). While overflowed, live events are
        ///   dropped - they are part of the loss the owed resync covers - and the waiter places
        ///   that resync the moment the consumer frees space.
        /// </summary>
        internal void TryDeliver(ChangeEvent changeEvent)
        {
            lock (_sync)
            {
                if (_overflowed)
                {
                    _lastDroppedSeq = changeEvent.Seq;
                    return;
                }

                if (!Filter.Matches(changeEvent))
                {
                    return;
                }

                if (!_queue.Writer.TryWrite(changeEvent))
                {
                    // Dropped: the consumer is slow. It is owed exactly one resync, delivered as
                    // soon as queue space frees - independent of any future commit.
                    _overflowed = true;
                    _lastDroppedSeq = changeEvent.Seq;
                    StartOwedResyncWaiter();
                }
            }
        }

        /// <summary>
        ///   Starts the single waiter that places the owed <c>resync(overflow)</c> once the
        ///   consumer frees queue space. Runs on the thread pool - never the writer thread, never
        ///   the dispatcher gate.
        /// </summary>
        private void StartOwedResyncWaiter()
        {
            if (_resyncWaiterActive)
            {
                return;
            }

            _resyncWaiterActive = true;
            _ = DeliverOwedResyncAsync();
        }

        private async Task DeliverOwedResyncAsync()
        {
            try
            {
                while (true)
                {
                    // Completes when space is available (the consumer read) or the channel
                    // completed (unsubscribe/dispose - nothing left to signal).
                    var canWrite = await _queue.Writer.WaitToWriteAsync().ConfigureAwait(false);

                    lock (_sync)
                    {
                        if (!canWrite || !_overflowed)
                        {
                            _resyncWaiterActive = false;
                            return;
                        }

                        var resync = ChangeEvent.Resync(DateTime.UtcNow, ChangeFeedDispatcher.ResyncReasonOverflow);
                        resync.Seq = _lastDroppedSeq;

                        if (_queue.Writer.TryWrite(resync))
                        {
                            _overflowed = false;
                            _resyncWaiterActive = false;
                            return;
                        }

                        // Space vanished between the wait and the write (only this waiter and the
                        // dispatcher write, and the dispatcher drops while overflowed - so this is
                        // a completed-channel race at teardown); loop and re-wait.
                    }
                }
            }
            catch (ChannelClosedException)
            {
                lock (_sync)
                {
                    _resyncWaiterActive = false;
                }
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
