// MIT License
//
// Fallen8ActivityTest.cs
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
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// ActivityListener tests for the engine and app spans (feature observability):
    /// cross-thread parenting of the transaction span, checkpoint spans with their tags, and
    /// the codegen compile span.
    /// </summary>
    [TestClass]
    public class Fallen8ActivityTest
    {
        private sealed class SpanCollector : IDisposable
        {
            private readonly ActivityListener _listener;
            public readonly ConcurrentQueue<Activity> Stopped = new ConcurrentQueue<Activity>();

            public SpanCollector(params string[] sourceNames)
            {
                _listener = new ActivityListener
                {
                    ShouldListenTo = source => sourceNames.Contains(source.Name),
                    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                    ActivityStopped = activity => Stopped.Enqueue(activity)
                };
                ActivitySource.AddActivityListener(_listener);
            }

            public void Dispose()
            {
                _listener.Dispose();
            }
        }

        [TestMethod]
        public void TransactionSpan_ParentsToTheEnqueuingActivity_AcrossTheWriterThread()
        {
            using var collector = new SpanCollector("NoSQL.GraphDB.Core");
            using var engine = new Fallen8(TestLoggerFactory.Create());

            // Simulates the HTTP server activity that would be current on the request thread.
            var parent = new Activity("test-http-request");
            parent.Start();
            try
            {
                var tx = new CreateVertexTransaction
                {
                    Definition = new VertexDefinition { CreationDate = 1u, Label = "person" }
                };
                engine.EnqueueTransaction(tx).WaitUntilFinished();
            }
            finally
            {
                parent.Stop();
            }

            var span = collector.Stopped.Single(a => a.OperationName == "fallen8.transaction.execute");
            Assert.AreEqual(parent.TraceId, span.TraceId,
                "the execute span joins the ENQUEUING request's trace even though it runs on the writer thread");
            Assert.AreEqual(parent.SpanId, span.ParentSpanId, "and is its direct child");
            Assert.AreEqual(nameof(CreateVertexTransaction), span.GetTagItem("transaction.type"));
            Assert.AreEqual(nameof(TransactionState.Finished), span.GetTagItem("transaction.state"));
            Assert.AreEqual(true, span.GetTagItem("transaction.durable"),
                "durability is stamped at the group flush, before the span closes");
        }

        [TestMethod]
        public void CheckpointSpans_CarryPartitionsBytesAndReplayCount()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "f8_obs_span_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                using var collector = new SpanCollector("NoSQL.GraphDB.Core");
                using var engine = new Fallen8(TestLoggerFactory.Create());
                engine.EnqueueTransaction(new CreateVertexTransaction
                {
                    Definition = new VertexDefinition { CreationDate = 1u, Label = "person" }
                }).WaitUntilFinished();

                var save = new SaveTransaction { Path = Path.Combine(tempDir, "snap.f8s"), SavePartitions = 2 };
                engine.EnqueueTransaction(save).WaitUntilFinished();

                var saveSpan = collector.Stopped.Single(a => a.OperationName == "fallen8.checkpoint.save");
                Assert.AreEqual(2, saveSpan.GetTagItem("checkpoint.partitions"));
                Assert.IsTrue((Int64)saveSpan.GetTagItem("checkpoint.bytes") > 0L);

                using var restored = new Fallen8(TestLoggerFactory.Create());
                restored.EnqueueTransaction(new LoadTransaction { Path = save.ActualPath }).WaitUntilFinished();

                var loadSpan = collector.Stopped.Single(a => a.OperationName == "fallen8.checkpoint.load");
                Assert.IsTrue((Int64)loadSpan.GetTagItem("checkpoint.bytes") > 0L);
                Assert.AreEqual(0, loadSpan.GetTagItem("checkpoint.wal.replayed"), "no WAL, nothing replayed");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
            }
        }

        [TestMethod]
        public void NoListener_NoSpansAreCreated()
        {
            // The zero-config guarantee for traces: with nothing listening to the engine
            // source, HasListeners() is false, the WorkItem never carries a span, and
            // StartActivity would return null anyway. Observable pin: a listener on an
            // UNRELATED source sees nothing from a full transaction round-trip.
            Assert.IsFalse(NoSQL.GraphDB.Core.Diagnostics.Fallen8Diagnostics.Source.HasListeners(),
                "precondition: nothing listens to the engine source");

            using var unrelated = new SpanCollector("some.unrelated.source");
            using var engine = new Fallen8(TestLoggerFactory.Create());
            engine.EnqueueTransaction(new CreateVertexTransaction
            {
                Definition = new VertexDefinition { CreationDate = 1u, Label = "person" }
            }).WaitUntilFinished();

            Assert.AreEqual(0, unrelated.Stopped.Count, "no span objects are created when unsampled");
        }

        [TestMethod]
        public void CodegenCompileSpan_CarriesArtifactAndSuccess()
        {
            using var collector = new SpanCollector("NoSQL.GraphDB.App");

            var error = NoSQL.GraphDB.Core.App.Helper.CodeGenerationHelper.GeneratePathTraverser(
                out var traverser,
                new NoSQL.GraphDB.App.Controllers.Model.PathSpecification
                {
                    Filter = new NoSQL.GraphDB.App.Controllers.Model.PathFilterSpecification
                    {
                        Vertex = "return (v) => true;"
                    }
                });
            Assert.IsNull(error);
            Assert.IsNotNull(traverser);

            var span = collector.Stopped.Single(a => a.OperationName == "fallen8.codegen.compile");
            Assert.AreEqual("path_traverser", span.GetTagItem("artifact"));
            Assert.AreEqual(true, span.GetTagItem("success"));
        }
    }
}
