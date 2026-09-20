// MIT License
//
// AgentTrace.cs
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
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;

namespace NoSQL.GraphDB.Agents.Runtime
{
    /// <summary>What a trace step records. Serialized as the camel-case names the API documents.</summary>
    public enum TraceStepKind
    {
        /// <summary>The agent moved to a new state.</summary>
        StateChanged = 0,

        /// <summary>One model call, with the backend and model the instance reported for it.</summary>
        ModelCall = 1,

        /// <summary>One tool invocation, with capped captures of what went in and came back.</summary>
        ToolCall = 2,

        /// <summary>A message to or from the agent.</summary>
        Message = 3,

        /// <summary>
        ///   A spawn, and the kind is deliberately OVERLOADED: it is ordinarily the first step of a
        ///   trace, meaning "this agent was spawned", and it also appears on a parent's trace
        ///   meaning "this agent spawned that one". Ordinarily rather than always, because the
        ///   registry journals outside its lock and says so: a shutdown cancelling everything can
        ///   record an ending for an agent whose spawn has not been journaled yet, which puts the
        ///   cancellation first. <c>AgentRegistry.TryAdmit</c> is the one home for that window.
        ///   <para>
        ///     <see cref="TraceStep.ChildId" /> is how the two are told apart, and it is the only
        ///     way: the first carries a state and the host instance and no child, the second carries
        ///     a child and nothing else. This said only "this agent spawned another", so a client
        ///     rendering the documented kind showed "a1-17 spawned nothing" as the first row of
        ///     every agent that spawned nothing at all.
        ///   </para>
        /// </summary>
        Spawn = 4,

        /// <summary>The mechanical count of citations in the final text against the calls that
        /// actually happened.</summary>
        CitationCheck = 5,

        /// <summary>Steps were dropped to stay inside the bound. Carries how many, so a reader can
        /// tell a short trace from a truncated one.</summary>
        Dropped = 6,
    }

    /// <summary>
    ///   One step in an agent's trace.
    ///
    ///   <para>
    ///     <b>One flat shape rather than a type per kind</b>, with absent fields omitted from the
    ///     JSON. A reader gets the fields that apply to the kind in front of them and nothing else,
    ///     and there is one definition of a step to keep in step with the API rather than seven. The
    ///     cost is stated rather than hidden: which fields are meaningful depends on
    ///     <see cref="Kind" />, and the doc comment on each says which kind it belongs to.
    ///   </para>
    /// </summary>
    public sealed class TraceStep
    {
        /// <summary>Position in this agent's trace. Monotonic, and it does NOT restart when steps
        /// are dropped, so a gap in the numbers is itself the evidence of a drop.</summary>
        [JsonPropertyName("seq")]
        public Int64 Seq
        {
            get; set;
        }

        [JsonPropertyName("at")]
        public DateTimeOffset At
        {
            get; set;
        }

        [JsonPropertyName("kind")]
        public String Kind { get; set; } = String.Empty;

        /// <summary>How long the step took, as the HOST measured it. Not the backend's own reported
        /// duration, which does not cover a remote provider's routing: one measured step reported
        /// 45 ms for a call that took 41 seconds.</summary>
        [JsonPropertyName("durationMs")]
        public Int64? DurationMs
        {
            get; set;
        }

        // ---- modelCall

        /// <summary>
        ///   The backend that served this step, as the instance reported it.
        ///   <para>
        ///     <b>THE home for the per-step provenance rule.</b> Per STEP, not per host: a
        ///     deployment that switches backend mid-day shows it here, and one that runs two
        ///     backends behind one gateway shows which answered. The adapter, the meter and the
        ///     journal each carry a pointer to this field rather than a fourth copy of the reason,
        ///     because a change to how provenance is recorded should not leave three sites
        ///     asserting the old rule.
        ///   </para>
        /// </summary>
        [JsonPropertyName("backend")]
        public String? Backend
        {
            get; set;
        }

        [JsonPropertyName("model")]
        public String? Model
        {
            get; set;
        }

        [JsonPropertyName("inputTokens")]
        public Int64? InputTokens
        {
            get; set;
        }

        [JsonPropertyName("outputTokens")]
        public Int64? OutputTokens
        {
            get; set;
        }

