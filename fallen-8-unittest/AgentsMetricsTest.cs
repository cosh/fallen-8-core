// MIT License
//
// AgentsMetricsTest.cs
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
using System.Diagnostics.Metrics;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Agents.Configuration;
using NoSQL.GraphDB.Agents.Diagnostics;
using NoSQL.GraphDB.Agents.Runtime;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The agent host's meter (feature agent-host, spec 3.6): that each instrument the spec names
    ///   is emitted, with the tags it names, and that NO user input reaches a tag value.
    ///
    ///   <para>
    ///     The tag-hygiene half is the one that matters most and the one a reader cannot check by
    ///     eye: a task sentence or an agent name in a tag lets a caller mint unbounded time series
    ///     in somebody else's monitoring backend, and the caller controls both on every spawn. So
    ///     the test spawns with values that would be unmistakable in a tag and asserts that none of
    ///     them appears in one.
    ///   </para>
    /// </summary>
    [TestClass]
    public class AgentsMetricsTest
    {
        [TestMethod]
        public void EveryInstrumentTheSpecNamesIsEmittedWithTheTagsItNames()
        {
            using var recorded = new Recorder();
            using var metrics = new AgentsMetrics(() => 3);
            recorded.Listen(AgentsMetrics.MeterName);

            metrics.ModelCall("Nahil", "assistant", inputTokens: 11, outputTokens: 3, durationMs: 42);
            metrics.ToolCall("count_vertices", success: true);
            metrics.Finished(AgentState.Completed, BudgetKind.None, "assistant");
            recorded.Collect();

            // f8a.agents.tokens, tagged by direction and role, because a host whose prompts are
            // long and whose answers are short needs the two counted apart.
            var tokens = recorded.Of("f8a.agents.tokens");
            Assert.AreEqual(2, tokens.Count, "one measurement per direction");
            Assert.AreEqual(11, tokens.Single(m => m.Tag("direction") == "input").Value);
            Assert.AreEqual(3, tokens.Single(m => m.Tag("direction") == "output").Value);
            Assert.IsTrue(tokens.All(m => m.Tag("role") == "assistant"));

            // f8a.agents.step.duration: wall clock as the HOST measured it, tagged by the backend
            // the instance named.
            var duration = recorded.Of("f8a.agents.step.duration").Single();
            Assert.AreEqual(42, duration.Value);
            Assert.AreEqual("Nahil", duration.Tag("backend"));

            var tool = recorded.Of("f8a.agents.tool.calls").Single();
            Assert.AreEqual(1, tool.Value);
            Assert.AreEqual("count_vertices", tool.Tag("tool"));
            Assert.AreEqual("True", tool.Tag("success"));

            var completed = recorded.Of("f8a.agents.completed").Single();
            Assert.AreEqual(1, completed.Value);
            Assert.AreEqual("completed", completed.Tag("outcome"));

            // f8a.agents.active is an observable gauge, so it reports on collection rather than on
            // a call, and it reads the registry rather than a tally of its own.
            Assert.AreEqual(3, recorded.Of("f8a.agents.active").Single().Value);
        }

        [TestMethod]
        public void ABudgetEndingCarriesWHICHBudgetBecauseTheAnswerDiffers()
        {
            // budgetExceeded alone cannot tell an operator whether to raise a cap or fix a loop, so
            // the outcome tag carries the budget's own name. Still a closed set, which is what
            // keeps it safe to tag: four endings plus four budget names.
            using var recorded = new Recorder();
            using var metrics = new AgentsMetrics(() => 0);
            recorded.Listen(AgentsMetrics.MeterName);

            metrics.Finished(AgentState.BudgetExceeded, BudgetKind.Tokens, "assistant");
            metrics.Finished(AgentState.BudgetExceeded, BudgetKind.Time, "worker");
            metrics.Finished(AgentState.Cancelled, BudgetKind.None, "assistant");
            recorded.Collect();

            var outcomes = recorded.Of("f8a.agents.completed")
                .Select(m => m.Tag("outcome"))
                .OrderBy(o => o, StringComparer.Ordinal)
                .ToList();

            CollectionAssert.AreEqual(
                new[] { "budgetExceeded:time", "budgetExceeded:tokens", "cancelled" },
                outcomes.ToArray(),
                "an outcome that did not name the budget would make the two budget endings "
                + "indistinguishable, which is the one distinction an operator acts on");

            // The degenerate pairing is not invented: a terminal state with no budget reports the
            // state alone rather than a trailing separator.
            Assert.AreEqual("budgetExceeded",
                AgentsMetrics.Outcome(AgentState.BudgetExceeded, BudgetKind.None));
        }

        [TestMethod]
        public void NoUserInputReachesATagValue()
        {
            // THE tag-hygiene rule. A caller supplies the task and the name on every spawn, so
            // tagging by either hands a caller the ability to mint unbounded time series in an
            // operator's monitoring backend. The agent ID is not tagged either: ids are unbounded
            // over a host's lifetime, and per-agent detail is what the trace and the feed are for.
            const String Task = "TASK-MARKER-would-explode-cardinality";
            const String Name = "NAME-MARKER-also-unbounded";

            using var recorded = new Recorder();
            var options = new AgentsOptions();
            var wrapped = Options.Create(options);

            using var metrics = new AgentsMetrics(() => 0);
            using var feed = new AgentFeedDispatcher(wrapped,
                TestLoggerFactory.Create().CreateLogger<AgentFeedDispatcher>());
            var journal = new AgentJournal(feed, wrapped, clock: null, metrics: metrics);
            using var registry = new AgentRegistry(wrapped,
                TestLoggerFactory.Create().CreateLogger<AgentRegistry>(), journal);

            recorded.Listen(AgentsMetrics.MeterName);

            Assert.IsTrue(registry.TryAdmit(new AgentSpawn("assistant", Task) { Name = Name },
                out var agent, out _));

            // Every fact the journal writes a metric for, driven through the journal rather than
            // the meter, because the journal is where the agent's own values are in scope and so
            // is where a name or a task would leak in.
            journal.ModelCall(agent, "Nahil", "some-model", durationMs: 7, inputTokens: 2,
                outputTokens: 1, usageReported: true);
            journal.ToolCall(agent, "call-1", "count_vertices", "{\"ns\":\"default\"}", "8",
                success: true, error: null, durationMs: 3);
            Assert.IsTrue(registry.Finish(agent.Id, AgentState.Completed, resultText: "eight"));
            recorded.Collect();

            Assert.IsTrue(recorded.All.Count > 0, "this test proves nothing if nothing was recorded");

            foreach (var measurement in recorded.All)
            {
                foreach (var tag in measurement.Tags)
                {
                    var value = Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture)
                        ?? String.Empty;

                    Assert.IsFalse(value.Contains("MARKER", StringComparison.Ordinal),
                        "user input reached the tag '" + tag.Key + "' on "
                        + measurement.Instrument + ": " + value);
                    Assert.AreNotEqual(agent.Id, value,
                        "the agent id reached the tag '" + tag.Key + "' on "
                        + measurement.Instrument + ", and ids are unbounded over a host's lifetime");
                }
            }
        }

        [TestMethod]
        public void AFaultingExporterDoesNotFaultTheRunBeingMeasured()
        {
            // CONTAINMENT, and precisely: Counter.Add and Histogram.Record invoke listener
            // callbacks INLINE, on the agent's own thread, and the BCL does not swallow what they
            // throw. So an exporter having a bad day would end the run being measured. Every
            // recording path is wrapped for that reason, and this is the test of it.
            using var metrics = new AgentsMetrics(() => 1);

            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == AgentsMetrics.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<Int64>(
                (i, v, t, s) => throw new InvalidOperationException("the exporter is unwell"));
            listener.SetMeasurementEventCallback<Double>(
                (i, v, t, s) => throw new InvalidOperationException("the exporter is unwell"));
            listener.Start();

            // None of these may throw. They are what an agent calls, so a throw here IS a failed
            // run, reported as the agent's fault.
            metrics.ModelCall("Nahil", "assistant", 1, 1, 1);
            metrics.ToolCall("count_vertices", true);
            metrics.Finished(AgentState.Failed, BudgetKind.None, "assistant");

            // What is deliberately NOT claimed: that a throwing listener cannot fault the
            // COLLECTOR. RecordObservableInstruments invokes the listener's own callback in the
            // collector's stack, so a listener that throws there faults itself, which is not
            // something this host can contain and not something it should pretend to.
        }

        [TestMethod]
        public void AFaultingGaugeSourceReportsNothingRatherThanThrowingAtTheCollector()
        {
            // The gauge's callback is the one an author is least likely to think about: it runs on
            // the COLLECTOR's thread, at a moment no agent chose, so a throw there is an exception
            // in somebody else's loop. It reads the registry, and a registry read can throw if the
            // host is tearing down.
            using var metrics = new AgentsMetrics(
                () => throw new InvalidOperationException("the registry is gone"));

            var observed = new List<Int64>();
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == AgentsMetrics.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<Int32>((i, v, t, s) => observed.Add(v));
            listener.Start();

            listener.RecordObservableInstruments();

            CollectionAssert.AreEqual(new Int64[] { 0 }, observed.ToArray(),
                "a gauge whose source threw has to report a number rather than an exception, and "
                + "zero is the only honest one: the host cannot say how many agents are live");
        }

        [TestMethod]
        public async System.Threading.Tasks.Task AStateChangeCarriesWallClockSoLiveCostNeedsNoPolling()
        {
            // The fourth counter on a state change, which was the one missing: input, output,
            // total, steps and toolCalls rode on every event and duration did not, so a subscriber
            // rendering live cost had to poll GET /agents for that one number. Measured from
            // admission, as an ending's duration is, so a run reads as one series.
            var clock = new Stepping(DateTimeOffset.Parse("2026-09-16T09:00:00Z"));
            var options = new AgentsOptions();
            var wrapped = Options.Create(options);

            using var feed = new AgentFeedDispatcher(wrapped,
                TestLoggerFactory.Create().CreateLogger<AgentFeedDispatcher>());
            var journal = new AgentJournal(feed, wrapped, clock: clock);
            using var registry = new AgentRegistry(wrapped,
                TestLoggerFactory.Create().CreateLogger<AgentRegistry>(), journal, clock);

            Assert.IsTrue(feed.TrySubscribe(AgentFeedFilter.All, out var subscription, out _));
            using (subscription)
            {
                Assert.IsTrue(registry.TryAdmit(new AgentSpawn("assistant", "count"),
                    out var agent, out _));

                clock.Advance(TimeSpan.FromMilliseconds(1500));
                Assert.IsTrue(registry.Advance(agent.Id, AgentState.Running));

                using var budget = new System.Threading.CancellationTokenSource(
                    TimeSpan.FromSeconds(10));

                AgentEvent moved = null;
                while (moved == null)
                {
                    var next = await subscription.ReadAsync(budget.Token);
                    Assert.IsNotNull(next, "the feed ended before the state change arrived");
                    if (next.Kind == "agentStateChanged")
                    {
                        moved = next;
                    }
                }

                Assert.AreEqual(1500L, moved.DurationMs,
                    "a state change has to carry the run's wall clock so far, or a subscriber "
                    + "polls the listing for the one counter the feed does not send");
                Assert.AreEqual("running", moved.State);
            }
        }

        /// <summary>A clock a test moves by hand, so a duration is asserted rather than tolerated.</summary>
        private sealed class Stepping : TimeProvider
        {
            private DateTimeOffset _now;

            public Stepping(DateTimeOffset start)
            {
                _now = start;
            }

            public override DateTimeOffset GetUtcNow() => _now;

            public void Advance(TimeSpan by)
            {
                _now = _now.Add(by);
            }
        }

        #region recording

        /// <summary>
        ///   A <see cref="MeterListener" /> that keeps what it saw. Measurements are appended under
        ///   a lock because the BCL invokes the callback on whichever thread recorded, and a plain
        ///   list mutated from several would be a flake rather than a test.
        /// </summary>
        private sealed class Recorder : IDisposable
        {
            private readonly Object _gate = new Object();
            private readonly List<Measurement> _measurements = new List<Measurement>();
            private readonly MeterListener _listener = new MeterListener();
            private String _meter = String.Empty;

            public IReadOnlyList<Measurement> All
            {
                get
                {
                    lock (_gate)
                    {
                        return new List<Measurement>(_measurements);
                    }
                }
            }

            public void Listen(String meterName)
            {
                _meter = meterName;
                _listener.InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == _meter)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                };

                _listener.SetMeasurementEventCallback<Int64>((i, v, t, s) => Add(i.Name, v, t));
                _listener.SetMeasurementEventCallback<Double>((i, v, t, s) => Add(i.Name, (Int64)v, t));
                _listener.SetMeasurementEventCallback<Int32>((i, v, t, s) => Add(i.Name, v, t));
                _listener.Start();
            }

            /// <summary>Pulls the observable instruments, which report on collection rather than on
            /// a call.</summary>
            public void Collect()
            {
                _listener.RecordObservableInstruments();
            }

            public IReadOnlyList<Measurement> Of(String instrument)
            {
                var hits = All.Where(m => m.Instrument == instrument).ToList();
                Assert.IsTrue(hits.Count > 0, "nothing was recorded for " + instrument
                    + "; recorded: " + String.Join(", ", All.Select(m => m.Instrument).Distinct()));
                return hits;
            }

            private void Add(String instrument, Int64 value, ReadOnlySpan<KeyValuePair<String, Object>> tags)
            {
                var copied = new List<KeyValuePair<String, Object>>(tags.Length);
                foreach (var tag in tags)
                {
                    copied.Add(tag);
                }

                lock (_gate)
                {
                    _measurements.Add(new Measurement(instrument, value, copied));
                }
            }

            public void Dispose()
            {
                _listener.Dispose();
            }
        }

        private sealed class Measurement
        {
            public Measurement(String instrument, Int64 value,
                IReadOnlyList<KeyValuePair<String, Object>> tags)
            {
                Instrument = instrument;
                Value = value;
                Tags = tags;
            }

            public String Instrument
            {
                get;
            }

            public Int64 Value
            {
                get;
            }

            public IReadOnlyList<KeyValuePair<String, Object>> Tags
            {
                get;
            }

            public String Tag(String key)
            {
                foreach (var tag in Tags)
                {
                    if (tag.Key == key)
                    {
                        return Convert.ToString(tag.Value,
                            System.Globalization.CultureInfo.InvariantCulture) ?? String.Empty;
                    }
                }

                return String.Empty;
            }
        }

        #endregion
    }
}
