// MIT License
//
// PathFilterArityTest.cs
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
using System.Linq;
using Microsoft.AspNetCore.Http;
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
    /// <remarks>
    ///   This is also the home of the "nothing to compile" short-circuit (audit defect <b>B24</b>): a
    ///   <c>/path</c> request that supplies no filter and no cost fragment must compile NOTHING (no
    ///   Roslyn Emit, no collectible load context) and still behave exactly like the generated
    ///   all-<c>return null;</c> provider it replaces. The two subjects belong together because they are
    ///   the two halves of one question, what a filter block does to the compile path, and because the
    ///   boundary between them is easy to get wrong: a PRESENT default filter block carries real
    ///   fragments (<c>"return (v) =&gt; true;"</c>) and therefore DOES compile, while an ABSENT one, or
    ///   one whose every fragment is blank, must not.
    /// </remarks>
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

        // ---- B24: nothing to compile short-circuits Roslyn ----------------------------------------
        //      The mirror image of the group above: where a PRESENT filter block must reach the
        //      compiler and bind, an absent (or wholly blank) one must not reach it at all, while
        //      still behaving like the all-null provider it replaces.

        [TestMethod]
        public void B24_FilterlessSpecification_CompilesNothing_AndMatchesEverything()
        {
            // The default REST body (and the default MCP f8_paths call): no filter, no cost.
            var definition = new PathSpecification();
            Assert.IsNull(definition.Filter, "precondition: filter defaults to absent");
            Assert.IsNull(definition.Cost, "precondition: cost defaults to absent");

            var before = CodeGenerationHelper.PathCompileCount;

            var message = CodeGenerationHelper.GeneratePathTraverser(out IPathTraverser traverser, definition);

            Assert.IsNull(message, "A code-free specification is not an error. Got: " + message);
            Assert.IsNotNull(traverser,
                "A null traverser means 'compile failed' to the controller, so the short-circuit must still hand one back.");
            Assert.AreEqual(0, CodeGenerationHelper.PathCompileCount - before,
                "A request with nothing to compile must not reach Roslyn (no Emit, no collectible load context).");

            // Byte-for-byte the behaviour of the generated provider it replaces: every factory
            // returns null, which IPathTraverser defines as match-everything / default cost.
            Assert.IsNull(traverser.VertexFilter(TraversalContext.Empty), "no vertex filter was supplied");
            Assert.IsNull(traverser.EdgeFilter(TraversalContext.Empty), "no edge filter was supplied");
            Assert.IsNull(traverser.EdgePropertyFilter(TraversalContext.Empty), "no edge-property filter was supplied");
            Assert.IsNull(traverser.VertexCost(TraversalContext.Empty), "no vertex cost was supplied");
            Assert.IsNull(traverser.EdgeCost(TraversalContext.Empty), "no edge cost was supplied");
        }

        [TestMethod]
        public void B24_FilterlessSpecification_ReusesOneStatelessInstance()
        {
            var before = CodeGenerationHelper.PathCompileCount;

            Assert.IsNull(CodeGenerationHelper.GeneratePathTraverser(out IPathTraverser first, new PathSpecification()));
            Assert.IsNull(CodeGenerationHelper.GeneratePathTraverser(out IPathTraverser second, new PathSpecification()));

            Assert.AreSame(first, second,
                "The no-op traverser is stateless, so it must be a shared instance rather than a per-request allocation.");
            Assert.AreEqual(0, CodeGenerationHelper.PathCompileCount - before, "Still nothing to compile.");
        }

        [TestMethod]
        public void B24_PresentButAllBlankFragments_AlsoCompileNothing()
        {
            // Present blocks whose every fragment is blank: GenerateMethodSyntax would have emitted
            // the same all-null provider, so this is the same "nothing to compile" case.
            var definition = new PathSpecification
            {
                Filter = new PathFilterSpecification { Vertex = "   ", Edge = null, EdgeProperty = "" },
                Cost = new PathCostSpecification { Vertex = "\t", Edge = null }
            };

            var before = CodeGenerationHelper.PathCompileCount;

            var message = CodeGenerationHelper.GeneratePathTraverser(out IPathTraverser traverser, definition);

            Assert.IsNull(message, "Blank fragments have always meant match-everything, not an error. Got: " + message);
            Assert.IsNotNull(traverser);
            Assert.AreEqual(0, CodeGenerationHelper.PathCompileCount - before,
                "Whitespace-only fragments carry no code, so nothing may be compiled.");
            Assert.IsNull(traverser.VertexFilter(TraversalContext.Empty));
            Assert.IsNull(traverser.VertexCost(TraversalContext.Empty));
        }

        [TestMethod]
        public void B24_ASingleNonBlankFragment_StillCompiles_AndLeavesTheOtherSlotsNull()
        {
            // The negative case: ONE fragment is enough to keep the compile path, and the blank
            // siblings must still come back null (unchanged behaviour).
            var (fallen8, _, _) = TwoConnectedVertices();
            var vertex = fallen8.GetAllVertices().First();

            var definition = new PathSpecification
            {
                Filter = new PathFilterSpecification
                {
                    Vertex = "return (v) => v.Label == \"person\";",
                    Edge = null,
                    EdgeProperty = "   "
                }
            };

            var before = CodeGenerationHelper.PathCompileCount;

            var message = CodeGenerationHelper.GeneratePathTraverser(out IPathTraverser traverser, definition);

            Assert.IsNull(message, "A valid fragment must compile. Got: " + message);
            Assert.IsNotNull(traverser);
            Assert.AreEqual(1, CodeGenerationHelper.PathCompileCount - before,
                "A specification carrying real code must still be compiled exactly once.");

            var vertexFilter = traverser.VertexFilter(TraversalContext.Empty);
            Assert.IsNotNull(vertexFilter, "The supplied fragment must be bound.");
            Assert.IsTrue(vertexFilter(vertex), "The compiled filter must match a 'person' vertex.");
            Assert.IsNull(traverser.EdgeFilter(TraversalContext.Empty), "A blank sibling stays null (match everything).");
            Assert.IsNull(traverser.EdgePropertyFilter(TraversalContext.Empty), "A blank sibling stays null.");
            Assert.IsNull(traverser.VertexCost(TraversalContext.Empty), "An absent cost block stays null.");

            fallen8.Dispose();
        }

        // ---- controller: default filters find the path; malformed -> 400 --------------------------

        [TestMethod]
        public void Controller_WithDefaultFilterBlock_AndWithNoFilterAtAll_ReturnsThePath_NotEmpty()
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

            var result = controller.CalculateShortestPath(a, b, spec).Result;
            Assert.IsNotNull(result.Value, "A default filter block must not produce a BadRequest.");
            Assert.AreEqual(1, result.Value.Count, "A path exists, so the default-filter request must return it, not [].");

            // B24, merged in: the same end-to-end equivalence for a request that carries NO filter
            // block at all. Skipping the compile must not turn the match-everything traversal into a
            // no-match one. Both bodies are exercised here on purpose, because they are NOT the same
            // input: the default filter block above carries real fragments and does reach Roslyn,
            // whereas an absent block has nothing to compile and must not pay for one.
            var before = CodeGenerationHelper.PathCompileCount;

            var filterless = controller.CalculateShortestPath(a, b, new PathSpecification
            {
                PathAlgorithmName = "BLS",
                MaxDepth = 3,
                MaxResults = 1
            }).Result;

            Assert.IsNotNull(filterless.Value, "A code-free request must not produce an error result.");
            Assert.AreEqual(1, filterless.Value.Count,
                "A path exists, so a filterless request must return it - the short-circuit keeps match-everything semantics.");
            Assert.AreEqual(0, CodeGenerationHelper.PathCompileCount - before,
                "The default /path body must not pay a Roslyn compile.");

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

            var result = controller.CalculateShortestPath(a, b, spec).Result;

            Assert.IsNull(result.Value, "A malformed filter must not return a 200 body.");
            var problem = ProblemAssert.AssertProblem(result.Result, StatusCodes.Status400BadRequest);
            Assert.IsNotNull(problem.Detail, "The 400 body must carry the compiler diagnostics.");

            fallen8.Dispose();
        }

        // ---- api-error-contract E5: a fragment that COMPILES but THROWS at runtime is a 500, not a
        //      masked 200-empty "no path" (the /path broad-catch-to-200 defect). --------------------

        [TestMethod]
        public void Controller_WhenCompiledFragmentThrowsAtRuntime_Returns500_NotSilentEmpty200()
        {
            var (fallen8, a, b) = TwoConnectedVertices();
            var controller = new GraphController(new UnitTestLogger<GraphController>(), fallen8);

            // This vertex filter COMPILES (returns a bool) but throws a NullReferenceException the
            // moment it is invoked during traversal. Before the fix, the broad catch swallowed it and
            // returned 200 with [], indistinguishable from a genuine no-path result.
            var spec = new PathSpecification
            {
                PathAlgorithmName = "BLS",
                MaxDepth = 3,
                MaxResults = 1,
                Filter = new PathFilterSpecification { Vertex = "return (v) => ((string)null).Length == 0;" }
            };

            var result = controller.CalculateShortestPath(a, b, spec).Result;

            Assert.IsNull(result.Value, "A runtime fault must not be reported as a 200 body.");
            var objectResult = result.Result as ObjectResult;
            Assert.IsNotNull(objectResult, "A runtime fault in a compiled fragment must surface as a status result, not a silent empty 200.");
            Assert.AreEqual(500, objectResult.StatusCode, "The masked empty-200 is now a real 500 (mirroring /subgraph).");

            fallen8.Dispose();
        }
    }
}
