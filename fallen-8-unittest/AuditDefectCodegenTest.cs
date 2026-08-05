// MIT License
//
// AuditDefectCodegenTest.cs
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
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using NoSQL.GraphDB.Core.App.Helper;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Audit-defect regressions on the dynamic-code generators (<c>CodeGenerationHelper</c>):
    ///   <list type="bullet">
    ///     <item><description><b>B24</b> - a <c>/path</c> request that supplies no filter and no cost
    ///     fragment must compile NOTHING (no Roslyn Emit, no collectible load context) and still
    ///     behave exactly like the generated all-<c>return null;</c> provider it replaces.</description></item>
    ///     <item><description><b>B09</b> - the fragment and generated-source length caps
    ///     (dynamic-code-resource-limits R2) must refuse an oversize SUBGRAPH fragment the same way
    ///     they already refuse an oversize <c>/path</c> fragment: before Roslyn, naming the offending
    ///     field.</description></item>
    ///   </list>
    /// </summary>
    [TestClass]
    public class AuditDefectCodegenTest
    {
        /// <summary>The cap mirrored from <c>CodeGenerationHelper.MaxFilterFragmentLength</c> (internal
        /// to the apiApp, which declares no <c>InternalsVisibleTo</c>).</summary>
        private const int MaxFilterFragmentLength = 100_000;

        /// <summary>The cap mirrored from <c>CodeGenerationHelper.MaxGeneratedSourceLength</c>.</summary>
        private const int MaxGeneratedSourceLength = 1_000_000;

        /// <summary>A syntactically VALID fragment padded to <paramref name="totalLength"/> with a
        /// trailing comment. Valid on purpose: if Roslyn ever ran on it, the compile would SUCCEED, so
        /// an error proves the length guard fired first.</summary>
        private static String Fragment(String lambda, int totalLength)
        {
            var head = lambda + " //";
            Assert.IsTrue(totalLength >= head.Length, "test setup: the padding target must fit the fragment");
            return head + new String('x', totalLength - head.Length);
        }

        private static (Fallen8 graph, int a, int b) TwoConnectedVertices()
        {
            var fallen8 = new Fallen8(TestLoggerFactory.Create());
            var verticesTx = new CreateVerticesTransaction();
            verticesTx.AddVertex(1u, "person");
            verticesTx.AddVertex(1u, "person");
            fallen8.EnqueueTransaction(verticesTx).WaitUntilFinished();
            var v = verticesTx.GetCreatedVertices();

            var edgesTx = new CreateEdgesTransaction();
            edgesTx.AddEdge(v[0].Id, "knows", v[1].Id, 1u, "knows");
            fallen8.EnqueueTransaction(edgesTx).WaitUntilFinished();

            return (fallen8, v[0].Id, v[1].Id);
        }

        #region B24 - nothing to compile short-circuits Roslyn

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

        [TestMethod]
        public void B24_Controller_FilterlessRequest_StillReturnsThePath()
        {
            // The equivalence that matters end to end: skipping the compile must not turn the
            // match-everything traversal into a no-match one.
            var (fallen8, a, b) = TwoConnectedVertices();
            var controller = new GraphController(new UnitTestLogger<GraphController>(), fallen8);

            var before = CodeGenerationHelper.PathCompileCount;

            var result = controller.CalculateShortestPath(a, b, new PathSpecification
            {
                PathAlgorithmName = "BLS",
                MaxDepth = 3,
                MaxResults = 1
            }).Result;

            Assert.IsNotNull(result.Value, "A code-free request must not produce an error result.");
            Assert.AreEqual(1, result.Value.Count,
                "A path exists, so a filterless request must return it - the short-circuit keeps match-everything semantics.");
            Assert.AreEqual(0, CodeGenerationHelper.PathCompileCount - before,
                "The default /path body must not pay a Roslyn compile.");

            fallen8.Dispose();
        }

        #endregion

        #region B09 - the subgraph compile path honours the same length caps as /path

        [TestMethod]
        public void B09_OversizeSubGraphFragment_IsRefused_TheSameWay_AsAnOversizePathFragment()
        {
            var oversize = Fragment("return (v) => true;", MaxFilterFragmentLength + 1);
            var lengthText = oversize.Length.ToString();
            var capText = MaxFilterFragmentLength.ToString();

            // /path: the reference behaviour.
            var pathBefore = CodeGenerationHelper.PathCompileCount;
            var pathError = CodeGenerationHelper.GeneratePathTraverser(out IPathTraverser traverser,
                new PathSpecification { Filter = new PathFilterSpecification { Vertex = oversize } });

            Assert.IsNotNull(pathError, "precondition: /path already refuses an oversize fragment.");
            Assert.IsNull(traverser);
            Assert.AreEqual(0, CodeGenerationHelper.PathCompileCount - pathBefore,
                "precondition: the /path cap is enforced BEFORE Roslyn.");
            StringAssert.Contains(pathError, "exceeds the maximum of " + capText);
            StringAssert.Contains(pathError, lengthText);

            // /subgraph: the same refusal, from the same single home.
            var subGraphError = CodeGenerationHelper.TryGenerateSubGraphDefinition(
                new SubGraphSpecification { Name = "oversize-vertex-filter", VertexFilter = oversize },
                out SubGraphDefinition definition);

            Assert.IsNotNull(subGraphError, "An oversize subgraph fragment must be refused, exactly as /path refuses one.");
            Assert.IsNull(definition, "A refused specification must not produce a definition.");
            StringAssert.Contains(subGraphError, "exceeds the maximum of " + capText);
            StringAssert.Contains(subGraphError, lengthText);
            StringAssert.Contains(subGraphError, "vertexFilter", "The refusal must name the offending field.");

            // The fragment is valid C#, so Roslyn would have SUCCEEDED: an error at all, with no
            // diagnostics in it, proves the guard fired before the compiler ran.
            Assert.IsFalse(subGraphError.Contains("ID: CS"), "The guard must run before Roslyn, so no diagnostics: " + subGraphError);
        }

        [TestMethod]
        public void B09_OversizePatternFragments_AreRefused_AndNameTheOffendingSlot()
        {
            var oversize = Fragment("return (e) => true;", MaxFilterFragmentLength + 1);

            var spec = new SubGraphSpecification
            {
                Name = "oversize-pattern-filter",
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification { Type = "Vertex", PatternName = "p" },
                    new PatternSpecification
                    {
                        Type = "Edge",
                        PatternName = "rel",
                        Direction = "OutgoingEdge",
                        EdgeFilter = oversize
                    }
                }
            };

            var error = CodeGenerationHelper.TryGenerateSubGraphDefinition(spec, out var definition);

            Assert.IsNotNull(error, "An oversize pattern fragment must be refused.");
            Assert.IsNull(definition);
            StringAssert.Contains(error, "patterns[1].edgeFilter",
                "The refusal must name the offending pattern slot so the 400 body is actionable.");
            StringAssert.Contains(error, "exceeds the maximum of " + MaxFilterFragmentLength.ToString());
        }

        [TestMethod]
        public void B09_OversizeVariableLengthEdgePatternFragment_IsRefused()
        {
            // The variable-length edge pattern registers its slots through the same helper, so it
            // must be capped too (and the cap must fire before the pattern is added).
            var spec = new SubGraphSpecification
            {
                Name = "oversize-variable-length-filter",
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification
                    {
                        Type = "VariableLengthEdge",
                        PatternName = "hops",
                        Direction = "OutgoingEdge",
                        MinLength = 1,
                        MaxLength = 3,
                        EdgePropertyFilter = Fragment("return (p) => true;", MaxFilterFragmentLength + 1)
                    }
                }
            };

            var error = CodeGenerationHelper.TryGenerateSubGraphDefinition(spec, out var definition);

            Assert.IsNotNull(error, "An oversize variable-length-edge fragment must be refused.");
            Assert.IsNull(definition);
            StringAssert.Contains(error, "patterns[0].edgePropertyFilter");
        }

        [TestMethod]
        public void B09_WhitespaceOnlyOversizeFragment_IsRefused_NotSilentlyDropped()
        {
            // A blank fragment normally means "match everything" and registers no slot. An oversize
            // one is still abuse of the compile surface, so the cap is checked FIRST - exactly how
            // the /path guard treats a whitespace-only oversize fragment.
            var oversize = new String(' ', MaxFilterFragmentLength + 1);

            var error = CodeGenerationHelper.TryGenerateSubGraphDefinition(
                new SubGraphSpecification { Name = "oversize-blank", EdgeFilter = oversize }, out var definition);

            Assert.IsNotNull(error, "An oversize whitespace-only fragment must be refused, not silently dropped.");
            Assert.IsNull(definition);
            StringAssert.Contains(error, "edgeFilter");
        }

        [TestMethod]
        public void B09_FragmentExactlyAtTheCap_IsAccepted()
        {
            // The boundary: the cap is exclusive (> not >=), so a fragment of exactly
            // MaxFilterFragmentLength chars must still compile.
            var atCap = Fragment("return (v) => v.Label != \"b09-at-cap-marker\";", MaxFilterFragmentLength);
            Assert.AreEqual(MaxFilterFragmentLength, atCap.Length, "test setup");

            var error = CodeGenerationHelper.TryGenerateSubGraphDefinition(
                new SubGraphSpecification { Name = "at-cap", VertexFilter = atCap }, out var definition);

            Assert.IsNull(error, "A fragment exactly at the cap must be accepted. Got: " + error);
            Assert.IsNotNull(definition);
            Assert.IsNotNull(definition.VertexFilter, "The at-cap fragment must have compiled into a bound delegate.");
        }

        [TestMethod]
        public void B09_OversizeGeneratedSubGraphSource_IsRefused_BeforeTheCompileCache()
        {
            // Every individual fragment is UNDER the per-fragment cap; together they blow the
            // generated-source cap. The refusal must happen before the source becomes a cache key
            // (the generated source IS the subgraph provider cache key).
            const int perFragment = 90_000;
            var patterns = new List<PatternSpecification>();
            for (var i = 0; i < 6; i++)
            {
                patterns.Add(new PatternSpecification
                {
                    Type = "Edge",
                    PatternName = "rel" + i.ToString(),
                    Direction = "OutgoingEdge",
                    EdgePropertyFilter = Fragment("return (p) => p != \"b09-source-cap-" + i.ToString() + "\";", perFragment),
                    EdgeFilter = Fragment("return (e) => e.Label != \"b09-source-cap-" + i.ToString() + "\";", perFragment)
                });
            }

            Assert.IsTrue(patterns.Count * 2 * perFragment > MaxGeneratedSourceLength,
                "test setup: the fragments must together exceed the generated-source cap");

            var error = CodeGenerationHelper.TryGenerateSubGraphDefinition(
                new SubGraphSpecification { Name = "oversize-source", Patterns = patterns }, out var definition);

            Assert.IsNotNull(error, "A generated source over the cap must be refused.");
            Assert.IsNull(definition);
            StringAssert.Contains(error, "generated subgraph filter source");
            StringAssert.Contains(error, "exceeds the maximum of " + MaxGeneratedSourceLength.ToString());
            Assert.IsFalse(error.Contains("ID: CS"), "The cap must fire before Roslyn: " + error);
        }

        #endregion
    }
}
