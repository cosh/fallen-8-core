// MIT License
//
// AgentFeed.cs
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
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.Agents.Configuration;

namespace NoSQL.GraphDB.Agents.Runtime
{
    /// <summary>The six things the feed reports. Nothing else is an event, deliberately: a model
    /// call is a TRACE step, because a subscriber watching a swarm does not want one per step.</summary>
    public enum AgentEventKind
    {
        AgentSpawned = 0,
        AgentStateChanged = 1,
        AgentMessage = 2,
        ToolCalled = 3,
        AgentCompleted = 4,
        AgentFailed = 5,
    }

    /// <summary>
    ///   One feed event.
    ///
    ///   <para>
    ///     Flat, with absent fields omitted, for the reason <see cref="TraceStep" /> is: one
    ///     definition of an event rather than six, and a reader sees only what applies. The overlap
    ///     with a trace step is deliberate and one-directional - a step is the record, an event is
    ///     the notification, and where both carry a capture it is the SAME capped string, capped
    ///     once where the capping is defined.
    ///   </para>
    /// </summary>
    public sealed class AgentEvent
    {
        /// <summary>Position in this host's feed, across all agents. Monotonic from process start,
        /// which is why the id a subscriber sees is prefixed with the host instance.</summary>
        [JsonPropertyName("seq")]
        public Int64 Seq
        {
            get; set;
        }

        [JsonPropertyName("ts")]
        public DateTimeOffset Ts
        {
            get; set;
        }

        [JsonPropertyName("kind")]
        public String Kind { get; set; } = String.Empty;

        [JsonPropertyName("agentId")]
        public String AgentId { get; set; } = String.Empty;

        [JsonPropertyName("parentId")]
        public String? ParentId
        {
            get; set;
        }

        [JsonPropertyName("role")]
        public String? Role
        {
            get; set;
        }

        [JsonPropertyName("name")]
        public String? Name
        {
            get; set;
        }

        // ---- on EVERY kind, not only the transitions: a subscriber rendering a list should not
        // have to join against the listing to label a row, nor poll to price one. This header used
        // to read "agentStateChanged, and carried on every ending too", and these headers ARE the
        // contract a reader uses to know which fields apply to which kind, so it said a toolCalled
        // frame carries no counters when every frame does.

        [JsonPropertyName("state")]
        public String? State
        {
            get; set;
        }

        [JsonPropertyName("budget")]
        public String? Budget
        {
            get; set;
        }

        /// <summary>
        ///   The counters, on every event of every kind. Present so a subscriber can render live
        ///   cost WITHOUT polling, which is the whole reason they are here rather than only on the
        ///   listing, and that reason applies to a tool call as much as to a transition. There are
        ///   five of them plus a flag, not "the four" this said: the flag is what separates a
        ///   backend reporting zero from one reporting nothing.
        /// </summary>
        [JsonPropertyName("tokens")]
        public EventCounters? Tokens
        {
            get; set;
        }

        // ---- agentMessage

        [JsonPropertyName("direction")]
        public String? Direction
        {
            get; set;
        }

        [JsonPropertyName("messageId")]
        public String? MessageId
        {
            get; set;
        }

        [JsonPropertyName("inReplyTo")]
        public String? InReplyTo
        {
            get; set;
        }

        [JsonPropertyName("text")]
        public String? Text
        {
            get; set;
        }

        // ---- toolCalled

        [JsonPropertyName("toolCallId")]
        public String? ToolCallId
        {
            get; set;
        }

        [JsonPropertyName("tool")]
        public String? Tool
        {
            get; set;
        }

        /// <summary>A capped SUMMARY, never the full payload. The trace is where a reviewer goes for
        /// more, and past the trace's own caps the graph is.</summary>
        [JsonPropertyName("arguments")]
        public String? Arguments
        {
            get; set;
        }

        [JsonPropertyName("result")]
        public String? Result
        {
            get; set;
        }

        [JsonPropertyName("truncated")]
        public Boolean? Truncated
        {
            get; set;
        }

        [JsonPropertyName("argumentsBytes")]
        public Int64? ArgumentsBytes
        {
            get; set;
        }

        [JsonPropertyName("resultBytes")]
        public Int64? ResultBytes
        {
            get; set;
        }

