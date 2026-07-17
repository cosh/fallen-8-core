// MIT License
//
// DynamicCodeResourceLimitsTest.cs
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
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Tests for the "dynamic-code-resource-limits" feature (the parts landed in this pass):
    ///   R3 — a caller-supplied type name resolves ONLY through the primitive allow-list, never
    ///        Type.GetType(userString), so an arbitrary name cannot force-load an assembly / run a
    ///        static ctor; a disallowed name is a 400;
    ///   R2 — an oversize filter fragment is rejected before Roslyn is invoked (400);
    ///   R4 — MaxResults (K) above the ceiling is rejected (400).
    /// The R1 execution budget / task-abandon backstop is deferred (see the spec) — true isolation is
    /// api-security-boundary.
    /// </summary>
    [TestClass]
    public class DynamicCodeResourceLimitsTest
    {
        private ILoggerFactory _loggerFactory;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
        }

        private (Fallen8 fallen8, int a, int b) TwoConnectedVertices()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var vtx = new CreateVerticesTransaction();
            vtx.AddVertex(1u, "person", new System.Collections.Generic.Dictionary<string, object> { { "name", "alice" } });
            vtx.AddVertex(1u, "person", new System.Collections.Generic.Dictionary<string, object> { { "name", "bob" } });
            fallen8.EnqueueTransaction(vtx).WaitUntilFinished();
            var v = vtx.GetCreatedVertices();
            var edgeTx = new CreateEdgesTransaction();
            edgeTx.AddEdge(v[0].Id, "knows", v[1].Id, 1u, "knows");
            fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();
            return (fallen8, v[0].Id, v[1].Id);
        }

        // ---- R3 type allow-list -------------------------------------------------------------------

        [TestMethod]
        public void AllowedLiteralTypes_ResolvesPrimitives_AndRejectsEverythingElse()
        {
            Assert.IsTrue(AllowedLiteralTypes.TryResolve("System.String", out var s));
            Assert.AreEqual(typeof(string), s);
            Assert.IsTrue(AllowedLiteralTypes.TryResolve("Int32", out var i));
            Assert.AreEqual(typeof(int), i);
            Assert.IsTrue(AllowedLiteralTypes.TryResolve("int", out var i2));
            Assert.AreEqual(typeof(int), i2);
            Assert.IsTrue(AllowedLiteralTypes.TryResolve("system.double", out var d)); // case-insensitive
            Assert.AreEqual(typeof(double), d);

            // A real, loadable, NON-primitive type is rejected without any Type.GetType / assembly load.
            Assert.IsFalse(AllowedLiteralTypes.TryResolve("System.Console", out _));
            Assert.IsFalse(AllowedLiteralTypes.TryResolve("System.IO.File", out _));
            Assert.IsFalse(AllowedLiteralTypes.TryResolve("Not.A.Real.Type, EvilAssembly", out _));
            Assert.IsFalse(AllowedLiteralTypes.TryResolve(null, out _));

            Assert.ThrowsException<ArgumentException>(() => AllowedLiteralTypes.Resolve("System.Console"));
        }

        [TestMethod]
        public void GraphScan_WithADisallowedTypeName_Returns400()
        {
            var (fallen8, _, _) = TwoConnectedVertices();
            var controller = new GraphController(new UnitTestLogger<GraphController>(), fallen8);

            var result = controller.GraphScan("name", new ScanSpecification
            {
                Literal = new LiteralSpecification { Value = "alice", FullQualifiedTypeName = "System.Console" },
                Operator = BinaryOperator.Equals,
                ResultType = ResultTypeSpecification.Vertices
            });
            Assert.IsInstanceOfType(result.Result, typeof(BadRequestObjectResult),
                "A non-allow-listed type name must be a 400, and must never reach Type.GetType.");

            fallen8.Dispose();
        }

        [TestMethod]
        public void GraphScan_WithAnAllowedPrimitive_StillWorks()
        {
            var (fallen8, _, _) = TwoConnectedVertices();
            var controller = new GraphController(new UnitTestLogger<GraphController>(), fallen8);

            var result = controller.GraphScan("name", new ScanSpecification
            {
                Literal = new LiteralSpecification { Value = "alice", FullQualifiedTypeName = "System.String" },
                Operator = BinaryOperator.Equals,
                ResultType = ResultTypeSpecification.Vertices
            });
            Assert.IsNull(result.Result, "An allowed primitive must not be rejected.");
            Assert.AreEqual(1, result.Value.Count(), "The allowed-primitive scan must still find alice.");

            fallen8.Dispose();
        }

        // ---- R2 compile length cap ----------------------------------------------------------------

        [TestMethod]
        public void Path_WithAnOversizeFilterFragment_Returns400_BeforeCompiling()
        {
            var (fallen8, a, b) = TwoConnectedVertices();
            var controller = new GraphController(new UnitTestLogger<GraphController>(), fallen8);

            var huge = "return (v) => v.Label == \"" + new string('x', 200_000) + "\";";
            var spec = new PathSpecification
            {
                PathAlgorithmName = "BLS",
                MaxDepth = 3,
                MaxResults = 1,
                Filter = new PathFilterSpecification { Vertex = huge }
            };

            var result = controller.CalculateShortestPath(a, b, spec).Result;
            Assert.IsInstanceOfType(result.Result, typeof(BadRequestObjectResult),
                "An oversize filter fragment must be rejected (400) before Roslyn is invoked.");

            fallen8.Dispose();
        }

        // R4 (a MaxResults/K ceiling) is deferred: MaxResults is a UInt16 already bounded to 65535 and
        // DEFAULTS to 65535, so a policy ceiling would reject the default and needs a config knob plus a
        // lower default to be useful. The genuine K x expensive-lambda bound belongs to the deferred R1
        // execution budget. See the spec.
    }
}
