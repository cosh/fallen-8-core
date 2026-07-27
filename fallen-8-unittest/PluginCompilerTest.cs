// MIT License
//
// PluginCompilerTest.cs
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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core.Plugins;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Tests for the full-type plugin compile bridge (feature plugin-registration, Phase 2):
    ///   whole-source compilation, and the contract validation (exactly one implementing public type,
    ///   parameterless ctor, activatable, matching PluginName). No engine required - this is the pure
    ///   compile+validate step.
    /// </summary>
    [TestClass]
    public class PluginCompilerTest
    {
        private readonly PluginCompiler _compiler = new PluginCompiler();

        private static PluginDefinition Def(string name, string source,
            PluginContract contract = PluginContract.GraphFunction,
            PluginCategory category = PluginCategory.Function)
        {
            return new PluginDefinition
            {
                Name = name,
                Category = category,
                Contract = contract,
                SourceCode = source,
                CreatedAt = DateTime.UtcNow
            };
        }

        private const string ValidFunctionSource = @"
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
    public string Description => ""all vertices of a label"";
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

        [TestMethod]
        public void ValidGraphFunction_Compiles_And_ImplementsContract()
        {
            var ok = _compiler.TryCompile(Def("NeighboursOfLabel", ValidFunctionSource), out var type, out var error);

            Assert.IsTrue(ok, "expected success but got: " + error);
            Assert.IsNull(error);
            Assert.IsNotNull(type);
            Assert.IsTrue(typeof(IGraphFunction).IsAssignableFrom(type));
        }

        [TestMethod]
        public void SyntaxError_Fails_WithDiagnostics()
        {
            var ok = _compiler.TryCompile(Def("Broken", "public class Broken { this is not c# }"), out var type, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(type);
            StringAssert.Contains(error, "Failed to compile");
        }

        [TestMethod]
        public void NoImplementor_Fails()
        {
            var src = "public sealed class NotAPlugin { public int X() => 1; }";
            var ok = _compiler.TryCompile(Def("NotAPlugin", src), out var type, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(type);
            StringAssert.Contains(error, "exactly one public class implementing");
        }

        [TestMethod]
        public void MultipleImplementors_Fails()
        {
            var src = ValidFunctionSource + @"
public sealed class SecondFunc : NoSQL.GraphDB.Core.Plugins.IGraphFunction
{
    public string PluginName => ""SecondFunc"";
    public Type PluginCategory => typeof(NoSQL.GraphDB.Core.Plugins.IGraphFunction);
    public string Description => ""x"";
    public string Manufacturer => ""x"";
    public void Initialize(NoSQL.GraphDB.Core.IFallen8 f, System.Collections.Generic.IDictionary<string, object> p) { }
    public void Dispose() { }
    public bool TryInvoke(out NoSQL.GraphDB.Core.Plugins.GraphFunctionResult r, System.Collections.Generic.IDictionary<string, object> p)
    { r = NoSQL.GraphDB.Core.Plugins.GraphFunctionResult.FromElements(null, null); return true; }
}";
            var ok = _compiler.TryCompile(Def("NeighboursOfLabel", src), out var type, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(type);
            StringAssert.Contains(error, "it contains 2");
        }

        [TestMethod]
        public void PluginNameMismatch_Fails()
        {
            // Source declares PluginName "NeighboursOfLabel" but we register under a different name.
            var ok = _compiler.TryCompile(Def("different-name", ValidFunctionSource), out var type, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(type);
            StringAssert.Contains(error, "must equal the registration name");
        }

        [TestMethod]
        public void NoParameterlessConstructor_Fails()
        {
            var src = @"
using System;
using System.Collections.Generic;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Plugins;

public sealed class NeedsArg : IGraphFunction
{
    public NeedsArg(int x) { }
    public string PluginName => ""NeedsArg"";
    public Type PluginCategory => typeof(IGraphFunction);
    public string Description => ""x"";
    public string Manufacturer => ""x"";
    public void Initialize(IFallen8 f, IDictionary<string, object> p) { }
    public void Dispose() { }
    public bool TryInvoke(out GraphFunctionResult r, IDictionary<string, object> p)
    { r = GraphFunctionResult.FromElements(null, null); return true; }
}";
            var ok = _compiler.TryCompile(Def("NeedsArg", src), out var type, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(type);
            StringAssert.Contains(error, "public parameterless constructor");
        }

        [TestMethod]
        public void OversizeSource_RejectedBeforeCompile()
        {
            var huge = new string('/', PluginCompiler.MaxPluginSourceLength + 1);
            var ok = _compiler.TryCompile(Def("Big", huge), out var type, out var error);

            Assert.IsFalse(ok);
            Assert.IsNull(type);
            StringAssert.Contains(error, "exceeds the maximum");
        }

        [TestMethod]
        public void ValidPathAlgorithm_Compiles()
        {
            // A minimal IShortestPathAlgorithm proves the algorithm-category contract path compiles.
            var src = @"
using System;
using System.Collections.Generic;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Plugin;

public sealed class NoopPath : IShortestPathAlgorithm
{
    public string PluginName => ""NoopPath"";
    public Type PluginCategory => typeof(IShortestPathAlgorithm);
    public string Description => ""x"";
    public string Manufacturer => ""x"";
    public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { }
    public void Dispose() { }
    public bool TryCalculateShortestPath(out List<Path> result, ShortestPathDefinition definition)
    { result = new List<Path>(); return true; }
}";
            var ok = _compiler.TryCompile(
                Def("NoopPath", src, PluginContract.Path, PluginCategory.Algorithm), out var type, out var error);

            Assert.IsTrue(ok, "expected success but got: " + error);
            Assert.IsNotNull(type);
            Assert.IsTrue(typeof(NoSQL.GraphDB.Core.Algorithms.Path.IShortestPathAlgorithm).IsAssignableFrom(type));
        }
    }
}