        /// <summary>Present and true when the backend reported NO usage for this step, so the zeros
        /// above are an absence rather than a measurement.</summary>
        [JsonPropertyName("unreportedUsage")]
        public Boolean? UnreportedUsage
        {
            get; set;
        }

        // ---- toolCall

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

        /// <summary>The arguments as the model produced them, capped at
        /// <c>Agents:Trace:ArgsBytes</c>.</summary>
        [JsonPropertyName("arguments")]
        public String? Arguments
        {
            get; set;
        }

        /// <summary>The result, capped at <c>Agents:Trace:ResultBytes</c>.</summary>
        [JsonPropertyName("result")]
        public String? Result
        {
            get; set;
        }

        /// <summary>True when either capture was cut. Paired with the byte counts below, so a reader
        /// knows both that it was cut and how much there was.</summary>
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

        /// <summary>Why the invocation failed. Present only when <see cref="Success" /> is false, and
        /// it is the message the model was told too: the framework's for a call that threw, the
        /// tool's own for a <see cref="ToolRefusal" />.</summary>
        [JsonPropertyName("error")]
        public String? Error
        {
            get; set;
        }

        // ---- message

        /// <summary><c>toAgent</c> or <c>fromAgent</c>.</summary>
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

        /// <summary>The message text. Capped at <c>Agents:Trace:ResultBytes</c> like a tool result,
        /// because it is the same kind of thing: something a reviewer reads rather than a payload
        /// this host stores.</summary>
        [JsonPropertyName("text")]
        public String? Text
        {
            get; set;
        }

        // ---- stateChanged

        [JsonPropertyName("state")]
        public String? State
        {
            get; set;
        }

        /// <summary>Which budget ended the run. Present only on the state change into
        /// <c>budgetExceeded</c>.</summary>
        [JsonPropertyName("budget")]
        public String? Budget
        {
            get; set;
        }

        // ---- spawn

        [JsonPropertyName("childId")]
        public String? ChildId
        {
            get; set;
        }

        // ---- citationCheck

        /// <summary>Cited tool names that a call in this trace actually used.</summary>
        [JsonPropertyName("validCitations")]
        public Int32? ValidCitations
        {
            get; set;
        }

        /// <summary>Cited tool names with no matching call. Not proof of a lie, and the doc says so:
        /// a dangling citation means the answer points at work this trace has no record of, which is
        /// exactly what a reviewer should look at.</summary>
        [JsonPropertyName("danglingCitations")]
        public Int32? DanglingCitations
        {
            get; set;
        }

        // ---- dropped

        [JsonPropertyName("droppedSteps")]
        public Int64? DroppedSteps
        {
            get; set;
        }

        /// <summary>The host instance that produced this step. On the FIRST step of every trace, so
        /// a reader comparing two traces can tell whether the same process produced them; nothing
        /// here survives a restart.</summary>
        [JsonPropertyName("hostInstanceId")]
        public String? HostInstanceId
        {
            get; set;
        }
    }

    /// <summary>
    ///   One agent's trace: a bounded, in-order record of what it did.
    ///
    ///   <para>
    ///     <b>Bounded by dropping the OLDEST, and never silently.</b> Past
    ///     <c>Agents:Trace:MaxSteps</c> the front of the buffer goes, because review needs recency
    ///     rather than an archive, and ONE marker row at the front carries the running total of what
    ///     went, because a trace that quietly lost its middle would let a reviewer believe they had
    ///     the whole run.
    ///   </para>
    ///   <para>
    ///     <b>The marker is a single row held outside the buffer, not a step appended to it.</b>
    ///     That is a correction rather than a preference: appending one per overflow round left the
    ///     previous round's marker in place, so at steady state the buffer alternated real steps and
    ///     markers and held only half the steps it was configured for. Measured, a bound of 1000
    ///     after 3000 steps held 500 real steps and 500 markers, each reporting a different total.
    ///     One row, updated in place, is the shape the doc always claimed.
    ///   </para>
    ///   <para>
    ///     <b>A marker is not a step.</b> It is not counted as recorded or dropped, so
    ///     <see cref="Recorded" /> and <see cref="Dropped" /> are counts of an agent's real work
    ///     rather than of this class's own bookkeeping. It does CARRY a sequence number, but a
    ///     borrowed one: the sequence of the last step it reports on, so that it and the step after
    ///     it read as consecutive. It consumes none of its own.
    ///   </para>
    ///   <para>
    ///     Lives ON the agent record, so it is evicted exactly when the agent is and there is no
    ///     second lifetime to get wrong.
    ///   </para>
    /// </summary>
    public sealed class AgentTrace
    {
        private readonly Object _gate = new Object();
        private readonly Queue<TraceStep> _steps = new Queue<TraceStep>();
        private readonly Int32 _maxSteps;

