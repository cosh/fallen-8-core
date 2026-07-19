// MIT License
//
// PropertyIngestionCultureTest.cs
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
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.App.Controllers.Model;
using NoSQL.GraphDB.Core.Expression;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Feature property-ingestion-culture: the REST property/literal round-trip must be
    ///   locale-independent. Every test runs under a comma-decimal culture (de-DE, where "."
    ///   is a group separator) so a CurrentCulture-based conversion would misparse "0.8" as 8
    ///   on ingest and render 0.8 as "0,8" on egress. With the InvariantCulture fix these all
    ///   pass; on the pre-fix code they fail - so they pin the bug, not just the fix.
    /// </summary>
    [TestClass]
    public class PropertyIngestionCultureTest
    {
        private static readonly CultureInfo CommaDecimal = CultureInfo.GetCultureInfo("de-DE");

        private Fallen8 _fallen8;
        private GraphController _controller;

        [TestInitialize]
        public void TestInitialize()
        {
            var loggerFactory = TestLoggerFactory.Create();
            _fallen8 = new Fallen8(loggerFactory);
            _controller = new GraphController(loggerFactory.CreateLogger<GraphController>(), _fallen8);
        }

        /// <summary>Runs the body with CurrentCulture forced to de-DE, always restoring after.</summary>
        private static async Task UnderCommaDecimal(Func<Task> body)
        {
            var previousCulture = CultureInfo.CurrentCulture;
            var previousUiCulture = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentCulture = CommaDecimal;
            CultureInfo.CurrentUICulture = CommaDecimal;
            try
            {
                await body();
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
                CultureInfo.CurrentUICulture = previousUiCulture;
            }
        }

        private static PropertySpecification Property(string id, string type, string value) =>
            new PropertySpecification { PropertyId = id, FullQualifiedTypeName = type, PropertyValue = value };

        private async Task<int> AddVertexAsync(string label, params PropertySpecification[] properties)
        {
            var before = _controller.GetGraph(10_000).Vertices.Select(v => v.Id).ToHashSet();
            await _controller.AddVertex(
                new VertexSpecification { Label = label, CreationDate = 0, Properties = properties.ToList() },
                waitForCompletion: true);
            var added = _controller.GetGraph(10_000).Vertices.Select(v => v.Id).Where(id => !before.Contains(id)).ToList();
            Assert.AreEqual(1, added.Count, "Exactly one vertex should have been created.");
            return added[0];
        }

        private string PropertyValueOf(int vertexId, string propertyId) =>
            _controller.GetVertex(vertexId).Properties.Single(p => p.PropertyId == propertyId).PropertyValue;

        [TestMethod]
        public void TestCulture_IsActuallyCommaDecimal()
        {
            // Guard: if the runner had no comma-decimal culture, the whole suite would be a
            // no-op that passes trivially. Fail loudly instead.
            Assert.AreEqual("0,8", (0.8).ToString(CommaDecimal), "de-DE must format 0.8 with a comma.");
        }

        [TestMethod]
        public async Task DoubleProperty_RoundTripsInvariant_UnderCommaDecimalCulture()
        {
            await UnderCommaDecimal(async () =>
            {
                var id = await AddVertexAsync(
                    "person",
                    Property("weight", "System.Double", "0.8"),
                    Property("ratio", "System.Single", "0.5"),
                    Property("balance", "System.Decimal", "1.25"));

                // Ingest parsed the fraction (not 8/5/125) AND egress renders it invariantly
                // ("0.8", never "0,8" or "8"). This is the exact bug the fixture seeding hit.
                Assert.AreEqual("0.8", PropertyValueOf(id, "weight"), "double must round-trip as 0.8");
                Assert.AreEqual("0.5", PropertyValueOf(id, "ratio"), "single must round-trip as 0.5");
                Assert.AreEqual("1.25", PropertyValueOf(id, "balance"), "decimal must round-trip as 1.25");
            });
        }

        [TestMethod]
        public async Task GraphScan_DecimalLiteral_MatchesUnderCommaDecimalCulture()
        {
            await UnderCommaDecimal(async () =>
            {
                var id = await AddVertexAsync("person", Property("weight", "System.Double", "0.8"));

                // The scan literal "0.8" must parse to 0.8 (not 8) to equal the stored value.
                var scan = _controller.GraphScan("weight", new ScanSpecification
                {
                    Literal = new LiteralSpecification { Value = "0.8", FullQualifiedTypeName = "System.Double" },
                    Operator = BinaryOperator.Equals,
                    ResultType = ResultTypeSpecification.Vertices,
                });

                Assert.IsNotNull(scan.Value, "A well-formed scan must return results, not a 4xx.");
                CollectionAssert.AreEquivalent(new[] { id }, scan.Value.ToList(), "The decimal scan must find the vertex.");
            });
        }

        [TestMethod]
        public async Task AddProperty_DecimalValue_RoundTripsInvariant_UnderCommaDecimalCulture()
        {
            await UnderCommaDecimal(async () =>
            {
                var id = await AddVertexAsync("person", Property("name", "System.String", "Alice"));

                await _controller.AddProperty(id, "score", Property("score", "System.Double", "1.5"), waitForCompletion: true);

                Assert.AreEqual("1.5", PropertyValueOf(id, "score"), "an updated decimal property must round-trip as 1.5");
            });
        }

        [TestMethod]
        public async Task RangeIndexScan_DecimalLimits_MatchUnderCommaDecimalCulture()
        {
            await UnderCommaDecimal(async () =>
            {
                // Integer-valued key (parses the same in any culture) with DECIMAL limits, so this
                // discriminates the limit parse specifically: buggy "2.5"/"3.5" -> 25/35 would put
                // 3.0 outside [25,35] and the scan would miss it.
                var id = await AddVertexAsync("gauge", Property("weight", "System.Double", "3"));
                Assert.IsTrue(_controller.CreateIndex(new PluginSpecification { UniqueId = "wIdx", PluginType = "RangeIndex" }),
                    "RangeIndex should be created.");
                Assert.IsTrue(
                    _controller.AddToIndex("wIdx", new IndexAddToSpecification
                    {
                        GraphElementId = id,
                        Key = Property("weight", "System.Double", "3"),
                    }),
                    "The vertex should be added to the index.");

                var scan = _controller.RangeIndexScan(new RangeIndexScanSpecification
                {
                    IndexId = "wIdx",
                    LeftLimit = "2.5",
                    RightLimit = "3.5",
                    FullQualifiedTypeName = "System.Double",
                    IncludeLeft = true,
                    IncludeRight = true,
                    ResultType = ResultTypeSpecification.Vertices,
                });

                Assert.IsNotNull(scan.Value, "A well-formed range scan must return results, not a 4xx.");
                CollectionAssert.AreEquivalent(new[] { id }, scan.Value.ToList(), "3.0 must fall within the decimal range [2.5, 3.5].");
            });
        }

        [TestMethod]
        public async Task IntegerAndStringProperties_AreUnaffectedByCulture()
        {
            // Regression guard: no decimal separator, so these must be byte-identical to the
            // invariant form even under de-DE (the fix must not disturb them).
            string underComma = null;
            await UnderCommaDecimal(async () =>
            {
                var id = await AddVertexAsync(
                    "person",
                    Property("age", "System.Int32", "30"),
                    Property("name", "System.String", "Alice"));
                Assert.AreEqual("30", PropertyValueOf(id, "age"));
                Assert.AreEqual("Alice", PropertyValueOf(id, "name"));
                underComma = PropertyValueOf(id, "age");
            });
            Assert.AreEqual("30", underComma);
        }

        [TestMethod]
        public async Task DateTimeProperty_StillRoundTripsInvariant()
        {
            await UnderCommaDecimal(async () =>
            {
                var id = await AddVertexAsync("event", Property("at", "System.DateTime", "2024-01-15T08:30:00"));

                var stored = PropertyValueOf(id, "at");
                // Egress uses the ISO-8601 "O" round-trip form; it must parse back invariantly.
                var parsed = DateTime.Parse(stored, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                Assert.AreEqual(new DateTime(2024, 1, 15, 8, 30, 0), parsed);
            });
        }
    }
}
