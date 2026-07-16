// MIT License
//
// ObservabilityOverheadBenchmark.cs
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
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Opt-in overhead check for the engine instrumentation (feature observability): the
    ///   write path with NO meter listener (the zero-config production default - Enabled gates
    ///   skip every timestamp) versus WITH a listener attached. Remove [Ignore] and run with
    ///   dotnet test --filter "TestCategory=Benchmark" to reproduce the numbers in the
    ///   feature README.
    /// </summary>
    [TestClass]
    public class ObservabilityOverheadBenchmark
    {
        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Opt-in benchmark; remove [Ignore] and run with --filter TestCategory=Benchmark")]
        public void WritePath_MetricsOffVsOn()
        {
            const int warmup = 5_000;
            const int count = 50_000;

            var offTps = MeasureTransactionsPerSecond(warmup, count, attachListener: false);
            var onTps = MeasureTransactionsPerSecond(warmup, count, attachListener: true);

            Console.WriteLine($"Write path, {count:N0} single-vertex transactions: " +
                              $"metrics OFF {offTps:N0} tx/s, metrics ON (listener attached) {onTps:N0} tx/s " +
                              $"({(offTps - onTps) / offTps * 100:F1}% delta)");

            Assert.IsTrue(onTps > offTps * 0.5, "instrumentation must not halve the write path");
        }

        private static double MeasureTransactionsPerSecond(int warmup, int count, bool attachListener)
        {
            using var listener = attachListener ? CreateListener() : null;
            using var engine = new Fallen8(TestLoggerFactory.Create());

            for (var i = 0; i < warmup; i++)
            {
                Enqueue(engine).WaitUntilFinished();
            }

            var stopwatch = Stopwatch.StartNew();
            TransactionInformation last = null;
            for (var i = 0; i < count; i++)
            {
                last = Enqueue(engine);
            }
            last.WaitUntilFinished();
            stopwatch.Stop();

            return count / stopwatch.Elapsed.TotalSeconds;
        }

        private static TransactionInformation Enqueue(Fallen8 engine)
        {
            return engine.EnqueueTransaction(new CreateVertexTransaction
            {
                Definition = new VertexDefinition { CreationDate = 1u, Label = "bench" }
            });
        }

        private static MeterListener CreateListener()
        {
            var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == "NoSQL.GraphDB.Core")
                    {
                        l.EnableMeasurementEvents(instrument);
                    }
                }
            };
            listener.SetMeasurementEventCallback<Int64>((i, v, t, s) => { });
            listener.SetMeasurementEventCallback<Double>((i, v, t, s) => { });
            listener.Start();
            return listener;
        }
    }
}