        [JsonPropertyName("success")]
        public Boolean? Success
        {
            get; set;
        }

        /// <summary>
        ///   Wall clock in milliseconds. On an ENDING it is the whole run, on a
        ///   <c>toolCalled</c> the call, and on an <c>agentStateChanged</c> the run so far. Absent
        ///   on a spawn, where it would always be zero.
        /// </summary>
        [JsonPropertyName("durationMs")]
        public Int64? DurationMs
        {
            get; set;
        }

        // ---- agentCompleted and agentFailed

        [JsonPropertyName("resultText")]
        public String? ResultText
        {
            get; set;
        }

        [JsonPropertyName("failure")]
        public String? Failure
        {
            get; set;
        }

        [JsonPropertyName("citations")]
        public CitationCounts? Citations
        {
            get; set;
        }
    }

    /// <summary>What an agent has spent, as a feed event carries it.</summary>
    public sealed class EventCounters
    {
        [JsonPropertyName("input")]
        public Int64 Input
        {
            get; set;
        }

        [JsonPropertyName("output")]
        public Int64 Output
        {
            get; set;
        }

        [JsonPropertyName("total")]
        public Int64 Total
        {
            get; set;
        }

        [JsonPropertyName("steps")]
        public Int64 Steps
        {
            get; set;
        }

        [JsonPropertyName("toolCalls")]
        public Int64 ToolCalls
        {
            get; set;
        }

        /// <summary>True when at least one step's usage went unreported, so the totals are a floor.</summary>
        [JsonPropertyName("unreportedUsage")]
        public Boolean UnreportedUsage
        {
            get; set;
        }
    }

    /// <summary>
    ///   The mechanical citation count. Not a judgement and the naming says so: <c>valid</c> is a
    ///   citation naming a tool this run actually called, <c>dangling</c> is one that names nothing
    ///   in the trace. No judge model and no claim schema; it exists so a reviewer can see at a
    ///   glance whether an answer points back at work that happened.
    /// </summary>
    public sealed class CitationCounts
    {
        [JsonPropertyName("valid")]
        public Int32 Valid
        {
            get; set;
        }

        [JsonPropertyName("dangling")]
        public Int32 Dangling
        {
            get; set;
        }
    }

    /// <summary>
    ///   Which events a subscriber asked for. Built by the parser, never by a caller, so an unknown
    ///   value cannot reach it: the parser refuses instead, and a refusal is the whole point.
    /// </summary>
    public sealed class AgentFeedFilter
    {
        internal AgentFeedFilter(IReadOnlyCollection<String>? agents, IReadOnlyCollection<AgentEventKind>? kinds)
        {
            Agents = agents;
            Kinds = kinds;
        }

        /// <summary>Null means every agent.</summary>
        public IReadOnlyCollection<String>? Agents
        {
            get;
        }

        /// <summary>Null means every kind.</summary>
        public IReadOnlyCollection<AgentEventKind>? Kinds
        {
            get;
        }

        /// <summary>Everything.</summary>
        public static AgentFeedFilter All { get; } = new AgentFeedFilter(null, null);

        public Boolean Admits(AgentEvent candidate)
        {
            if (Kinds != null && !Kinds.Any(k => String.Equals(AgentEventKinds.Wire(k), candidate.Kind,
                    StringComparison.Ordinal)))
            {
                return false;
            }

            // A filter on agents matches the agent itself OR its parent, so subscribing to an
            // orchestrator shows the swarm it is running rather than only its own two events.
            return Agents == null
                || Agents.Contains(candidate.AgentId, StringComparer.Ordinal)
                || (candidate.ParentId != null && Agents.Contains(candidate.ParentId, StringComparer.Ordinal));
        }
    }

    /// <summary>The wire spellings, and the parser that is the only way to build a filter.</summary>
    public static class AgentEventKinds
    {
        private static readonly IReadOnlyDictionary<String, AgentEventKind> ByName =
            new Dictionary<String, AgentEventKind>(StringComparer.OrdinalIgnoreCase)
            {
                ["agentSpawned"] = AgentEventKind.AgentSpawned,
                ["agentStateChanged"] = AgentEventKind.AgentStateChanged,
                ["agentMessage"] = AgentEventKind.AgentMessage,
                ["toolCalled"] = AgentEventKind.ToolCalled,
                ["agentCompleted"] = AgentEventKind.AgentCompleted,
                ["agentFailed"] = AgentEventKind.AgentFailed,
            };

