// MIT License
//
// ChangeFeedDispatcher.cs
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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace NoSQL.GraphDB.Core.ChangeFeed
{
    /// <summary>
    ///   The change feed (feature change-feed): receives committed-transaction descriptors from
    ///   the single writer thread via a NON-BLOCKING bounded inbox, and - on its own background
    ///   task - expands them into per-element <see cref="ChangeEvent"/>s, assigns monotonic
    ///   sequence numbers (commit order), appends them to the catch-up ring buffer, and fans out
    ///   to every subscriber's bounded queue.
    ///
    ///   <para>Threading contract: <see cref="Publish"/> is called ONLY by the writer thread and
    ///   only ever does a channel <c>TryWrite</c> (a full inbox sets the lost-events flag; the
    ///   writer is never delayed). Everything else - expansion, sequencing, ring append, fan-out,
    ///   subscribe-time replay - happens under <see cref="_gate"/> on the dispatcher task or a
    ///   subscribing request thread, never on the writer.</para>
    ///
    ///   <para>Delivery contract (spec §1): in-order, at-most-once per connection, with an
    ///   explicit <c>resync</c> whenever continuity was lost. All subscribers observe the same
    ///   total order (ascending seq = commit order); filtering removes events from a view, never
    ///   reorders them.</para>
    /// </summary>
    public sealed class ChangeFeedDispatcher : IDisposable
    {
        #region resync reasons

        public const String ResyncReasonTrim = "trim";
        public const String ResyncReasonTabulaRasa = "tabulaRasa";
        public const String ResyncReasonLoad = "load";
        public const String ResyncReasonDelegateWrite = "delegateWrite";
        public const String ResyncReasonOverflow = "overflow";
        public const String ResyncReasonSeekOutOfRange = "seekOutOfRange";

        #endregion

        #region data

        /// <summary>
        ///   The writer→dispatcher inbox. Bounded so a stalled dispatcher cannot grow it without
        ///   bound; <c>FullMode.Wait</c> makes the writer's <c>TryWrite</c> return false when full
        ///   (the drop signal) instead of silently discarding.
        /// </summary>
        private readonly Channel<ChangeDescriptor> _inbox;

        /// <summary>Set (interlocked) when the inbox dropped a descriptor; the dispatcher turns it
        /// into a resync for the ring and every subscriber once it catches up.</summary>
        private int _lostEvents;

        /// <summary>Guards sequencing + ring + subscriber fan-out/registration. Never touched by
        /// the writer thread.</summary>
        private readonly object _gate = new object();

        private readonly ChangeFeedRingBuffer _ring;

        private readonly List<ChangeFeedSubscription> _subscriptions = new List<ChangeFeedSubscription>();

        private long _lastSeq;

        private readonly Task _dispatchLoop;

        private readonly ChangeFeedOptions _options;

        private readonly ILogger<ChangeFeedDispatcher> _logger;

        private bool _disposed;

        #endregion

        /// <summary>
        ///   The per-process feed epoch: a client cannot mistake a post-restart sequence number
        ///   for a pre-restart one.
        /// </summary>
        public Guid Epoch
        {
            get;
        } = Guid.NewGuid();

        /// <summary>The configured limits (subscriber queue size, max subscribers, buffer size).</summary>
        public ChangeFeedOptions Options => _options;

        /// <summary>The number of live subscriptions (tests/diagnostics).</summary>
        public int SubscriberCount
        {
            get
            {
                lock (_gate)
                {
                    return _subscriptions.Count;
                }
            }
        }

        /// <summary>
        ///   The highest sequence number the dispatcher has processed (tests/diagnostics). A
        ///   subscription without <c>since</c> starts at THIS position - dispatch is asynchronous,
        ///   so it may trail the newest commit briefly; a caller that needs a deterministic start
        ///   passes <c>since</c>.
        /// </summary>
        public long LastSeq
        {
            get
            {
                lock (_gate)
                {
                    return _lastSeq;
                }
            }
        }

        public ChangeFeedDispatcher(ChangeFeedOptions options, ILogger<ChangeFeedDispatcher> logger)
            : this(options, logger, inboxCapacityForTest: null)
        {
        }

        /// <summary>
        ///   Test seam: <paramref name="inboxCapacityForTest"/> shrinks the writer→dispatcher
        ///   inbox so the overflow path can be exercised deterministically (together with
        ///   <see cref="PauseDispatchForTest"/>).
        /// </summary>
        internal ChangeFeedDispatcher(ChangeFeedOptions options, ILogger<ChangeFeedDispatcher> logger,
            Int32? inboxCapacityForTest)
        {
            _options = options ?? new ChangeFeedOptions();
            _logger = logger;
            _ring = new ChangeFeedRingBuffer(_options.BufferSize);

            // The inbox holds DESCRIPTORS (one per committed transaction, possibly many events
            // each); sized like the ring so a briefly-stalled dispatcher rarely drops.
            _inbox = Channel.CreateBounded<ChangeDescriptor>(new BoundedChannelOptions(
                inboxCapacityForTest ?? Math.Max(64, _options.BufferSize))
            {
                SingleWriter = true, // the single writer thread
                SingleReader = true, // the dispatch loop
                FullMode = BoundedChannelFullMode.Wait
            });

            _dispatchLoop = Task.Run(DispatchLoopAsync);
        }

        /// <summary>
        ///   Test seam: holds the dispatch gate so the dispatcher stalls mid-stream (after it
        ///   reads a descriptor, before it processes it), letting a test fill the bounded inbox
        ///   deterministically. Dispose releases the gate. Test-only; never used in production.
        /// </summary>
        internal IDisposable PauseDispatchForTest()
        {
            System.Threading.Monitor.Enter(_gate);
            return new GateHold(_gate);
        }

        private sealed class GateHold : IDisposable
        {
            private object _held;

            internal GateHold(object gate)
            {
                _held = gate;
            }

            public void Dispose()
            {
                var gate = _held;
                _held = null;
                if (gate != null)
                {
                    System.Threading.Monitor.Exit(gate);
                }
            }
        }

        #region writer side

        /// <summary>
        ///   Publishes one committed transaction's descriptor. WRITER THREAD ONLY; called after
        ///   the commit group's WAL fsync. Never blocks and never throws: a full inbox drops the
        ///   descriptor and sets the lost-events flag, which the dispatcher turns into a
        ///   <c>resync</c> for everyone.
        /// </summary>
        internal void Publish(ChangeDescriptor descriptor)
        {
            if (descriptor == null)
            {
                return;
            }

            if (!_inbox.Writer.TryWrite(descriptor))
            {
                Interlocked.Exchange(ref _lostEvents, 1);
            }
        }

        #endregion

        #region dispatcher

        /// <summary>How often an idle dispatcher wakes to convert a pending lost-events flag: the
        /// writer sets the flag AFTER its failed TryWrite, so a dispatcher that drained and parked
        /// inside that window would otherwise defer the resync until the next commit - which may
        /// never come. One timer per second for the whole feed, only while idle.</summary>
        private static readonly TimeSpan IdleWakeInterval = TimeSpan.FromSeconds(1);

        private async Task DispatchLoopAsync()
        {
            try
            {
                var reader = _inbox.Reader;
                while (true)
                {
                    bool hasData;
                    try
                    {
                        using var idleWake = new CancellationTokenSource(IdleWakeInterval);
                        hasData = await reader.WaitToReadAsync(idleWake.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Idle wake: convert a lost-events flag set after the last drain, even
                        // when no further commit ever arrives.
                        DrainLostEventsFlag();
                        continue;
                    }

                    if (!hasData)
                    {
                        break; // inbox completed (dispose)
                    }

                    while (reader.TryRead(out var descriptor))
                    {
                        ProcessDescriptor(descriptor);
                    }

                    // The inbox is drained: if the writer dropped anything since the last check,
                    // continuity was lost for everyone - ring included, so a later ?since= replay
                    // reproduces the resync in order.
                    DrainLostEventsFlag();
                }

                DrainLostEventsFlag();
            }
            catch (Exception ex)
            {
                // The dispatcher must never take the process down; a fault here degrades the feed,
                // not the database - but it must not degrade SILENTLY: complete every subscriber
                // stream so clients observe the end and reconnect (getting a replay or a resync)
                // instead of receiving keepalives forever on a dead feed.
                _logger?.LogError(ex, "The change-feed dispatcher faulted; subscriber streams are completed so clients reconnect.");

                lock (_gate)
                {
                    foreach (var subscription in _subscriptions)
                    {
                        subscription.Complete();
                    }
                    _subscriptions.Clear();
                }
            }
        }

        private void DrainLostEventsFlag()
        {
            if (Interlocked.Exchange(ref _lostEvents, 0) == 1)
            {
                ProcessDescriptor(ChangeDescriptor.ForResync(ResyncReasonOverflow));
            }
        }

        private void ProcessDescriptor(ChangeDescriptor descriptor)
        {
            lock (_gate)
            {
                foreach (var item in descriptor.Items)
                {
                    var changeEvent = new ChangeEvent(descriptor.Ts, item.Kind, item.Element, item.Id,
                        item.Label, item.Key, item.SourceId, item.TargetId, item.ResyncReason)
                    {
                        Seq = ++_lastSeq
                    };

                    _ring.Append(changeEvent);

                    foreach (var subscription in _subscriptions)
                    {
                        subscription.TryDeliver(changeEvent);
                    }
                }
            }
        }

        #endregion

        #region subscriptions

        /// <summary>
        ///   Subscribes with an optional catch-up position. Under the gate, so the replay and the
        ///   registration are atomic with respect to dispatch: nothing falls between the replayed
        ///   window and the live stream.
        ///
        ///   <para>Catch-up semantics (spec §3.5): a <paramref name="sinceSeq"/> whose epoch
        ///   matches and whose window is still buffered replays exactly the missed events
        ///   (filtered) and continues live gap-free. A seek outside the buffered window - or an
        ///   epoch mismatch (a restarted server) - starts with <c>resync(seekOutOfRange)</c>.
        ///   No <paramref name="sinceSeq"/> starts at the live head with no replay.</para>
        /// </summary>
        /// <returns>false when <see cref="ChangeFeedOptions.MaxSubscribers"/> is reached.</returns>
        public bool TrySubscribe(ChangeFeedFilter filter, Guid? sinceEpoch, long? sinceSeq,
            out ChangeFeedSubscription subscription)
        {
            lock (_gate)
            {
                if (_disposed || _subscriptions.Count >= _options.MaxSubscribers)
                {
                    subscription = null;
                    return false;
                }

                subscription = new ChangeFeedSubscription(this, filter, _options.SubscriberQueueSize);

                if (sinceSeq.HasValue)
                {
                    var epochMatches = !sinceEpoch.HasValue || sinceEpoch.Value == Epoch;
                    if (epochMatches && _ring.CanReplayFrom(sinceSeq.Value))
                    {
                        var target = subscription;
                        _ring.ReplayFrom(sinceSeq.Value, e => target.TryDeliver(e));
                    }
                    else
                    {
                        // Continuity cannot be established (evicted window, nonsense seek, or a
                        // different process epoch): say so in-band, then stream live.
                        var resync = ChangeEvent.Resync(DateTime.UtcNow, ResyncReasonSeekOutOfRange);
                        resync.Seq = _lastSeq;
                        subscription.TryDeliver(resync);
                    }
                }

                _subscriptions.Add(subscription);
                return true;
            }
        }

        /// <summary>Unregisters a subscription and completes its queue (idempotent).</summary>
        internal void Unsubscribe(ChangeFeedSubscription subscription)
        {
            lock (_gate)
            {
                _subscriptions.Remove(subscription);
            }

            subscription.Complete();
        }

        #endregion

        /// <summary>
        ///   Stops the dispatcher: completes the inbox, waits for the loop to drain, and
        ///   completes every subscriber queue. Called from the engine's dispose, after the writer
        ///   thread has stopped.
        /// </summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
            }

            _inbox.Writer.TryComplete();
            try
            {
                _dispatchLoop.Wait(TimeSpan.FromSeconds(10));
            }
            catch (AggregateException)
            {
                // The loop's own catch already logged; never throw from dispose.
            }

            lock (_gate)
            {
                foreach (var subscription in _subscriptions)
                {
                    subscription.Complete();
                }
                _subscriptions.Clear();
            }
        }
    }
}
