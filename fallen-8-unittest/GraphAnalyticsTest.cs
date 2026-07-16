// MIT License
//
// GraphAnalyticsTest.cs
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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms;
using NoSQL.GraphDB.Core.Algorithms.Analytics;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Engine-level tests for the analytics algorithm family (feature graph-analytics):
    /// hand-computable fixtures with pinned expected values for all five algorithms, plus the
    /// cross-cutting scoping, removed-element, budget and determinism cases.
    /// </summary>
    [TestClass]
    public class GraphAnalyticsTest
    {
        private Fallen8 _fallen8;

        [TestInitialize]
        public void TestInitialize()
        {
            _fallen8 = new Fallen8(TestLoggerFactory.Create());
        }

        [TestCleanup]
        public void TestCleanup()
        {
            _fallen8.Dispose();
        }

        #region helpers

        private Int32 Vertex(string label = "person")
        {
            var tx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition { CreationDate = 1u, Label = label }
            };
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.VertexCreated.Id;
        }

        private void Edge(Int32 source, Int32 target, string edgePropertyId = "link")
        {
            var tx = new CreateEdgeTransaction
            {
                Definition = new EdgeDefinition
                {
                    SourceVertexId = source,
                    TargetVertexId = target,
                    EdgePropertyId = edgePropertyId,
                    CreationDate = 1u
                }
            };
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();
        }

        private GraphAnalyticsResult Run(string algorithm, GraphAnalyticsDefinition definition = null)
        {
            Assert.IsTrue(_fallen8.TryRunAnalytics(out var result, algorithm, definition ?? new GraphAnalyticsDefinition()),
                algorithm + " must produce a result");
            return result;
        }

        #endregion

        #region discovery & facade

        [TestMethod]
        public void AllFiveBuiltins_AreDiscovered_WithZeroFactoryChanges()
        {
            Assert.IsTrue(PluginFactory.TryGetAvailablePlugins<IGraphAnalyticsAlgorithm>(out var names));
            var set = names.ToHashSet();
            foreach (var expected in new[] { "PAGERANK", "WCC", "LABELPROPAGATION", "DEGREE", "TRIANGLECOUNT" })
            {
                Assert.IsTrue(set.Contains(expected), expected + " must be discovered");
            }
        }

        [TestMethod]
        public void Facade_UnknownPlugin_ReturnsFalse_AndInvalidDefinitionsAreRejected()
        {
            Assert.IsFalse(_fallen8.TryRunAnalytics(out _, "NOPE", new GraphAnalyticsDefinition()));

            Assert.IsFalse(_fallen8.TryRunAnalytics(out _, "PAGERANK",
                new GraphAnalyticsDefinition { MaxIterations = GraphAnalyticsDefinition.MaxIterationsCeiling + 1 }));
            Assert.IsFalse(_fallen8.TryRunAnalytics(out _, "PAGERANK",
                new GraphAnalyticsDefinition { MaxIterations = -1 }));
            Assert.IsFalse(_fallen8.TryRunAnalytics(out _, "PAGERANK",
                new GraphAnalyticsDefinition { Epsilon = -0.1 }));
            Assert.IsFalse(_fallen8.TryRunAnalytics(out _, "PAGERANK", new GraphAnalyticsDefinition
            {
                Parameters = new Dictionary<string, object> { { "DampingFactor", 1.5 } }
            }));
        }

        [TestMethod]
        public void EmptyGraph_EveryAlgorithm_ReturnsTrueWithEmptyMaps()
        {
            foreach (var algorithm in new[] { "PAGERANK", "WCC", "LABELPROPAGATION", "DEGREE", "TRIANGLECOUNT" })
            {
                var result = Run(algorithm);
                Assert.AreEqual(0, result.VertexScores.Count, algorithm);
                Assert.AreEqual(0, result.VertexPartitions.Count, algorithm);
                Assert.IsTrue(result.Converged, algorithm);
                Assert.IsFalse(result.BudgetExhausted, algorithm);
            }
        }

        #endregion

        #region degree

        [TestMethod]
        public void Degree_Star_HubAndLeaves_InOutBoth()
        {
            var hub = Vertex();
            var leaves = new[] { Vertex(), Vertex(), Vertex() };
            foreach (var leaf in leaves)
            {
                Edge(hub, leaf);
            }

            var both = Run("DEGREE");
            Assert.AreEqual(3d, both.VertexScores[hub]);
            Assert.AreEqual(1d, both.VertexScores[leaves[0]]);
            Assert.AreEqual(3d, (Double)both.Statistics["Max"]);
            Assert.AreEqual(1.5d, (Double)both.Statistics["Mean"], 1e-9);
            Assert.IsTrue(both.Converged);

            var outOnly = Run("DEGREE", new GraphAnalyticsDefinition { Direction = Direction.OutgoingEdge });
            Assert.AreEqual(3d, outOnly.VertexScores[hub]);
            Assert.AreEqual(0d, outOnly.VertexScores[leaves[0]]);

            var inOnly = Run("DEGREE", new GraphAnalyticsDefinition { Direction = Direction.IncomingEdge });
            Assert.AreEqual(0d, inOnly.VertexScores[hub]);
            Assert.AreEqual(1d, inOnly.VertexScores[leaves[0]]);
        }

        [TestMethod]
        public void Degree_ParallelEdges_CountMultiply_AndSelfLoopContributesToBoth()
        {
            var a = Vertex();
            var b = Vertex();
            Edge(a, b);
            Edge(a, b);
            var c = Vertex();
            Edge(c, c);

            var result = Run("DEGREE");
            Assert.AreEqual(2d, result.VertexScores[a], "two parallel out-edges");
            Assert.AreEqual(2d, result.VertexScores[b], "two parallel in-edges");
            Assert.AreEqual(2d, result.VertexScores[c], "a self-loop contributes 1 to out and 1 to in");
        }

        #endregion

        #region pagerank

        [TestMethod]
        public void PageRank_TwoVertexCycle_IsHalfHalf()
        {
            var a = Vertex();
            var b = Vertex();
            Edge(a, b);
            Edge(b, a);

            var result = Run("PAGERANK");
            Assert.IsTrue(result.Converged);
            Assert.AreEqual(0.5d, result.VertexScores[a], 1e-4);
            Assert.AreEqual(0.5d, result.VertexScores[b], 1e-4);
        }

        [TestMethod]
        public void PageRank_FourVertexGraph_MatchesIndependentlyComputedValues()
        {
            // The classic small fixture: A->B, A->C, B->C, C->A, D->C (d = 0.85).
            // Values computed independently by power iteration to convergence.
            var a = Vertex();
            var b = Vertex();
            var c = Vertex();
            var d = Vertex();
            Edge(a, b);
            Edge(a, c);
            Edge(b, c);
            Edge(c, a);
            Edge(d, c);

            var result = Run("PAGERANK");
            Assert.IsTrue(result.Converged);

            var sum = result.VertexScores.Values.Sum();
            Assert.AreEqual(1d, sum, 1e-6, "ranks sum to 1 over in-scope vertices");

            // Fixed point of: rA = base + d*rC; rB = base + d*rA/2; rC = base + d*(rA/2 + rB + rD);
            // rD = base (dangling D has out-degree 1 to C - no wait, D->C so D is not dangling).
            // With every vertex having out-degree >= 1 there is no dangling mass:
            // rD = (1-d)/4 = 0.0375; solving the linear system gives:
            Assert.AreEqual(0.372526d, result.VertexScores[a], 1e-4);
            Assert.AreEqual(0.195824d, result.VertexScores[b], 1e-4);
            Assert.AreEqual(0.394150d, result.VertexScores[c], 1e-4);
            Assert.AreEqual(0.0375d, result.VertexScores[d], 1e-4);
        }

        [TestMethod]
        public void PageRank_DanglingVertex_RanksStillSumToOne()
        {
            var a = Vertex();
            var b = Vertex();
            var dangling = Vertex();
            Edge(a, b);
            Edge(b, dangling);

            var result = Run("PAGERANK");
            Assert.IsTrue(result.Converged);
            Assert.AreEqual(1d, result.VertexScores.Values.Sum(), 1e-6,
                "dangling mass is redistributed uniformly, so ranks still sum to 1");
            Assert.IsTrue(result.VertexScores[dangling] > result.VertexScores[a],
                "the dangling sink receives rank from b");
        }

        [TestMethod]
        public void PageRank_DampingZero_IsUniform()
        {
            var a = Vertex();
            var b = Vertex();
            var c = Vertex();
            Edge(a, b);
            Edge(b, c);

            var result = Run("PAGERANK", new GraphAnalyticsDefinition
            {
                Parameters = new Dictionary<string, object> { { "DampingFactor", 0d } }
            });
            Assert.IsTrue(result.Converged);
            Assert.AreEqual(1d / 3d, result.VertexScores[a], 1e-9);
            Assert.AreEqual(1d / 3d, result.VertexScores[b], 1e-9);
            Assert.AreEqual(1d / 3d, result.VertexScores[c], 1e-9);
        }

        [TestMethod]
        public void PageRank_IterationCap_IsANormalOutcome()
        {
            // Asymmetric on purpose: the uniform start is NOT the fixed point here (unlike a
            // 2-cycle, which converges in one iteration).
            var a = Vertex();
            var b = Vertex();
            Edge(a, b);

            var result = Run("PAGERANK", new GraphAnalyticsDefinition { MaxIterations = 1 });
            Assert.IsFalse(result.Converged, "one iteration cannot converge from the uniform start");
            Assert.AreEqual(1, result.IterationsRun);
            Assert.IsFalse(result.BudgetExhausted);
            Assert.AreEqual(2, result.VertexScores.Count, "values are usable");
        }

        #endregion

        #region wcc

        [TestMethod]
        public void Wcc_TwoDisjointChains_TwoComponents_SmallestMemberIds()
        {
            var a1 = Vertex();
            var a2 = Vertex();
            var a3 = Vertex();
            Edge(a1, a2);
            Edge(a2, a3);

            var b1 = Vertex();
            var b2 = Vertex();
            Edge(b2, b1); // direction must not matter

            var result = Run("WCC");
            Assert.AreEqual(2, (Int32)result.Statistics["ComponentCount"]);
            Assert.AreEqual(a1, result.VertexPartitions[a1]);
            Assert.AreEqual(a1, result.VertexPartitions[a2]);
            Assert.AreEqual(a1, result.VertexPartitions[a3]);
            Assert.AreEqual(b1, result.VertexPartitions[b1], "component id = smallest member id, direction-blind");
            Assert.AreEqual(b1, result.VertexPartitions[b2]);
            Assert.IsTrue(result.Converged);
        }

        [TestMethod]
        public void Wcc_SingletonVertex_IsItsOwnComponent()
        {
            var lonely = Vertex();
            var result = Run("WCC");
            Assert.AreEqual(1, (Int32)result.Statistics["ComponentCount"]);
            Assert.AreEqual(lonely, result.VertexPartitions[lonely]);
        }

        #endregion

        #region label propagation

        [TestMethod]
        public void LabelPropagation_TwoCliquesWithBridge_TwoCommunities_Deterministic()
        {
            // Two triangles joined by one bridge edge.
            var clique1 = new[] { Vertex(), Vertex(), Vertex() };
            var clique2 = new[] { Vertex(), Vertex(), Vertex() };
            foreach (var clique in new[] { clique1, clique2 })
            {
                Edge(clique[0], clique[1]);
                Edge(clique[1], clique[2]);
                Edge(clique[2], clique[0]);
            }
            Edge(clique1[2], clique2[0]);

            var first = Run("LABELPROPAGATION");
            Assert.AreEqual(2, (Int32)first.Statistics["CommunityCount"]);
            Assert.IsTrue(first.Converged);
            Assert.AreEqual(4, first.IterationsRun,
                "hand-derived under the pinned synchronous smallest-label rules: labels settle after round 3, round 4 detects no change");

            var community1 = first.VertexPartitions[clique1[0]];
            Assert.AreEqual(community1, first.VertexPartitions[clique1[1]]);
            Assert.AreEqual(community1, first.VertexPartitions[clique1[2]]);
            var community2 = first.VertexPartitions[clique2[0]];
            Assert.AreEqual(community2, first.VertexPartitions[clique2[1]]);
            Assert.AreEqual(community2, first.VertexPartitions[clique2[2]]);
            Assert.AreNotEqual(community1, community2);

            // Determinism pinned by running twice.
            var second = Run("LABELPROPAGATION");
            foreach (var pair in first.VertexPartitions)
            {
                Assert.AreEqual(pair.Value, second.VertexPartitions[pair.Key], "run twice => identical");
            }
        }

        [TestMethod]
        public void LabelPropagation_IsolatedVertices_KeepTheirOwnLabels_AndConvergeInOneRound()
        {
            var a = Vertex();
            var b = Vertex();

            var result = Run("LABELPROPAGATION");
            Assert.IsTrue(result.Converged);
            Assert.AreEqual(1, result.IterationsRun, "a neighbourless round changes nothing");
            Assert.AreEqual(a, result.VertexPartitions[a]);
            Assert.AreEqual(b, result.VertexPartitions[b]);
            Assert.AreEqual(2, (Int32)result.Statistics["CommunityCount"]);
        }

        #endregion

        #region triangles

        [TestMethod]
        public void Triangles_K4_HasFourTriangles_ThreePerVertex()
        {
            var v = new[] { Vertex(), Vertex(), Vertex(), Vertex() };
            for (var i = 0; i < 4; i++)
            {
                for (var j = i + 1; j < 4; j++)
                {
                    Edge(v[i], v[j]);
                }
            }

            var result = Run("TRIANGLECOUNT");
            Assert.AreEqual(4L, (Int64)result.Statistics["TriangleCount"]);
            foreach (var id in v)
            {
                Assert.AreEqual(3d, result.VertexScores[id], "each K4 vertex sits in 3 triangles");
            }
        }

        [TestMethod]
        public void Triangles_FourCycle_HasNone()
        {
            var v = new[] { Vertex(), Vertex(), Vertex(), Vertex() };
            Edge(v[0], v[1]);
            Edge(v[1], v[2]);
            Edge(v[2], v[3]);
            Edge(v[3], v[0]);

            var result = Run("TRIANGLECOUNT");
            Assert.AreEqual(0L, (Int64)result.Statistics["TriangleCount"]);
        }

        [TestMethod]
        public void Triangles_ParallelEdgesDeduplicated_SelfLoopsIgnored()
        {
            var a = Vertex();
            var b = Vertex();
            var c = Vertex();
            Edge(a, b);
            Edge(a, b); // doubled edge still counts one triangle
            Edge(b, c);
            Edge(c, a);
            Edge(a, a); // self-loop ignored

            var result = Run("TRIANGLECOUNT");
            Assert.AreEqual(1L, (Int64)result.Statistics["TriangleCount"]);
            Assert.AreEqual(1d, result.VertexScores[a]);
        }

        #endregion

        #region scoping, removal, budgets

        [TestMethod]
        public void LabelScoping_IsInducedSubgraph_OutOfScopeNeighboursInvisible()
        {
            var p1 = Vertex("person");
            var p2 = Vertex("person");
            var robot = Vertex("robot");
            Edge(p1, p2);
            Edge(p2, robot); // leaves the induced subgraph

            var result = Run("DEGREE", new GraphAnalyticsDefinition { VertexLabel = "person" });
            Assert.AreEqual(2, result.VertexScores.Count, "only persons participate");
            Assert.AreEqual(1d, result.VertexScores[p2], "the edge to the robot is invisible");
            Assert.IsFalse(result.VertexScores.ContainsKey(robot));
        }

        [TestMethod]
        public void EdgePropertyScoping_OnlyTheNamedGroupIsTraversed()
        {
            var a = Vertex();
            var b = Vertex();
            var c = Vertex();
            Edge(a, b, "knows");
            Edge(a, c, "owns");

            var result = Run("DEGREE", new GraphAnalyticsDefinition { EdgePropertyId = "knows" });
            Assert.AreEqual(1d, result.VertexScores[a]);
            Assert.AreEqual(1d, result.VertexScores[b]);
            Assert.AreEqual(0d, result.VertexScores[c], "the owns edge is out of scope");

            var wcc = Run("WCC", new GraphAnalyticsDefinition { EdgePropertyId = "knows" });
            Assert.AreEqual(2, (Int32)wcc.Statistics["ComponentCount"], "{a,b} and {c}");
        }

        [TestMethod]
        public void RemovedElements_AreSkipped()
        {
            var a = Vertex();
            var b = Vertex();
            var doomed = Vertex();
            Edge(a, b);
            Edge(b, doomed);

            _fallen8.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = doomed })
                .WaitUntilFinished();

            var degree = Run("DEGREE");
            Assert.AreEqual(2, degree.VertexScores.Count);
            Assert.AreEqual(1d, degree.VertexScores[b], "the edge to the removed vertex no longer counts");

            var wcc = Run("WCC");
            Assert.AreEqual(1, (Int32)wcc.Statistics["ComponentCount"]);
        }

        [TestMethod]
        public void Budget_NearZero_SinglePassReturnsFalse_IterativeKeepsLastCompletedPass()
        {
            for (var i = 0; i < 200; i++)
            {
                Vertex();
            }

            // A budget that is already exhausted when checked: single-pass algorithms
            // return false (partial single-pass values are meaningless).
            var exhausted = new GraphAnalyticsDefinition { TimeBudget = TimeSpan.FromTicks(1) };
            Assert.IsFalse(_fallen8.TryRunAnalytics(out _, "DEGREE", exhausted));
            Assert.IsFalse(_fallen8.TryRunAnalytics(out _, "WCC",
                new GraphAnalyticsDefinition { TimeBudget = TimeSpan.FromTicks(1) }));
            Assert.IsFalse(_fallen8.TryRunAnalytics(out _, "PAGERANK",
                new GraphAnalyticsDefinition { TimeBudget = TimeSpan.FromTicks(1) }),
                "iterative too: the budget died before ONE completed pass");
        }

        [TestMethod]
        public void LabelAndEdgePropertyScoping_LabelPropagationAndTriangles()
        {
            // A person triangle plus a robot vertex closing further triangles - out of scope.
            var p = new[] { Vertex("person"), Vertex("person"), Vertex("person") };
            var robot = Vertex("robot");
            Edge(p[0], p[1]);
            Edge(p[1], p[2]);
            Edge(p[2], p[0]);
            Edge(p[0], robot);
            Edge(p[1], robot);

            var scopedTriangles = Run("TRIANGLECOUNT", new GraphAnalyticsDefinition { VertexLabel = "person" });
            Assert.AreEqual(1L, (Int64)scopedTriangles.Statistics["TriangleCount"],
                "the p0-p1-robot triangle is invisible under the induced person scope");
            Assert.IsFalse(scopedTriangles.VertexScores.ContainsKey(robot));

            var unscopedTriangles = Run("TRIANGLECOUNT");
            Assert.AreEqual(2L, (Int64)unscopedTriangles.Statistics["TriangleCount"],
                "without the scope the robot closes a second triangle");

            // Label propagation under label scoping: the robot bridge is invisible, so the
            // person triangle forms one community of the three person vertices only.
            var scopedLp = Run("LABELPROPAGATION", new GraphAnalyticsDefinition { VertexLabel = "person" });
            Assert.AreEqual(3, scopedLp.VertexPartitions.Count);
            Assert.IsFalse(scopedLp.VertexPartitions.ContainsKey(robot));
            Assert.AreEqual(1, (Int32)scopedLp.Statistics["CommunityCount"]);

            // Edge-property scoping: only the "strong" group is traversed. A strong TRIANGLE
            // (a pair would oscillate under synchronous update - the two-vertex swap) plus a
            // weak-attached fourth vertex.
            var s = new[] { Vertex("s"), Vertex("s"), Vertex("s"), Vertex("s") };
            Edge(s[0], s[1], "strong");
            Edge(s[1], s[2], "strong");
            Edge(s[2], s[0], "strong");
            Edge(s[0], s[3], "weak");
            var lpByEdgeProperty = Run("LABELPROPAGATION", new GraphAnalyticsDefinition
            {
                VertexLabel = "s",
                EdgePropertyId = "strong"
            });
            Assert.AreEqual(2, (Int32)lpByEdgeProperty.Statistics["CommunityCount"],
                "the strong triangle forms one community; s3's weak edge is out of scope, it keeps its own label");
            Assert.AreEqual(s[3], lpByEdgeProperty.VertexPartitions[s[3]]);

            var trianglesByEdgeProperty = Run("TRIANGLECOUNT", new GraphAnalyticsDefinition
            {
                VertexLabel = "person",
                EdgePropertyId = "nope"
            });
            Assert.AreEqual(0L, (Int64)trianglesByEdgeProperty.Statistics["TriangleCount"],
                "no edge group named 'nope' - no triangles");
        }

        [TestMethod]
        public void Budget_ExhaustionAfterACompletedPass_ReturnsPartialWithFlag()
        {
            // A 100k chain: PageRank information propagates one hop per iteration, so the
            // L1 delta stays positive for ~100k iterations - convergence inside a 200 ms
            // budget is impossible, while the FIRST pass (one 100k-vertex sweep) finishes
            // orders of magnitude faster. Deterministically: >= 1 completed pass, then the
            // budget dies -> partial values with BudgetExhausted = true.
            const int n = 100_000;
            var verticesTx = new CreateVerticesTransaction();
            for (var i = 0; i < n; i++)
            {
                verticesTx.AddVertex(1u, "chain");
            }
            _fallen8.EnqueueTransaction(verticesTx).WaitUntilFinished();

            var edgesTx = new CreateEdgesTransaction();
            for (var i = 0; i < n - 1; i++)
            {
                edgesTx.AddEdge(i, "next", i + 1, 1u);
            }
            _fallen8.EnqueueTransaction(edgesTx).WaitUntilFinished();

            var pageRank = Run("PAGERANK", new GraphAnalyticsDefinition
            {
                MaxIterations = GraphAnalyticsDefinition.MaxIterationsCeiling,
                Epsilon = Double.Epsilon,
                TimeBudget = TimeSpan.FromMilliseconds(200)
            });
            Assert.IsTrue(pageRank.BudgetExhausted, "the budget died mid-run");
            Assert.IsFalse(pageRank.Converged);
            Assert.IsTrue(pageRank.IterationsRun >= 1, "at least one pass completed");
            Assert.AreEqual(n, pageRank.VertexScores.Count, "the last completed pass's values are usable");

            // Label propagation on the same chain: labels move one hop per round, ~n/2
            // rounds to converge - the 200 ms budget dies first.
            var lp = Run("LABELPROPAGATION", new GraphAnalyticsDefinition
            {
                MaxIterations = GraphAnalyticsDefinition.MaxIterationsCeiling,
                TimeBudget = TimeSpan.FromMilliseconds(200)
            });
            Assert.IsTrue(lp.BudgetExhausted);
            Assert.IsFalse(lp.Converged);
            Assert.IsTrue(lp.IterationsRun >= 1);
            Assert.AreEqual(n, lp.VertexPartitions.Count);
        }

        [TestMethod]
        public void Budget_NearZero_LabelPropagationAndTriangles_ReturnFalse()
        {
            for (var i = 0; i < 50; i++)
            {
                Vertex();
            }

            Assert.IsFalse(_fallen8.TryRunAnalytics(out _, "LABELPROPAGATION",
                new GraphAnalyticsDefinition { TimeBudget = TimeSpan.FromTicks(1) }),
                "the budget died before one completed round");
            Assert.IsFalse(_fallen8.TryRunAnalytics(out _, "TRIANGLECOUNT",
                new GraphAnalyticsDefinition { TimeBudget = TimeSpan.FromTicks(1) }),
                "single-pass: a partial triangle count is meaningless");
        }

        [TestMethod]
        public void Budget_Cancellation_IsHonoured()
        {
            for (var i = 0; i < 10; i++)
            {
                Vertex();
            }

            using var source = new CancellationTokenSource();
            source.Cancel();

            Assert.IsFalse(_fallen8.TryRunAnalytics(out _, "DEGREE",
                new GraphAnalyticsDefinition { CancellationToken = source.Token }));
        }

        #endregion
    }
}