        /// <summary>Every kind's name, for the message a refusal carries.</summary>
        public static IReadOnlyCollection<String> Names => (IReadOnlyCollection<String>)ByName.Keys;

        /// <summary>
        ///   The kinds this host can actually EMIT today, which is not all of them.
        ///
        ///   <para>
        ///     <c>agentMessage</c> is accepted by the filter and emitted by nothing. The swarm
        ///     shipped and publishes none of them: a worker's spawn, its ending and its result are
        ///     feed events and trace steps of their own, so nothing needed a message. The one thing
        ///     left that would publish one is the deferred conversation route (spec 3.4a), which is
        ///     why <see cref="AgentJournal.Message" /> ships with no caller. A subscriber filtering
        ///     on the kind today would wait forever for an event that cannot arrive, which is
        ///     exactly what this feed's parser-not-compiler stance exists to prevent elsewhere.
        ///   </para>
        ///   <para>
        ///     So it is REPORTED rather than refused. Refusing it would mean a client's filter
        ///     breaking the day that route lands, and silently accepting it would mean a client
        ///     waiting on nothing; naming it on the status route and in the route's own
        ///     documentation is the only option that is true both now and later. A test reads the
        ///     product sources for a caller of that method and fails in BOTH directions, so this
        ///     list cannot drift from the code again.
        ///   </para>
        /// </summary>
        public static IReadOnlyCollection<String> Emitted { get; } = new[]
        {
            "agentSpawned", "agentStateChanged", "toolCalled", "agentCompleted", "agentFailed",
        };

        public static String Wire(AgentEventKind kind)
        {
            return kind switch
            {
                AgentEventKind.AgentSpawned => "agentSpawned",
                AgentEventKind.AgentStateChanged => "agentStateChanged",
                AgentEventKind.AgentMessage => "agentMessage",
                AgentEventKind.ToolCalled => "toolCalled",
                AgentEventKind.AgentCompleted => "agentCompleted",
                AgentEventKind.AgentFailed => "agentFailed",
                _ => "unknown",
            };
        }

        /// <summary>
        ///   Parses the query string's filters.
        ///
        ///   <para>
        ///     <b>A parser and not a compiler:</b> an unknown kind is a 400 naming the accepted set,
        ///     never a silently empty stream. The same stance the change feed takes, and for the
        ///     same reason - a subscriber whose typo produced silence cannot tell it from an idle
        ///     host, and would wait indefinitely for events that were being discarded.
        ///   </para>
        ///   <para>
        ///     An agent id is NOT validated against the registry. That is deliberate: subscribing
        ///     before a spawn is a legitimate race for a caller that wants to miss nothing, and
        ///     there is no catch-up buffer to fall back on.
        ///   </para>
        /// </summary>
        public static Boolean TryParse(String?[]? agents, String?[]? kinds, out AgentFeedFilter filter,
            out String problem)
        {
            filter = AgentFeedFilter.All;
            problem = String.Empty;

            var wantedKinds = new List<AgentEventKind>();
            foreach (var raw in Split(kinds))
            {
                if (!ByName.TryGetValue(raw, out var kind))
                {
                    problem = String.Format(
                        "Unknown feed event kind '{0}'. Accepted kinds are {1}; of those, {2} are "
                        + "emitted today (the status route reports both lists).", raw,
                        String.Join(", ", ByName.Keys), String.Join(", ", Emitted));
                    return false;
                }

                if (!wantedKinds.Contains(kind))
                {
                    wantedKinds.Add(kind);
                }
            }

            var wantedAgents = Split(agents).Distinct(StringComparer.Ordinal).ToList();

            filter = new AgentFeedFilter(
                wantedAgents.Count == 0 ? null : wantedAgents,
                wantedKinds.Count == 0 ? null : wantedKinds);
            return true;
        }

