// MIT License
//
// AuditDefectReportingTest.cs
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
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Analytics;
using NoSQL.GraphDB.Core.Index.Spatial;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Regression tests for the audit defects on the two reporting surfaces:
    ///  - B52: GET /statistics leaked the engine's negative "count not supported" sentinel (the
    ///    spatial R-Tree's CountOfKeys) as a real key count, while GET /status normalised the same
    ///    sentinel to null. Both inventories now normalise through
    ///    <see cref="IndexStatsREST.NonNegativeCount"/>, so they cannot disagree.
    ///  - B41: <see cref="AGraphAnalyticsAlgorithm.Manufacturer"/> was non-virtual, so a
    ///    third-party subclass of the documented convenience base was advertised under the
    ///    built-ins' vendor. It is virtual now; the built-ins keep the default.
    /// </summary>
    [TestClass]
    public class AuditDefectReportingTest
    {
        private const String DictionaryIndexName = "byName";
        private const String SpatialIndexName = "byLocation";

        private sealed class TestFactory : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
            }
        }

        private static Fallen8 EngineOf(TestFactory factory)
        {
            return factory.Services.GetRequiredService<NoSQL.GraphDB.App.Namespaces.Fallen8Namespaces>()
                .Default.Engine;
        }

        /// <summary>
        /// Arranges the two indices the sentinel contract needs: a DictionaryIndex (counts BOTH keys
        /// and values honestly) and a SpatialIndex (an R-Tree: CountOfValues is honest,
        /// CountOfKeys answers the negative "not supported" sentinel). The spatial index is created
        /// engine-side on purpose - its Initialize needs live CLR objects the REST pluginOptions
        /// cannot carry (pinned by StatusIndexInventoryTest).
        /// </summary>
        private static void SeedTwoIndices(TestFactory factory)
        {
            var engine = EngineOf(factory);

            var vertices = new CreateVerticesTransaction();
            vertices.AddVertex(1u, "person");
            vertices.AddVertex(1u, "person");
            engine.EnqueueTransaction(vertices).WaitUntilFinished();
            var created = vertices.GetCreatedVertices().ToArray();
            Assert.AreEqual(2, created.Length, "Arrange failed: the vertices were not created.");

            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out var dictionaryIndex, DictionaryIndexName,
                "DictionaryIndex"), "Arrange failed: the dictionary index was not created.");
            dictionaryIndex.AddOrUpdate("alice", created[0]);
            dictionaryIndex.AddOrUpdate("bob", created[1]);

            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out var spatialIndex, SpatialIndexName,
                "SpatialIndex", RTreeParameters()), "Arrange failed: the spatial index was not created.");
            spatialIndex.AddOrUpdate(new Point(1.0f, 1.0f), created[0]);
            spatialIndex.AddOrUpdate(new Point(2.0f, 2.0f), created[1]);

            Assert.IsTrue(spatialIndex.CountOfKeys() < 0,
                "Arrange failed: the R-Tree must answer the negative 'count not supported' sentinel " +
                "- that sentinel is what this defect was about leaking.");
            Assert.AreEqual(2, spatialIndex.CountOfValues(),
                "Arrange failed: the R-Tree counts VALUES honestly, so only the key count may null out.");
        }

        private static IDictionary<String, Object> RTreeParameters()
        {
            return new Dictionary<String, Object>
            {
                ["IMetric"] = new NoSQL.GraphDB.Core.Index.Spatial.Implementation.Metric.EuclidianMetric(),
                ["MinCount"] = 2,
                ["MaxCount"] = 5,
                ["Space"] = new List<IDimension>
                {
                    new NoSQL.GraphDB.Core.Index.Spatial.Implementation.Geometry.RealDimension(),
                    new NoSQL.GraphDB.Core.Index.Spatial.Implementation.Geometry.RealDimension(),
                }
            };
        }

        private static async Task<JsonElement> Get(HttpClient client, String path)
        {
            using var response = await client.GetAsync(path);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        }

        /// <summary>
        /// The per-index (keys, values) pair of one inventory, keyed by index name. Both fields must
        /// be PRESENT on the wire - a null count is reported as an explicit null, never omitted, so
        /// a client can tell "not supported" apart from "field missing".
        /// </summary>
        private static Dictionary<String, (Int32? Keys, Int32? Values)> CountsByIndex(
            JsonElement inventory, String nameProperty)
        {
            return inventory.EnumerateArray().ToDictionary(
                entry => entry.GetProperty(nameProperty).GetString(),
                entry => (Keys: Count(entry, "keys"), Values: Count(entry, "values")));
        }

        private static Int32? Count(JsonElement entry, String property)
        {
            Assert.IsTrue(entry.TryGetProperty(property, out var value),
                property + " must be present on every inventory entry (null, never omitted)");
            return value.ValueKind == JsonValueKind.Null ? (Int32?)null : value.GetInt32();
        }

        #region B52 - /statistics must not leak the negative count sentinel

        [TestMethod]
        public async Task Statistics_SpatialKeyCount_IsNull_NotTheNegativeSentinel()
        {
            using var factory = new TestFactory();
            using var client = factory.CreateClient();
            SeedTwoIndices(factory);

            var indices = CountsByIndex((await Get(client, "/statistics")).GetProperty("indices"), "name");

            Assert.IsNull(indices[SpatialIndexName].Keys,
                "the R-Tree's negative 'count not supported' sentinel must surface as null, never as -1");
            Assert.AreEqual<Int32?>(2, indices[SpatialIndexName].Values,
                "a count the index DOES support is still reported (only the sentinel nulls out)");
            Assert.AreEqual<Int32?>(2, indices[DictionaryIndexName].Keys,
                "an index with real counts is unaffected by the normalisation");
            Assert.AreEqual<Int32?>(2, indices[DictionaryIndexName].Values);
        }

        [TestMethod]
        public async Task Statistics_And_Status_ReportIdenticalIndexCounts()
        {
            using var factory = new TestFactory();
            using var client = factory.CreateClient();
            SeedTwoIndices(factory);

            // The agreement assertion is the load-bearing one: it is what stops the two discovery
            // surfaces drifting apart again (the /statistics field was Int32 and raw, /status
            // Int32? and normalised).
            var statistics = CountsByIndex((await Get(client, "/statistics")).GetProperty("indices"), "name");
            var status = CountsByIndex((await Get(client, "/status")).GetProperty("indices"), "indexId");

            CollectionAssert.AreEquivalent(statistics.Keys.ToList(), status.Keys.ToList(),
                "both inventories list the same indices");
            foreach (var name in statistics.Keys)
            {
                Assert.AreEqual(status[name].Keys, statistics[name].Keys,
                    "/statistics and /status must report the same key count for " + name);
                Assert.AreEqual(status[name].Values, statistics[name].Values,
                    "/statistics and /status must report the same value count for " + name);
            }
        }

        [TestMethod]
        public async Task Statistics_WithoutAnySpatialIndex_ReportsPlainNumbers()
        {
            // The unchanged default: nullable fields do not mean "usually null". Every index that
            // supports counting still answers a number.
            using var factory = new TestFactory();
            using var client = factory.CreateClient();
            Assert.IsTrue(EngineOf(factory).IndexFactory.TryCreateIndex(out _, DictionaryIndexName,
                "DictionaryIndex"), "Arrange failed: the dictionary index was not created.");

            var indices = CountsByIndex((await Get(client, "/statistics")).GetProperty("indices"), "name");

            Assert.AreEqual<Int32?>(0, indices[DictionaryIndexName].Keys, "a fresh index reports zero, not null");
            Assert.AreEqual<Int32?>(0, indices[DictionaryIndexName].Values);
        }

        [TestMethod]
        public void NonNegativeCount_MapsOnlyNegativesToNull()
        {
            Assert.IsNull(IndexStatsREST.NonNegativeCount(-1), "the documented R-Tree sentinel");
            Assert.IsNull(IndexStatsREST.NonNegativeCount(Int32.MinValue), "any negative is a sentinel");
            Assert.IsNull(IndexStatsREST.NonNegativeCount(null), "an absent index stays absent");

            Assert.AreEqual<Int32?>(0, IndexStatsREST.NonNegativeCount(0), "zero is a real count, not a sentinel");
            Assert.AreEqual<Int32?>(1, IndexStatsREST.NonNegativeCount(1));
            Assert.AreEqual<Int32?>(Int32.MaxValue, IndexStatsREST.NonNegativeCount(Int32.MaxValue));
        }

        #endregion

        #region B41 - a third-party analytics subclass declares its own vendor

        [TestMethod]
        public void AnalyticsBase_ManufacturerGetter_IsVirtual_ButThePluginCategoryStaysFixed()
        {
            var manufacturer = typeof(AGraphAnalyticsAlgorithm)
                .GetProperty(nameof(AGraphAnalyticsAlgorithm.Manufacturer), BindingFlags.Public | BindingFlags.Instance)
                .GetGetMethod();
            Assert.IsTrue(manufacturer.IsVirtual && !manufacturer.IsFinal,
                "Manufacturer must be overridable - the base is offered to third parties");

            var category = typeof(AGraphAnalyticsAlgorithm)
                .GetProperty(nameof(AGraphAnalyticsAlgorithm.PluginCategory), BindingFlags.Public | BindingFlags.Instance)
                .GetGetMethod();
            Assert.IsFalse(category.IsVirtual && !category.IsFinal,
                "PluginCategory is the contract category, not a vendor choice - it stays fixed");
        }

        [TestMethod]
        public void ThirdPartySubclass_DeclaresItsOwnVendor_ThroughTheBaseReference()
        {
            // Read through the BASE reference: that is how PluginFactory (via IPlugin) reads it, so
            // this is the assertion a non-virtual member could not satisfy.
            AGraphAnalyticsAlgorithm thirdParty = new VendorDeclaringAnalyticsAlgorithm();
            Assert.AreEqual(VendorDeclaringAnalyticsAlgorithm.TestManufacturer, thirdParty.Manufacturer);
            Assert.AreEqual(VendorDeclaringAnalyticsAlgorithm.TestManufacturer,
                ((IPlugin)thirdParty).Manufacturer);
        }

        [TestMethod]
        public void SubclassThatDoesNotOverride_InheritsTheBuiltInVendor()
        {
            // The unchanged default (and the documented limitation of making it virtual rather than
            // abstract): a subclass that says nothing still reports the built-ins' vendor.
            Assert.AreEqual(BuiltInVendor, new InheritingAnalyticsAlgorithm().Manufacturer);
        }

        [TestMethod]
        public void PluginDescriptions_ShowTheThirdPartyVendor_AndKeepTheBuiltInsUnchanged()
        {
            Assert.IsTrue(
                PluginFactory.TryGetAvailablePluginsWithDescriptions<IGraphAnalyticsAlgorithm>(out var descriptions));

            Assert.IsTrue(descriptions.TryGetValue(VendorDeclaringAnalyticsAlgorithm.TestPluginName,
                out var thirdParty),
                "the test-assembly plugin must be discovered (top-level public, parameterless ctor)");
            StringAssert.Contains(thirdParty,
                "*MANUFACTURER: " + VendorDeclaringAnalyticsAlgorithm.TestManufacturer,
                "the listing must advertise the subclass's own vendor");
            Assert.IsFalse(thirdParty.Contains(BuiltInVendor, StringComparison.Ordinal),
                "no trace of the built-ins' vendor may remain on a third-party plugin");

            foreach (var builtIn in new[] { "PAGERANK", "WCC", "LABELPROPAGATION", "DEGREE", "TRIANGLECOUNT" })
            {
                Assert.IsTrue(descriptions.TryGetValue(builtIn, out var description),
                    builtIn + " must still be listed");
                StringAssert.Contains(description, "*MANUFACTURER: " + BuiltInVendor,
                    builtIn + " keeps the default vendor - the built-ins genuinely are authored by it");
            }
        }

        /// <summary>The vendor the five built-in analytics algorithms report (the base default).</summary>
        private const String BuiltInVendor = "Henning Rauch";

        /// <summary>
        /// A subclass that does NOT override Manufacturer, pinning the inherited default. Deliberately
        /// non-public (and nested) so <c>PluginFactory</c> never discovers it - only the overriding
        /// double below is meant to appear in the analytics plugin listing.
        /// </summary>
        private sealed class InheritingAnalyticsAlgorithm : AGraphAnalyticsAlgorithm
        {
            public override String PluginName => "AUDITB41INHERITING";

            public override String Description => "A test algorithm that declares no vendor of its own.";

            protected override Boolean TryRunCore(out GraphAnalyticsResult result,
                GraphAnalyticsDefinition definition, Workspace workspace, BudgetGuard budget,
                Stopwatch stopwatch)
            {
                result = null;
                return false;
            }
        }

        #endregion
    }

    /// <summary>
    /// A third-party analytics plugin double: it subclasses the convenience base
    /// <see cref="AGraphAnalyticsAlgorithm"/> exactly as the plugin guide recommends and declares its
    /// OWN vendor by overriding <see cref="AGraphAnalyticsAlgorithm.Manufacturer"/> (audit defect
    /// B41 - before the fix that member was non-virtual and this override could not compile). It is a
    /// top-level public type with a public parameterless constructor so <c>PluginFactory</c>
    /// discovers it by plugin name (nested types report <c>IsNestedPublic</c>, not <c>IsPublic</c>,
    /// so they are skipped). Running it is an inert no-op.
    /// </summary>
    /// <remarks>
    /// Consequence of being globally discoverable: this double is enumerated by
    /// <c>PluginFactory.TryGetAvailablePlugins&lt;IGraphAnalyticsAlgorithm&gt;()</c> during test runs,
    /// so any FUTURE test asserting an exact set or count of available ANALYTICS plugins must filter
    /// the test doubles out (e.g. by <see cref="Manufacturer"/> == "fallen-8 tests"). The same note on
    /// the index and service sides lives on <c>ThrowingOnLoadIndex</c> and <c>StoppableTestService</c>.
    /// </remarks>
    public sealed class VendorDeclaringAnalyticsAlgorithm : AGraphAnalyticsAlgorithm
    {
        public const String TestPluginName = "AUDITB41THIRDPARTY";
        public const String TestManufacturer = "fallen-8 tests";

        public override String PluginName => TestPluginName;

        public override String Description => "A test algorithm that declares its own vendor.";

        public override String Manufacturer => TestManufacturer;

        protected override Boolean TryRunCore(out GraphAnalyticsResult result,
            GraphAnalyticsDefinition definition, Workspace workspace, BudgetGuard budget,
            Stopwatch stopwatch)
        {
            result = null;
            return false;
        }
    }
}
