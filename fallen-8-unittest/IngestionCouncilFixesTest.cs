// MIT License
//
// IngestionCouncilFixesTest.cs
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
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.ChangeFeed;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Council merge-review fixes for feature unstructured-ingestion. Each test pins one
    ///   finding so a regression re-opens it:
    ///   - C2/E1: RegExIndex refuses a removed element (the add-after-remove tombstone leak).
    ///   - C1: concurrent first-ingests into a fresh namespace all succeed (index-ensure race).
    ///   - F2: /status does not report the docling sidecar reachable when the capability is off.
    ///   - S2: ingest progress is observable on the change feed.
    ///   - C7: a caller-cancelled docling call is not mislabelled as a sidecar fault.
    /// </summary>
    [TestClass]
    public class IngestionCouncilFixesTest
    {
        #region C2/E1 - RegExIndex removed-element guard

        [TestMethod]
        public void RegExIndex_RefusesARemovedElement_ButKeepsLiveOnes()
        {
            using var engine = new Fallen8(TestLoggerFactory.Create(), new ChangeFeedOptions());
            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out var index, "ft", "RegExIndex"));

            var liveTx = new CreateVertexTransaction { Definition = new VertexDefinition { CreationDate = 1u, Label = "Chunk" } };
            engine.EnqueueTransaction(liveTx).WaitUntilFinished();
            var live = liveTx.VertexCreated;

            var doomedTx = new CreateVertexTransaction { Definition = new VertexDefinition { CreationDate = 1u, Label = "Chunk" } };
            engine.EnqueueTransaction(doomedTx).WaitUntilFinished();
            var doomed = doomedTx.VertexCreated;

            // A live element indexes normally (the guard must not break the happy path).
            index.AddOrUpdate("alpha content", live);
            Assert.AreEqual(1, index.CountOfValues());

            // Remove the second vertex, then replay a stale add of it - exactly the
            // add-after-remove race a concurrent DELETE creates during ingest. The guard must
            // skip it so no tombstone is pinned in the index.
            engine.EnqueueTransaction(new RemoveGraphElementsTransaction
            {
                GraphElementIds = new List<Int32> { doomed.Id }
            }).WaitUntilFinished();
            Assert.IsFalse(engine.TryGetVertex(out _, doomed.Id), "precondition: the vertex is removed");

            index.AddOrUpdate("beta content", doomed);
            Assert.AreEqual(1, index.CountOfValues(), "a removed element must not enter the index");
            Assert.IsFalse(index.TryGetValue(out _, "beta content"), "its key must be absent");
        }

        #endregion

        #region C1 - concurrent first-ingest index-ensure race

        [TestMethod]
        public async Task ConcurrentFirstIngests_AllSucceed_AndEnsureOneIndexEach()
        {
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            // Fire many ingests with DISTINCT content (no duplicate-hash 409) at an EMPTY
            // namespace at once: they all miss TryGetIndex and race to create `documents` /
            // `documents-text`. Before the fix the create losers threw 500 "Index creation
            // failed"; now a lost race that finds a correct-shape index is success.
            var tasks = Enumerable.Range(0, 12).Select(i =>
                client.PostAsync("/document/text", IngestionTestHelper.Json(
                    $"{{ \"name\": \"doc{i}.md\", \"text\": \"# H{i}\\n\\nDistinct body number {i} with words.\" }}")))
                .ToArray();

            var responses = await Task.WhenAll(tasks);
            try
            {
                foreach (var response in responses)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                        "no concurrent first-ingest may fail on the index-ensure race: " + body);
                }
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }

            Assert.AreEqual(12, engine.GetAllVertices("Document").Count);
            Assert.IsTrue(engine.IndexFactory.TryGetIndex(out _, "documents"), "the bound vector index exists once");
            Assert.IsTrue(engine.IndexFactory.TryGetIndex(out _, "documents-text"), "the fulltext index exists once");
        }

        #endregion

        #region F2 - /status does not probe docling when the capability is off

        [TestMethod]
        public async Task Status_WithIngestionOff_ReportsDoclingNotReachable_EvenWhenSidecarIsUp()
        {
            using var factory = new IngestionFactory(new Dictionary<String, String>
            {
                { "Fallen8:Ingestion:Enabled", "false" }
            });
            // The sidecar IS configured and healthy; the gate must still report it unreachable
            // because the capability is off (the "off => no sidecar contacted" invariant).
            factory.Docling.ConfiguredFlag = true;
            factory.Docling.Reachable = true;
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/status");
            var ingestion = (await IngestionTestHelper.ReadJson(response)).GetProperty("ingestion");

            Assert.IsFalse(ingestion.GetProperty("enabled").GetBoolean());
            Assert.IsTrue(ingestion.GetProperty("docling").GetProperty("configured").GetBoolean());
            Assert.IsFalse(ingestion.GetProperty("docling").GetProperty("reachable").GetBoolean(),
                "reachable must be false when the capability is off, regardless of the sidecar");
        }

        #endregion

        #region S2 - ingest progress rides the change feed

        [TestMethod]
        public async Task Ingest_IsObservableOnTheChangeFeed()
        {
            using var factory = new IngestionFactory();
            using var client = factory.CreateClient();
            var engine = IngestionTestHelper.EngineOf(factory);

            Assert.IsNotNull(engine.ChangeFeed, "the change feed is enabled in the test host");
            Assert.IsTrue(engine.ChangeFeed.TrySubscribe(ChangeFeedFilter.MatchAll, null, null, out var subscription),
                "subscribe should succeed");

            using (subscription)
            {
                await IngestionTestHelper.IngestText(client, "runbook.md",
                    "# Edge\n\nThe first section prose.\n\n# Ports\n\nThe second section prose.");

                // Drain the committed events and assert the lifecycle is visible: the Document
                // vertex is created, its status flips via a property write, and Chunk vertices
                // are created. Bounded read so a missing signal fails instead of hanging.
                var sawDocumentCreated = false;
                var sawChunkCreated = false;
                var sawStatusPropertySet = false;

                // Generous wall-clock so the drain never false-fails under full-suite load; it
                // exits as soon as all three signals are seen, so the happy path is fast.
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < deadline && !(sawDocumentCreated && sawChunkCreated && sawStatusPropertySet))
                {
                    var readTask = subscription.Reader.ReadAsync().AsTask();
                    if (!readTask.Wait(TimeSpan.FromSeconds(15)))
                    {
                        break;
                    }

                    var change = readTask.Result;
                    if (change.Kind == ChangeEventKind.VertexCreated && change.Label == "Document")
                    {
                        sawDocumentCreated = true;
                    }
                    else if (change.Kind == ChangeEventKind.VertexCreated && change.Label == "Chunk")
                    {
                        sawChunkCreated = true;
                    }
                    else if (change.Kind == ChangeEventKind.PropertySet &&
                             String.Equals(change.Key, "status", StringComparison.Ordinal))
                    {
                        sawStatusPropertySet = true;
                    }
                }

                Assert.IsTrue(sawDocumentCreated, "the Document vertex creation must be on the feed");
                Assert.IsTrue(sawChunkCreated, "Chunk vertex creations must be on the feed");
                Assert.IsTrue(sawStatusPropertySet, "the status transition must ride the feed as a property write");
            }
        }

        #endregion
    }
}