        /// <summary>
        ///   Values from the query string, comma-separated OR repeated, both accepted. Blanks are
        ///   dropped rather than refused: <c>?kinds=</c> is how a client says "no filter", and
        ///   refusing it would make the empty case an error.
        /// </summary>
        private static IEnumerable<String> Split(String?[]? values)
        {
            if (values == null)
            {
                yield break;
            }

            foreach (var value in values)
            {
                if (value == null)
                {
                    continue;
                }

                foreach (var part in value.Split(',', StringSplitOptions.TrimEntries))
                {
                    if (part.Length > 0)
                    {
                        yield return part;
                    }
                }
            }
        }
    }

    /// <summary>
    ///   The host's event feed: one broadcast, many bounded subscribers.
    ///
    ///   <para>
    ///     <b>Publishing never blocks and never waits for a subscriber.</b> A run is what produces
    ///     events, and a slow reader must not be able to slow an agent down, still less hold a model
    ///     call open. So each subscriber owns a bounded queue written only with a non-blocking
    ///     <c>TryWrite</c>, and a subscriber that fills it is DROPPED rather than served stale
    ///     events or allowed to apply back-pressure.
    ///   </para>
    ///   <para>
    ///     <b>Dropped rather than silently thinned</b>, which is the same honesty rule the trace's
    ///     bound follows: a feed that quietly skipped events would let a reader believe they had
    ///     seen everything. A dropped subscriber's stream ends, it reconnects, and the trace is the
    ///     documented way to find out what it missed. Getting this right turns on one measured
    ///     detail of the channel, stated on the queue itself: only <c>FullMode.Wait</c> makes
    ///     <c>TryWrite</c> report a full queue, and the three drop modes return success and discard.
    ///   </para>
    ///   <para>
    ///     <b>Delivery order is the sequence order</b>, because both happen under one lock. There is
    ///     no catch-up buffer, so a subscriber cannot tell an event delivered late from one lost.
    ///   </para>
    /// </summary>
    public sealed class AgentFeedDispatcher : IDisposable
    {
        private readonly Object _gate = new Object();
        private readonly List<AgentFeedSubscription> _subscribers = new List<AgentFeedSubscription>();
        private readonly IOptions<AgentsOptions> _options;
        private readonly ILogger<AgentFeedDispatcher> _logger;
        private Int64 _sequence;
        private Boolean _disposed;

