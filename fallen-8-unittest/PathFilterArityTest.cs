// MIT License
//
// PathFilterArityTest.cs
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

using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.App.Helper;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Regression tests for the "path-filter-arity-fix" feature. The shipped default <c>/path</c> edge
    /// filters used to be TWO-argument lambdas (<c>(e,d)</c>/<c>(p,d)</c>) that cannot compile against
    /// the ONE-argument <c>Delegates.EdgeFilter</c>/<c>EdgePropertyFilter</c>, so any <c>/path</c>
    /// request that carried a filter block silently returned <c>200</c> with <c>[]</c>. These pin that
    /// the shipped defaults now compile end-to-end, that a real path is returned, and that a genuinely
    /// malformed fragment surfaces as <c>400</c> (not a silent empty).
    /// </summary>
    [TestClass]
    public class PathFilterArityTest
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
            vtx.AddVertex(1u, "person");
            vtx.AddVertex(1u, "person");
            fallen8.EnqueueTransaction(vtx).WaitUntilFinished();
            var v = vtx.GetCreatedVertices();

            var edgeTx = new CreateEdgesTransaction();
            edgeTx.AddEdge(v[0].Id, "knows", v[1].Id, 1u, "knows");
            fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();

            return (fallen8, v[0].Id, v[1].Id);
        }

        // ---- codegen: the shipped defaults compile and bind ---------------------------------------

        [TestMethod]
        public void DefaultPathFilters_CompileEndToEnd_AndBind()
        {
            var (fallen8, _, _) = TwoConnectedVertices();
            var vertex = fallen8.GetAllVertices().First();
            var edge = fallen8.GetAllEdges().First();

            // A PRESENT (default) filter block is exactly what used to fail to compile.
            var definition = new PathSpecification
            {
                PathAlgorithmName = "BLS",
                MaxDepth = 3,
                MaxResults = 1,
                Filter = new PathFilterSpecification()
            };

            var compilerMessage = CodeGenerationHelper.GeneratePathTraverser(out IPathTraverser traverser, definition);

            Assert.IsNull(compilerMessage, "The shipped default path filters must compile with no diagnostics. Got: " + compilerMessage);
            Assert.IsNotNull(traverser, "A successful compile must produce a traverser.");

            // Each produced delegate must bind (one argument) and, being the match-all defaults, return true.
            Assert.IsTrue(traverser.EdgePropertyFilter(TraversalContext.Empty)("knows"), "The default edge-property filter must match all.");
            Assert.IsTrue(traverser.VertexFilter(TraversalContext.Empty)(vertex), "The default vertex filter must match all.");
            Assert.IsTrue(traverser.EdgeFilter(TraversalContext.Empty)(edge), "The default edge filter must match all.");

            fallen8.Dispose();
        }

        [TestMethod]
        public void CustomOneArgEdgeFilter_Compiles_AndDiscriminates()
        {
            var (fallen8, _, _) = TwoConnectedVertices();
            var edge = fallen8.GetAllEdges().First(); // label "knows"

            var definition = new PathSpecification
            {
                PathAlgorithmName = "BLS",
                MaxDepth = 3,
                MaxResults = 1,
                Filter = new PathFilterSpecification
                {
                    Edge = "return (e) => e.Label == \"knows\";",
                    EdgeProperty = "return (p) => p == \"knows\";",
                    Vertex = "return (v) => true;"
                }
            };

            var compilerMessage = CodeGenerationHelper.GeneratePathTraverser(out IPathTraverser traverser, definition);
            Assert.IsNull(compilerMessage, "A custom one-arg filter must compile. Got: " + compilerMessage);
            Assert.IsNotNull(traverser);

            Assert.IsTrue(traverser.EdgeFilter(TraversalContext.Empty)(edge), "The 'knows' edge must pass the custom filter.");
            Assert.IsTrue(traverser.EdgePropertyFilter(TraversalContext.Empty)("knows"));
            Assert.IsFalse(traverser.EdgePropertyFilter(TraversalContext.Empty)("dislikes"), "A non-matching edge property must be excluded.");

            fallen8.Dispose();
        }

        [TestMethod]
        public void MalformedFilterFragment_YieldsACompilerMessage_AndNoTraverser()
        {
            var definition = new PathSpecification
            {
                PathAlgorithmName = "BLS",
                MaxDepth = 3,
                MaxResults = 1,
                Filter = new PathFilterSpecification { Edge = "this is not valid C#" }
            };

            var compilerMessage = CodeGenerationHelper.GeneratePathTraverser(out IPathTraverser traverser, definition);

            Assert.IsNotNull(compilerMessage, "A malformed fragment must produce a compiler message.");
            Assert.IsNull(traverser, "A failed compile must not produce a traverser.");
        }

        // ---- controller: default filters find the path; malformed -> 400 --------------------------

        [TestMethod]
        public void Controller_WithDefaultFilterBlock_ReturnsThePath_NotEmpty()
        {
            var (fallen8, a, b) = TwoConnectedVertices();
            var controller = new GraphController(new UnitTestLogger<GraphController>(), fallen8);

            // A present-but-default filter block: the exact shape that used to return 200-empty.
            var spec = new PathSpecification
            {
                PathAlgorithmName = "BLS",
                MaxDepth = 3,
                MaxResults = 1,
                Filter = new PathFilterSpecification()
            };

            var result = controller.CalculateShortestPath(a, b, spec);
            Assert.IsNotNull(result.Value, "A default filter block must not produce a BadRequest.");
            Assert.AreEqual(1, result.Value.Count, "A path exists, so the default-filter request must return it, not [].");

            fallen8.Dispose();
        }

        [TestMethod]
        public void Controller_WithMalformedFilter_Returns400_WithDiagnostics()
        {
            var (fallen8, a, b) = TwoConnectedVertices();
            var controller = new GraphController(new UnitTestLogger<GraphController>(), fallen8);

            var spec = new PathSpecification
            {
                PathAlgorithmName = "BLS",
                MaxDepth = 3,
                MaxResults = 1,
                Filter = new PathFilterSpecification { Edge = "this is not valid C#" }
            };

            var result = controller.CalculateShortestPath(a, b, spec);

            Assert.IsNull(result.Value, "A malformed filter must not return a 200 body.");
            var badRequest = result.Result as BadRequestObjectResult;
            Assert.IsNotNull(badRequest, "A malformed filter fragment must surface as 400, not a silent empty 200.");
            Assert.AreEqual(400, badRequest.StatusCode);
            Assert.IsNotNull(badRequest.Value, "The 400 body must carry the compiler diagnostics.");

            fallen8.Dispose();
        }
    }
}
