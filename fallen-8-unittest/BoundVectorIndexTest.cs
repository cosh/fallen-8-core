// MIT License
//
// BoundVectorIndexTest.cs
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
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Index.Vector;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Persistency;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Feature element-embeddings, Phase 3: BOUND vector indices - a pure derived projection
    ///   of a named element embedding. Membership by embedding, writer-thread projection on
    ///   every embedding surface (typed transaction, raw property writes, element creation),
    ///   header-only persistence with rebuild-on-load, and WAL-replay correctness with zero
    ///   operator action.
    /// </summary>
    [TestClass]
    public class BoundVectorIndexTest
    {
        private Fallen8 _fallen8;

        [TestInitialize]
        public void TestInitialize()
        {
            _fallen8 = new Fallen8(TestLoggerFactory.Create());
        }

        [TestCleanup]
        public void TestCleanup()
        {
            _fallen8.Dispose();
        }

        #region helpers

        private int Vertex(string label = "person", Dictionary<string, object> properties = null)
        {
            var tx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition { CreationDate = 1u, Label = label, Properties = properties }
            };
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.VertexCreated.Id;
        }

        private void SetEmbedding(int elementId, string name, float[] vector)
        {
            _fallen8.EnqueueTransaction(new SetEmbeddingsTransaction().SetEmbedding(elementId, name, vector))
                .WaitUntilFinished();
        }

        private IVectorIndex CreateBoundIndex(string indexName, int dimension, string embeddingName = "default",
            string metric = null)
        {
            var options = new Dictionary<string, object>
            {
                { "dimension", dimension },
                { "embeddingName", embeddingName }
            };
            if (metric != null)
            {
                options["metric"] = metric;
            }

            Assert.IsTrue(_fallen8.IndexFactory.TryCreateIndex(out var index, indexName, "VectorIndex", options),
                "bound vector index creation must succeed");
            return (IVectorIndex)index;
        }

        private static List<int> KnnIds(IVectorIndex index, float[] query, int k)
        {
            Assert.IsTrue(index.TryNearestNeighbors(out var result, query, k));
            return result.Entries.Select(e => e.Element.Id).ToList();
        }

        #endregion

        #region creation & binding

        [TestMethod]
        public void BoundIndex_CreatedOverExistingData_MaterializesImmediately()
        {
            var a = Vertex();
            var b = Vertex();
            SetEmbedding(a, "default", new[] { 1f, 0f });
            SetEmbedding(b, "default", new[] { 0f, 1f });
            SetEmbedding(Vertex(), "other", new[] { 1f, 1f }); // different name: not a member

            var index = CreateBoundIndex("emb", 2);
            Assert.AreEqual("default", index.EmbeddingName);
            Assert.AreEqual(2, index.CountOfValues(), "existing embeddings of the bound name are members");
            CollectionAssert.AreEqual(new List<int> { a, b }, KnnIds(index, new[] { 1f, 0f }, 2));
        }

        [TestMethod]
        public void BoundIndex_InvalidEmbeddingName_FailsCreation()
        {
            Assert.IsFalse(_fallen8.IndexFactory.TryCreateIndex(out _, "bad", "VectorIndex",
                new Dictionary<string, object> { { "dimension", 2 }, { "embeddingName", "not valid!" } }));
        }

        [TestMethod]
        public void BoundIndex_WrongDimensionEmbedding_IsSkippedSilently()
        {
            var good = Vertex();
            SetEmbedding(good, "default", new[] { 1f, 0f });
            SetEmbedding(Vertex(), "default", new[] { 1f, 0f, 0f }); // 3-dim into a 2-dim index

            var index = CreateBoundIndex("emb", 2);
            Assert.AreEqual(1, index.CountOfValues(), "a mismatched dimension is not a member (family silent-skip)");
        }

        #endregion

        #region projection on write

        [TestMethod]
        public void EmbeddingWrites_ProjectIntoTheBoundIndex_SetReplaceRemove()
        {
            var index = CreateBoundIndex("emb", 2);
            var a = Vertex();

            SetEmbedding(a, "default", new[] { 1f, 0f });
            Assert.AreEqual(1, index.CountOfValues(), "a committed embedding write is projected");

            SetEmbedding(a, "default", new[] { 0f, 1f });
            Assert.AreEqual(1, index.CountOfValues());
            Assert.IsTrue(index.TryNearestNeighbors(out var result, new[] { 0f, 1f }, 1));
            Assert.AreEqual(1f, result.Entries[0].Score, 1e-6f, "the replacement vector ranks");

            SetEmbedding(a, "default", null);
            Assert.AreEqual(0, index.CountOfValues(), "an embedding removal purges the projection");
        }

        [TestMethod]
        public void RawPropertyWrites_UnderTheReservedKey_ProjectToo()
        {
            var index = CreateBoundIndex("emb", 2);
            var a = Vertex();

            // The raw property surface (the bulk-import path) writes the reserved key directly.
            _fallen8.EnqueueTransaction(new AddPropertyTransaction
            {
                Definition = new PropertyAddDefinition
                {
                    GraphElementId = a,
                    PropertyId = "$embedding:default",
                    Property = new[] { 1f, 0f }
                }
            }).WaitUntilFinished();
            Assert.AreEqual(1, index.CountOfValues(), "a raw reserved-key property write is projected");

            _fallen8.EnqueueTransaction(new RemovePropertyTransaction { GraphElementId = a, PropertyId = "$embedding:default" })
                .WaitUntilFinished();
            Assert.AreEqual(0, index.CountOfValues(), "removing the reserved property purges the projection");
        }

        [TestMethod]
        public void ElementsCreatedWithEmbeddingProperties_AreMembersImmediately()
        {
            var index = CreateBoundIndex("emb", 2);

            var a = Vertex(properties: new Dictionary<string, object> { { "$embedding:default", new[] { 1f, 0f } } });
            Assert.AreEqual(1, index.CountOfValues(), "creation-time embeddings are projected (bulk import)");
            CollectionAssert.AreEqual(new List<int> { a }, KnnIds(index, new[] { 1f, 0f }, 1));
        }

        [TestMethod]
        public void ElementRemoval_PurgesTheProjection()
        {
            var index = CreateBoundIndex("emb", 2);
            var a = Vertex();
            SetEmbedding(a, "default", new[] { 1f, 0f });

            _fallen8.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = a }).WaitUntilFinished();
            Assert.AreEqual(0, index.CountOfValues());
        }

        [TestMethod]
        public void UnprojectableReplacement_PurgesTheSlot_InsteadOfPinningTheOldVector()
        {
            // Council finding (correctness review): a committed replacement the index cannot
            // rank must PURGE the projection, never keep ranking the element by its PREVIOUS
            // vector - otherwise live answers and a load-rebuild from element state disagree.
            var index = CreateBoundIndex("emb", 2, metric: "Cosine");
            var a = Vertex();
            SetEmbedding(a, "default", new[] { 1f, 0f });
            Assert.AreEqual(1, index.CountOfValues());

            // Zero-norm is storable element state (metric-agnostic storage) but cannot rank
            // under the bound index's Cosine metric.
            SetEmbedding(a, "default", new[] { 0f, 0f });
            Assert.AreEqual(0, index.CountOfValues(),
                "an unprojectable replacement purges the slot (unprojectable = not a member)");

            // Same for a dimension-changing replacement via the engine API.
            SetEmbedding(a, "default", new[] { 1f, 0f });
            Assert.AreEqual(1, index.CountOfValues());
            SetEmbedding(a, "default", new[] { 1f, 0f, 0f });
            Assert.AreEqual(0, index.CountOfValues(),
                "a dimension-changing replacement purges the slot");

            // A raw property write to the reserved key with a NON-vector value purges too.
            SetEmbedding(a, "default", new[] { 1f, 0f });
            Assert.AreEqual(1, index.CountOfValues());
            _fallen8.EnqueueTransaction(new RemovePropertyTransaction { GraphElementId = a, PropertyId = "$embedding:default" })
                .WaitUntilFinished();
            _fallen8.EnqueueTransaction(new AddPropertyTransaction
            {
                Definition = new PropertyAddDefinition { GraphElementId = a, PropertyId = "$embedding:default", Property = "not a vector" }
            }).WaitUntilFinished();
            Assert.AreEqual(0, index.CountOfValues(),
                "a non-vector value under the reserved key is not a member");
        }

        [TestMethod]
        public void RolledBackEmbeddingBatch_LeavesTheProjectionUntouched()
        {
            var index = CreateBoundIndex("emb", 2);
            var a = Vertex();
            SetEmbedding(a, "default", new[] { 1f, 0f });

            // Invalid batch (NaN): validation fails batch-first, nothing applied or projected.
            _fallen8.EnqueueTransaction(new SetEmbeddingsTransaction()
                    .SetEmbedding(a, "default", new[] { 0f, 1f })
                    .SetEmbedding(a, "other", new[] { float.NaN, 0f }))
                .WaitUntilFinished();

            Assert.IsTrue(index.TryNearestNeighbors(out var result, new[] { 1f, 0f }, 1));
            Assert.AreEqual(1f, result.Entries[0].Score, 1e-6f, "the prior vector still ranks - nothing was applied");
        }

        [TestMethod]
        public void UnboundIndex_IsUntouchedByEmbeddingWrites()
        {
            Assert.IsTrue(_fallen8.IndexFactory.TryCreateIndex(out var raw, "raw", "VectorIndex",
                new Dictionary<string, object> { { "dimension", 2 } }));
            var a = Vertex();
            SetEmbedding(a, "default", new[] { 1f, 0f });

            Assert.AreEqual(0, raw.CountOfValues(), "an unbound index only changes through explicit adds");
        }

        #endregion

        #region persistence

        [TestMethod]
        public void BoundIndex_SaveLoad_RebuildsFromElementState_WithIdenticalScores()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "f8_boundvec_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var a = Vertex();
                var b = Vertex();
                SetEmbedding(a, "default", new[] { 0.6f, -0.8f });
                SetEmbedding(b, "default", new[] { 0.9f, 0.1f });
                var index = CreateBoundIndex("emb", 2, metric: "Cosine");

                var query = new[] { 1f, 0f };
                Assert.IsTrue(index.TryNearestNeighbors(out var before, query, 2));

                var save = new SaveTransaction { Path = Path.Combine(tempDir, "bound.f8s"), SavePartitions = 1 };
                _fallen8.EnqueueTransaction(save).WaitUntilFinished();

                using var restored = new Fallen8(TestLoggerFactory.Create());
                restored.EnqueueTransaction(new LoadTransaction { Path = save.ActualPath }).WaitUntilFinished();

                Assert.IsTrue(restored.IndexFactory.TryGetIndex(out var reloadedRaw, "emb"));
                var reloaded = (IVectorIndex)reloadedRaw;
                Assert.AreEqual("default", reloaded.EmbeddingName, "the binding survives the checkpoint");
                Assert.IsTrue(reloaded.TryNearestNeighbors(out var after, query, 2));

                Assert.AreEqual(before.Entries.Count, after.Entries.Count);
                for (var i = 0; i < before.Entries.Count; i++)
                {
                    Assert.AreEqual(before.Entries[i].Element.Id, after.Entries[i].Element.Id);
                    Assert.AreEqual(before.Entries[i].Score, after.Entries[i].Score,
                        "the rebuilt projection scores identically - the vectors came from element state");
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
            }
        }

        [TestMethod]
        public void UnboundIndex_NewFormat_RoundTripsSlotsAndModelOption()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "f8_rawvec_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var a = Vertex();
                Assert.IsTrue(_fallen8.IndexFactory.TryCreateIndex(out var rawIndex, "raw", "VectorIndex",
                    new Dictionary<string, object> { { "dimension", 2 }, { "model", "bge-micro-v2@q8#2#Cosine" } }));
                Assert.IsTrue(_fallen8.TryGetGraphElement(out var element, a));
                rawIndex.AddOrUpdate(new[] { 0.3f, 0.4f }, element);

                var save = new SaveTransaction { Path = Path.Combine(tempDir, "raw.f8s"), SavePartitions = 1 };
                _fallen8.EnqueueTransaction(save).WaitUntilFinished();

                using var restored = new Fallen8(TestLoggerFactory.Create());
                restored.EnqueueTransaction(new LoadTransaction { Path = save.ActualPath }).WaitUntilFinished();

                Assert.IsTrue(restored.IndexFactory.TryGetIndex(out var reloadedRaw, "raw"));
                var reloaded = (IVectorIndex)reloadedRaw;
                Assert.IsNull(reloaded.EmbeddingName);
                Assert.AreEqual("bge-micro-v2@q8#2#Cosine", reloaded.Model, "the declared model identity persists");
                Assert.AreEqual(1, reloaded.CountOfValues(), "unbound vectors persist in the sidecar as before");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
            }
        }

        [TestMethod]
        public void BoundIndex_WalReplay_RecoversTheProjection_WithZeroOperatorAction()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "f8_boundwal_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var walPath = Path.Combine(tempDir, "bound.wal");
            try
            {
                int a;
                using (var writer = new Fallen8(TestLoggerFactory.Create(), new WriteAheadLogOptions(walPath)))
                {
                    var tx = new CreateVertexTransaction { Definition = new VertexDefinition { CreationDate = 1u, Label = "p" } };
                    writer.EnqueueTransaction(tx).WaitUntilFinished();
                    a = tx.VertexCreated.Id;

                    // The embedding write is WAL-logged; the index membership is derived, so
                    // nothing index-related needs to be logged at all.
                    writer.EnqueueTransaction(new SetEmbeddingsTransaction().SetEmbedding(a, "default", new[] { 1f, 0f }))
                        .WaitUntilFinished();
                } // crash: no checkpoint - the index sidecar never existed

                using var recovered = new Fallen8(TestLoggerFactory.Create(), new WriteAheadLogOptions(walPath));

                // The operator re-creates the bound index by config/API as on first setup - or it
                // exists from a checkpoint; either way the projection derives from element state.
                Assert.IsTrue(recovered.IndexFactory.TryCreateIndex(out var index, "emb", "VectorIndex",
                    new Dictionary<string, object> { { "dimension", 2 }, { "embeddingName", "default" } }));

                Assert.AreEqual(1, index.CountOfValues(),
                    "the WAL-replayed embedding is a member - the vector survived the crash on the element");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
            }
        }

        [TestMethod]
        public void BoundIndex_CheckpointPlusWalTail_ReplaysEmbeddingWritesIntoTheLoadedIndex()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "f8_boundwal2_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var walPath = Path.Combine(tempDir, "bound.wal");
            try
            {
                // This is THE retired-workaround scenario: checkpoint with the bound index,
                // then embedding writes AFTER the checkpoint, then crash. Pre-feature, those
                // vectors were lost (index writes are not WAL-logged) and needed manual
                // re-adds; now replaying the embedding transactions re-projects them.
                string checkpointPath;
                int a, b;
                using (var writer = new Fallen8(TestLoggerFactory.Create(), new WriteAheadLogOptions(walPath)))
                {
                    var tx = new CreateVerticesTransaction();
                    tx.AddVertex(1u, "p");
                    tx.AddVertex(1u, "p");
                    writer.EnqueueTransaction(tx).WaitUntilFinished();
                    a = tx.GetCreatedVertices()[0].Id;
                    b = tx.GetCreatedVertices()[1].Id;

                    writer.EnqueueTransaction(new SetEmbeddingsTransaction().SetEmbedding(a, "default", new[] { 1f, 0f }))
                        .WaitUntilFinished();
                    Assert.IsTrue(writer.IndexFactory.TryCreateIndex(out _, "emb", "VectorIndex",
                        new Dictionary<string, object> { { "dimension", 2 }, { "embeddingName", "default" } }));

                    var save = new SaveTransaction { Path = Path.Combine(tempDir, "bound.f8s"), SavePartitions = 1 };
                    writer.EnqueueTransaction(save).WaitUntilFinished();
                    checkpointPath = save.ActualPath;

                    // Post-checkpoint write: only the WAL carries it.
                    writer.EnqueueTransaction(new SetEmbeddingsTransaction().SetEmbedding(b, "default", new[] { 0f, 1f }))
                        .WaitUntilFinished();
                }

                // Recovery: open the anchored WAL, load its paired checkpoint - the log's
                // post-checkpoint tail replays onto the loaded state.
                using var recovered = new Fallen8(TestLoggerFactory.Create(), new WriteAheadLogOptions(walPath));
                recovered.EnqueueTransaction(new LoadTransaction { Path = checkpointPath }).WaitUntilFinished();

                Assert.IsTrue(recovered.IndexFactory.TryGetIndex(out var reloadedRaw, "emb"));
                var reloaded = (IVectorIndex)reloadedRaw;
                Assert.AreEqual(2, reloaded.CountOfValues(),
                    "checkpointed AND post-checkpoint embeddings are members after replay - no re-add step");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
            }
        }

        #endregion
    }
}
