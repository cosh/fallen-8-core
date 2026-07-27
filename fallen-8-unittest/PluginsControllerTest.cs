// MIT License
//
// PluginsControllerTest.cs
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

using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Integration tests for the plugin registration REST surface + the engine resolution seam
    ///   (feature plugin-registration, Phase 3), against a real in-memory Fallen8 and the
    ///   PluginsController: register/list/get/delete, transparent algorithm resolution through the
    ///   engine, graph-function invocation, built-in-name collision, duplicate/quota, compile
    ///   failure, and per-namespace isolation.
    /// </summary>
    [TestClass]
    public class PluginsControllerTest
    {
        private Fallen8 _fallen8;
        private PluginsController _controller;

        [TestInitialize]
        public void TestInitialize()
        {
            var loggerFactory = TestLoggerFactory.Create();
            _fallen8 = new Fallen8(loggerFactory);
            _controller = new PluginsController(loggerFactory.CreateLogger<PluginsController>(), _fallen8);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            _fallen8.Dispose();
        }

        #region source + helpers

        private const string FunctionSource = @"
using System;
using System.Collections.Generic;
using System.Linq;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Plugins;

public sealed class NeighboursOfLabel : IGraphFunction
{
    private IFallen8 _graph;
    public string PluginName => ""NeighboursOfLabel"";
    public Type PluginCategory => typeof(IGraphFunction);
    public string Description => ""vertices of a label"";
    public string Manufacturer => ""test"";
    public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { _graph = fallen8; }
    public void Dispose() { }
    public bool TryInvoke(out GraphFunctionResult result, IDictionary<string, object> parameters)
    {
        var label = parameters != null && parameters.TryGetValue(""label"", out var l) ? l as string : null;
        var verts = _graph.GetAllVertices(label);
        result = GraphFunctionResult.FromElements(verts, null);
        return true;
    }
}";

        private const string PathAlgorithmSource = @"
using System;
using System.Collections.Generic;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Plugin;

public sealed class MyPath : IShortestPathAlgorithm
{
    public string PluginName => ""MyPath"";
    public Type PluginCategory => typeof(IShortestPathAlgorithm);
    public string Description => ""x"";
    public string Manufacturer => ""x"";
    public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { }
    public void Dispose() { }
    public bool TryCalculateShortestPath(out List<Path> result, ShortestPathDefinition definition)
    { result = new List<Path>(); return true; }
}";

        private static AlgorithmPluginRegistration Algo(string name, string contract, string source)
            => new AlgorithmPluginRegistration { Name = name, Contract = contract, SourceCode = source };

        private static FunctionPluginRegistration Func(string name, string source)
            => new FunctionPluginRegistration { Name = name, SourceCode = source };

        private static int StatusCodeOf(IActionResult result)
        {
            switch (result)
            {
                case ObjectResult o when o.StatusCode.HasValue: return o.StatusCode.Value;
                case StatusCodeResult s: return s.StatusCode;
                default: Assert.Fail($"Unexpected result type {result.GetType().Name}."); return 0;
            }
        }

        private static int StatusCodeOf(Task<IActionResult> result) => StatusCodeOf(result.Result);

        private void AddVertex(string label)
        {
            var info = _fallen8.EnqueueTransaction(new CreateVerticesTransaction
            {
                Vertices = new List<VertexDefinition> { new VertexDefinition { CreationDate = 1, Label = label, Properties = null } }
            });
            info.WaitUntilFinished();
        }

        #endregion

        [TestMethod]
        public void RegisterFunction_Invoke_List_Get_Delete_Lifecycle()
        {
            Assert.AreEqual(201, StatusCodeOf(_controller.RegisterFunction(Func("NeighboursOfLabel", FunctionSource))));

            // Listed and retrievable with its source.
            var list = (_controller.GetAllPlugins() as OkObjectResult).Value as List<PluginSummaryREST>;
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("NeighboursOfLabel", list[0].Name);
            Assert.AreEqual("Function", list[0].Category);

            var detail = (_controller.GetPlugin("NeighboursOfLabel") as OkObjectResult).Value as PluginDetailREST;
            StringAssert.Contains(detail.SourceCode, "IGraphFunction");

            // Invoke: one "person" vertex exists, so the function returns exactly it.
            AddVertex("person");
            AddVertex("company");
            var invocation = new GraphFunctionInvocation { Parameters = new Dictionary<string, string> { { "label", "person" } } };
            var invokeResult = _controller.InvokeFunction("NeighboursOfLabel", invocation);
            Assert.AreEqual(200, StatusCodeOf(invokeResult));
            var payload = (invokeResult as OkObjectResult).Value as GraphFunctionResultREST;
            Assert.AreEqual(1, payload.Vertices.Count);
            Assert.AreEqual("person", payload.Vertices[0].Label);

            // Delete, then gone.
            Assert.AreEqual(204, StatusCodeOf(_controller.DeletePlugin("NeighboursOfLabel")));
            Assert.AreEqual(404, StatusCodeOf(_controller.GetPlugin("NeighboursOfLabel")));
        }

        [TestMethod]
        public void RegisterAlgorithm_Path_ResolvesTransparentlyThroughTheEngine()
        {
            Assert.AreEqual(201, StatusCodeOf(_controller.RegisterAlgorithm(Algo("MyPath", "Path", PathAlgorithmSource))));

            var def = new ShortestPathDefinition { SourceVertexId = 0, DestinationVertexId = 0 };

            // The engine resolves the registered algorithm by name (no built-in is named "MyPath").
            Assert.IsTrue(_fallen8.TryCalculateShortestPath(out _, "MyPath", def),
                "the registered algorithm should resolve transparently through the engine");

            // A truly unknown name still resolves to nothing.
            Assert.IsFalse(_fallen8.TryCalculateShortestPath(out _, "NoSuchAlgorithm", def));
        }

        [TestMethod]
        public void RegisterFunction_DuplicateName_Returns409()
        {
            Assert.AreEqual(201, StatusCodeOf(_controller.RegisterFunction(Func("NeighboursOfLabel", FunctionSource))));
            Assert.AreEqual(409, StatusCodeOf(_controller.RegisterFunction(Func("NeighboursOfLabel", FunctionSource))));
        }

        [TestMethod]
        public void RegisterAlgorithm_BuiltInNameCollision_Returns409()
        {
            // "BLS" is a built-in path algorithm; a registered plugin must not shadow it.
            var result = _controller.RegisterAlgorithm(Algo("BLS", "Path", PathAlgorithmSource.Replace("MyPath", "BLS")));
            Assert.AreEqual(409, StatusCodeOf(result));
        }

        [TestMethod]
        public void RegisterFunction_BeyondQuota_Returns409()
        {
            _fallen8.Plugins.MaxCount = 1;
            Assert.AreEqual(201, StatusCodeOf(_controller.RegisterFunction(Func("NeighboursOfLabel", FunctionSource))));

            var second = FunctionSource.Replace("NeighboursOfLabel", "SecondFunc");
            Assert.AreEqual(409, StatusCodeOf(_controller.RegisterFunction(Func("SecondFunc", second))));
        }

        [TestMethod]
        public void RegisterFunction_CompileFailure_Returns400()
        {
            var result = _controller.RegisterFunction(Func("Broken", "public class Broken { not valid c# }"));
            Assert.AreEqual(400, StatusCodeOf(result));
        }

        [TestMethod]
        public void RegisterAlgorithm_InvalidContract_Returns400()
        {
            var result = _controller.RegisterAlgorithm(Algo("MyPath", "NotAContract", PathAlgorithmSource));
            Assert.AreEqual(400, StatusCodeOf(result));
        }

        [TestMethod]
        public void RegisterFunction_PluginNameMismatch_Returns400()
        {
            // Source declares PluginName "NeighboursOfLabel" but we register under a different name.
            var result = _controller.RegisterFunction(Func("different", FunctionSource));
            Assert.AreEqual(400, StatusCodeOf(result));
        }

        [TestMethod]
        public void InvokeFunction_Unknown_Returns404()
        {
            var result = _controller.InvokeFunction("nope", new GraphFunctionInvocation());
            Assert.AreEqual(404, StatusCodeOf(result));
        }

        [TestMethod]
        public void InvokeFunction_ReturningFalse_Returns400()
        {
            var falseSource = @"
using System;
using System.Collections.Generic;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Plugins;

public sealed class AlwaysFalse : IGraphFunction
{
    public string PluginName => ""AlwaysFalse"";
    public Type PluginCategory => typeof(IGraphFunction);
    public string Description => ""x"";
    public string Manufacturer => ""x"";
    public void Initialize(IFallen8 f, IDictionary<string, object> p) { }
    public void Dispose() { }
    public bool TryInvoke(out GraphFunctionResult result, IDictionary<string, object> parameters)
    { result = null; return false; }
}";
            Assert.AreEqual(201, StatusCodeOf(_controller.RegisterFunction(Func("AlwaysFalse", falseSource))));
            Assert.AreEqual(400, StatusCodeOf(_controller.InvokeFunction("AlwaysFalse", new GraphFunctionInvocation())));
        }

        [TestMethod]
        public void Validate_ReportsValidityWithoutRegistering()
        {
            var ok = (_controller.ValidateFunction(new PluginValidationSpecification
            {
                Name = "NeighboursOfLabel",
                SourceCode = FunctionSource
            }) as OkObjectResult).Value as PluginValidationREST;
            Assert.IsTrue(ok.Valid);
            Assert.IsNull(ok.Error);

            var bad = (_controller.ValidateFunction(new PluginValidationSpecification
            {
                Name = "Broken",
                SourceCode = "public class Broken { nope }"
            }) as OkObjectResult).Value as PluginValidationREST;
            Assert.IsFalse(bad.Valid);
            Assert.IsNotNull(bad.Error);

            // Validation registers nothing.
            Assert.AreEqual(0, _fallen8.Plugins.Count);
        }

        [TestMethod]
        public void Registration_IsPerNamespace_NotVisibleInAnotherEngine()
        {
            Assert.AreEqual(201, StatusCodeOf(_controller.RegisterFunction(Func("NeighboursOfLabel", FunctionSource))));

            using var other = new Fallen8(TestLoggerFactory.Create());
            var otherController = new PluginsController(TestLoggerFactory.Create().CreateLogger<PluginsController>(), other);

            var otherList = (otherController.GetAllPlugins() as OkObjectResult).Value as List<PluginSummaryREST>;
            Assert.AreEqual(0, otherList.Count, "a plugin registered in one namespace must not be visible in another");
            Assert.AreEqual(404, StatusCodeOf(otherController.InvokeFunction("NeighboursOfLabel", new GraphFunctionInvocation())));
        }
    }
}
