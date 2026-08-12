// MIT License
//
// HostPluginApiSurfaceTest.cs
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
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Analytics;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Plugins;
using NoSQL.GraphDB.Core.Serializer;
using NoSQL.GraphDB.Core.Service;
using NoSQL.GraphDB.Core.Transaction;

using PathAlgorithms = NoSQL.GraphDB.Core.Algorithms.Path;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The apiApp sweep of host plugin registration (feature host-plugin-registration, "what the
    ///   tests must pin" item 8): the REST layer of an embedded-engine host must tell the truth about
    ///   a plugin the HOST registered as a type - it is listed where a name is listed, and it is
    ///   projected by the plugin DTOs even though it carries no source.
    ///
    ///   <para>Every plugin type here is <c>internal</c> ON PURPOSE, exactly as in
    ///   <c>HostPluginRegistrationTest</c>: <c>PluginFactory</c> discovery only ever yields PUBLIC
    ///   exported types, so a name that shows up in an inventory could not have come from a scan of
    ///   this assembly.</para>
    /// </summary>
    [TestClass]
    public class HostPluginApiSurfaceTest
    {
        private ILoggerFactory _loggerFactory;
        private Fallen8 _fallen8;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
            _fallen8 = new Fallen8(_loggerFactory);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            _fallen8.Dispose();
        }

        #region helpers

        private AdminController Admin()
        {
            return new AdminController(_loggerFactory.CreateLogger<AdminController>(), _fallen8, null, null);
        }

        private PluginsController Plugins()
        {
            return new PluginsController(_loggerFactory.CreateLogger<PluginsController>(), _fallen8);
        }

        private void Register<
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>()
            where T : class, IPlugin, new()
        {
            var info = _fallen8.RegisterPluginType<T>();
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState,
                "registering " + typeof(T).Name + " must succeed");
        }

        private static T OkValue<T>(IActionResult result) where T : class
        {
            var ok = result as OkObjectResult;
            Assert.IsNotNull(ok, "the GET surface must answer 200 OK");
            var value = ok.Value as T;
            Assert.IsNotNull(value, "the 200 body must be a " + typeof(T).Name);
            return value;
        }

        /// <summary>The apiApp's generated XML documentation, which is what the OpenAPI document's
        /// schema descriptions are built from - so asserting against it asserts against the shipped
        /// contract description without needing a running server.</summary>
        private static String DocumentedSummary(String memberName)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "fallen-8-core-apiApp.xml");
            Assert.IsTrue(File.Exists(path),
                "the apiApp XML documentation must sit next to the test assembly; it is the source of the OpenAPI descriptions");

            var summary = XDocument.Load(path).Descendants("member")
                .Where(m => (String)m.Attribute("name") == memberName)
                .Select(m => m.Element("summary"))
                .FirstOrDefault();

            Assert.IsNotNull(summary, "no XML documentation for " + memberName);
            return summary.Value;
        }

        /// <summary>Just the parenthesised list of possible values out of a documented summary - not the
        /// whole text, whose surrounding prose may well NAME a value it forgot to list.</summary>
        private static String DocumentedValueList(String memberName)
        {
            var summary = DocumentedSummary(memberName);
            var open = summary.IndexOf('(');
            var close = open < 0 ? -1 : summary.IndexOf(')', open + 1);

            Assert.IsTrue(close > open && open >= 0,
                "the description must carry a parenthesised list of the possible values; it reads: " + summary);

            return summary.Substring(open + 1, close - open - 1);
        }

        #endregion

        /// <summary>
        ///   Spec item 8: the plugin list DTOs must tolerate a null source. A host-registered type has
        ///   none and is nonetheless visible through the GET surfaces, so a projection that assumed
        ///   source - by dereferencing it, or by substituting an empty string that reads as "an empty
        ///   plugin" - would misreport it.
        /// </summary>
        [TestMethod]
        public void PluginGetSurfaces_ProjectAHostRegisteredTypeThatCarriesNoSource()
        {
            Register<SweepHostIndex>();
            Register<SweepHostService>();

            var listed = OkValue<List<PluginSummaryREST>>(Plugins().GetAllPlugins());

            var index = listed.SingleOrDefault(s => s.Name == SweepHostIndex.RegisteredName);
            Assert.IsNotNull(index, "GET /plugins must list a host-registered index type");
            Assert.AreEqual("Index", index.Category, "the category is projected from PluginCategory.Index");
            Assert.AreEqual("Index", index.Contract, "the contract is projected from PluginContract.Index");
            Assert.AreEqual("Compiled", index.CompileState,
                "a host entry has a real artifact type, so it is Compiled - sourceless is not the same as uncompiled");

            var service = listed.SingleOrDefault(s => s.Name == SweepHostService.RegisteredName);
            Assert.IsNotNull(service, "GET /plugins must list a host-registered service type");
            Assert.AreEqual("Service", service.Category);
            Assert.AreEqual("Service", service.Contract);

            var detail = OkValue<PluginDetailREST>(Plugins().GetPlugin(SweepHostIndex.RegisteredName));
            Assert.AreEqual(SweepHostIndex.RegisteredName, detail.Name);
            Assert.IsNull(detail.SourceCode,
                "a host-registered type has no source: the detail DTO must report null, never an invented empty string");
            Assert.IsNull(detail.CompileDiagnostics, "nothing failed to compile, so there are no diagnostics");

            // The wire shape, not just the object: a null source must serialize and deserialize.
            var json = JsonSerializer.Serialize(detail);
            var roundTripped = JsonSerializer.Deserialize<PluginDetailREST>(json);
            Assert.AreEqual(SweepHostIndex.RegisteredName, roundTripped.Name);
            Assert.IsNull(roundTripped.SourceCode, "the null source must survive the JSON round trip");
        }

        /// <summary>
        ///   /status is the inventory a client reads to learn what this namespace can create and run by
        ///   NAME. A host-registered index or service resolves by name through the registry, so it must
        ///   appear there too - and the discovered built-ins must keep appearing, because the two sets
        ///   are UNIONED, never swapped.
        /// </summary>
        [TestMethod]
        public async Task Status_ListsHostRegisteredNames_UnionedWithTheDiscoveredBuiltIns()
        {
            var builtInIndices = PluginFactory.AvailableBuiltInNames(PluginContract.Index).ToList();
            var builtInServices = PluginFactory.AvailableBuiltInNames(PluginContract.Service).ToList();
            var builtInPaths = PluginFactory.AvailableBuiltInNames(PluginContract.Path).ToList();
            var builtInAnalytics = PluginFactory.AvailableBuiltInNames(PluginContract.Analytics).ToList();

            // Without this, "the built-ins are still listed" would be a vacuous claim about empty sets.
            Assert.IsTrue(builtInIndices.Count > 0, "this host discovers built-in index plugins");
            Assert.IsTrue(builtInPaths.Count > 0, "this host discovers built-in path plugins");
            Assert.IsTrue(builtInAnalytics.Count > 0, "this host discovers built-in analytics plugins");

            Register<SweepHostIndex>();
            Register<SweepHostService>();
            Register<SweepHostPathAlgorithm>();
            Register<SweepHostAnalyticsAlgorithm>();

            var status = await Admin().Status();

            CollectionAssert.IsSubsetOf(builtInIndices, status.AvailableIndexPlugins,
                "the discovered index built-ins must still be listed");
            CollectionAssert.IsSubsetOf(builtInServices, status.AvailableServicePlugins,
                "the discovered service built-ins must still be listed");
            CollectionAssert.IsSubsetOf(builtInPaths, status.AvailablePathPlugins,
                "the discovered path built-ins must still be listed");
            CollectionAssert.IsSubsetOf(builtInAnalytics, status.AvailableAnalyticsPlugins,
                "the discovered analytics built-ins must still be listed");

            Assert.IsTrue(status.AvailableIndexPlugins.Contains(SweepHostIndex.RegisteredName),
                "a host-registered index type is creatable by name, so /status must list it");
            Assert.IsTrue(status.AvailableServicePlugins.Contains(SweepHostService.RegisteredName),
                "a host-registered service type is addable by name, so /status must list it");
            Assert.IsTrue(status.AvailablePathPlugins.Contains(SweepHostPathAlgorithm.RegisteredName),
                "a host-registered path algorithm is invocable by name, so /status must list it");
            Assert.IsTrue(status.AvailableAnalyticsPlugins.Contains(SweepHostAnalyticsAlgorithm.RegisteredName),
                "a host-registered analytics algorithm is invocable by name, so /status must list it");

            // One name, once: the lists are de-duplicated unions, not concatenations.
            CollectionAssert.AllItemsAreUnique(status.AvailableIndexPlugins);
            CollectionAssert.AllItemsAreUnique(status.AvailableServicePlugins);
            CollectionAssert.AllItemsAreUnique(status.AvailablePathPlugins);
            CollectionAssert.AllItemsAreUnique(status.AvailableAnalyticsPlugins);
        }

        /// <summary>
        ///   A registered name that only the registry knows must also be listed in the namespace that
        ///   registered it and NOWHERE else: the registry is per namespace, and /status answers for the
        ///   addressed one.
        /// </summary>
        [TestMethod]
        public async Task Status_OfAnotherNamespace_DoesNotListThisOnesHostRegisteredNames()
        {
            Register<SweepHostIndex>();

            using var other = new Fallen8(_loggerFactory);
            var otherStatus = await new AdminController(
                _loggerFactory.CreateLogger<AdminController>(), other, null, null).Status();

            Assert.IsFalse(otherStatus.AvailableIndexPlugins.Contains(SweepHostIndex.RegisteredName),
                "a host registration belongs to one namespace's registry only");
        }

        /// <summary>
        ///   The plugin DTO's category/contract documentation is the OpenAPI description a client reads
        ///   to know the possible values, so an enum member that is not named there is an incomplete
        ///   published contract. This fails when a maintainer adds a category or contract without
        ///   documenting it - which is exactly what happened when Index and Service were added.
        /// </summary>
        [TestMethod]
        public void PluginSummaryDto_DocumentsEveryCategoryAndContract()
        {
            var category = DocumentedValueList("P:NoSQL.GraphDB.App.Controllers.Model.PluginSummaryREST.Category");
            foreach (var name in Enum.GetNames<PluginCategory>())
            {
                Assert.IsTrue(category.Contains("\"" + name + "\"", StringComparison.Ordinal),
                    "the documented category values must list PluginCategory." + name + "; they read: " + category);
            }

            var contract = DocumentedValueList("P:NoSQL.GraphDB.App.Controllers.Model.PluginSummaryREST.Contract");
            foreach (var name in Enum.GetNames<PluginContract>())
            {
                Assert.IsTrue(contract.Contains("\"" + name + "\"", StringComparison.Ordinal),
                    "the documented contract values must list PluginContract." + name + "; they read: " + contract);
            }
        }
    }

    #region plugin types under test (internal, so no scan can ever find them)

    /// <summary>A host-registrable index; all behaviour comes from <see cref="ABucketIndex"/>.</summary>
    internal sealed class SweepHostIndex : ABucketIndex
    {
        internal const String RegisteredName = "Sweep-Host-Index";

        public override String PluginName => RegisteredName;

        public override String Description => "a host-registered index, seen by the REST inventory";
    }

    /// <summary>A host-registrable service; inert apart from its running flag.</summary>
    internal sealed class SweepHostService : IService
    {
        internal const String RegisteredName = "Sweep-Host-Service";

        public String PluginName => RegisteredName;
        public Type PluginCategory => typeof(IService);
        public String Description => "a host-registered service, seen by the REST inventory";
        public String Manufacturer => "test";
        public DateTime StartTime => DateTime.MinValue;

        public Boolean IsRunning
        {
            get; private set;
        }

        public IDictionary<String, String> Metadata => new Dictionary<String, String>();

        public void Initialize(IFallen8 fallen8, IDictionary<String, Object> parameter) { }
        public void Save(SerializationWriter writer) { }
        public void Load(SerializationReader reader, IFallen8 fallen8) { }
        public void OnServiceRestart() { }
        public void Dispose() { }

        public Boolean TryStart()
        {
            IsRunning = true;
            return true;
        }

        public Boolean TryStop()
        {
            IsRunning = false;
            return true;
        }
    }

    /// <summary>A host-registrable path algorithm; it never has to run here, only be listed.</summary>
    internal sealed class SweepHostPathAlgorithm : PathAlgorithms.IShortestPathAlgorithm
    {
        internal const String RegisteredName = "Sweep-Host-Path";

        public String PluginName => RegisteredName;
        public Type PluginCategory => typeof(PathAlgorithms.IShortestPathAlgorithm);
        public String Description => "a host-registered path algorithm, seen by the REST inventory";
        public String Manufacturer => "test";
        public void Initialize(IFallen8 fallen8, IDictionary<String, Object> parameter) { }
        public void Dispose() { }

        public Boolean TryCalculateShortestPath(out List<PathAlgorithms.Path> result,
            PathAlgorithms.ShortestPathDefinition definition)
        {
            result = new List<PathAlgorithms.Path>();
            return true;
        }
    }

    /// <summary>A host-registrable analytics algorithm; it never has to run here, only be listed.</summary>
    internal sealed class SweepHostAnalyticsAlgorithm : IGraphAnalyticsAlgorithm
    {
        internal const String RegisteredName = "Sweep-Host-Analytics";

        public String PluginName => RegisteredName;
        public Type PluginCategory => typeof(IGraphAnalyticsAlgorithm);
        public String Description => "a host-registered analytics algorithm, seen by the REST inventory";
        public String Manufacturer => "test";
        public void Initialize(IFallen8 fallen8, IDictionary<String, Object> parameter) { }
        public void Dispose() { }

        public Boolean TryRunAnalytics(out GraphAnalyticsResult result, GraphAnalyticsDefinition definition)
        {
            result = new GraphAnalyticsResult(new Dictionary<Int32, Double>(), null, true, 0, TimeSpan.Zero, false);
            return true;
        }
    }

    #endregion
}
