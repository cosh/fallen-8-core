// MIT License
//
// ScanSurfacePinningTest.cs
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
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Index.Fulltext;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pins the scan-surface behaviour of <see cref="Fallen8"/> that had no test before the
    /// structural decomposition moves code (feature structural-decomposition, Phase 3): the
    /// <c>FulltextIndexScan</c> wrapper, <c>GetCountOf&lt;T&gt;</c>, the <c>GraphScan</c> operator
    /// arms CoreTest leaves uncovered (Lower / GreaterOrEquals / NotEquals), and the
    /// invalid-operator default branches of <c>GraphScan</c>, <c>IndexScan</c> and the
    /// non-range-index arm of <c>RangeIndexScan</c>.
    /// </summary>
    [TestClass]
    public class ScanSurfacePinningTest
    {
        private ILoggerFactory _loggerFactory;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
        }

        #region helpers

        private static int CreateVertex(Fallen8 fallen8, string label = "person", Dictionary<string, object> properties = null)
        {
            var tx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition
                {
                    CreationDate = 1u,
                    Label = label,
                    Properties = properties
                }
            };
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.VertexCreated.Id;
        }

        private static int CreateEdge(Fallen8 fallen8, int sourceId, int targetId, string edgePropertyId = "knows", string label = "knows")
        {
            var tx = new CreateEdgesTransaction();
            tx.AddEdge(sourceId, edgePropertyId, targetId, 1u, label);
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedEdges()[0].Id;
        }

        /// <summary>Vertices aged 20, 30, 40 and 50 carrying an "age" property (the CoreTest seed shape).</summary>
        private static List<int> CreateAgedVertices(Fallen8 fallen8)
        {
            var ids = new List<int>();
            for (var age = 20; age <= 50; age += 10)
            {
                ids.Add(CreateVertex(fallen8, "person", new Dictionary<string, object> { { "age", age } }));
            }
            return ids;
        }

        private static int[] AgesOf(IEnumerable<AGraphElementModel> elements)
        {
            var ages = new List<int>();
            foreach (var element in elements)
            {
                Assert.IsTrue(element.TryGetProperty(out int age, "age"), "every seeded element carries an age");
                ages.Add(age);
            }
            return ages.ToArray();
        }

        /// <summary>
        /// An engine with two sentence-keyed vertices and a "fts" RegExIndex registered on the
        /// ENGINE's IndexFactory (the factory <c>FulltextIndexScan</c> resolves through).
        /// </summary>
        private Fallen8 CreateEngineWithFulltextIndex(out int foxVertexId, out int dogVertexId)
        {
            var fallen8 = new Fallen8(_loggerFactory);
            foxVertexId = CreateVertex(fallen8);
            dogVertexId = CreateVertex(fallen8);
            Assert.IsTrue(fallen8.TryGetVertex(out var foxVertex, foxVertexId));
            Assert.IsTrue(fallen8.TryGetVertex(out var dogVertex, dogVertexId));

            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out var fulltextIndex, "fts", "RegExIndex"),
                "the fulltext index must register on the engine's factory");
            fulltextIndex.AddOrUpdate("The quick brown fox jumps", foxVertex);
            fulltextIndex.AddOrUpdate("A lazy dog sleeps all day", dogVertex);
            return fallen8;
        }

        #endregion

        #region FulltextIndexScan

        [TestMethod]
        public void FulltextIndexScan_OnAFulltextIndex_FindsTheMatchingElements()
        {
            var fallen8 = CreateEngineWithFulltextIndex(out var foxVertexId, out _);

            var found = fallen8.FulltextIndexScan(out var result, "fts", "fox");

            Assert.IsTrue(found, "the wrapper must delegate to the fulltext index's TryQuery");
            Assert.IsNotNull(result);
            Assert.AreEqual(1, result.Elements.Count, "exactly the fox sentence matches");
            Assert.AreEqual(foxVertexId, result.Elements[0].GraphElement.Id);
        }

        [TestMethod]
        public void FulltextIndexScan_UnknownIndexName_ReturnsFalse_WithNullResult()
        {
            // The positive control above proves the query itself matches - here ONLY the index
            // name is wrong.
            var fallen8 = CreateEngineWithFulltextIndex(out _, out _);

            var found = fallen8.FulltextIndexScan(out var result, "no-such-index", "fox");

            Assert.IsFalse(found, "an unknown index name answers false, it does not throw");
            Assert.IsNull(result);
        }

        [TestMethod]
        public void FulltextIndexScan_NonFulltextIndex_ReturnsFalse_WithNullResult()
        {
            // The dictionary index CONTAINS the searched element under the same sentence key, so a
            // false here is due to the index TYPE, not missing data.
            var fallen8 = CreateEngineWithFulltextIndex(out var foxVertexId, out _);
            Assert.IsTrue(fallen8.TryGetVertex(out var foxVertex, foxVertexId));
            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out var dictionaryIndex, "dict", "DictionaryIndex"));
            dictionaryIndex.AddOrUpdate("The quick brown fox jumps", foxVertex);

            var found = fallen8.FulltextIndexScan(out var result, "dict", "fox");

            Assert.IsFalse(found, "an existing but non-fulltext index answers false");
            Assert.IsNull(result);
        }

        [TestMethod]
        public void FulltextIndexScan_BlankQueryOrIndexId_ThrowsArgumentException()
        {
            // Today's contract: a null/empty/whitespace query or index id is rejected with an
            // ArgumentException BEFORE any index resolution (both guards use IsNullOrWhiteSpace,
            // so even null throws ArgumentException, not ArgumentNullException).
            var fallen8 = CreateEngineWithFulltextIndex(out _, out _);
            FulltextSearchResult result;

            Assert.ThrowsException<ArgumentException>(() => fallen8.FulltextIndexScan(out result, "fts", ""));
            Assert.ThrowsException<ArgumentException>(() => fallen8.FulltextIndexScan(out result, "fts", "   "));
            Assert.ThrowsException<ArgumentException>(() => fallen8.FulltextIndexScan(out result, "fts", null));
            Assert.ThrowsException<ArgumentException>(() => fallen8.FulltextIndexScan(out result, "  ", "fox"));
        }

        #endregion

        #region GetCountOf<T>

        [TestMethod]
        public void GetCountOf_CountsLiveElementsPerType()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            Assert.AreEqual(0, fallen8.GetCountOf<VertexModel>(), "an empty graph has no vertices");
            Assert.AreEqual(0, fallen8.GetCountOf<EdgeModel>(), "an empty graph has no edges");

            var a = CreateVertex(fallen8);
            var b = CreateVertex(fallen8);
            var c = CreateVertex(fallen8);
            CreateEdge(fallen8, a, b);
            CreateEdge(fallen8, b, c);

            Assert.AreEqual(3, fallen8.GetCountOf<VertexModel>());
            Assert.AreEqual(2, fallen8.GetCountOf<EdgeModel>());
            Assert.AreEqual(5, fallen8.GetCountOf<AGraphElementModel>(), "the base type counts vertices AND edges");
            Assert.AreEqual(0, fallen8.GetCountOf<string>(), "a type no graph element instantiates counts zero");
        }

        [TestMethod]
        public void GetCountOf_AfterRemovals_ExcludesTombstonedElements()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var a = CreateVertex(fallen8);
            var b = CreateVertex(fallen8);
            var c = CreateVertex(fallen8);
            CreateEdge(fallen8, a, b);
            var edgeBC = CreateEdge(fallen8, b, c);

            // Removing a vertex is a SOFT delete (the tombstone keeps its slot) and cascades its
            // edges - the count must reflect the live set, not the slot count.
            fallen8.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = a }).WaitUntilFinished();
            Assert.AreEqual(2, fallen8.GetCountOf<VertexModel>(), "the removed vertex no longer counts");
            Assert.AreEqual(1, fallen8.GetCountOf<EdgeModel>(), "the cascaded edge no longer counts");

            fallen8.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = edgeBC }).WaitUntilFinished();
            Assert.AreEqual(0, fallen8.GetCountOf<EdgeModel>(), "a directly removed edge no longer counts");
            Assert.AreEqual(2, fallen8.GetCountOf<VertexModel>(), "removing an edge leaves its endpoints");

            // GetCountOf is what recalculates the public counters - the two views must agree.
            Assert.AreEqual(fallen8.VertexCount, fallen8.GetCountOf<VertexModel>());
            Assert.AreEqual(fallen8.EdgeCount, fallen8.GetCountOf<EdgeModel>());

            fallen8.EnqueueTransaction(new TabulaRasaTransaction()).WaitUntilFinished();
            Assert.AreEqual(0, fallen8.GetCountOf<AGraphElementModel>(), "TabulaRasa empties every type's count");
        }

        #endregion

        #region GraphScan operator arms + default branch

        [TestMethod]
        public void GraphScan_LowerOperator_ReturnsStrictlySmallerMatches()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            CreateAgedVertices(fallen8);

            var found = fallen8.GraphScan(out var result, "age", 40, BinaryOperator.Lower);

            Assert.IsTrue(found);
            CollectionAssert.AreEquivalent(new[] { 20, 30 }, AgesOf(result),
                "Lower is strict: the boundary value 40 must not match");
        }

        [TestMethod]
        public void GraphScan_GreaterOrEqualsOperator_IncludesTheBoundary()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            CreateAgedVertices(fallen8);

            var found = fallen8.GraphScan(out var result, "age", 40, BinaryOperator.GreaterOrEquals);

            Assert.IsTrue(found);
            CollectionAssert.AreEquivalent(new[] { 40, 50 }, AgesOf(result),
                "GreaterOrEquals includes the boundary value 40");
        }

        [TestMethod]
        public void GraphScan_NotEqualsOperator_ExcludesExactlyTheMatchingValue()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            CreateAgedVertices(fallen8);

            var found = fallen8.GraphScan(out var result, "age", 30, BinaryOperator.NotEquals);

            Assert.IsTrue(found);
            CollectionAssert.AreEquivalent(new[] { 20, 40, 50 }, AgesOf(result));
        }

        [TestMethod]
        public void GraphScan_NoMatch_ReturnsFalse_WithEmptyResult()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            CreateAgedVertices(fallen8);

            var found = fallen8.GraphScan(out var result, "age", 20, BinaryOperator.Lower);

            Assert.IsFalse(found, "a valid operator with no match answers false");
            Assert.IsNotNull(result, "a no-match scan hands back an empty list, not null");
            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void GraphScan_InvalidOperator_ReturnsFalse_WithEmptyNonNullResult()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            CreateAgedVertices(fallen8);

            // Positive control on the same data: a valid operator matches - the empty result
            // below is due to the OPERATOR, not the seed.
            Assert.IsTrue(fallen8.GraphScan(out _, "age", 30, BinaryOperator.Equals));

            var found = fallen8.GraphScan(out var result, "age", 30, (BinaryOperator)999);

            Assert.IsFalse(found, "the default operator branch answers false");
            Assert.IsNotNull(result, "GraphScan's default branch hands back an EMPTY list (unlike IndexScan's null)");
            Assert.AreEqual(0, result.Count);
        }

        #endregion

        #region IndexScan / RangeIndexScan default branches

        [TestMethod]
        public void IndexScan_UnknownIndex_ReturnsFalse_WithNullResult()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            CreateAgedVertices(fallen8);

            var found = fallen8.IndexScan(out var result, "no-such-index", 20, BinaryOperator.Equals);

            Assert.IsFalse(found);
            Assert.IsNull(result);
        }

        [TestMethod]
        public void IndexScan_InvalidOperator_ReturnsFalse_WithNullResult_OnDictionaryAndRangeIndexes()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var ids = CreateAgedVertices(fallen8);
            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out var dictionaryIndex, "dictIdx", "DictionaryIndex"));
            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out var rangeIndex, "rangeIdx", "RangeIndex"));
            foreach (var id in ids)
            {
                Assert.IsTrue(fallen8.TryGetVertex(out var vertex, id));
                Assert.IsTrue(vertex.TryGetProperty(out int age, "age"));
                dictionaryIndex.AddOrUpdate(age, vertex);
                rangeIndex.AddOrUpdate(age, vertex);
            }

            // Positive control: both indices answer a valid operator, so the default branch below
            // is reached because of the operator, not missing data.
            Assert.IsTrue(fallen8.IndexScan(out var control, "dictIdx", 20, BinaryOperator.Equals));
            Assert.AreEqual(1, control.Count);

            // Non-range index: straight into the generic switch's default arm.
            Assert.IsFalse(fallen8.IndexScan(out var dictionaryResult, "dictIdx", 20, (BinaryOperator)999));
            Assert.IsNull(dictionaryResult, "IndexScan's default branch hands back null (unlike GraphScan's empty list)");

            // Range index: TryOrderedRangeIndexScan declines the unknown operator, then the
            // generic default arm answers null + false the same way.
            Assert.IsFalse(fallen8.IndexScan(out var rangeResult, "rangeIdx", 20, (BinaryOperator)999));
            Assert.IsNull(rangeResult);
        }

        [TestMethod]
        public void RangeIndexScan_NonRangeOrUnknownIndex_ReturnsFalse_WithNullResult()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var ids = CreateAgedVertices(fallen8);
            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out var dictionaryIndex, "dictIdx", "DictionaryIndex"));
            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out var rangeIndex, "rangeIdx", "RangeIndex"));
            foreach (var id in ids)
            {
                Assert.IsTrue(fallen8.TryGetVertex(out var vertex, id));
                Assert.IsTrue(vertex.TryGetProperty(out int age, "age"));
                dictionaryIndex.AddOrUpdate(age, vertex);
                rangeIndex.AddOrUpdate(age, vertex);
            }

            // Positive control: the real range index answers the same window.
            Assert.IsTrue(fallen8.RangeIndexScan(out var control, "rangeIdx", 20, 40));
            Assert.AreEqual(3, control.Count);

            // The same window against a NON-range index declines - false with a null result.
            Assert.IsFalse(fallen8.RangeIndexScan(out var dictionaryResult, "dictIdx", 20, 40));
            Assert.IsNull(dictionaryResult);

            Assert.IsFalse(fallen8.RangeIndexScan(out var unknownResult, "no-such-index", 20, 40));
            Assert.IsNull(unknownResult);
        }

        #endregion
    }
}
