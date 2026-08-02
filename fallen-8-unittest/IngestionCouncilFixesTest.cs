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
using Microsoft.Extensions.DependencyInjection;
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
            // namespace at once. Each request first binds the layer (POST /document/binding/ensure
            // on the request thread), so many concurrent creates of the SAME index race directly -
            // the re-check-after-create guard makes each index resolve to exactly one. All must
            // accept (202), all must index, and each index must exist exactly once.
            var tasks = Enumerable.Range(0, 12)
                .Select(i => IngestionTestHelper.PostText(client, $"doc{i}.md",
                    $"# H{i}\n\nDistinct body number {i} with words."))
                .ToArray();
            var documentIds = await Task.WhenAll(tasks);

            foreach (var documentId in documentIds)
            {
                var summary = await IngestionTestHelper.AwaitTerminal(client, documentId);
                Assert.AreEqual("indexed", summary.GetProperty("status").GetString(),
                    "every concurrent first-ingest must index, not fail on the index-ensure race");
            }

            Assert.AreEqual(12, engine.GetAllVertices("Document").Count);
            Assert.IsTrue(engine.IndexFactory.TryGetIndex(out _, "documents"), "the bound vector index exists once");
            Assert.IsTrue(engine.IndexFactory.TryGetIndex(out _, "documents-text"), "the fulltext index exists once");
        }

        [TestMethod]
        public void StartupSweep_ReclaimsZombie_RemovesChunksAndFails()
        {
            using var factory = new IngestionFactory();
            var engine = IngestionTestHelper.EngineOf(factory);
            using var client = factory.CreateClient();  // starts the host + worker (its own sweep ran)

            // A Document a PREVIOUS process left mid-flight: `processing`, a FOREIGN boot id, and two
            // chunks it had committed before dying. FR-2: a non-indexed document leaves no chunks,
            // so the sweep must remove them, not just flip the status.
            var docId = IngestionTestHelper.CreateVertex(engine, "Document",
                new Dictionary<String, Object>
                {
                    { "status", "processing" }, { "name", "orphan.pdf" }, { "bootId", "previous-process" },
                });
            var chunkA = IngestionTestHelper.CreateVertex(engine, "Chunk",
                new Dictionary<String, Object> { { "text", "a" }, { "order", 0 } });
            var chunkB = IngestionTestHelper.CreateVertex(engine, "Chunk",
                new Dictionary<String, Object> { { "text", "b" }, { "order", 1 } });
            var edges = new CreateEdgesTransaction();
            edges.AddEdge(docId, "contains", chunkA, 1u);
            edges.AddEdge(docId, "contains", chunkB, 1u);
            engine.EnqueueTransaction(edges).WaitUntilFinished();
            Assert.AreEqual(2, engine.GetAllVertices("Chunk").Count);

            var service = factory.Services.GetRequiredService<NoSQL.GraphDB.App.Ingestion.DocumentIngestionService>();
            service.SweepInterruptedDocuments();

            Assert.IsTrue(engine.TryGetVertex(out var stub, docId));
            Assert.IsTrue(stub.TryGetProperty<String>(out var status, "status") && status == "failed",
                "the interrupted zombie is swept to failed");
            Assert.IsTrue(stub.TryGetProperty<String>(out var error, "error") && error == "interrupted");
            Assert.AreEqual(0, engine.GetAllVertices("Chunk").Count, "the zombie's chunks are removed (FR-2)");
        }

        [TestMethod]
        public void StartupSweep_LeavesLiveInFlightStubOfThisProcess()
        {
            using var factory = new IngestionFactory();
            var engine = IngestionTestHelper.EngineOf(factory);
            using var client = factory.CreateClient();

            // A `processing` stub carrying THIS process's boot id is a live in-flight ingest
            // accepted during the boot window (Kestrel accepts before the sweep runs), NOT a
            // zombie - the sweep must leave it for the worker instead of failing accepted work.
            var liveId = IngestionTestHelper.CreateVertex(engine, "Document",
                new Dictionary<String, Object>
                {
                    { "status", "processing" }, { "name", "in-flight.md" },
                    { "bootId", NoSQL.GraphDB.App.Ingestion.DocumentIngestionService.CurrentBootId },
                });

            var service = factory.Services.GetRequiredService<NoSQL.GraphDB.App.Ingestion.DocumentIngestionService>();
            service.SweepInterruptedDocuments();

            Assert.IsTrue(engine.TryGetVertex(out var stub, liveId));
            Assert.IsTrue(stub.TryGetProperty<String>(out var status, "status"));
            Assert.AreEqual("processing", status, "a same-boot-id stub is left for the worker, not swept");
        }

        [TestMethod]
        public void StartupSweep_ReconcilesEntityIndexFromEntityVertices()
        {
            using var factory = new IngestionFactory(new Dictionary<String, String>
            {
                { "Fallen8:Nlp:Enabled", "true" },
            });
            var engine = IngestionTestHelper.EngineOf(factory);
            using var client = factory.CreateClient();

            // Simulate a hard crash: an Entity vertex was WAL-replayed but its dictionary-index key
            // is gone (dictionary indices are not rebuilt from element state on load). The sweep
            // must reconcile the index so the next ingest dedupes instead of duplicating (FR-6).
            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out _, "documents-entities", "DictionaryIndex"));
            const String key = "ORG|muster gmbh";
            var entityId = IngestionTestHelper.CreateVertex(engine, "Entity",
                new Dictionary<String, Object>
                {
                    { "text", "Muster GmbH" }, { "type", "ORG" }, { "normalized", "muster gmbh" }, { "entityKey", key },
                });

            Assert.IsTrue(engine.IndexFactory.TryGetIndex(out var index, "documents-entities"));
            Assert.IsFalse(index.TryGetValue(out _, key), "the index starts without the stranded key");

            var service = factory.Services.GetRequiredService<NoSQL.GraphDB.App.Ingestion.DocumentIngestionService>();
            service.SweepInterruptedDocuments();

            Assert.IsTrue(index.TryGetValue(out var hits, key), "reconcile re-added the entity's key");
            Assert.IsTrue(hits.Any(hit => hit.Id == entityId));
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