        /// <summary>
        ///   Every tool name a call was recorded for, kept SEPARATELY from the buffer and not
        ///   bounded with it.
        ///   <para>
        ///     The grounding check counts a citation against what the run actually called, and the
        ///     buffer forgets its oldest steps, so counting against the buffer would make a citation
        ///     to a real early call dangle for a reason that is not the model's fault. Bounded in
        ///     practice by the number of DISTINCT tools the MCP server advertises, which is a small
        ///     consolidated set.
        ///   </para>
        /// </summary>
        private readonly HashSet<String> _toolsCalled = new HashSet<String>(StringComparer.OrdinalIgnoreCase);

        private Int64 _sequence;
        private Int64 _dropped;
        private TraceStep? _marker;

        /// <param name="maxSteps">
        ///   The rows the whole view may hold, marker included. A positive value below 2 is floored
        ///   at 2: once anything has been dropped the marker occupies one row, so a bound of 1 could
        ///   hold either a step or the news that steps were lost, and silently keeping both would
        ///   double the bound an operator configured. Zero or less is unbounded.
        /// </param>
        public AgentTrace(Int32 maxSteps)
        {
            _maxSteps = maxSteps > 0 ? Math.Max(2, maxSteps) : maxSteps;
        }

        /// <summary>How many steps have EVER been recorded, including dropped ones. The
        /// denominator for "am I looking at the whole run". Counts real steps only.</summary>
        public Int64 Recorded => Volatile.Read(ref _sequence);

        /// <summary>How many real steps were dropped to stay inside the bound.</summary>
        public Int64 Dropped => Volatile.Read(ref _dropped);

        /// <summary>
        ///   Appends a step, stamping its sequence and dropping the oldest if that is what the bound
        ///   requires. Returns the step as recorded, so a caller that also publishes it to the feed
        ///   publishes the same values rather than recomputing them.
        /// </summary>
        public TraceStep Record(TraceStep step, DateTimeOffset at)
        {
            if (step == null)
            {
                throw new ArgumentNullException(nameof(step));
            }

            lock (_gate)
            {
                step.Seq = ++_sequence;
                step.At = at;
                _steps.Enqueue(step);

                if (step.Tool != null && step.Success != false)
                {
                    // Remembered outside the bound, so a citation to a call the buffer has since
                    // forgotten still counts as grounded.
                    //
                    // A FAILED call is not remembered, and that is the whole point of the set: a
                    // refused or errored call is work that did not happen, so an answer citing its
                    // name is exactly the fabrication the grounding count exists to expose. A run
                    // whose every spawn was refused on a cap, or whose every graph read answered
                    // 401, would otherwise score fully grounded.
                    _toolsCalled.Add(step.Tool);
                }

                // Measured against the ROWS a reader would get, marker included, rather than against
                // the buffer alone. Comparing the buffer to a bound that the marker also counts
                // against is what let the first overflow return MaxSteps + 1 rows: the marker's row
                // was reserved only on rounds where a marker already existed, so the round that
                // created it dropped one step and then added a row.
                var rows = _steps.Count + (_marker == null ? 0 : 1);
                if (_maxSteps > 0 && rows > _maxSteps)
                {
                    // One row is the marker's from the moment it exists, and the constructor floors
                    // the bound at 2, so there is always room for at least one real step.
                    var room = _maxSteps - 1;
                    while (_steps.Count > room)
                    {
                        _steps.Dequeue();
                        _dropped++;
                    }

                    // ONE marker, updated in place. Appending a new one per round left the previous
                    // round's in the buffer, which halved the real steps a trace held; see the
                    // class doc for the measurement.
                    _marker ??= new TraceStep
                    {
                        Kind = TraceStepKinds.Wire(TraceStepKind.Dropped),
                    };
                    _marker.At = at;
                    _marker.DroppedSteps = _dropped;

                    // The sequence of the last step it reports on, so the marker and the step after
                    // it read as consecutive: "this many went, and the record resumes here". The
                    // buffer is never empty here, because room is at least 1.
                    _marker.Seq = _steps.Peek().Seq - 1;
                }

                return step;
            }
        }

