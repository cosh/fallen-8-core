// MIT License
//
// PathAlgorithmParityTest.cs
//
// Copyright (c) 2026 Henning Rauch
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
using NoSQL.GraphDB.App.Controllers.Sample;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Cross-checks the two shortest-path plugins against each other on the same graph.
    ///
    /// The parity contract being tested: with NO cost delegates every DIJKSTRA step costs 1, so
    /// its K least-weight paths enumerate fewest-hop paths first. BLS is hop-count only and stops
    /// at the first level that reaches the target, so it can only ever return fewest-hop paths.
    /// Therefore, for the same graph, endpoints, and maxDepth, the set of paths BLS returns must
    /// equal DIJKSTRA's fewest-hop tier (its unit-cost paths of minimal length). DIJKSTRA
    /// returning ADDITIONAL, longer paths after that tier is intended behaviour, not a
    /// discrepancy; BLS returning FEWER fewest-hop paths than DIJKSTRA is a bug.
    /// </summary>
    [TestClass]
    public class PathAlgorithmParityTest
    {
        #region helpers

        /// <summary>
        /// The MaxResults used when probing for the complete fewest-hop tier. Must exceed the
        /// number of fewest-hop paths of every pair under test; the parity check reports (instead
        /// of silently passing) when a tier hits this cap.
        /// </summary>
        private const Int32 TierProbeMaxResults = 64;

        private static Fallen8 NewFallen8()
        {
            return new Fallen8(TestLoggerFactory.Create());
        }

        private static Int32[] AddVertices(Fallen8 fallen8, Int32 count)
        {
            var tx = new CreateVerticesTransaction();
            for (var i = 0; i < count; i++)
            {
                tx.AddVertex(0);
            }

            fallen8.EnqueueTransaction(tx).WaitUntilFinished();

            return tx.GetCreatedVertices().Select(v => v.Id).ToArray();
        }

        private static void AddEdges(Fallen8 fallen8, params (Int32 From, Int32 To)[] edges)
        {
            var tx = new CreateEdgesTransaction();
            foreach (var edge in edges)
            {
                tx.AddEdge(edge.From, "A", edge.To, 0);
            }

            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
        }

        private static List<Path> Run(Fallen8 fallen8, String algorithm, Int32 sourceId, Int32 targetId, Int32 maxDepth, Int32 maxResults)
        {
            List<Path> paths;
            fallen8.TryCalculateShortestPath(out paths, algorithm, new ShortestPathDefinition
            {
                SourceVertexId = sourceId,
                DestinationVertexId = targetId,
                MaxDepth = maxDepth,
                MaxResults = maxResults
            });

            return paths ?? new List<Path>();
        }

        /// <summary>The ordered vertex-id sequence of a path (source, then each hop's target).</summary>
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

        /// <summary>
        /// A path identity usable across both algorithms: the traversed edge ids interleaved with
        /// the visited vertex ids, e.g. "0-[200]->166-[1104]->184". Two paths over the same graph
        /// are the same route exactly when these strings match.
        /// </summary>
        private static String Route(Path path)
        {
            var elements = path.GetPathElements();
            if (elements.Count == 0)
            {
                return "(empty)";
            }

            var route = elements[0].SourceVertex.Id.ToString();
            foreach (var element in elements)
            {
                route += $"-[{element.Edge.Id}]->{element.TargetVertex.Id}";
            }

            return route;
        }

        /// <summary>
        /// Validates that a returned path is well-formed: non-empty, starts at the source, ends at
        /// the target, and every hop continues where the previous one ended.
        /// </summary>
        /// <returns>A defect description, or null when the path is structurally sound.</returns>
        private static String DescribeStructuralDefect(Path path, Int32 sourceId, Int32 targetId)
        {
            var elements = path.GetPathElements();
            if (elements.Count == 0)
            {
                return "path has no elements";
            }

            if (elements[0].SourceVertex.Id != sourceId)
            {
                return $"path {Route(path)} starts at {elements[0].SourceVertex.Id} instead of {sourceId}";
            }

            if (elements[elements.Count - 1].TargetVertex.Id != targetId)
            {
                return $"path {Route(path)} ends at {elements[elements.Count - 1].TargetVertex.Id} instead of {targetId}";
            }

            for (var i = 1; i < elements.Count; i++)
            {
                if (elements[i].SourceVertex.Id != elements[i - 1].TargetVertex.Id)
                {
                    return $"path {Route(path)} is disconnected between hop {i - 1} and hop {i}";
                }
            }

            return null;
        }

        /// <summary>
        /// Runs BLS and unit-cost DIJKSTRA for one vertex pair and compares BLS's result set with
        /// DIJKSTRA's fewest-hop tier (see the class doc for why that is the parity contract).
        /// </summary>
        /// <returns>All observed discrepancies for this pair; empty means parity holds.</returns>
        private static List<String> CheckShortestTierParity(Fallen8 fallen8, Int32 sourceId, Int32 targetId, Int32 maxDepth)
        {
            var mismatches = new List<String>();
            var pair = $"{sourceId}->{targetId}";

            var blsPaths = Run(fallen8, "BLS", sourceId, targetId, maxDepth, TierProbeMaxResults);
            var dijkstraPaths = Run(fallen8, "DIJKSTRA", sourceId, targetId, maxDepth, TierProbeMaxResults);

            foreach (var path in blsPaths)
            {
                var defect = DescribeStructuralDefect(path, sourceId, targetId);
                if (defect != null)
                {
                    mismatches.Add($"{pair}: BLS returned a malformed path: {defect}");
                }
            }

            foreach (var path in dijkstraPaths)
            {
                var defect = DescribeStructuralDefect(path, sourceId, targetId);
                if (defect != null)
                {
                    mismatches.Add($"{pair}: DIJKSTRA returned a malformed path: {defect}");
                }
            }

            if (blsPaths.Count == 0 && dijkstraPaths.Count == 0)
            {
                return mismatches;
            }

            if (blsPaths.Count == 0 || dijkstraPaths.Count == 0)
            {
                mismatches.Add($"{pair}: BLS found {blsPaths.Count} path(s), DIJKSTRA found {dijkstraPaths.Count}");
                return mismatches;
            }

            // DIJKSTRA returns non-decreasing unit-cost weight == non-decreasing hop count, so the
            // first path carries the minimal hop count and the tier is its length-equal prefix.
            var minHops = dijkstraPaths[0].GetLength();
            var dijkstraTier = dijkstraPaths.Where(p => p.GetLength() == minHops).ToList();

            if (dijkstraPaths.Count == TierProbeMaxResults && dijkstraTier.Count == dijkstraPaths.Count)
            {
                mismatches.Add($"{pair}: the fewest-hop tier hits the {TierProbeMaxResults}-result probe cap, so the comparison would be incomplete; raise TierProbeMaxResults");
                return mismatches;
            }

            foreach (var path in blsPaths)
            {
                if (path.GetLength() != minHops)
                {
                    mismatches.Add($"{pair}: BLS returned a {path.GetLength()}-hop path {Route(path)} although the fewest-hop distance is {minHops}");
                }
            }

            var blsRoutes = blsPaths.Select(Route).ToList();
            var dijkstraTierRoutes = dijkstraTier.Select(Route).ToList();

            if (blsRoutes.Count != blsRoutes.Distinct().Count())
            {
                mismatches.Add($"{pair}: BLS returned duplicate paths: {String.Join(", ", blsRoutes)}");
            }

            foreach (var route in dijkstraTierRoutes.Except(blsRoutes))
            {
                mismatches.Add($"{pair}: fewest-hop path {route} is found by DIJKSTRA but missing from BLS");
            }

            foreach (var route in blsRoutes.Except(dijkstraTierRoutes))
            {
                mismatches.Add($"{pair}: BLS path {route} is not in DIJKSTRA's fewest-hop tier");
            }

            return mismatches;
        }

        private static void AssertParity(List<String> mismatches)
        {
            Assert.AreEqual(0, mismatches.Count,
                "BLS and unit-cost DIJKSTRA disagree on the fewest-hop tier:\n" + String.Join("\n", mismatches));
        }

        #endregion

        [TestMethod]
        public void ParallelEdges_BothAlgorithmsReturnOnePathPerEdge()
        {
            // Arrange - two parallel edges S->T: two distinct one-hop paths.
            var fallen8 = NewFallen8();
            var ids = AddVertices(fallen8, 2);
            AddEdges(fallen8, (ids[0], ids[1]), (ids[0], ids[1]));

            // maxDepth 1 exercises BLS's dedicated depth-one branch, maxDepth 7 the bidirectional one.
            foreach (var maxDepth in new[] { 1, 7 })
            {
                // Act
                var blsPaths = Run(fallen8, "BLS", ids[0], ids[1], maxDepth, 5);
                var dijkstraPaths = Run(fallen8, "DIJKSTRA", ids[0], ids[1], maxDepth, 5);

                // Assert
                Assert.AreEqual(2, dijkstraPaths.Count, $"DIJKSTRA should return one path per parallel edge (maxDepth {maxDepth})");
                Assert.AreEqual(2, dijkstraPaths.Select(Route).Distinct().Count(), $"DIJKSTRA's two paths should use distinct edges (maxDepth {maxDepth})");

                Assert.AreEqual(2, blsPaths.Count, $"BLS should return one path per parallel edge (maxDepth {maxDepth})");
                Assert.AreEqual(2, blsPaths.Select(Route).Distinct().Count(), $"BLS's two paths should use distinct edges (maxDepth {maxDepth})");

                AssertParity(CheckShortestTierParity(fallen8, ids[0], ids[1], maxDepth));
            }
        }

        [TestMethod]
        public void ConvergingEqualLengthPaths_BlsReturnsAllFewestHopPaths()
        {
            // Arrange - the minimal shape of the studio finding: two 3-hop routes S->A->C->T and
            // S->B->C->T that CONVERGE on the shared intermediate vertex C. An algorithm that
            // keeps only one predecessor per frontier vertex loses one of them.
            var fallen8 = NewFallen8();
            var ids = AddVertices(fallen8, 5);
            Int32 s = ids[0], a = ids[1], b = ids[2], c = ids[3], t = ids[4];
            AddEdges(fallen8, (s, a), (s, b), (a, c), (b, c), (c, t));

            // Act - same parameters as the studio query (maxDepth 7, maxResults 5).
            var blsPaths = Run(fallen8, "BLS", s, t, 7, 5);
            var dijkstraPaths = Run(fallen8, "DIJKSTRA", s, t, 7, 5);

            // Assert - DIJKSTRA sees both routes.
            Assert.AreEqual(2, dijkstraPaths.Count, "DIJKSTRA should find both 3-hop routes");
            Assert.IsTrue(dijkstraPaths.Any(p => VertexIds(p).SequenceEqual(new[] { s, a, c, t })), "DIJKSTRA should find S->A->C->T");
            Assert.IsTrue(dijkstraPaths.Any(p => VertexIds(p).SequenceEqual(new[] { s, b, c, t })), "DIJKSTRA should find S->B->C->T");

            // ...and BLS must see the same two fewest-hop routes.
            Assert.IsTrue(blsPaths.Any(p => VertexIds(p).SequenceEqual(new[] { s, a, c, t })), "BLS should find S->A->C->T");
            Assert.IsTrue(blsPaths.Any(p => VertexIds(p).SequenceEqual(new[] { s, b, c, t })), "BLS should find S->B->C->T");
            Assert.AreEqual(2, blsPaths.Count, "BLS should find both 3-hop routes");

            AssertParity(CheckShortestTierParity(fallen8, s, t, 7));
        }

        [TestMethod]
        public void LongerTiers_AreDijkstraOnlyByDesign()
        {
            // Arrange - one 2-hop route S->A->T and one 3-hop route S->B->C->T. This pins the
            // INTENDED difference between the plugins: BLS stops after the fewest-hop tier while
            // DIJKSTRA's K-shortest keeps going into longer paths, so "DIJKSTRA returned more
            // paths than BLS" is only a bug when the extra paths are fewest-hop ones.
            var fallen8 = NewFallen8();
            var ids = AddVertices(fallen8, 5);
            Int32 s = ids[0], a = ids[1], b = ids[2], c = ids[3], t = ids[4];
            AddEdges(fallen8, (s, a), (a, t), (s, b), (b, c), (c, t));

            // Act
            var blsPaths = Run(fallen8, "BLS", s, t, 7, 5);
            var dijkstraPaths = Run(fallen8, "DIJKSTRA", s, t, 7, 5);

            // Assert - BLS: exactly the fewest-hop route.
            Assert.AreEqual(1, blsPaths.Count, "BLS should return only the fewest-hop tier");
            CollectionAssert.AreEqual(new List<Int32> { s, a, t }, VertexIds(blsPaths[0]), "BLS should return S->A->T");

            // DIJKSTRA: the same route first, then the longer one.
            Assert.AreEqual(2, dijkstraPaths.Count, "DIJKSTRA should also return the longer route");
            CollectionAssert.AreEqual(new List<Int32> { s, a, t }, VertexIds(dijkstraPaths[0]), "DIJKSTRA should rank the fewest-hop route first");
            CollectionAssert.AreEqual(new List<Int32> { s, b, c, t }, VertexIds(dijkstraPaths[1]), "DIJKSTRA's second path should be S->B->C->T");

            AssertParity(CheckShortestTierParity(fallen8, s, t, 7));
        }

        [TestMethod]
        public void SampleGraph_AllVertexPairs_AgreeOnFewestHopTier()
        {
            // Arrange - the 5-person sample graph, checked over every ordered vertex pair.
            var fallen8 = NewFallen8();
            TestGraphGenerator.GenerateSampleGraphAsync(fallen8).Wait();

            // Act
            var mismatches = new List<String>();
            for (var source = 0; source < 5; source++)
            {
                for (var target = 0; target < 5; target++)
                {
                    if (source != target)
                    {
                        mismatches.AddRange(CheckShortestTierParity(fallen8, source, target, 7));
                    }
                }
            }

            // Assert
            AssertParity(mismatches);
        }

        [TestMethod]
        public void ScaleFreeStyleGraph_AgreeOnFewestHopTier()
        {
            // Arrange - a deterministic replica of the studio's "generate sample graph" shape
            // (ScaleFreeNetwork.CreateScaleFreeNetworkAsync(200, 5): 200 vertices, 5 random distinct
            // out-edges per vertex on the single edge property "A"), seeded so failures reproduce.
            const Int32 nodeCount = 200;
            const Int32 edgesPerVertex = 5;

            var fallen8 = NewFallen8();
            var ids = AddVertices(fallen8, nodeCount);

            var prng = new Random(20260717);
            var edgesTx = new CreateEdgesTransaction();
            foreach (var source in ids)
            {
                var targets = new List<Int32>();
                while (targets.Count < edgesPerVertex)
                {
                    var candidate = ids[prng.Next(0, nodeCount)];
                    if (!targets.Contains(candidate))
                    {
                        targets.Add(candidate);
                    }
                }

                foreach (var target in targets)
                {
                    edgesTx.AddEdge(source, "A", target, 0);
                }
            }

            fallen8.EnqueueTransaction(edgesTx).WaitUntilFinished();

            // Act - a deterministic spread of vertex pairs, including the studio query's 0->10.
            var mismatches = new List<String>();
            foreach (var source in new[] { 0, 1, 2, 3, 4 })
            {
                foreach (var target in new[] { 10, 50, 99, 150, 199 })
                {
                    mismatches.AddRange(CheckShortestTierParity(fallen8, source, target, 7));
                }
            }

            // Assert
            AssertParity(mismatches);
        }
    }
}
