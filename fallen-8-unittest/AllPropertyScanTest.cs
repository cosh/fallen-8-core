// MIT License
//
// AllPropertyScanTest.cs
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
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pins the all-property-search engine contract (features/open/all-property-search/):
    /// <see cref="Fallen8.GraphScanAllProperties"/> is a cold, case-insensitive substring scan
    /// over EVERY property value (rendered to its invariant string form, so numbers/booleans/dates
    /// are searchable), OR across an element's keys, skipping reserved embedding entries and
    /// removed elements, honouring the label restrictor, and returning both vertices and edges.
    /// The contains behaviour is exclusive to this path; the named-key <see cref="Fallen8.GraphScan"/>
    /// keeps its typed operator comparators (unaffected here).
    /// </summary>
    [TestClass]
    public class AllPropertyScanTest
    {
        private ILoggerFactory _loggerFactory;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
        }

        /// <summary>Seeds a vertex, returns its id.</summary>
        private static int AddVertex(Fallen8 fallen8, string label, Dictionary<string, object> properties)
        {
            var tx = new CreateVerticesTransaction();
            tx.AddVertex(1u, label, properties);
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedVertices()[0].Id;
        }

        [TestMethod]
        public void MatchesAnyPropertyAcrossKeys_CaseInsensitively_Or()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v1 = AddVertex(fallen8, "company", new Dictionary<string, object> { { "name", "Acme Corp" }, { "city", "Berlin" } });
            var v2 = AddVertex(fallen8, "company", new Dictionary<string, object> { { "name", "Globex" }, { "note", "supplies ACME parts" } });
            AddVertex(fallen8, "company", new Dictionary<string, object> { { "name", "Initech" }, { "city", "Boston" } });

            // "acme" is in v1's name and in v2's note (different keys) - OR across keys and
            // case-insensitive; Initech matches on no key.
            Assert.IsTrue(fallen8.GraphScanAllProperties(out var hits, "acme"));
            CollectionAssert.AreEquivalent(new List<int> { v1, v2 }, hits.Select(h => h.Id).ToList());

            fallen8.Dispose();
        }

        [TestMethod]
        public void StringifiesNumericBooleanAndDateValues_InvariantCulture()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            AddVertex(fallen8, "sample", new Dictionary<string, object>
            {
                { "age", 42 },
                { "score", 3.14d },
                { "active", true },
                { "born", new DateTime(2019, 5, 1) },
            });

            // Prove the render is INVARIANT, not the host culture: under a comma-decimal culture a
            // CurrentCulture-based render would emit "3,14" and the dot-form scan would miss. Set the
            // default thread culture too so any parallel-scan worker thread inherits it.
            var originalCurrent = CultureInfo.CurrentCulture;
            var originalDefault = CultureInfo.DefaultThreadCurrentCulture;
            try
            {
                var german = new CultureInfo("de-DE");
                CultureInfo.CurrentCulture = german;
                CultureInfo.DefaultThreadCurrentCulture = german;

                Assert.IsTrue(fallen8.GraphScanAllProperties(out _, "42"), "int 42 is searchable as text.");
                Assert.IsTrue(fallen8.GraphScanAllProperties(out _, "3.14"), "double renders with an invariant dot.");
                Assert.IsFalse(fallen8.GraphScanAllProperties(out _, "3,14"), "the host's comma-decimal form must NOT leak in.");
                Assert.IsTrue(fallen8.GraphScanAllProperties(out _, "true"), "bool renders as True, matched case-insensitively.");
                Assert.IsTrue(fallen8.GraphScanAllProperties(out _, "2019"), "a DateTime's year substring is searchable.");
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCurrent;
                CultureInfo.DefaultThreadCurrentCulture = originalDefault;
            }

            fallen8.Dispose();
        }

        [TestMethod]
        public void SkipsReservedEmbeddingEntries_ButScansNormalValues()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            // Only a reserved model stamp whose STRING value contains "embed": it must never match.
            var stampOnly = AddVertex(fallen8, "doc", new Dictionary<string, object>
            {
                { AGraphElementModel.EmbeddingModelStampPrefix + "default", "nomic-embed-text" },
            });
            // A normal property carrying "embed" - a real hit.
            var normal = AddVertex(fallen8, "doc", new Dictionary<string, object> { { "note", "we embed vectors" } });
            // A reserved vector (float[]) alongside a normal hit: the vector must neither match nor throw.
            var withVector = AddVertex(fallen8, "doc", new Dictionary<string, object>
            {
                { AGraphElementModel.EmbeddingPropertyPrefix + "default", new float[] { 0.1f, 0.2f } },
                { "title", "embed manual" },
            });

            Assert.IsTrue(fallen8.GraphScanAllProperties(out var hits, "embed"));
            CollectionAssert.AreEquivalent(new List<int> { normal, withVector }, hits.Select(h => h.Id).ToList(),
                "the reserved model stamp is excluded; normal values (even next to a vector) are searched.");
            Assert.IsFalse(hits.Any(h => h.Id == stampOnly), "a reserved-stamp-only element never matches content.");

            fallen8.Dispose();
        }

        [TestMethod]
        public void LabelRestrictor_NarrowsByExactLabel()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var company = AddVertex(fallen8, "company", new Dictionary<string, object> { { "name", "Acme" } });
            var person = AddVertex(fallen8, "person", new Dictionary<string, object> { { "name", "Acme fan" } });

            Assert.IsTrue(fallen8.GraphScanAllProperties(out var all, "acme"));
            CollectionAssert.AreEquivalent(new List<int> { company, person }, all.Select(h => h.Id).ToList());

            Assert.IsTrue(fallen8.GraphScanAllProperties(out var companies, "acme", "company"));
            CollectionAssert.AreEquivalent(new List<int> { company }, companies.Select(h => h.Id).ToList());

            Assert.IsFalse(fallen8.GraphScanAllProperties(out var none, "acme", "nonexistent"));
            Assert.AreEqual(0, none.Count);

            fallen8.Dispose();
        }

        [TestMethod]
        public void ScansEdgesToo_ReturnsVerticesAndEdges()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var alice = AddVertex(fallen8, "person", new Dictionary<string, object> { { "name", "Alice" } });
            var bob = AddVertex(fallen8, "person", new Dictionary<string, object> { { "name", "Bob" } });

            var edgeTx = new CreateEdgesTransaction();
            edgeTx.AddEdge(alice, "knows", bob, 1u, "knows",
                new Dictionary<string, object> { { "relation", "knows Alice well" } });
            fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();

            // "alice" is in the vertex name AND the edge property value.
            Assert.IsTrue(fallen8.GraphScanAllProperties(out var hits, "alice"));
            Assert.AreEqual(2, hits.Count, "the vertex and the edge both match.");
            Assert.IsTrue(hits.OfType<VertexModel>().Any(v => v.Id == alice), "the vertex is returned.");
            Assert.IsTrue(hits.OfType<EdgeModel>().Any(), "the edge is returned (result-type filtering is a REST concern).");
            Assert.IsFalse(hits.Any(h => h.Id == bob), "Bob matches nothing.");

            fallen8.Dispose();
        }

        [TestMethod]
        public void RemovedElement_IsExcluded()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var id = AddVertex(fallen8, "company", new Dictionary<string, object> { { "name", "RemoveMe Corp" } });

            Assert.IsTrue(fallen8.GraphScanAllProperties(out var before, "removeme"));
            Assert.AreEqual(1, before.Count);

            fallen8.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = id }).WaitUntilFinished();

            Assert.IsFalse(fallen8.GraphScanAllProperties(out var after, "removeme"),
                "a removed element never surfaces in the scan.");
            Assert.AreEqual(0, after.Count);

            fallen8.Dispose();
        }

        [TestMethod]
        public void BlankTerm_MatchesNothing_WithoutThrowing()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            AddVertex(fallen8, "company", new Dictionary<string, object> { { "name", "Acme" } });

            foreach (var blank in new[] { null, "", "   " })
            {
                Assert.IsFalse(fallen8.GraphScanAllProperties(out var hits, blank),
                    $"a blank term ('{blank ?? "null"}') matches nothing.");
                Assert.AreEqual(0, hits.Count);
            }

            fallen8.Dispose();
        }

        [TestMethod]
        public void NoMatch_ReturnsEmpty_AndFalse()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            AddVertex(fallen8, "company", new Dictionary<string, object> { { "name", "Acme" } });

            Assert.IsFalse(fallen8.GraphScanAllProperties(out var hits, "not-present-anywhere"));
            Assert.AreEqual(0, hits.Count);

            fallen8.Dispose();
        }

        [TestMethod]
        public void NonConvertibleValue_IsSkipped_NotRenderedToTypeName()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            // A non-reserved float[] blob: it must be skipped, not rendered to "System.Single[]"
            // (which would false-positive a search for "single"), and it must not throw.
            AddVertex(fallen8, "sample", new Dictionary<string, object> { { "blob", new float[] { 1f, 2f } } });

            Assert.IsFalse(fallen8.GraphScanAllProperties(out var hits, "single"),
                "a non-convertible blob is skipped, never rendered to its type name.");
            Assert.AreEqual(0, hits.Count);

            fallen8.Dispose();
        }
    }
}