        /// <summary>
        ///   The steps this trace still holds, oldest first, with the drop marker at the front when
        ///   anything has been dropped. One snapshot under one lock, so the rows a reader sees and
        ///   the totals it sees alongside them describe the same moment.
        /// </summary>
        public IReadOnlyList<TraceStep> Steps()
        {
            lock (_gate)
            {
                return Snapshot();
            }
        }

        /// <summary>
        ///   Everything the trace route reports, taken together under one lock. Reading the rows and
        ///   the two totals separately let one response contradict itself: a step count that did not
        ///   match the totals printed beside it.
        /// </summary>
        public (IReadOnlyList<TraceStep> Steps, Int64 Recorded, Int64 Dropped) View()
        {
            lock (_gate)
            {
                return (Snapshot(), _sequence, _dropped);
            }
        }

        /// <summary>The last <paramref name="count" /> steps, for the detail route, which shows a
        /// tail rather than the whole trace.</summary>
        public IReadOnlyList<TraceStep> Tail(Int32 count)
        {
            lock (_gate)
            {
                var all = Snapshot();
                if (count <= 0 || all.Count <= count)
                {
                    return all;
                }

                return ((List<TraceStep>)all).GetRange(all.Count - count, count);
            }
        }

        /// <summary>The marker, if any, then the buffer. Callers hold the lock.</summary>
        private IReadOnlyList<TraceStep> Snapshot()
        {
            var rows = new List<TraceStep>(_steps.Count + 1);
            if (_marker != null)
            {
                // Copied, because the live marker is mutated in place on every later drop and a
                // caller must not see a snapshot change under it.
                rows.Add(new TraceStep
                {
                    Seq = _marker.Seq,
                    At = _marker.At,
                    Kind = _marker.Kind,
                    DroppedSteps = _marker.DroppedSteps,
                });
            }

            rows.AddRange(_steps);
            return rows;
        }

        /// <summary>
        ///   The citation counts this run recorded, or null if no check ran.
        ///
        ///   <para>
        ///     Read back off the trace's own <c>citationCheck</c> step rather than recomputed, so
        ///     every reader of a finished run reports the same pair: the detail route, an
        ///     orchestrator collecting a worker's result, and the ending event all agree by
        ///     construction. THE one home for that read, because there are two callers now and a
        ///     second copy of the scan is a second answer waiting to differ.
        ///   </para>
        ///   <para>
        ///     Scans what the trace still holds, not a tail of it. The detail route used to scan
        ///     its own twenty-step tail, which would have reported no citations for any run whose
        ///     check had already been dropped from the front of a long trace.
        ///   </para>
        /// </summary>
        public CitationCounts? Citations()
        {
            lock (_gate)
            {
                foreach (var step in _steps)
                {
                    if (step.ValidCitations != null || step.DanglingCitations != null)
                    {
                        return new CitationCounts
                        {
                            Valid = step.ValidCitations ?? 0,
                            Dangling = step.DanglingCitations ?? 0,
                        };
                    }
                }

                return null;
            }
        }

