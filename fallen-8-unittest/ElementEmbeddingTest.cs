// MIT License
//
// ElementEmbeddingTest.cs
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
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.ChangeFeed;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Persistency;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Feature element-embeddings, Phase 0: the typed accessor, the reserved-property storage
    ///   behind it, and the SetEmbeddingsTransaction write path (replace semantics, batch
    ///   atomicity, WAL replay).
    /// </summary>
    [TestClass]
    public class ElementEmbeddingTest
    {
        private Fallen8 _fallen8;

        [TestInitialize]
        public void Setup()
        {
            _fallen8 = new Fallen8(TestLoggerFactory.Create());
        }

        [TestCleanup]
        public void Teardown()
        {
            _fallen8?.Dispose();
        }

        #region helpers

        private int Vertex(string label = "person")
        {
            var tx = new CreateVertexTransaction { Definition = new VertexDefinition { CreationDate = 1u, Label = label } };
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.VertexCreated.Id;
        }

        private int Edge(int source, int target)
        {
            var tx = new CreateEdgesTransaction();
            tx.AddEdge(source, "knows", target, 1u, "knows");
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedEdges()[0].Id;
        }

        private void SetEmbedding(int elementId, string name, float[] vector)
        {
            _fallen8.EnqueueTransaction(new SetEmbeddingsTransaction().SetEmbedding(elementId, name, vector))
                .WaitUntilFinished();
        }

        private AGraphElementModel Element(int id)
        {
            Assert.IsTrue(_fallen8.TryGetGraphElement(out var element, id));
            return element;
        }

        #endregion

        #region accessor semantics

        [TestMethod]
        public void TryGetEmbedding_AbsentEmbedding_ReturnsFalse()
        {
            var element = Element(Vertex());

            Assert.IsFalse(element.TryGetEmbedding(out var vector));
            Assert.AreEqual(0, vector.Length);
            Assert.IsFalse(element.TryGetEmbedding(out _, "nope"));
        }

        [TestMethod]
        public void TryGetEmbedding_AfterSet_ReturnsTheVector_OnVertexAndEdge()
        {
            var v = Vertex();
            var w = Vertex();
            var e = Edge(v, w);

            SetEmbedding(v, "default", new[] { 1f, 2f, 3f });
            SetEmbedding(e, "default", new[] { -4f, 0.5f });

            Assert.IsTrue(Element(v).TryGetEmbedding(out var vertexVector));
            CollectionAssert.AreEqual(new[] { 1f, 2f, 3f }, vertexVector.ToArray());

            Assert.IsTrue(Element(e).TryGetEmbedding(out var edgeVector));
            CollectionAssert.AreEqual(new[] { -4f, 0.5f }, edgeVector.ToArray());
        }

        [TestMethod]
        public void TryGetEmbedding_NamedEmbeddings_AreIndependent_AndMayDifferInDimension()
        {
            var v = Vertex();
            SetEmbedding(v, "default", new[] { 1f, 2f });
            SetEmbedding(v, "title", new[] { 9f, 8f, 7f, 6f });

            var element = Element(v);
            Assert.IsTrue(element.TryGetEmbedding(out var def));
            Assert.AreEqual(2, def.Length);
            Assert.IsTrue(element.TryGetEmbedding(out var title, "title"));
            Assert.AreEqual(4, title.Length);
            Assert.IsFalse(element.TryGetEmbedding(out _, "other"));
        }

        [TestMethod]
        public void TryGetEmbedding_NonVectorValueUnderReservedKey_ReturnsFalse_WithoutThrowing()
        {
            var v = Vertex();

            // A raw property write may put anything under the reserved prefix; the accessor
            // must degrade to "no embedding", never throw.
            _fallen8.EnqueueTransaction(new AddPropertyTransaction
            {
                Definition = new PropertyAddDefinition { GraphElementId = v, PropertyId = "$embedding:weird", Property = "not a vector" }
            }).WaitUntilFinished();

            Assert.IsFalse(Element(v).TryGetEmbedding(out _, "weird"));
        }

        [TestMethod]
        public void Embedding_IsVisibleAsAReservedProperty()
        {
            var v = Vertex();
            SetEmbedding(v, "default", new[] { 1f });

            var properties = Element(v).GetAllProperties();
            Assert.IsTrue(properties.ContainsKey("$embedding:default"),
                "v1 stores the embedding behind the documented reserved property key");
            Assert.IsInstanceOfType(properties["$embedding:default"], typeof(float[]));
        }

        [TestMethod]
        public void IsValidEmbeddingName_Table()
        {
            Assert.IsTrue(AGraphElementModel.IsValidEmbeddingName("default"));
            Assert.IsTrue(AGraphElementModel.IsValidEmbeddingName("Title_2-v"));
            Assert.IsTrue(AGraphElementModel.IsValidEmbeddingName(new string('a', 64)));

            Assert.IsFalse(AGraphElementModel.IsValidEmbeddingName(null));
            Assert.IsFalse(AGraphElementModel.IsValidEmbeddingName(""));
            Assert.IsFalse(AGraphElementModel.IsValidEmbeddingName(new string('a', 65)));
            Assert.IsFalse(AGraphElementModel.IsValidEmbeddingName("with space"));
            Assert.IsFalse(AGraphElementModel.IsValidEmbeddingName("dot.name"));
            Assert.IsFalse(AGraphElementModel.IsValidEmbeddingName("$embedding:default"));
        }

        #endregion

        #region write semantics

        [TestMethod]
        public void SetEmbedding_ReplacesThePriorVector()
        {
            var v = Vertex();
            SetEmbedding(v, "default", new[] { 1f, 2f });
            SetEmbedding(v, "default", new[] { 3f, 4f });

            Assert.IsTrue(Element(v).TryGetEmbedding(out var vector));
            CollectionAssert.AreEqual(new[] { 3f, 4f }, vector.ToArray());
        }

        [TestMethod]
        public void SetEmbedding_NullVector_RemovesTheEmbedding()
        {
            var v = Vertex();
            SetEmbedding(v, "default", new[] { 1f, 2f });
            SetEmbedding(v, "default", null);

            Assert.IsFalse(Element(v).TryGetEmbedding(out _));
            Assert.AreEqual(0, Element(v).GetPropertyCount(), "removal drops the reserved property");
        }

        [TestMethod]
        public void SetEmbeddings_IntraBatchLastWriteWins()
        {
            var v = Vertex();
            var tx = new SetEmbeddingsTransaction()
                .SetEmbedding(v, "default", new[] { 1f })
                .SetEmbedding(v, "default", new[] { 2f });
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();

            Assert.IsTrue(Element(v).TryGetEmbedding(out var vector));
            CollectionAssert.AreEqual(new[] { 2f }, vector.ToArray());
        }

        [TestMethod]
        public void SetEmbeddings_InvalidItem_RollsBackTheWholeBatch()
        {
            var v = Vertex();
            SetEmbedding(v, "default", new[] { 1f });

            // Batch: a valid replace plus an invalid item (non-finite component). Validation is
            // batch-first, so nothing may be applied.
            var tx = new SetEmbeddingsTransaction()
                .SetEmbedding(v, "default", new[] { 9f })
                .SetEmbedding(v, "other", new[] { float.NaN });
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();

            Assert.IsTrue(Element(v).TryGetEmbedding(out var vector));
            CollectionAssert.AreEqual(new[] { 1f }, vector.ToArray(), "the valid item must not have been applied");
            Assert.IsFalse(Element(v).TryGetEmbedding(out _, "other"));
        }

        [TestMethod]
        public void SetEmbeddings_RejectsInvalidInput_Table()
        {
            var v = Vertex();

            foreach (var (name, vector) in new (string, float[])[]
            {
                ("bad name!", new[] { 1f }),
                (null, new[] { 1f }),
                ("default", Array.Empty<float>()),
                ("default", new float[NoSQL.GraphDB.Core.Index.Vector.VectorIndex.MaxDimension + 1]),
                ("default", new[] { float.PositiveInfinity }),
                ("default", new[] { float.NaN })
            })
            {
                _fallen8.EnqueueTransaction(new SetEmbeddingsTransaction().SetEmbedding(v, name, vector))
                    .WaitUntilFinished();
                Assert.IsFalse(Element(v).TryGetEmbedding(out _, name ?? "default"),
                    $"invalid write (name='{name}', dim={vector?.Length}) must not be applied");
            }
        }

        [TestMethod]
        public void SetEmbeddings_UnknownElement_IsANoOp_LikeThePropertyPath()
        {
            _fallen8.EnqueueTransaction(new SetEmbeddingsTransaction().SetEmbedding(4242, "default", new[] { 1f }))
                .WaitUntilFinished();
            // Nothing to assert beyond "no throw, engine still usable":
            Assert.AreEqual(0, _fallen8.VertexCount);
        }

        [TestMethod]
        public void SetEmbedding_ZeroNormVector_IsStorable()
        {
            // Zero-norm is a COSINE-query concern, not a storage concern (an L2/DotProduct
            // consumer ranks it fine); storage stays metric-agnostic.
            var v = Vertex();
            SetEmbedding(v, "default", new[] { 0f, 0f });

            Assert.IsTrue(Element(v).TryGetEmbedding(out var vector));
            Assert.AreEqual(2, vector.Length);
        }

        #endregion

        #region durability

        [TestMethod]
        public void SetEmbeddings_SurviveWalReplay_IncludingRemovals()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "f8_embedwal_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var walPath = Path.Combine(tempDir, "embeddings.wal");
            try
            {
                int keeper, dropper;
                using (var writer = new Fallen8(TestLoggerFactory.Create(), new WriteAheadLogOptions(walPath)))
                {
                    var tx = new CreateVerticesTransaction();
                    tx.AddVertex(1u, "person");
                    tx.AddVertex(1u, "person");
                    writer.EnqueueTransaction(tx).WaitUntilFinished();
                    keeper = tx.GetCreatedVertices()[0].Id;
                    dropper = tx.GetCreatedVertices()[1].Id;

                    writer.EnqueueTransaction(new SetEmbeddingsTransaction()
                            .SetEmbedding(keeper, "default", new[] { 1.5f, -2f })
                            .SetEmbedding(keeper, "title", new[] { 7f })
                            .SetEmbedding(dropper, "default", new[] { 3f }))
                        .WaitUntilFinished();
                    writer.EnqueueTransaction(new SetEmbeddingsTransaction().SetEmbedding(dropper, "default", null))
                        .WaitUntilFinished();
                } // crash: no save - the WAL alone carries the embeddings

                using var recovered = new Fallen8(TestLoggerFactory.Create(), new WriteAheadLogOptions(walPath));
                Assert.IsTrue(recovered.TryGetGraphElement(out var recoveredKeeper, keeper));
                Assert.IsTrue(recoveredKeeper.TryGetEmbedding(out var vector));
                CollectionAssert.AreEqual(new[] { 1.5f, -2f }, vector.ToArray());
                Assert.IsTrue(recoveredKeeper.TryGetEmbedding(out var title, "title"));
                CollectionAssert.AreEqual(new[] { 7f }, title.ToArray());

                Assert.IsTrue(recovered.TryGetGraphElement(out var recoveredDropper, dropper));
                Assert.IsFalse(recoveredDropper.TryGetEmbedding(out _), "the replayed removal wins");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
            }
        }

        [TestMethod]
        public void SetEmbeddings_EmitsAPropertySetChangeFeedEvent()
        {
            using var engine = new Fallen8(TestLoggerFactory.Create(), new ChangeFeedOptions());
            var tx = new CreateVertexTransaction { Definition = new VertexDefinition { CreationDate = 1u, Label = "p" } };
            engine.EnqueueTransaction(tx).WaitUntilFinished();
            var id = tx.VertexCreated.Id;

            // Align the dispatcher head past the creation before subscribing.
            Assert.IsTrue(System.Threading.SpinWait.SpinUntil(() => engine.ChangeFeed.LastSeq >= 1, 5000));
            Assert.IsTrue(engine.ChangeFeed.TrySubscribe(ChangeFeedFilter.MatchAll, null, null, out var subscription));
            using (subscription)
            {
                engine.EnqueueTransaction(new SetEmbeddingsTransaction().SetEmbedding(id, "default", new[] { 1f }))
                    .WaitUntilFinished();

                var read = subscription.Reader.ReadAsync().AsTask();
                Assert.IsTrue(read.Wait(5000), "expected a change-feed event for the embedding write");
                var evt = read.Result;
                Assert.AreEqual(ChangeEventKind.PropertySet, evt.Kind);
                Assert.AreEqual(id, evt.Id);
                Assert.AreEqual("$embedding:default", evt.Key);
            }
        }

        #endregion
    }
}
