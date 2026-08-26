// MIT License
//
// AnalyticsPluginVendorContractTest.cs
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
using System.Diagnostics;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core.Algorithms.Analytics;
using NoSQL.GraphDB.Core.Plugin;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// The vendor contract a THIRD-PARTY analytics plugin author gets from the documented
    /// convenience base: <see cref="AGraphAnalyticsAlgorithm.Manufacturer"/> was non-virtual (audit
    /// defect B41), so a subclass of that base was advertised under the built-ins' vendor. It is
    /// virtual now, the plugin category stays fixed, and a subclass that overrides nothing still
    /// inherits the built-ins' default.
    /// <para>
    /// The file also owns the unit of <see cref="IndexStatsREST.NonNegativeCount"/>, the one-line
    /// helper both index inventories normalise their counts through (the engine's negative "count
    /// not supported" sentinel becomes null; every real count survives). Its two REST consumers,
    /// which must report identical counts, are pinned in <c>ObservabilityEndpointTest</c>.
    /// </para>
    /// </summary>
    [TestClass]
    public class AnalyticsPluginVendorContractTest
    {
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

        #region B52 - the negative "count not supported" sentinel maps to null

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
