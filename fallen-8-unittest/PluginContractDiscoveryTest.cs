// MIT License
//
// PluginContractDiscoveryTest.cs
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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core.Algorithms.Analytics;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Plugins;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Pins the single-homed contract-to-interface discovery (consolidation-audit CA-13):
    ///   <see cref="PluginFactory.ContractInterface"/> maps each <see cref="PluginContract"/> to its
    ///   engine interface, and <see cref="PluginFactory.AvailableBuiltInNames"/> derives the
    ///   built-in name set from it. The parity assertion is the load-bearing one: the non-generic
    ///   accessor must reproduce the generic <c>TryGetAvailablePlugins&lt;T&gt;</c> exactly (same set
    ///   AND order), because the four consumers (compiler contract check, register-time collision
    ///   guard, /status union, subgraph list) rely on that.
    /// </summary>
    [TestClass]
    public class PluginContractDiscoveryTest
    {
        [TestMethod]
        public void ContractInterface_MapsEachContractToItsEngineInterface()
        {
            Assert.AreEqual(typeof(IShortestPathAlgorithm), PluginFactory.ContractInterface(PluginContract.Path));
            Assert.AreEqual(typeof(ISubGraphAlgorithm), PluginFactory.ContractInterface(PluginContract.SubGraph));
            Assert.AreEqual(typeof(IGraphAnalyticsAlgorithm), PluginFactory.ContractInterface(PluginContract.Analytics));
            Assert.AreEqual(typeof(IGraphFunction), PluginFactory.ContractInterface(PluginContract.GraphFunction));
            Assert.IsNull(PluginFactory.ContractInterface((PluginContract)999), "an unknown contract maps to null");
        }

        [TestMethod]
        public void AvailableBuiltInNames_MatchesTheGenericDiscovery_InSetAndOrder()
        {
            AssertParity<IShortestPathAlgorithm>(PluginContract.Path);
            AssertParity<ISubGraphAlgorithm>(PluginContract.SubGraph);
            AssertParity<IGraphAnalyticsAlgorithm>(PluginContract.Analytics);
        }

        [TestMethod]
        public void AvailableBuiltInNames_Analytics_ContainsEveryBuiltInAlgorithm()
        {
            var names = PluginFactory.AvailableBuiltInNames(PluginContract.Analytics).ToList();
            CollectionAssert.IsSubsetOf(
                new[] { "PAGERANK", "WCC", "LABELPROPAGATION", "DEGREE", "TRIANGLECOUNT" }, names,
                "every built-in analytics algorithm must be discoverable by contract");
        }

        [TestMethod]
        public void AvailableBuiltInNames_GraphFunction_IsEmpty()
        {
            // No built-in IGraphFunction exists, so the scan is empty - the reason CollidesWithBuiltIn
            // never rejects a graph-function name (its previous default:false behaviour is preserved).
            Assert.AreEqual(0, PluginFactory.AvailableBuiltInNames(PluginContract.GraphFunction).Count());
        }

        private static void AssertParity<T>(PluginContract contract)
        {
            PluginFactory.TryGetAvailablePlugins<T>(out var generic);
            CollectionAssert.AreEqual(
                (generic ?? Enumerable.Empty<String>()).ToList(),
                PluginFactory.AvailableBuiltInNames(contract).ToList(),
                contract + ": AvailableBuiltInNames must match TryGetAvailablePlugins<T> in set and order");
        }
    }
}