        /// <summary>
        ///   Every tool NAME this trace has recorded a call for, INCLUDING calls the buffer has
        ///   since dropped. Names rather than tool-call ids, because no backend shows a tool-call id
        ///   to the model, so an id is not something it could cite.
        ///   <para>
        ///     Surviving the bound is the point: the grounding check counts a citation against what
        ///     the run actually called, and counting against the surviving buffer instead would make
        ///     a citation to a real early call dangle on a long run.
        ///   </para>
        /// </summary>
        public IReadOnlyCollection<String> ToolsCalled()
        {
            lock (_gate)
            {
                return new HashSet<String>(_toolsCalled, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>The wire spellings for a step kind, and the byte-capping every capture goes
    /// through. Both live here because both are part of what a trace step IS.</summary>
    public static class TraceStepKinds
    {
        public static String Wire(TraceStepKind kind)
        {
            return kind switch
            {
                TraceStepKind.StateChanged => "stateChanged",
                TraceStepKind.ModelCall => "modelCall",
                TraceStepKind.ToolCall => "toolCall",
                TraceStepKind.Message => "message",
                TraceStepKind.Spawn => "spawn",
                TraceStepKind.CitationCheck => "citationCheck",
                TraceStepKind.Dropped => "dropped",
                _ => "unknown",
            };
        }

        /// <summary>
        ///   Caps a capture at <paramref name="maxBytes" /> and reports how big it really was.
        ///
        ///   <para>
        ///     Cut on a UTF-8 CHARACTER boundary, not a byte index: a capture cut mid-sequence is
        ///     invalid UTF-8, and a reviewer would get a replacement character or a serializer
        ///     failure instead of the text. The byte count reported is of the ORIGINAL, which is the
        ///     number that tells a reader how much they are not seeing.
        ///   </para>
        /// </summary>
        public static String? Cap(String? text, Int32 maxBytes, out Int64 totalBytes, out Boolean truncated)
        {
            truncated = false;
            totalBytes = 0;

            if (text == null)
            {
                return null;
            }

            totalBytes = Encoding.UTF8.GetByteCount(text);
            if (maxBytes <= 0 || totalBytes <= maxBytes)
            {
                return text;
            }

            // Walk characters until the next one would not fit, which keeps surrogate pairs whole
            // because a pair is counted as the four bytes it encodes to.
            var kept = new StringBuilder();
            var used = 0;
            var index = 0;
            while (index < text.Length)
            {
                var runeLength = Char.IsHighSurrogate(text[index]) && index + 1 < text.Length
                    && Char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
                var bytes = Encoding.UTF8.GetByteCount(text.AsSpan(index, runeLength));
                if (used + bytes > maxBytes)
                {
                    break;
                }

                kept.Append(text, index, runeLength);
                used += bytes;
                index += runeLength;
            }

            truncated = true;
            return kept.ToString();
        }

        /// <summary>The suffix a truncated capture carries, so a reader sees the cut rather than
        /// inferring it from a flag they might not have looked at.</summary>
        public static String Marker(Int64 totalBytes)
        {
            return String.Format(CultureInfo.InvariantCulture, "... [truncated, {0} bytes total]",
                totalBytes);
        }

        /// <summary>
        ///   Caps a capture and appends its truncation marker, keeping the whole result inside
        ///   <paramref name="maxBytes" /> whenever the marker itself fits.
        ///
        ///   <para>
        ///     The qualification is load-bearing and this summary used to omit it. When the cap is
        ///     smaller than the marker the marker is returned ALONE and exceeds the cap: measured,
        ///     every cap from 1 to 31 bytes yields the same 32 bytes for a 500 byte input, and the
        ///     threshold moves with the reported total, because the marker spells that total out.
        ///     The branch below chooses that deliberately and says why; what was wrong was a
        ///     summary promising a guarantee the code contradicts thirty lines later. The caps a
        ///     deployment actually sets are orders of magnitude above the marker, so this is a
        ///     degenerate-configuration statement, not a live overrun.
        ///   </para>
        ///
        ///   <para>
        ///     The reason this exists rather than callers doing both: capping and then appending put
        ///     the stored capture over the configured cap, so <c>Agents:Trace:ArgsBytes</c> was not
        ///     the cost of a capture, it was the cost before the marker. Room for the marker is
        ///     reserved first, which is the only way the setting means what it says.
        ///   </para>
        /// </summary>
        public static String? CapWithMarker(String? text, Int32 maxBytes, out Int64 totalBytes,
            out Boolean truncated)
        {
            var capped = Cap(text, maxBytes, out totalBytes, out truncated);
            if (!truncated)
            {
                return capped;
            }

            // Re-capped against the room the marker leaves. The marker's own length depends on the
            // total, which is known only after the first pass, so this is two passes rather than
            // one guess.
            var marker = Marker(totalBytes);
            var room = maxBytes - Encoding.UTF8.GetByteCount(marker);
            if (room <= 0)
            {
                // A cap too small to hold even the marker. The marker is the more useful half: it
                // says there was something and how much, where a few bytes of a payload says
                // neither.
                return marker;
            }

            return Cap(text, room, out _, out _) + marker;
        }
    }
}
