// MIT License
//
// CodegenCacheKeyingTest.cs
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

using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.App.Controllers.Cache;
using NoSQL.GraphDB.Core.App.Helper;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Tests for the "codegen-cache-keying" feature: the path-traverser cache keys on the compiled
    /// artifact's true dependency — the <c>(Filter, Cost)</c> pair — not the whole <see cref="PathSpecification"/>.
    /// Two <c>/path</c> requests that differ only in a numeric bound / algorithm name must reuse ONE
    /// compiled traverser (one Roslyn compile, one collectible context); two that differ in the filter
    /// or cost must still compile two.
    /// </summary>
    [TestClass]
    public class CodegenCacheKeyingTest
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

        private static PathSpecification Spec(string vertexFilterMarker, int maxDepth, int maxResults)
        {
            return new PathSpecification
            {
                PathAlgorithmName = "BLS",
                MaxDepth = (ushort)maxDepth,
                MaxResults = (ushort)maxResults,
                // A unique-but-match-all filter so this test's artifact is never shared with another
                // test's cache entry (the cache is process-wide/static).
                Filter = new PathFilterSpecification { Vertex = "return (v) => v.Label != \"" + vertexFilterMarker + "\";" }
            };
        }

        [TestMethod]
        public void RequestsDifferingOnlyInNumericBounds_CompileExactlyOnce_AndShareTheTraverser()
        {
            var (fallen8, a, b) = TwoConnectedVertices();
            var controller = new GraphController(new UnitTestLogger<GraphController>(), fallen8);
            var probe = new GeneratedCodeCache();

            const string marker = "codegen-cache-keying-bounds-A";
            var before = CodeGenerationHelper.PathCompileCount;

            // Same filter+cost, DIFFERENT numeric bounds (and MaxResults).
            var specDepth3 = Spec(marker, maxDepth: 3, maxResults: 1);
            var specDepth7 = Spec(marker, maxDepth: 7, maxResults: 9);
            Assert.AreNotEqual(specDepth3, specDepth7, "The two specs differ (their bounds), so a whole-spec key would miss.");

            _ = controller.CalculateShortestPath(a, b, specDepth3).Result;
            _ = controller.CalculateShortestPath(a, b, specDepth7).Result;
            Assert.AreEqual(1, CodeGenerationHelper.PathCompileCount - before,
                "Two bound-only-differing requests must compile the traverser exactly once.");

            // Both specs resolve to the SAME cached traverser instance.
            Assert.IsTrue(probe.TryGetTraverser(specDepth3, out var t3));
            Assert.IsTrue(probe.TryGetTraverser(specDepth7, out var t7));
            Assert.AreSame(t3, t7, "Bound-only-differing specs must share one compiled traverser.");

            fallen8.Dispose();
        }

        [TestMethod]
        public void RequestsDifferingInTheFilter_CompileTwoDistinctTraversers()
        {
            var (fallen8, a, b) = TwoConnectedVertices();
            var controller = new GraphController(new UnitTestLogger<GraphController>(), fallen8);
            var probe = new GeneratedCodeCache();

            var before = CodeGenerationHelper.PathCompileCount;

            var specX = Spec("codegen-cache-keying-filter-X", maxDepth: 3, maxResults: 1);
            var specY = Spec("codegen-cache-keying-filter-Y", maxDepth: 3, maxResults: 1);

            _ = controller.CalculateShortestPath(a, b, specX).Result;
            _ = controller.CalculateShortestPath(a, b, specY).Result;
            Assert.AreEqual(2, CodeGenerationHelper.PathCompileCount - before,
                "Requests with different filters must compile two distinct traversers (no false sharing).");

            Assert.IsTrue(probe.TryGetTraverser(specX, out var tx));
            Assert.IsTrue(probe.TryGetTraverser(specY, out var ty));
            Assert.AreNotSame(tx, ty, "Different filters must not share a traverser.");

            fallen8.Dispose();
        }
    }
}
