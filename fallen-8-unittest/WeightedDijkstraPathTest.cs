// MIT License
//
// WeightedDijkstraPathTest.cs
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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Controllers.Sample;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Tests for the weighted Dijkstra shortest path plugin ("DIJKSTRA"), covering the weighted
    /// single least-weight path, its bounds/filters, the K-shortest (Yen's) behaviour and the
    /// end-to-end REST/code-generation path. These are the genuinely weight-consuming tests the
    /// previous suite lacked.
    /// </summary>
    [TestClass]
    public class WeightedDijkstraPathTest
    {
        #region graph helper

        /// <summary>
        /// Tiny fluent builder that creates an isolated Fallen-8 instance, named vertices and
        /// weighted, labelled, directed edges. Edge weights are stored as <see cref="Double"/> so
        /// both the in-process cost delegates and the runtime-compiled cost fragments can read them.
        /// </summary>
        private sealed class GraphBuilder
        {
            private readonly Dictionary<String, Int32> _ids = new Dictionary<String, Int32>();

            public Fallen8 Fallen8
            {
                get;
            }

            public GraphBuilder(params String[] vertexNames)
            {
                Fallen8 = new Fallen8(TestLoggerFactory.Create());

                var tx = new CreateVerticesTransaction();
                foreach (var name in vertexNames)
                {
                    tx.AddVertex(0, "node", new Dictionary<String, Object> { { "name", name } });
                }

                Fallen8.EnqueueTransaction(tx).WaitUntilFinished();

                var created = tx.GetCreatedVertices();
                for (var i = 0; i < vertexNames.Length; i++)
                {
                    _ids[vertexNames[i]] = created[i].Id;
                }
            }

            public Int32 Id(String name)
            {
                return _ids[name];
            }

            public GraphBuilder Edge(String from, String to, Double weight, String label = "edge", String edgePropertyId = "e")
            {
                var tx = new CreateEdgesTransaction();
                tx.AddEdge(_ids[from], edgePropertyId, _ids[to], 0, label, new Dictionary<String, Object> { { "weight", weight } });
                Fallen8.EnqueueTransaction(tx).WaitUntilFinished();

                return this;
            }
        }

        /// <summary>Edge cost that reads the numeric "weight" property, defaulting to 1.0.</summary>
        private static Delegates.EdgeCost WeightCost()
        {
            return edge =>
            {
                Object weight;
                return edge.TryGetProperty(out weight, "weight") ? Convert.ToDouble(weight) : 1.0;
            };
        }

        /// <summary>Vertex cost that returns <paramref name="cost"/> for one vertex id, else 0.0.</summary>
        private static Delegates.VertexCost VertexCostFor(Int32 vertexId, Double cost)
        {
            return vertex => vertex.Id == vertexId ? cost : 0.0;
        }

        /// <summary>The ordered vertex-id sequence of a path (source, then each target).</summary>
        private static List<Int32> VertexIds(Path path)
        {
            var elements = path.GetPathElements();
            var result = new List<Int32>();
            if (elements.Count > 0)
            {
                result.Add(elements[0].SourceVertex.Id);
            }

            foreach (var element in elements)
            {
                result.Add(element.TargetVertex.Id);
            }

            return result;
        }

        /// <summary>Asserts two result lists are identical in count, per-path vertex order and weight.</summary>
        private static void AssertSameResult(List<Path> expected, List<Path> actual)
        {
            Assert.AreEqual(expected.Count, actual.Count, "result path count differs");
            for (var i = 0; i < expected.Count; i++)
            {
                CollectionAssert.AreEqual(VertexIds(expected[i]), VertexIds(actual[i]),
                    "path or vertex order differs at index " + i);
                Assert.AreEqual(expected[i].Weight, actual[i].Weight, 1e-9, "weight differs at index " + i);
            }
        }

        #endregion

        #region discriminating test (weight beats hops) - proves weights are consumed

        [TestMethod]
        public void Discriminating_DijkstraPrefersLeastWeightWhileBlsPrefersFewestHops()
        {
            // Arrange - A->B costs 10 in one hop; A->C->B costs 1+1=2 in two hops.
            var graph = new GraphBuilder("A", "B", "C");
            graph.Edge("A", "B", 10);
            graph.Edge("A", "C", 1);
            graph.Edge("C", "B", 1);

            var weighted = new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("B"),
                MaxDepth = 5,
                MaxResults = 1,
                EdgeCost = WeightCost()
            };

            // Act - DIJKSTRA (weighted) versus BLS (hop-count), same graph.
            List<Path> dijkstraPaths;
            var dijkstraFound = graph.Fallen8.TryCalculateShortestPath(out dijkstraPaths, "DIJKSTRA", weighted);

            List<Path> blsPaths;
            var blsFound = graph.Fallen8.TryCalculateShortestPath(out blsPaths, "BLS", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("B"),
                MaxDepth = 5,
                MaxResults = 1
            });

            // Assert - DIJKSTRA takes the cheaper, longer route A->C->B (weight 2).
            Assert.IsTrue(dijkstraFound, "DIJKSTRA should find a path");
            Assert.AreEqual(1, dijkstraPaths.Count);
            CollectionAssert.AreEqual(
                new List<Int32> { graph.Id("A"), graph.Id("C"), graph.Id("B") },
                VertexIds(dijkstraPaths[0]),
                "DIJKSTRA should take the least-weight route A->C->B");
            Assert.AreEqual(2.0, dijkstraPaths[0].Weight, 1e-9, "least-weight route weighs 2");

            // BLS takes the fewest-hop route A->B (1 hop) - a different answer.
            Assert.IsTrue(blsFound, "BLS should find a path");
            Assert.AreEqual(1, blsPaths.Count);
            CollectionAssert.AreEqual(
                new List<Int32> { graph.Id("A"), graph.Id("B") },
                VertexIds(blsPaths[0]),
                "BLS should take the fewest-hop route A->B");

            // The two algorithms genuinely disagree - the weight is being consumed.
            Assert.AreNotEqual(dijkstraPaths[0].GetLength(), blsPaths[0].GetLength(),
                "the weighted and hop-count answers must differ on this graph");
        }

        [TestMethod]
        public void Generic_ResolvesDijkstraPluginByType()
        {
            // Arrange
            var graph = new GraphBuilder("A", "B", "C");
            graph.Edge("A", "B", 10);
            graph.Edge("A", "C", 1);
            graph.Edge("C", "B", 1);

            // Act - the reflection-free generic entry point must resolve the same plugin.
            List<Path> paths;
            var found = graph.Fallen8.TryCalculateShortestPath<WeightedDijkstraShortestPath>(out paths, new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("B"),
                MaxDepth = 5,
                MaxResults = 1,
                EdgeCost = WeightCost()
            });

            // Assert
            Assert.IsTrue(found);
            Assert.AreEqual(2.0, paths[0].Weight, 1e-9);
        }

        #endregion

        #region weight population

        [TestMethod]
        public void SingleWeightedPath_TotalWeightEqualsSummedElementCost()
        {
            // Arrange
            var graph = new GraphBuilder("A", "B", "C");
            graph.Edge("A", "C", 2);
            graph.Edge("C", "B", 3);
            graph.Edge("A", "B", 100); // decoy expensive short-cut

            // Act
            List<Path> paths;
            var found = graph.Fallen8.TryCalculateShortestPath(out paths, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("B"),
                MaxDepth = 5,
                MaxResults = 1,
                EdgeCost = WeightCost()
            });

            // Assert - weight is non-zero and equals the sum of the per-element weights.
            Assert.IsTrue(found);
            var path = paths[0];
            Assert.AreEqual(2, path.GetLength());
            var sumOfElements = path.GetPathElements().Sum(e => e.Weight);
            Assert.AreEqual(5.0, path.Weight, 1e-9, "total weight should be 2+3");
            Assert.AreEqual(sumOfElements, path.Weight, 1e-9, "Path.Weight must equal the sum of element weights");
            Assert.AreEqual(2.0, path.GetPathElements()[0].Weight, 1e-9);
            Assert.AreEqual(3.0, path.GetPathElements()[1].Weight, 1e-9);
        }

        [TestMethod]
        public void VertexCost_ContributesToTotalAndCanChangeRoute()
        {
            // Arrange - without vertex cost, A->C->B (1+1) beats A->B (5).
            var graph = new GraphBuilder("A", "B", "C");
            graph.Edge("A", "B", 5);
            graph.Edge("A", "C", 1);
            graph.Edge("C", "B", 1);

            // Baseline (edge cost only): least weight is A->C->B = 2.
            List<Path> baseline;
            graph.Fallen8.TryCalculateShortestPath(out baseline, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("B"),
                MaxDepth = 5,
                MaxResults = 1,
                EdgeCost = WeightCost()
            });
            CollectionAssert.AreEqual(
                new List<Int32> { graph.Id("A"), graph.Id("C"), graph.Id("B") }, VertexIds(baseline[0]));
            Assert.AreEqual(2.0, baseline[0].Weight, 1e-9);

            // Act - make passing through C expensive (vertex cost 10); now A->B (5) wins.
            List<Path> withVertexCost;
            var found = graph.Fallen8.TryCalculateShortestPath(out withVertexCost, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("B"),
                MaxDepth = 5,
                MaxResults = 1,
                EdgeCost = WeightCost(),
                VertexCost = VertexCostFor(graph.Id("C"), 10)
            });

            // Assert - route flipped to the direct edge, total reflects the edge weight.
            Assert.IsTrue(found);
            CollectionAssert.AreEqual(
                new List<Int32> { graph.Id("A"), graph.Id("B") }, VertexIds(withVertexCost[0]),
                "vertex cost on C should push the route onto the direct A->B edge");
            Assert.AreEqual(5.0, withVertexCost[0].Weight, 1e-9);
        }

        #endregion

        #region MaxPathWeight bound

        [TestMethod]
        public void MaxPathWeight_ReturnsRouteWithinBound_PrunesOverweight()
        {
            // Arrange - expensive direct short-cut (weight 10) plus a cheap detour A->C->B (weight 2).
            var graph = new GraphBuilder("A", "B", "C");
            graph.Edge("A", "B", 10);
            graph.Edge("A", "C", 1);
            graph.Edge("C", "B", 1);

            // Act - single least-weight path within a bound of 5.
            List<Path> paths;
            var found = graph.Fallen8.TryCalculateShortestPath(out paths, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("B"),
                MaxDepth = 5,
                MaxResults = 1,
                MaxPathWeight = 5.0,
                EdgeCost = WeightCost()
            });

            // Assert - the in-bound detour is returned.
            Assert.IsTrue(found);
            CollectionAssert.AreEqual(
                new List<Int32> { graph.Id("A"), graph.Id("C"), graph.Id("B") }, VertexIds(paths[0]));
            Assert.IsTrue(paths[0].Weight <= 5.0);

            // Isolate the pruning: the weight-10 direct route genuinely exists - it is returned as a
            // second path when unbounded - but the bound excludes it. Same graph and same K; only the
            // bound differs, so the missing route is attributable solely to MaxPathWeight (not to the
            // detour simply being the least-weight route).
            ShortestPathDefinition AllRoutes(Double maxPathWeight) => new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("B"),
                MaxDepth = 5,
                MaxResults = 25,
                MaxPathWeight = maxPathWeight,
                EdgeCost = WeightCost()
            };

            List<Path> unbounded;
            var unboundedFound = graph.Fallen8.TryCalculateShortestPath(out unbounded, "DIJKSTRA", AllRoutes(Double.MaxValue));
            Assert.IsTrue(unboundedFound);
            Assert.AreEqual(2, unbounded.Count, "the weight-10 direct route exists alongside the weight-2 detour");
            Assert.IsTrue(unbounded.Any(p => p.Weight > 5.0), "one existing route is over the bound");

            List<Path> bounded;
            var boundedFound = graph.Fallen8.TryCalculateShortestPath(out bounded, "DIJKSTRA", AllRoutes(5.0));
            Assert.IsTrue(boundedFound);
            Assert.AreEqual(1, bounded.Count, "the bound must prune the over-weight route the unbounded query returned");
            Assert.IsTrue(bounded.All(p => p.Weight <= 5.0));
            CollectionAssert.AreEqual(
                new List<Int32> { graph.Id("A"), graph.Id("C"), graph.Id("B") }, VertexIds(bounded[0]));
        }

        [TestMethod]
        public void MaxPathWeight_OnlyRouteExceeds_ReturnsEmptyAndFalse()
        {
            // Arrange - the single available route weighs 10.
            var graph = new GraphBuilder("A", "B");
            graph.Edge("A", "B", 10);

            // Act - a bound below 10 leaves no admissible path.
            List<Path> pruned;
            var prunedFound = graph.Fallen8.TryCalculateShortestPath(out pruned, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("B"),
                MaxDepth = 5,
                MaxResults = 1,
                MaxPathWeight = 5.0,
                EdgeCost = WeightCost()
            });

            // Assert - empty list, not null, and false.
            Assert.IsFalse(prunedFound);
            Assert.IsNotNull(pruned);
            Assert.AreEqual(0, pruned.Count);

            // A bound equal to the weight admits it (inclusive).
            List<Path> admitted;
            var admittedFound = graph.Fallen8.TryCalculateShortestPath(out admitted, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("B"),
                MaxDepth = 5,
                MaxResults = 1,
                MaxPathWeight = 10.0,
                EdgeCost = WeightCost()
            });
            Assert.IsTrue(admittedFound);
            Assert.AreEqual(10.0, admitted[0].Weight, 1e-9);
        }

        #endregion

        #region MaxDepth bound

        [TestMethod]
        public void MaxDepth_RejectsCheaperLongRoute_ReturnsCostlierShortRoute()
        {
            // Arrange - A->B is 10 in one hop; A->C->D->B is 3 in three hops.
            var graph = new GraphBuilder("A", "B", "C", "D");
            graph.Edge("A", "B", 10);
            graph.Edge("A", "C", 1);
            graph.Edge("C", "D", 1);
            graph.Edge("D", "B", 1);

            // Act - a depth cap of 2 forbids the cheaper three-hop route.
            List<Path> capped;
            var cappedFound = graph.Fallen8.TryCalculateShortestPath(out capped, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("B"),
                MaxDepth = 2,
                MaxResults = 1,
                EdgeCost = WeightCost()
            });

            // Assert - only the costlier one-hop route fits.
            Assert.IsTrue(cappedFound);
            CollectionAssert.AreEqual(new List<Int32> { graph.Id("A"), graph.Id("B") }, VertexIds(capped[0]));
            Assert.AreEqual(10.0, capped[0].Weight, 1e-9);

            // With a depth of 3 the cheaper long route becomes available and wins.
            List<Path> deep;
            var deepFound = graph.Fallen8.TryCalculateShortestPath(out deep, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("B"),
                MaxDepth = 3,
                MaxResults = 1,
                EdgeCost = WeightCost()
            });
            Assert.IsTrue(deepFound);
            CollectionAssert.AreEqual(
                new List<Int32> { graph.Id("A"), graph.Id("C"), graph.Id("D"), graph.Id("B") }, VertexIds(deep[0]));
            Assert.AreEqual(3.0, deep[0].Weight, 1e-9);
        }

        [TestMethod]
        public void MaxDepth_CheapDeepArrivalDoesNotShadowCostlyShallowArrival()
        {
            // A cheap many-hop arrival at an intermediate vertex must NOT shadow a costly few-hop
            // arrival that is the ONLY way to reach the destination within MaxDepth. This is exactly
            // why the Dijkstra label is keyed on (vertexId, hops) rather than per-vertex: a per-vertex
            // distance would settle M at its cheap 2-hop weight and discard the costly 1-hop arrival
            // that alone can still reach B within a tight hop budget. A future "simplification" to
            // per-vertex distances must fail this test.
            //
            // Arrange - two ways to reach the intermediate M:
            //   costly & shallow:  A->M      (weight 10, 1 hop)
            //   cheap  & deep:      A->X->M   (weight 1 + 1 = 2, 2 hops)
            // plus M->B (weight 1). So A->M->B is 2 hops / weight 11; A->X->M->B is 3 hops / weight 3.
            var graph = new GraphBuilder("A", "B", "M", "X");
            graph.Edge("A", "M", 10);
            graph.Edge("M", "B", 1);
            graph.Edge("A", "X", 1);
            graph.Edge("X", "M", 1);

            // Act + Assert - at MaxDepth 2 the ONLY route within budget is the costlier short one:
            // B is reachable only via M, and M is reachable in 1 hop only by the weight-10 edge.
            List<Path> shallow;
            var shallowFound = graph.Fallen8.TryCalculateShortestPath(out shallow, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("B"),
                MaxDepth = 2,
                MaxResults = 1,
                EdgeCost = WeightCost()
            });
            Assert.IsTrue(shallowFound, "A->M->B (2 hops) must be found at MaxDepth 2");
            CollectionAssert.AreEqual(
                new List<Int32> { graph.Id("A"), graph.Id("M"), graph.Id("B") }, VertexIds(shallow[0]),
                "the cheap 2-hop arrival at M must not shadow the costly 1-hop arrival that reaches B within 2 hops");
            Assert.AreEqual(11.0, shallow[0].Weight, 1e-9);

            // At MaxDepth 3 the cheaper long route becomes reachable and wins.
            List<Path> deep;
            var deepFound = graph.Fallen8.TryCalculateShortestPath(out deep, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("B"),
                MaxDepth = 3,
                MaxResults = 1,
                EdgeCost = WeightCost()
            });
            Assert.IsTrue(deepFound);
            CollectionAssert.AreEqual(
                new List<Int32> { graph.Id("A"), graph.Id("X"), graph.Id("M"), graph.Id("B") }, VertexIds(deep[0]));
            Assert.AreEqual(3.0, deep[0].Weight, 1e-9);
        }

        [TestMethod]
        public void MaxDepth_HopCapIsResultInvariant_HugeDepthMatchesVertexCountDepth()
        {
            // The engine caps the effective hop budget at VertexCount - 1: a returned loop-free path
            // has at most that many edges, so the cap cannot change any result - it only stops the
            // (vertexId, hops) search from enumerating O(V * MaxDepth) states for a huge opt-in
            // MaxDepth. On a cyclic graph, an absurd MaxDepth must return exactly the same
            // paths/weights/order as MaxDepth = VertexCount, and must return promptly. Guards a future
            // regression of the cap.
            //
            // Arrange - directed triangle A->B->C->A (an undirected cycle) plus a tail C->D->E; all
            // unit weights. VertexCount = 5 (cap 4). Shortest A->E is A->C->D->E (C reached via the
            // C->A edge traversed backward), 3 hops / weight 3.
            var graph = new GraphBuilder("A", "B", "C", "D", "E");
            graph.Edge("A", "B", 1);
            graph.Edge("B", "C", 1);
            graph.Edge("C", "A", 1);
            graph.Edge("C", "D", 1);
            graph.Edge("D", "E", 1);

            ShortestPathDefinition Def(Int32 maxDepth, Int32 maxResults) => new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("E"),
                MaxDepth = maxDepth,
                MaxResults = maxResults,
                EdgeCost = WeightCost()
            };

            var vertexCount = graph.Fallen8.VertexCount;

            // Single least-weight path: huge MaxDepth vs MaxDepth = VertexCount.
            List<Path> reference1;
            graph.Fallen8.TryCalculateShortestPath(out reference1, "DIJKSTRA", Def(vertexCount, 1));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            List<Path> huge1;
            var huge1Found = graph.Fallen8.TryCalculateShortestPath(out huge1, "DIJKSTRA", Def(10_000, 1));
            stopwatch.Stop();

            Assert.IsTrue(huge1Found);
            AssertSameResult(reference1, huge1);
            CollectionAssert.AreEqual(
                new List<Int32> { graph.Id("A"), graph.Id("C"), graph.Id("D"), graph.Id("E") }, VertexIds(huge1[0]),
                "shortest A->E is the 3-hop A->C->D->E (C reached via the C->A edge backward)");
            Assert.AreEqual(3.0, huge1[0].Weight, 1e-9);
            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
                "the capped search must return promptly, not enumerate O(V * MaxDepth) states");

            // K-shortest (Yen's): the cap must flow into the spur searches identically too.
            List<Path> referenceK;
            graph.Fallen8.TryCalculateShortestPath(out referenceK, "DIJKSTRA", Def(vertexCount, 3));
            List<Path> hugeK;
            graph.Fallen8.TryCalculateShortestPath(out hugeK, "DIJKSTRA", Def(10_000, 3));
            AssertSameResult(referenceK, hugeK);
        }

        #endregion

        #region default (no cost) matches BLS length

        [TestMethod]
        public void DefaultNoCost_YieldsFewestHopPath_LikeBls_SmallGraph()
        {
            // Arrange - with no cost delegates every edge costs 1, so DIJKSTRA is fewest-hop.
            var graph = new GraphBuilder("A", "B", "C");
            graph.Edge("A", "B", 10);
            graph.Edge("A", "C", 1);
            graph.Edge("C", "B", 1);

            var definition = new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("B"),
                MaxDepth = 5,
                MaxResults = 1
            };

            // Act
            List<Path> dijkstra;
            graph.Fallen8.TryCalculateShortestPath(out dijkstra, "DIJKSTRA", definition);
            List<Path> bls;
            graph.Fallen8.TryCalculateShortestPath(out bls, "BLS", definition);

            // Assert - identical length (the direct one-hop edge), and DIJKSTRA weight equals hops.
            Assert.AreEqual(1, dijkstra[0].GetLength());
            Assert.AreEqual(bls[0].GetLength(), dijkstra[0].GetLength());
            CollectionAssert.AreEqual(new List<Int32> { graph.Id("A"), graph.Id("B") }, VertexIds(dijkstra[0]));
            Assert.AreEqual(1.0, dijkstra[0].Weight, 1e-9, "default cost is 1 per edge");
        }

        [TestMethod]
        public void DefaultNoCost_YieldsSameLengthAsBls_AbcGraph()
        {
            // Arrange - the 26-vertex chain; the only route from 0 to 20 is 20 hops.
            var fallen8 = new Fallen8(TestLoggerFactory.Create());
            TestGraphGenerator.GenerateAbcGraph(fallen8);

            var definition = new ShortestPathDefinition
            {
                SourceVertexId = 0,
                DestinationVertexId = 20,
                MaxDepth = 26,
                MaxResults = 1
            };

            // Act
            List<Path> dijkstra;
            var dijkstraFound = fallen8.TryCalculateShortestPath(out dijkstra, "DIJKSTRA", definition);
            List<Path> bls;
            var blsFound = fallen8.TryCalculateShortestPath(out bls, "BLS", definition);

            // Assert
            Assert.IsTrue(dijkstraFound);
            Assert.IsTrue(blsFound);
            Assert.AreEqual(bls[0].GetLength(), dijkstra[0].GetLength(), "both should return a 20-edge path");
            Assert.AreEqual(20, dijkstra[0].GetLength());
            Assert.AreEqual(20.0, dijkstra[0].Weight, 1e-9);
        }

        #endregion

        #region empty / false guard clauses

        [TestMethod]
        public void Disconnected_ReturnsEmptyAndFalse()
        {
            // Arrange - two components with no edge between them.
            var graph = new GraphBuilder("A", "B", "X", "Y");
            graph.Edge("A", "B", 1);
            graph.Edge("X", "Y", 1);

            // Act
            List<Path> paths;
            var found = graph.Fallen8.TryCalculateShortestPath(out paths, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("Y"),
                MaxDepth = 10,
                MaxResults = 5,
                EdgeCost = WeightCost()
            });

            // Assert
            Assert.IsFalse(found);
            Assert.IsNotNull(paths);
            Assert.AreEqual(0, paths.Count);
        }

        [TestMethod]
        public void NonexistentEndpoint_ReturnsEmptyAndFalse()
        {
            // Arrange
            var graph = new GraphBuilder("A", "B");
            graph.Edge("A", "B", 1);

            // Act - destination id does not exist.
            List<Path> paths;
            var found = graph.Fallen8.TryCalculateShortestPath(out paths, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = 999999,
                MaxDepth = 10,
                MaxResults = 5
            });

            // Assert
            Assert.IsFalse(found);
            Assert.IsNotNull(paths);
            Assert.AreEqual(0, paths.Count);
        }

        [TestMethod]
        public void SourceEqualsDestination_ReturnsEmptyAndFalse()
        {
            // Arrange
            var graph = new GraphBuilder("A", "B");
            graph.Edge("A", "B", 1);

            // Act
            List<Path> paths;
            var found = graph.Fallen8.TryCalculateShortestPath(out paths, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("A"),
                MaxDepth = 10,
                MaxResults = 5
            });

            // Assert
            Assert.IsFalse(found);
            Assert.IsNotNull(paths);
            Assert.AreEqual(0, paths.Count);
        }

        [TestMethod]
        public void MaxDepthZero_ReturnsEmptyAndFalse()
        {
            // Arrange
            var graph = new GraphBuilder("A", "B");
            graph.Edge("A", "B", 1);

            // Act
            List<Path> paths;
            var found = graph.Fallen8.TryCalculateShortestPath(out paths, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("B"),
                MaxDepth = 0,
                MaxResults = 5
            });

            // Assert
            Assert.IsFalse(found);
            Assert.IsNotNull(paths);
            Assert.AreEqual(0, paths.Count);
        }

        [TestMethod]
        public void MaxResultsZero_ReturnsEmptyAndFalse()
        {
            // Arrange
            var graph = new GraphBuilder("A", "B");
            graph.Edge("A", "B", 1);

            // Act
            List<Path> paths;
            var found = graph.Fallen8.TryCalculateShortestPath(out paths, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("B"),
                MaxDepth = 5,
                MaxResults = 0
            });

            // Assert
            Assert.IsFalse(found);
            Assert.IsNotNull(paths);
            Assert.AreEqual(0, paths.Count);
        }

        #endregion

        #region filters

        [TestMethod]
        public void EdgeFilter_RestrictsTraversalToAllowedLabel()
        {
            // Arrange - direct edge is "toll"; the two-hop route is "free".
            var graph = new GraphBuilder("A", "B", "C");
            graph.Edge("A", "B", 1, "toll");
            graph.Edge("A", "C", 1, "free");
            graph.Edge("C", "B", 1, "free");

            // Act - allow only "free" edges: the cheap direct route is off-limits.
            List<Path> paths;
            var found = graph.Fallen8.TryCalculateShortestPath(out paths, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("B"),
                MaxDepth = 5,
                MaxResults = 1,
                EdgeCost = WeightCost(),
                EdgeFilter = e => e.Label == "free"
            });

            // Assert - forced onto A->C->B, and every traversed edge is "free".
            Assert.IsTrue(found);
            CollectionAssert.AreEqual(
                new List<Int32> { graph.Id("A"), graph.Id("C"), graph.Id("B") }, VertexIds(paths[0]));
            foreach (var element in paths[0].GetPathElements())
            {
                Assert.AreEqual("free", element.Edge.Label);
            }
        }

        [TestMethod]
        public void VertexFilter_ExcludesFilteredVertex()
        {
            // Arrange - two routes to D; one through B, one through C.
            var graph = new GraphBuilder("A", "B", "C", "D");
            graph.Edge("A", "B", 1);
            graph.Edge("B", "D", 1);
            graph.Edge("A", "C", 1);
            graph.Edge("C", "D", 1);

            // Act - forbid C; the route must go through B.
            List<Path> paths;
            var found = graph.Fallen8.TryCalculateShortestPath(out paths, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("D"),
                MaxDepth = 5,
                MaxResults = 1,
                EdgeCost = WeightCost(),
                VertexFilter = v => v.Id != graph.Id("C")
            });

            // Assert
            Assert.IsTrue(found);
            CollectionAssert.DoesNotContain(VertexIds(paths[0]), graph.Id("C"), "the filtered vertex must not appear");
            CollectionAssert.AreEqual(
                new List<Int32> { graph.Id("A"), graph.Id("B"), graph.Id("D") }, VertexIds(paths[0]));
        }

        [TestMethod]
        public void EdgePropertyFilter_ExcludesEdgesOfRejectedPropertyId()
        {
            // Arrange - the direct edge is stored under edge-property id "toll"; the two-hop detour
            // under "free". The direct route (weight 1, 1 hop) is cheaper and shorter, so without the
            // filter DIJKSTRA would take it - rejecting the "toll" property id must make it
            // non-traversable and force the detour, isolating the edge-property filter's effect.
            var graph = new GraphBuilder("A", "B", "C");
            graph.Edge("A", "B", 1, "edge", "toll");
            graph.Edge("A", "C", 1, "edge", "free");
            graph.Edge("C", "B", 1, "edge", "free");

            // Act - reject the "toll" edge-property id.
            List<Path> paths;
            var found = graph.Fallen8.TryCalculateShortestPath(out paths, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("B"),
                MaxDepth = 5,
                MaxResults = 1,
                EdgeCost = WeightCost(),
                EdgePropertyFilter = id => id != "toll"
            });

            // Assert - forced onto A->C->B; every traversed edge sits under the allowed property id.
            Assert.IsTrue(found);
            CollectionAssert.AreEqual(
                new List<Int32> { graph.Id("A"), graph.Id("C"), graph.Id("B") }, VertexIds(paths[0]));
            foreach (var element in paths[0].GetPathElements())
            {
                Assert.AreEqual("free", element.EdgePropertyId, "the rejected 'toll' property must not be traversed");
            }
        }

        #endregion

        #region incoming-edge (against direction) traversal

        [TestMethod]
        public void IncomingEdgeTraversal_OnlyRouteRunsAgainstEdgeDirection()
        {
            // The only route to C runs A->B (with the edge direction), then B->C AGAINST the
            // direction of the C->B edge (undirected reachability over directed edges, as
            // documented). Pins that the backward step is recorded as Direction.IncomingEdge with
            // the correct route and weight.
            //
            // Arrange - A->B (weight 2) and C->B (weight 5). C's only edge is C->B, so C is reachable
            // from B only by traversing that edge backward.
            var graph = new GraphBuilder("A", "B", "C");
            graph.Edge("A", "B", 2);
            graph.Edge("C", "B", 5);

            // Act
            List<Path> paths;
            var found = graph.Fallen8.TryCalculateShortestPath(out paths, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("C"),
                MaxDepth = 5,
                MaxResults = 1,
                EdgeCost = WeightCost()
            });

            // Assert - route A->B->C, weight 7, and the second step is an incoming (backward) traversal.
            Assert.IsTrue(found);
            CollectionAssert.AreEqual(
                new List<Int32> { graph.Id("A"), graph.Id("B"), graph.Id("C") }, VertexIds(paths[0]));
            Assert.AreEqual(7.0, paths[0].Weight, 1e-9, "2 (A->B) + 5 (C->B traversed backward)");

            var elements = paths[0].GetPathElements();
            Assert.AreEqual(Direction.OutgoingEdge, elements[0].Direction, "A->B follows the edge direction");
            Assert.AreEqual(Direction.IncomingEdge, elements[1].Direction, "B->C runs against the C->B edge direction");
            Assert.AreEqual(graph.Id("B"), elements[1].SourceVertex.Id);
            Assert.AreEqual(graph.Id("C"), elements[1].TargetVertex.Id);
        }

        #endregion

        #region K-shortest (Yen's)

        /// <summary>
        /// Builds the diamond used by the K-shortest tests: A->B->D and A->C->D both weigh 2, the
        /// direct A->D weighs 5. C->D is labelled "rail"; every other edge is "road".
        /// </summary>
        private static GraphBuilder BuildDiamond()
        {
            var graph = new GraphBuilder("A", "B", "C", "D");
            graph.Edge("A", "B", 1, "road");
            graph.Edge("B", "D", 1, "road");
            graph.Edge("A", "C", 1, "road");
            graph.Edge("C", "D", 1, "rail");
            graph.Edge("A", "D", 5, "road");
            return graph;
        }

        [TestMethod]
        public void KShortest_ReturnsDistinctPathsInNonDecreasingWeightOrder()
        {
            // Arrange
            var graph = BuildDiamond();

            // Act - ask for all three loop-free A->D paths.
            List<Path> paths;
            var found = graph.Fallen8.TryCalculateShortestPath(out paths, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("D"),
                MaxDepth = 5,
                MaxResults = 3,
                EdgeCost = WeightCost()
            });

            // Assert - three paths, weights 2, 2, 5 in non-decreasing order, all distinct.
            Assert.IsTrue(found);
            Assert.AreEqual(3, paths.Count);
            for (var i = 1; i < paths.Count; i++)
            {
                Assert.IsTrue(paths[i - 1].Weight <= paths[i].Weight + 1e-9,
                    "paths must be in non-decreasing weight order");
            }

            Assert.AreEqual(2.0, paths[0].Weight, 1e-9);
            Assert.AreEqual(2.0, paths[1].Weight, 1e-9);
            Assert.AreEqual(5.0, paths[2].Weight, 1e-9);

            var signatures = paths.Select(p => String.Join(",", VertexIds(p))).ToList();
            Assert.AreEqual(signatures.Count, signatures.Distinct().Count(), "paths must be distinct");
        }

        [TestMethod]
        public void KShortest_FewerThanK_ReturnsAllWithoutDuplicates()
        {
            // Arrange - only three loop-free A->D paths exist.
            var graph = BuildDiamond();

            // Act - ask for more than exist.
            List<Path> paths;
            var found = graph.Fallen8.TryCalculateShortestPath(out paths, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("D"),
                MaxDepth = 5,
                MaxResults = 25,
                EdgeCost = WeightCost()
            });

            // Assert - exactly the three that exist, no duplicates.
            Assert.IsTrue(found);
            Assert.AreEqual(3, paths.Count);
            var signatures = paths.Select(p => String.Join(",", VertexIds(p))).ToList();
            Assert.AreEqual(signatures.Count, signatures.Distinct().Count());
        }

        [TestMethod]
        public void KShortest_TiesAreDeterministic()
        {
            // Act twice on independent graphs; the ordering of the two equal-weight paths must match.
            var first = new GraphBuilder("A", "B", "C", "D");
            var firstResult = RunTwoShortest(first);

            var second = new GraphBuilder("A", "B", "C", "D");
            var secondResult = RunTwoShortest(second);

            // Assert - both weight 2, identical vertex sequences across runs.
            Assert.AreEqual(2, firstResult.Count);
            Assert.AreEqual(2.0, firstResult[0].Weight, 1e-9);
            Assert.AreEqual(2.0, firstResult[1].Weight, 1e-9);

            CollectionAssert.AreEqual(VertexIds(firstResult[0]), VertexIds(secondResult[0]),
                "tie ordering must be deterministic across runs");
            CollectionAssert.AreEqual(VertexIds(firstResult[1]), VertexIds(secondResult[1]),
                "tie ordering must be deterministic across runs");
        }

        private static List<Path> RunTwoShortest(GraphBuilder graph)
        {
            graph.Edge("A", "B", 1);
            graph.Edge("B", "D", 1);
            graph.Edge("A", "C", 1);
            graph.Edge("C", "D", 1);

            List<Path> paths;
            graph.Fallen8.TryCalculateShortestPath(out paths, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("D"),
                MaxDepth = 5,
                MaxResults = 2,
                EdgeCost = WeightCost()
            });
            return paths;
        }

        [TestMethod]
        public void KShortest_RespectsMaxPathWeight()
        {
            // Arrange
            var graph = BuildDiamond();

            // Act - a bound of 2 excludes the weight-5 direct route.
            List<Path> paths;
            var found = graph.Fallen8.TryCalculateShortestPath(out paths, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("D"),
                MaxDepth = 5,
                MaxResults = 25,
                MaxPathWeight = 2.0,
                EdgeCost = WeightCost()
            });

            // Assert - only the two weight-2 routes.
            Assert.IsTrue(found);
            Assert.AreEqual(2, paths.Count);
            Assert.IsTrue(paths.All(p => p.Weight <= 2.0 + 1e-9));
        }

        [TestMethod]
        public void KShortest_RespectsMaxDepth()
        {
            // Arrange
            var graph = BuildDiamond();

            // Act - a depth cap of 1 admits only the direct edge.
            List<Path> paths;
            var found = graph.Fallen8.TryCalculateShortestPath(out paths, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("D"),
                MaxDepth = 1,
                MaxResults = 25,
                EdgeCost = WeightCost()
            });

            // Assert - only the one-hop A->D route (weight 5) survives.
            Assert.IsTrue(found);
            Assert.AreEqual(1, paths.Count);
            Assert.AreEqual(1, paths[0].GetLength());
            Assert.AreEqual(5.0, paths[0].Weight, 1e-9);
        }

        [TestMethod]
        public void KShortest_RespectsEdgeFilter()
        {
            // Arrange - filtering out "rail" removes C->D, so A->C->D is not traversable.
            var graph = BuildDiamond();

            // Act
            List<Path> paths;
            var found = graph.Fallen8.TryCalculateShortestPath(out paths, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("D"),
                MaxDepth = 5,
                MaxResults = 25,
                EdgeCost = WeightCost(),
                EdgeFilter = e => e.Label == "road"
            });

            // Assert - only A->B->D (2) and A->D (5) remain; no path uses the "rail" edge.
            Assert.IsTrue(found);
            Assert.AreEqual(2, paths.Count);
            foreach (var path in paths)
            {
                foreach (var element in path.GetPathElements())
                {
                    Assert.AreEqual("road", element.Edge.Label);
                }
            }
        }

        #endregion

        #region defensive negative cost

        [TestMethod]
        public void NegativeStepCost_IsClampedAndStillTerminates()
        {
            // A negative edge weight would break plain Dijkstra; the combined step cost must be
            // clamped to 0 (for the ordering AND the recorded weight), the search must terminate,
            // and the reported weight must reflect the clamp exactly. The only route runs through
            // the negative edge, so the clamp is observable on the returned path.
            //
            // Arrange - A->B (weight 3), B->C (weight -5); no direct A->C.
            var graph = new GraphBuilder("A", "B", "C");
            graph.Edge("A", "B", 3);
            graph.Edge("B", "C", -5);

            // Act
            List<Path> paths;
            var found = graph.Fallen8.TryCalculateShortestPath(out paths, "DIJKSTRA", new ShortestPathDefinition
            {
                SourceVertexId = graph.Id("A"),
                DestinationVertexId = graph.Id("C"),
                MaxDepth = 5,
                MaxResults = 1,
                EdgeCost = WeightCost()
            });

            // Assert - route A->B->C; the -5 step is clamped to 0, so the total is exactly 3 + 0 = 3
            // and every element weight is non-negative.
            Assert.IsTrue(found);
            CollectionAssert.AreEqual(
                new List<Int32> { graph.Id("A"), graph.Id("B"), graph.Id("C") }, VertexIds(paths[0]));
            var elements = paths[0].GetPathElements();
            Assert.AreEqual(3.0, elements[0].Weight, 1e-9, "A->B contributes its weight of 3");
            Assert.AreEqual(0.0, elements[1].Weight, 1e-9, "the negative B->C step is clamped to 0, not -5");
            Assert.AreEqual(3.0, paths[0].Weight, 1e-9, "total weight = 3 + clamp(-5) = 3");
        }

        #endregion

        #region end-to-end REST / code generation

        [TestMethod]
        public void RestEndToEnd_DijkstraHonoursCompiledCostBlock_NonZeroTotalWeight()
        {
            // Arrange - the discriminating graph, reached through the controller so the cost block
            // is compiled by CodeGenerationHelper and the algorithm resolved by name over REST.
            var graph = new GraphBuilder("A", "B", "C");
            graph.Edge("A", "B", 10);
            graph.Edge("A", "C", 1);
            graph.Edge("C", "B", 1);

            var controller = new GraphController(new UnitTestLogger<GraphController>(), graph.Fallen8);

            var spec = new PathSpecification
            {
                PathAlgorithmName = "DIJKSTRA",
                MaxDepth = 5,
                MaxResults = 1,
                Cost = new PathCostSpecification
                {
                    Vertex = "return (v) => 0.0;",
                    Edge = "return (e) => e.TryGetProperty<double>(out var w, \"weight\") ? w : 1.0;"
                }
            };

            // Act
            var result = controller.CalculateShortestPath(graph.Id("A"), graph.Id("B"), spec);

            // Assert - the weighted route A->C->B with a genuine, non-zero total weight of 2.
            Assert.IsNotNull(result);
            Assert.AreEqual(1, result.Count, "DIJKSTRA should surface one path via REST");
            Assert.AreEqual(2, result[0].PathElements.Count);
            Assert.AreEqual(2.0, result[0].TotalWeight, 1e-9, "compiled edge cost must produce a non-zero total weight");
            Assert.AreEqual(graph.Id("A"), result[0].PathElements[0].SourceVertexId);
            Assert.AreEqual(graph.Id("C"), result[0].PathElements[0].TargetVertexId);
            Assert.AreEqual(graph.Id("B"), result[0].PathElements[1].TargetVertexId);
        }

        [TestMethod]
        public void RestEndToEnd_DijkstraDiffersFromBls_OnSameCostBlock()
        {
            // Arrange
            var graph = new GraphBuilder("A", "B", "C");
            graph.Edge("A", "B", 10);
            graph.Edge("A", "C", 1);
            graph.Edge("C", "B", 1);

            var controller = new GraphController(new UnitTestLogger<GraphController>(), graph.Fallen8);

            PathCostSpecification Cost() => new PathCostSpecification
            {
                Vertex = "return (v) => 0.0;",
                Edge = "return (e) => e.TryGetProperty<double>(out var w, \"weight\") ? w : 1.0;"
            };

            // Act - same request, only the algorithm name differs.
            var dijkstra = controller.CalculateShortestPath(graph.Id("A"), graph.Id("B"),
                new PathSpecification { PathAlgorithmName = "DIJKSTRA", MaxDepth = 5, MaxResults = 1, Cost = Cost() });
            var bls = controller.CalculateShortestPath(graph.Id("A"), graph.Id("B"),
                new PathSpecification { PathAlgorithmName = "BLS", MaxDepth = 5, MaxResults = 1, Cost = Cost() });

            // Assert - DIJKSTRA takes the two-hop weighted route; BLS the one-hop route with weight 0.
            Assert.AreEqual(1, dijkstra.Count);
            Assert.AreEqual(2, dijkstra[0].PathElements.Count);
            Assert.AreEqual(2.0, dijkstra[0].TotalWeight, 1e-9);

            Assert.AreEqual(1, bls.Count);
            Assert.AreEqual(1, bls[0].PathElements.Count);
            Assert.AreEqual(0.0, bls[0].TotalWeight, 1e-9, "BLS ignores cost and leaves the weight at 0");
        }

        #endregion
    }
}