        public AgentFeedDispatcher(IOptions<AgentsOptions> options, ILogger<AgentFeedDispatcher> logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>How many streams are open. Reported by the status route.</summary>
        public Int32 SubscriberCount
        {
            get
            {
                lock (_gate)
                {
                    return _subscribers.Count;
                }
            }
        }

        /// <summary>Events published since this process started.</summary>
        public Int64 Published => Volatile.Read(ref _sequence);

        /// <summary>
        ///   Opens a stream, or refuses because this host already has as many as it may. The refusal
        ///   is a first-class outcome: a full subscriber table is transient and worth retrying,
        ///   which a caller can only act on if it is told which of the two it hit.
        /// </summary>
        public Boolean TrySubscribe(AgentFeedFilter filter, out AgentFeedSubscription subscription,
            out String problem)
        {
            subscription = null!;
            problem = String.Empty;

            var feed = _options.Value.Feed;

            lock (_gate)
            {
                if (_disposed)
                {
                    problem = "This agent host is shutting down.";
                    return false;
                }

                if (feed.MaxSubscribers > 0 && _subscribers.Count >= feed.MaxSubscribers)
                {
                    problem = String.Format(
                        "This host already has {0} feed subscribers, which is its limit "
                        + "(Agents:Feed:MaxSubscribers).", feed.MaxSubscribers);
                    return false;
                }

                subscription = new AgentFeedSubscription(filter ?? AgentFeedFilter.All,
                    Math.Max(1, feed.MaxQueuedEvents), this);
                _subscribers.Add(subscription);
                return true;
            }
        }

        /// <summary>
        ///   Stamps and broadcasts one event. Returns the event as published, so a caller that also
        ///   records it publishes the same values.
        /// </summary>
        public AgentEvent Publish(AgentEvent candidate, DateTimeOffset at)
        {
            if (candidate == null)
            {
                throw new ArgumentNullException(nameof(candidate));
            }

            var behind = 0;
            List<AgentFeedSubscription>? dropped = null;
            lock (_gate)
            {
                candidate.Seq = ++_sequence;
                candidate.Ts = at;

                // Stamped AND delivered under the one lock, so the order a subscriber receives is
                // the order of the sequence numbers. Stamping inside and writing outside let two
                // publishing agents interleave, so a subscriber could see seq 7 before seq 6 - and
                // with no catch-up buffer, out-of-order is indistinguishable from loss.
                //
                // Safe to hold here because nothing in the loop waits: TryWrite is non-blocking, and
                // AllowSynchronousContinuations is false, so a reader's continuation is scheduled
                // rather than run on this thread.
                foreach (var target in _subscribers)
                {
                    if (!target.Filter.Admits(candidate))
                    {
                        continue;
                    }

                    if (!target.TryWrite(candidate))
                    {
                        behind++;

                        // Collected rather than completed here, because completing a subscriber
                        // while it is still IN the table is only half of dropping it, and the half
                        // that shows. A completed-but-listed subscriber has a full channel forever,
                        // so every later publish re-entered this branch: measured, one slow reader
                        // and eleven further events logged eleven drop warnings for one drop, left
                        // SubscriberCount reporting the stream as open, and held one of
                        // Agents:Feed:MaxSubscribers until the reader disposed - which a reader
                        // that has stopped reading is precisely the one not about to do.
                        (dropped ??= new List<AgentFeedSubscription>()).Add(target);
                    }
                }

                if (dropped != null)
                {
                    // After the loop, not during it: the list is being enumerated.
                    foreach (var gone in dropped)
                    {
                        _subscribers.Remove(gone);
                        gone.Complete();
                    }
                }
            }

            if (behind > 0)
            {
                // Logged outside the lock: a log provider is somebody else's code and may do
                // anything, including block.
                _logger.LogWarning(
                    "{Behind} feed subscriber(s) fell more than {MaxQueued} events behind and were "
                    + "dropped (Agents:Feed:MaxQueuedEvents). Each should reconnect; "
                    + "GET /agent/{{id}}/trace is how it finds out what it missed.",
                    behind, Math.Max(1, _options.Value.Feed.MaxQueuedEvents));
            }

            return candidate;
        }

        public void Dispose()
        {
            List<AgentFeedSubscription> open;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                open = new List<AgentFeedSubscription>(_subscribers);
                _subscribers.Clear();
            }

            foreach (var subscription in open)
            {
                subscription.Complete();
            }
        }

        internal void Remove(AgentFeedSubscription subscription)
        {
            lock (_gate)
            {
                _subscribers.Remove(subscription);
            }
        }
    }

    /// <summary>One open stream: a bounded queue and the filter it was opened with.</summary>
    public sealed class AgentFeedSubscription : IDisposable
    {
        private readonly Channel<AgentEvent> _events;
        private readonly AgentFeedDispatcher _owner;
        private Boolean _disposed;

        internal AgentFeedSubscription(AgentFeedFilter filter, Int32 capacity, AgentFeedDispatcher owner)
        {
            Filter = filter;
            _owner = owner;

            // FullMode.Wait, and the choice is not what the name suggests: nothing here ever waits,
            // because the only writer is TryWrite, which never blocks whatever the mode is. What the
            // mode decides is what TryWrite RETURNS when the channel is full, and that is the whole
            // mechanism.
            //
            // Measured against the runtime: with DropWrite, DropOldest or DropNewest, TryWrite
            // returns TRUE on a full channel and discards an event silently. Only Wait returns
            // false. So this was DropWrite and the dispatcher's "drop the subscriber" branch was
            // dead code, which made a lagging subscriber silently thinned - the one failure this
            // design exists to prevent, arriving through the option meant to prevent it.
            //
            // AllowSynchronousContinuations stays FALSE (its default, stated because the dispatcher
            // relies on it): a reader's continuation must not run inside TryWrite, or it would run
            // on the publishing agent's thread while the dispatcher holds its lock.
            _events = Channel.CreateBounded<AgentEvent>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        }

        public AgentFeedFilter Filter
        {
            get;
        }

        /// <summary>The next event, or null when this stream has ended (dropped, or the host is
        /// stopping).</summary>
        public async Task<AgentEvent?> ReadAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _events.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        internal Boolean TryWrite(AgentEvent candidate)
        {
            return _events.Writer.TryWrite(candidate);
        }

        internal void Complete()
        {
            _events.Writer.TryComplete();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Complete();
            _owner.Remove(this);
        }
    }
}
