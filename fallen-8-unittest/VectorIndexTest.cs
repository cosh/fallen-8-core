// MIT License
//
// VectorIndexTest.cs
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
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Vector;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Engine-level tests for the vector index (feature vector-index): metric correctness
    /// against hand-computed values, deterministic ordering, bounds, replace semantics,
    /// lifecycle (write-end purge + read-end defense), constraints, and persistence.
    /// </summary>
    [TestClass]
    public class VectorIndexTest
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

        private VertexModel Vertex(string label = "person")
        {
            var tx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition { CreationDate = 1u, Label = label }
            };
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.VertexCreated;
        }

        private IVectorIndex CreateIndex(string name, int dimension, string metric = null)
        {
            var options = new Dictionary<string, object> { { "dimension", dimension } };
            if (metric != null)
            {
                options["metric"] = metric;
            }

            Assert.IsTrue(_fallen8.IndexFactory.TryCreateIndex(out var index, name, "VectorIndex", options),
                "vector index creation must succeed");
            return (IVectorIndex)index;
        }

        private static List<(int Id, float Score)> Knn(IVectorIndex index, float[] query, int k,
            VectorSearchConstraint constraint = null)
        {
            Assert.IsTrue(index.TryNearestNeighbors(out var result, query, k, constraint));
            return result.Entries.Select(e => (e.Element.Id, e.Score)).ToList();
        }

        #endregion

        #region creation

        [TestMethod]
        public void Creation_ViaTheExistingIndexSurface_Works_AndInvalidOptionsFail()
        {
            var index = CreateIndex("emb", 3, "L2");
            Assert.AreEqual(3, index.Dimension);
            Assert.AreEqual(VectorDistanceMetric.L2, index.Metric);
            Assert.IsTrue(_fallen8.IndexFactory.GetAvailableIndexPlugins().Contains("VectorIndex"),
                "the plugin is discovered like every other index family");

            // Missing dimension, bad dimension, bad metric: creation fails (logged), never throws out.
            Assert.IsFalse(_fallen8.IndexFactory.TryCreateIndex(out _, "bad1", "VectorIndex",
                new Dictionary<string, object>()));
            Assert.IsFalse(_fallen8.IndexFactory.TryCreateIndex(out _, "bad2", "VectorIndex",
                new Dictionary<string, object> { { "dimension", 0 } }));
            Assert.IsFalse(_fallen8.IndexFactory.TryCreateIndex(out _, "bad3", "VectorIndex",
                new Dictionary<string, object> { { "dimension", VectorIndex.MaxDimension + 1 } }));
            Assert.IsFalse(_fallen8.IndexFactory.TryCreateIndex(out _, "bad4", "VectorIndex",
                new Dictionary<string, object> { { "dimension", 3 }, { "metric", "Manhattan" } }));
        }

        [TestMethod]
        public void DefaultMetric_IsCosine()
        {
            Assert.AreEqual(VectorDistanceMetric.Cosine, CreateIndex("emb", 3).Metric);
        }

        #endregion

        #region metric correctness (hand-computed)

        [TestMethod]
        public void Cosine_MatchesHandComputedValues()
        {
            var index = CreateIndex("emb", 2, "Cosine");
            var a = Vertex(); // (1, 0)
            var b = Vertex(); // (0, 1)
            var c = Vertex(); // (-1, 0)
            var d = Vertex(); // (3, 4) - non-unit norm
            index.AddOrUpdate(new[] { 1f, 0f }, a);
            index.AddOrUpdate(new[] { 0f, 1f }, b);
            index.AddOrUpdate(new[] { -1f, 0f }, c);
            index.AddOrUpdate(new[] { 3f, 4f }, d);

            var hits = Knn(index, new[] { 1f, 0f }, 4);

            Assert.AreEqual(a.Id, hits[0].Id);
            Assert.AreEqual(1f, hits[0].Score, 1e-6f, "cos((1,0),(1,0)) = 1");
            Assert.AreEqual(d.Id, hits[1].Id);
            Assert.AreEqual(0.6f, hits[1].Score, 1e-6f, "cos((1,0),(3,4)) = 3/5");
            Assert.AreEqual(b.Id, hits[2].Id);
            Assert.AreEqual(0f, hits[2].Score, 1e-6f, "orthogonal");
            Assert.AreEqual(c.Id, hits[3].Id);
            Assert.AreEqual(-1f, hits[3].Score, 1e-6f, "opposite");
        }

        [TestMethod]
        public void DotProduct_MatchesHandComputedValues()
        {
            var index = CreateIndex("emb", 3, "DotProduct");
            var a = Vertex();
            var b = Vertex();
            index.AddOrUpdate(new[] { 1f, 2f, -3f }, a);
            index.AddOrUpdate(new[] { -2f, 0.5f, 1f }, b);

            var hits = Knn(index, new[] { 2f, -1f, 0.5f }, 2);

            // a . q = 2 - 2 - 1.5 = -1.5 ; b . q = -4 - 0.5 + 0.5 = -4. Higher is better: a first.
            Assert.AreEqual(a.Id, hits[0].Id);
            Assert.AreEqual(-1.5f, hits[0].Score, 1e-6f);
            Assert.AreEqual(b.Id, hits[1].Id);
            Assert.AreEqual(-4f, hits[1].Score, 1e-6f);
        }

        [TestMethod]
        public void L2_MatchesHandComputedValues_AndLowerIsBetter()
        {
            var index = CreateIndex("emb", 2, "L2");
            var near = Vertex();
            var far = Vertex();
            index.AddOrUpdate(new[] { 1f, 1f }, near);
            index.AddOrUpdate(new[] { 4f, 5f }, far);

            Assert.IsTrue(index.TryNearestNeighbors(out var result, new[] { 0f, 0f }, 2));
            Assert.IsFalse(result.HigherIsBetter, "L2 is a distance");

            Assert.AreEqual(near.Id, result.Entries[0].Element.Id);
            Assert.AreEqual((float)Math.Sqrt(2), result.Entries[0].Score, 1e-6f);
            Assert.AreEqual(far.Id, result.Entries[1].Element.Id);
            Assert.AreEqual((float)Math.Sqrt(41), result.Entries[1].Score, 1e-6f);
        }

        [TestMethod]
        public void EqualScores_TieBreakByAscendingElementId()
        {
            var index = CreateIndex("emb", 2, "Cosine");
            var first = Vertex();
            var second = Vertex();
            var third = Vertex();
            // All three are the SAME direction (scaled) => identical cosine similarity.
            index.AddOrUpdate(new[] { 2f, 2f }, second);
            index.AddOrUpdate(new[] { 1f, 1f }, first);
            index.AddOrUpdate(new[] { 5f, 5f }, third);

            var hits = Knn(index, new[] { 1f, 1f }, 2);

            var expected = new[] { first.Id, second.Id, third.Id }.OrderBy(id => id).Take(2).ToArray();
            CollectionAssert.AreEqual(expected, hits.Select(h => h.Id).ToArray(),
                "equal scores must resolve by ascending element id, deterministically");
        }

        #endregion

        #region bounds + replace

        [TestMethod]
        public void Bounds_AreEnforced()
        {
            var index = CreateIndex("emb", 3, "Cosine");
            var v = Vertex();
            index.AddOrUpdate(new[] { 1f, 0f, 0f }, v);

            // k larger than the corpus returns everything.
            Assert.AreEqual(1, Knn(index, new[] { 1f, 0f, 0f }, 100).Count);

            // Invalid queries return false.
            Assert.IsFalse(index.TryNearestNeighbors(out _, new[] { 1f, 0f, 0f }, 0));
            Assert.IsFalse(index.TryNearestNeighbors(out _, new[] { 1f, 0f, 0f }, VectorIndex.MaxK + 1));
            Assert.IsFalse(index.TryNearestNeighbors(out _, new[] { 1f, 0f }, 1), "wrong dimension");
            Assert.IsFalse(index.TryNearestNeighbors(out _, new[] { 0f, 0f, 0f }, 1), "zero-norm under cosine");

            // Invalid adds are the family's silent skip: logged, nothing indexed.
            var w = Vertex();
            index.AddOrUpdate(new[] { 1f, 0f }, w);          // wrong dimension
            index.AddOrUpdate(new[] { 0f, 0f, 0f }, w);      // zero-norm under cosine
            index.AddOrUpdate("not a vector", w);
            Assert.AreEqual(1, index.CountOfValues());
        }

        [TestMethod]
        public void ZeroNorm_IsAcceptable_UnderL2()
        {
            var index = CreateIndex("emb", 2, "L2");
            var origin = Vertex();
            index.AddOrUpdate(new[] { 0f, 0f }, origin);

            var hits = Knn(index, new[] { 0f, 0f }, 1);
            Assert.AreEqual(origin.Id, hits[0].Id);
            Assert.AreEqual(0f, hits[0].Score, 1e-6f);
        }

        [TestMethod]
        public void AddAgain_Replaces_OneVectorPerElement()
        {
            var index = CreateIndex("emb", 2, "L2");
            var v = Vertex();
            index.AddOrUpdate(new[] { 10f, 10f }, v);
            index.AddOrUpdate(new[] { 1f, 1f }, v); // replace

            Assert.AreEqual(1, index.CountOfKeys());
            Assert.AreEqual(1, index.CountOfValues());

            var hits = Knn(index, new[] { 0f, 0f }, 1);
            Assert.AreEqual((float)Math.Sqrt(2), hits[0].Score, 1e-6f, "the NEW vector ranks");

            Assert.IsFalse(index.TryGetValue(out _, new[] { 10f, 10f }), "the old vector is unfindable");
            Assert.IsTrue(index.TryGetValue(out var bucket, new[] { 1f, 1f }));
            Assert.AreEqual(v.Id, bucket[0].Id);
        }

        #endregion

        #region lifecycle

        [TestMethod]
        public void TransactionallyRemovedElement_NeverAppearsInKnnResults()
        {
            var index = CreateIndex("emb", 2, "L2");
            var keep = Vertex();
            var remove = Vertex();
            index.AddOrUpdate(new[] { 1f, 1f }, keep);
            index.AddOrUpdate(new[] { 0f, 0f }, remove); // the closest to the query

            _fallen8.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = remove.Id })
                .WaitUntilFinished();

            var hits = Knn(index, new[] { 0f, 0f }, 10);
            Assert.AreEqual(1, hits.Count, "the write-end purge removed the element from the index");
            Assert.AreEqual(keep.Id, hits[0].Id);
            Assert.AreEqual(1, index.CountOfValues(), "RemoveValue is exercised by the purge (O(1) slot map)");
        }

        [TestMethod]
        public void DefenseInDepth_ATombstonedElementLeftInASlot_IsSkipped()
        {
            // An UNREGISTERED index never sees the engine's write-end purge, so the tombstoned
            // element stays in its slot - the read-end liveness filter must still skip it.
            var index = new VectorIndex();
            index.Initialize(_fallen8, new Dictionary<string, object> { { "dimension", 2 }, { "metric", "L2" } });

            var keep = Vertex();
            var remove = Vertex();
            index.AddOrUpdate(new[] { 1f, 1f }, keep);
            index.AddOrUpdate(new[] { 0f, 0f }, remove);

            _fallen8.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = remove.Id })
                .WaitUntilFinished();

            Assert.AreEqual(2, index.CountOfValues(), "no purge reached this unregistered index");
            var hits = Knn(index, new[] { 0f, 0f }, 10);
            Assert.AreEqual(1, hits.Count, "the read end skips tombstoned slots regardless");
            Assert.AreEqual(keep.Id, hits[0].Id);
        }

        [TestMethod]
        public void TryRemoveKey_Wipe_AndCounts_BehaveLikeTheFamily()
        {
            var index = CreateIndex("emb", 2, "L2");
            var a = Vertex();
            var b = Vertex();
            index.AddOrUpdate(new[] { 1f, 2f }, a);
            index.AddOrUpdate(new[] { 1f, 2f }, b); // same vector, second element

            Assert.AreEqual(2, index.CountOfKeys());
            Assert.IsTrue(index.TryGetValue(out var bucket, new[] { 1f, 2f }));
            Assert.AreEqual(2, bucket.Count, "exact-match lookup finds every element with that vector");

            Assert.IsTrue(index.TryRemoveKey(new[] { 1f, 2f }));
            Assert.AreEqual(0, index.CountOfValues());
            Assert.IsFalse(index.TryRemoveKey(new[] { 1f, 2f }), "nothing left to remove");

            index.AddOrUpdate(new[] { 3f, 4f }, a);
            index.Wipe();
            Assert.AreEqual(0, index.CountOfKeys());
            Assert.AreEqual(0, Knn(index, new[] { 0f, 0f }, 5).Count);
        }

        #endregion

        #region constraints

        [TestMethod]
        public void Constraints_ReturnKMatchingElements()
        {
            var index = CreateIndex("emb", 2, "L2");
            var person = Vertex("person");
            var robot = Vertex("robot");
            var unlabeled = Vertex(null);

            var edgeTx = new CreateEdgesTransaction();
            edgeTx.AddEdge(person.Id, "knows", robot.Id, 1u, "knows");
            _fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();
            var edge = edgeTx.GetCreatedEdges()[0];

            index.AddOrUpdate(new[] { 0f, 0f }, person);
            index.AddOrUpdate(new[] { 0.1f, 0f }, robot);
            index.AddOrUpdate(new[] { 0.2f, 0f }, unlabeled);
            index.AddOrUpdate(new[] { 0.05f, 0f }, edge);

            // kind=vertex excludes the edge even though it scores second-best.
            var vertices = Knn(index, new[] { 0f, 0f }, 10,
                new VectorSearchConstraint { Kind = VectorSearchElementKind.Vertex });
            CollectionAssert.AreEqual(new[] { person.Id, robot.Id, unlabeled.Id }, vertices.Select(h => h.Id).ToArray());

            // kind=edge returns only the edge.
            var edges = Knn(index, new[] { 0f, 0f }, 10,
                new VectorSearchConstraint { Kind = VectorSearchElementKind.Edge });
            CollectionAssert.AreEqual(new[] { edge.Id }, edges.Select(h => h.Id).ToArray());

            // label filtering is exact; an unlabeled element never matches a label.
            var persons = Knn(index, new[] { 0f, 0f }, 10, new VectorSearchConstraint { Label = "person" });
            CollectionAssert.AreEqual(new[] { person.Id }, persons.Select(h => h.Id).ToArray());
        }

        #endregion

        #region persistence

        [TestMethod]
        public void SaveLoad_RoundTripsTheIndex_WithIdenticalScores()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "f8_vec_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var index = CreateIndex("emb", 3, "Cosine");
                var a = Vertex();
                var b = Vertex();
                var removedBeforeSave = Vertex();
                index.AddOrUpdate(new[] { 0.3f, -0.7f, 0.64f }, a);
                index.AddOrUpdate(new[] { -0.11f, 0.99f, 0.02f }, b);
                index.AddOrUpdate(new[] { 0.25f, -0.6f, 0.7f }, removedBeforeSave);
                _fallen8.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = removedBeforeSave.Id })
                    .WaitUntilFinished();

                var query = new[] { 0.25f, -0.6f, 0.7f };
                var before = Knn(index, query, 2);

                var save = new SaveTransaction { Path = Path.Combine(tempDir, "vec.f8s"), SavePartitions = 1 };
                _fallen8.EnqueueTransaction(save).WaitUntilFinished();

                using var restored = new Fallen8(TestLoggerFactory.Create());
                var load = new LoadTransaction { Path = save.ActualPath };
                restored.EnqueueTransaction(load).WaitUntilFinished();

                Assert.IsTrue(restored.IndexFactory.TryGetIndex(out var reloadedRaw, "emb"),
                    "the index rehydrates through the standard checkpoint (CanPersist)");
                var reloaded = (IVectorIndex)reloadedRaw;
                Assert.AreEqual(3, reloaded.Dimension);
                Assert.AreEqual(VectorDistanceMetric.Cosine, reloaded.Metric);

                Assert.AreEqual(2, reloaded.CountOfValues(),
                    "the element removed before the save is absent after the load");

                var after = Knn(reloaded, query, 2);
                Assert.AreEqual(before.Count, after.Count);
                for (var i = 0; i < before.Count; i++)
                {
                    Assert.AreEqual(before[i].Id, after[i].Id, "same ids after reload");
                    Assert.AreEqual(before[i].Score, after[i].Score, "identical scores after reload (same process)");
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
            }
        }

        [TestMethod]
        public void Load_ReadsAPreFeatureLegacySidecar_Unchanged()
        {
            // Pre-element-embeddings sidecars carry NO extended header: dimension, metric
            // byte, then a non-negative slot count (the branch keys the new format on a -1
            // sentinel there). A main-written checkpoint must load with the same vectors and
            // no binding/model.
            var index = new VectorIndex();
            index.Initialize(_fallen8, new Dictionary<string, object> { { "dimension", 2 } });
            var real = Vertex();

            using var stream = new MemoryStream();
            var writer = new NoSQL.GraphDB.Core.Serializer.SerializationWriter(stream, true);
            writer.Write(2);          // dimension
            writer.Write((byte)0);    // Cosine
            writer.Write(1);          // LEGACY: slot count, no sentinel/header
            writer.Write(real.Id);
            writer.Write(new[] { 0.6f, 0.8f });
            writer.UpdateHeader();
            writer.Flush();

            stream.Position = 0;
            var target = new VectorIndex();
            target.Initialize(_fallen8, new Dictionary<string, object> { { "dimension", 2 } });
            target.Load(new NoSQL.GraphDB.Core.Serializer.SerializationReader(stream), _fallen8);

            Assert.AreEqual(1, target.CountOfValues());
            Assert.IsNull(target.EmbeddingName, "a legacy sidecar is an unbound index");
            Assert.IsNull(target.Model, "a legacy sidecar declares no model identity");
            var hits = Knn(target, new[] { 0.6f, 0.8f }, 1);
            Assert.AreEqual(real.Id, hits.Single().Id);
            Assert.AreEqual(1f, hits.Single().Score, 1e-6f);
        }

        [TestMethod]
        public void Load_SkipsEntriesWhoseElementIsMissing()
        {
            var index = new VectorIndex();
            index.Initialize(_fallen8, new Dictionary<string, object> { { "dimension", 2 }, { "metric", "L2" } });
            var real = Vertex();
            index.AddOrUpdate(new[] { 1f, 2f }, real);

            // Serialize by hand, then load against an engine that lacks a referenced element.
            using var stream = new MemoryStream();
            var writer = new NoSQL.GraphDB.Core.Serializer.SerializationWriter(stream, true);
            writer.Write(2);          // dimension
            writer.Write((byte)2);    // L2
            writer.Write(2);          // count: one real, one phantom
            writer.Write(real.Id);
            writer.Write(new[] { 1f, 2f });
            writer.Write(4242);       // no such element
            writer.Write(new[] { 9f, 9f });
            writer.UpdateHeader();
            writer.Flush();

            stream.Position = 0;
            var target = new VectorIndex();
            target.Initialize(_fallen8, new Dictionary<string, object> { { "dimension", 2 } });
            target.Load(new NoSQL.GraphDB.Core.Serializer.SerializationReader(stream), _fallen8);

            Assert.AreEqual(1, target.CountOfValues(), "the phantom entry is skipped, the real one loads");
            Assert.AreEqual(VectorDistanceMetric.L2, target.Metric);
            var hits = Knn(target, new[] { 1f, 2f }, 5);
            Assert.AreEqual(real.Id, hits.Single().Id);
        }

        [TestMethod]
        public void Load_ThrowsOnCorruptHeader_SoTheIndexIsSkippedNotHalfInitialized()
        {
            // A corrupt header must throw (LoadIndices catches per index), never return
            // normally - OpenIndex would otherwise register an index with null internals.
            using (var stream = new MemoryStream())
            {
                var writer = new NoSQL.GraphDB.Core.Serializer.SerializationWriter(stream, true);
                writer.Write(VectorIndex.MaxDimension + 1); // invalid dimension
                writer.Write((byte)0);
                writer.Write(0);
                writer.UpdateHeader();
                writer.Flush();
                stream.Position = 0;

                var target = new VectorIndex();
                Assert.ThrowsException<InvalidDataException>(() =>
                    target.Load(new NoSQL.GraphDB.Core.Serializer.SerializationReader(stream), _fallen8));
            }

            using (var stream = new MemoryStream())
            {
                var writer = new NoSQL.GraphDB.Core.Serializer.SerializationWriter(stream, true);
                writer.Write(2);
                writer.Write((byte)99); // invalid metric
                writer.Write(0);
                writer.UpdateHeader();
                writer.Flush();
                stream.Position = 0;

                var target = new VectorIndex();
                Assert.ThrowsException<InvalidDataException>(() =>
                    target.Load(new NoSQL.GraphDB.Core.Serializer.SerializationReader(stream), _fallen8));
            }
        }

        #endregion

        #region non-finite input & score handling

        [TestMethod]
        public void NonFiniteComponents_AreRejected_OnAddAndQuery()
        {
            var index = CreateIndex("emb", 2, "L2");
            var v = Vertex();

            index.AddOrUpdate(new[] { float.NaN, 1f }, v);
            Assert.AreEqual(0, index.CountOfValues(), "a NaN component never enters the index");

            index.AddOrUpdate(new[] { float.PositiveInfinity, 1f }, v);
            Assert.AreEqual(0, index.CountOfValues(), "an Infinity component never enters the index");

            index.AddOrUpdate(new[] { 1f, 1f }, v);
            Assert.AreEqual(1, index.CountOfValues());

            Assert.IsFalse(index.TryNearestNeighbors(out _, new[] { float.NaN, 0f }, 1),
                "a NaN query is rejected");
            Assert.IsFalse(index.TryNearestNeighbors(out _, new[] { float.NegativeInfinity, 0f }, 1),
                "an Infinity query is rejected");
        }

        [TestMethod]
        public void NonFiniteScores_FromFiniteInputs_AreSkippedNotPoisoning()
        {
            // Finite, non-zero components can still score non-finite: under Cosine the
            // squared norm of [1e-25, 1e-25] underflows to 0, so the score is 0/0 or x/0.
            // Such a candidate must be skipped, never freeze the heap root (which would
            // degrade "top-k" to scan order for the whole corpus).
            var index = CreateIndex("emb", 2, "Cosine");
            var underflow = Vertex();
            var good = Vertex();
            var better = Vertex();

            index.AddOrUpdate(new[] { 1e-25f, 1e-25f }, underflow); // passes the zero-norm check
            index.AddOrUpdate(new[] { 0f, 1f }, good);
            index.AddOrUpdate(new[] { 1f, 0f }, better);

            var hits = Knn(index, new[] { 1f, 0f }, 3);
            Assert.AreEqual(2, hits.Count, "the underflowing candidate is skipped");
            Assert.AreEqual(better.Id, hits[0].Id, "the remaining candidates rank exactly");
            Assert.AreEqual(good.Id, hits[1].Id);
            Assert.IsTrue(hits.TrueForAll(h => h.Id != underflow.Id));

            // An underflowing QUERY makes every score non-finite: true, but empty.
            Assert.IsTrue(index.TryNearestNeighbors(out var result, new[] { 1e-25f, 1e-25f }, 3));
            Assert.AreEqual(0, result.Entries.Count);

            // Dot-product overflow to Infinity is skipped the same way.
            var dotIndex = CreateIndex("dot", 2, "DotProduct");
            var overflow = Vertex();
            var sane = Vertex();
            dotIndex.AddOrUpdate(new[] { 3e38f, 3e38f }, overflow);
            dotIndex.AddOrUpdate(new[] { 1f, 1f }, sane);

            var dotHits = Knn(dotIndex, new[] { 3e38f, 0f }, 2); // overflow * query -> Infinity
            Assert.AreEqual(1, dotHits.Count, "the overflowing candidate is skipped");
            Assert.AreEqual(sane.Id, dotHits[0].Id);
        }

        #endregion

        #region churn, races & the engine read helper

        [TestMethod]
        public void SlotMapIntegrity_SurvivesAddRemoveChurn()
        {
            var index = CreateIndex("emb", 2, "L2");
            var vertices = new List<VertexModel>();
            for (var i = 0; i < 12; i++)
            {
                var v = Vertex();
                vertices.Add(v);
                index.AddOrUpdate(new[] { (float)i, 0f }, v);
            }

            // Remove middles (each triggers swap-last), then the new middles again.
            index.RemoveValue(vertices[3]);
            index.RemoveValue(vertices[7]);
            index.RemoveValue(vertices[0]);
            Assert.AreEqual(9, index.CountOfValues());

            // Re-add one removed element with a NEW vector, replace a survivor in place.
            index.AddOrUpdate(new[] { 100f, 0f }, vertices[3]);
            index.AddOrUpdate(new[] { 200f, 0f }, vertices[5]);
            Assert.AreEqual(10, index.CountOfValues());

            // Every surviving element is findable at its exact vector...
            Assert.IsTrue(index.TryGetValue(out var bucket3, new[] { 100f, 0f }));
            Assert.AreEqual(vertices[3].Id, bucket3.Single().Id);
            Assert.IsTrue(index.TryGetValue(out var bucket5, new[] { 200f, 0f }));
            Assert.AreEqual(vertices[5].Id, bucket5.Single().Id);
            Assert.IsFalse(index.TryGetValue(out _, new[] { 5f, 0f }), "the replaced vector is unfindable");
            Assert.IsFalse(index.TryGetValue(out _, new[] { 0f, 0f }), "removed vectors are unfindable");

            // ...and kNN over the churned index returns exactly the live set, exactly ordered.
            var hits = Knn(index, new[] { 0f, 0f }, 100);
            Assert.AreEqual(10, hits.Count);
            Assert.AreEqual(vertices[1].Id, hits[0].Id, "x=1 is now the closest to the origin");
            Assert.AreEqual(vertices[3].Id, hits[8].Id, "x=100");
            Assert.AreEqual(vertices[5].Id, hits[9].Id, "x=200");
        }

        [TestMethod]
        public void ShrinkAfterRemovals_PreservesEveryLiveVectorAndKnnOrder()
        {
            // Pins the memory-footprint slab shrink (VectorIndex.RemoveSlotOf): growing well past the
            // 16-slot floor and then removing down below a quarter of capacity must reclaim memory
            // WITHOUT dropping or mis-mapping any live slot.
            var index = CreateIndex("emb", 2, "L2");
            var vertices = new List<VertexModel>();
            for (var i = 1; i <= 40; i++) // capacity doubles 16 -> 32 -> 64
            {
                var v = Vertex();
                vertices.Add(v);
                index.AddOrUpdate(new[] { (float)i, 0f }, v);
            }
            Assert.AreEqual(40, index.CountOfValues());

            // Remove the far 32 ({9,0}..{40,0}); 8 live * 4 = 32 < 64 capacity fires the shrink.
            for (var i = 9; i <= 40; i++)
            {
                index.RemoveValue(vertices[i - 1]);
            }
            Assert.AreEqual(8, index.CountOfValues(), "8 survivors after the slab shrank");

            // Every survivor is still mapped to its exact vector (the slot map survived the resize).
            for (var i = 1; i <= 8; i++)
            {
                Assert.IsTrue(index.TryGetValue(out var bucket, new[] { (float)i, 0f }),
                    "survivor at {" + i + ",0} must still be found after the slab shrank");
                Assert.AreEqual(vertices[i - 1].Id, bucket.Single().Id);
            }

            // kNN over the shrunk slab returns exactly the 8 live ids, nearest ({1,0}) first.
            var hits = Knn(index, new[] { 0f, 0f }, 100);
            var expected = Enumerable.Range(1, 8).Select(i => vertices[i - 1].Id).ToArray();
            CollectionAssert.AreEqual(expected, hits.Select(h => h.Id).ToArray(),
                "shrink preserves every live vector and ascending-distance order");
        }

        [TestMethod]
        public void Wipe_ReleasesGrownSlab_ThenStaysUsable()
        {
            // Pins the Wipe() slab release (VectorIndex.Wipe): a grown index resets to the 16-slot
            // arrays a fresh index starts with, and the reset arrays remain valid for reuse.
            var index = CreateIndex("emb", 2, "L2");
            for (var i = 1; i <= 40; i++) // grow capacity past the 16-slot floor
            {
                index.AddOrUpdate(new[] { (float)i, 0f }, Vertex());
            }
            Assert.AreEqual(40, index.CountOfValues());

            index.Wipe();
            Assert.AreEqual(0, index.CountOfValues());
            Assert.AreEqual(0, Knn(index, new[] { 0f, 0f }, 10).Count, "a wiped index yields no neighbors");

            // The freshly reset 16-slot arrays are usable: adding works and ranks correctly.
            var v = Vertex();
            index.AddOrUpdate(new[] { 2f, 0f }, v);
            var hits = Knn(index, new[] { 0f, 0f }, 5);
            Assert.AreEqual(1, hits.Count);
            Assert.AreEqual(v.Id, hits[0].Id);
        }

        [TestMethod]
        public void AddOrUpdate_SkipsATombstonedElement_ClosingThePurgeReAddRace()
        {
            // If a re-add slips in AFTER the write-end purge ran for a committed removal,
            // nothing would ever purge the entry again - it must be refused at the door.
            var index = CreateIndex("emb", 2, "L2");
            var doomed = Vertex();
            _fallen8.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = doomed.Id })
                .WaitUntilFinished();

            index.AddOrUpdate(new[] { 1f, 1f }, doomed);

            Assert.AreEqual(0, index.CountOfValues(), "a removed element never (re-)enters a slot");
        }

        [TestMethod]
        public void VectorIndexScan_TheEngineReadHelper_ResolvesAndDelegates()
        {
            var index = CreateIndex("emb", 2, "L2");
            var v = Vertex();
            index.AddOrUpdate(new[] { 1f, 2f }, v);

            Assert.IsTrue(_fallen8.VectorIndexScan(out var result, "emb", new[] { 1f, 2f }, 1));
            Assert.AreEqual(v.Id, result.Entries.Single().Element.Id);
            Assert.AreEqual(0f, result.Entries[0].Score, 1e-6f);

            Assert.IsFalse(_fallen8.VectorIndexScan(out _, "nope", new[] { 1f, 2f }, 1),
                "unknown index");

            Assert.IsTrue(_fallen8.IndexFactory.TryCreateIndex(out _, "dict", "DictionaryIndex",
                new Dictionary<string, object>()));
            Assert.IsFalse(_fallen8.VectorIndexScan(out _, "dict", new[] { 1f, 2f }, 1),
                "not a vector index");
        }

        #endregion
    }
}

