// MIT License
//
// PathExecutionBudgetTest.cs
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
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Pins the cooperative execution budget of the path algorithms (audit defect B22): a
    ///   traversal can be bounded by a deadline, a step cap or the caller's cancellation; an
    ///   exhausted budget is reported as "no result plus BudgetExhausted" (never a truncated
    ///   success and never an empty "no path exists" answer), the REST surface answers 408, and a
    ///   request that names NO budget behaves exactly as it always did.
    ///
    ///   <para>What is deliberately NOT tested: a fragment that never returns. The budget is
    ///   cooperative - it is sampled between delegate invocations - so such a fragment still holds
    ///   its thread, and a test for it would hang the suite. Every test here carries a hard
    ///   wall-clock ceiling for the same reason.</para>
    /// </summary>
    [TestClass]
    public class PathExecutionBudgetTest
    {
        /// <summary>Every test in this class must finish well inside this; a budget that fails to
        /// stop a traversal must show up as a failed assertion, not as a hanging suite.</summary>
        private const Int32 WallClockCeilingMilliseconds = 30_000;

        #region fixtures

        private static Fallen8 NewFallen8()
        {
            return new Fallen8(TestLoggerFactory.Create());
        }

        private static Int32[] AddVertices(Fallen8 fallen8, Int32 count)
        {
            var tx = new CreateVerticesTransaction();
            for (var i = 0; i < count; i++)
            {
                tx.AddVertex(0, "person");
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

        /// <summary>A simple chain v0 -&gt; v1 -&gt; ... -&gt; v(count-1).</summary>
        private static (Fallen8 Fallen8, Int32[] Ids) Chain(Int32 count)
        {
            var fallen8 = NewFallen8();
            var ids = AddVertices(fallen8, count);

            var edges = new List<(Int32, Int32)>();
            for (var i = 0; i < count - 1; i++)
            {
                edges.Add((ids[i], ids[i + 1]));
            }

            AddEdges(fallen8, edges.ToArray());

            return (fallen8, ids);
        }

        /// <summary>Two disjoint two-hop routes from the first to the last vertex, so a run returns
        /// SEVERAL paths and a parity comparison has something to compare.</summary>
        private static (Fallen8 Fallen8, Int32[] Ids) Diamond()
        {
            var fallen8 = NewFallen8();
            var ids = AddVertices(fallen8, 4);

            AddEdges(fallen8,
                (ids[0], ids[1]), (ids[1], ids[3]),
                (ids[0], ids[2]), (ids[2], ids[3]));

            return (fallen8, ids);
        }

        private static ShortestPathDefinition Definition(Int32 from, Int32 to, Int32 maxDepth, Int32 maxResults)
        {
            return new ShortestPathDefinition
            {
                SourceVertexId = from,
                DestinationVertexId = to,
                MaxDepth = maxDepth,
                MaxResults = maxResults
            };
        }

        /// <summary>The ordered vertex-id sequence of a path, so two runs can be compared exactly.</summary>
        private static String Signature(Path path)
        {
            var elements = path.GetPathElements();
            var ids = new List<Int32>();
            if (elements.Count > 0)
            {
                ids.Add(elements[0].SourceVertex.Id);
            }

            foreach (var element in elements)
            {
                ids.Add(element.TargetVertex.Id);
            }

            return String.Join("-", ids);
        }

        private static List<String> Signatures(List<Path> paths)
        {
            return (paths ?? new List<Path>()).Select(Signature).ToList();
        }

        #endregion

        #region engine: the previously unbounded case is now bounded

        [DataTestMethod]
        [DataRow("BLS")]
        [DataRow("DIJKSTRA")]
        public void StepBudget_StopsTheTraversal_AndReportsExhaustionInsteadOfAResult(String algorithm)
        {
            var (fallen8, ids) = Chain(6);
            var watch = Stopwatch.StartNew();

            try
            {
                var definition = Definition(ids[0], ids[5], 5, 1);
                definition.StepBudget = 1;

                var found = fallen8.TryCalculateShortestPath(out var paths, algorithm, definition);

                Assert.IsFalse(found, "A traversal stopped by the step budget must not report success.");
                Assert.IsTrue(definition.BudgetExhausted,
                    "The exhausted budget must be visible to the caller - otherwise a timeout is indistinguishable from 'no path exists'.");
                Assert.AreEqual(0, (paths ?? new List<Path>()).Count,
                    "A partially explored search must not hand out paths.");
                Assert.IsTrue(watch.ElapsedMilliseconds < WallClockCeilingMilliseconds, "wall-clock ceiling");
            }
            finally
            {
                fallen8.Dispose();
            }
        }

        [DataTestMethod]
        [DataRow("BLS")]
        [DataRow("DIJKSTRA")]
        public void CancelledToken_StopsTheTraversalBeforeItStarts(String algorithm)
        {
            var (fallen8, ids) = Chain(6);
            var watch = Stopwatch.StartNew();

            using (var source = new CancellationTokenSource())
            {
                try
                {
                    source.Cancel();

                    var definition = Definition(ids[0], ids[5], 5, 1);
                    definition.CancellationToken = source.Token;

                    var found = fallen8.TryCalculateShortestPath(out var paths, algorithm, definition);

                    Assert.IsFalse(found, "A cancelled caller must not be served a result.");
                    Assert.IsTrue(definition.BudgetExhausted, "Cancellation must surface as budget exhaustion.");
                    Assert.AreEqual(0, (paths ?? new List<Path>()).Count, "no paths on cancellation");
                    Assert.IsTrue(watch.ElapsedMilliseconds < WallClockCeilingMilliseconds, "wall-clock ceiling");
                }
                finally
                {
                    fallen8.Dispose();
                }
            }
        }

        /// <summary>
        ///   The defect's real victim: a slow (not hostile) filter fragment. The SAME request finds
        ///   its path when unbudgeted and is stopped - with an honest exhaustion signal - under a
        ///   deadline it cannot meet.
        /// </summary>
        [TestMethod]
        public void SlowFilter_UnderATightDeadline_IsStopped_WhileTheUnbudgetedRunStillFindsThePath()
        {
            var (fallen8, ids) = Chain(6);
            var watch = Stopwatch.StartNew();

            try
            {
                Delegates.VertexFilter slowFilter = _ =>
                {
                    //a fragment that is merely expensive, not endless - exactly what a cooperative budget can contain
                    Thread.Sleep(20);
                    return true;
                };

                var unbudgeted = Definition(ids[0], ids[5], 5, 1);
                unbudgeted.VertexFilter = slowFilter;

                Assert.IsTrue(fallen8.TryCalculateShortestPath(out var reference, "BLS", unbudgeted),
                    "Without a budget the slow filter must still produce the path (nothing changed for unbudgeted callers).");
                Assert.IsFalse(unbudgeted.BudgetExhausted, "An unbudgeted run can never report exhaustion.");
                Assert.AreEqual(1, reference.Count, "the chain has exactly one path within the depth bound");

                var budgeted = Definition(ids[0], ids[5], 5, 1);
                budgeted.VertexFilter = slowFilter;
                budgeted.TimeBudget = TimeSpan.FromMilliseconds(1);

                Assert.IsFalse(fallen8.TryCalculateShortestPath(out var budgetedPaths, "BLS", budgeted),
                    "The deadline must stop the traversal.");
                Assert.IsTrue(budgeted.BudgetExhausted, "The stopped traversal must report exhaustion.");
                Assert.AreEqual(0, (budgetedPaths ?? new List<Path>()).Count, "no paths from an unfinished search");
                Assert.IsTrue(watch.ElapsedMilliseconds < WallClockCeilingMilliseconds, "wall-clock ceiling");
            }
            finally
            {
                fallen8.Dispose();
            }
        }

        #endregion

        #region engine: the unchanged default and the generous budget

        [DataTestMethod]
        [DataRow("BLS")]
        [DataRow("DIJKSTRA")]
        public void NoBudget_IsUnbounded_AndNeverReportsExhaustion(String algorithm)
        {
            var (fallen8, ids) = Diamond();

            try
            {
                var definition = Definition(ids[0], ids[3], 4, 8);

                Assert.IsTrue(fallen8.TryCalculateShortestPath(out var paths, algorithm, definition),
                    "A default definition names no budget, so it must behave exactly as before.");
                Assert.IsFalse(definition.BudgetExhausted, "An unbudgeted run never reports exhaustion.");
                Assert.IsTrue(paths.Count >= 2, "the diamond has two routes");
            }
            finally
            {
                fallen8.Dispose();
            }
        }

        [DataTestMethod]
        [DataRow("BLS")]
        [DataRow("DIJKSTRA")]
        public void GenerousBudget_ReturnsTheSameResultAsAnUnbudgetedRun(String algorithm)
        {
            var (fallen8, ids) = Diamond();

            using (var source = new CancellationTokenSource())
            {
                try
                {
                    var unbudgeted = Definition(ids[0], ids[3], 4, 8);
                    Assert.IsTrue(fallen8.TryCalculateShortestPath(out var reference, algorithm, unbudgeted),
                        "reference run");

                    var budgeted = Definition(ids[0], ids[3], 4, 8);
                    budgeted.TimeBudget = TimeSpan.FromSeconds(30);
                    budgeted.StepBudget = 1_000_000;
                    budgeted.CancellationToken = source.Token;

                    Assert.IsTrue(fallen8.TryCalculateShortestPath(out var budgetedPaths, algorithm, budgeted),
                        "A budget nobody exceeds must not change the outcome.");
                    Assert.IsFalse(budgeted.BudgetExhausted, "a generous budget must not trip");

                    CollectionAssert.AreEqual(Signatures(reference), Signatures(budgetedPaths),
                        "A generous budget must return the same paths, in the same order, as the unbudgeted run.");
                }
                finally
                {
                    fallen8.Dispose();
                }
            }
        }

        /// <summary>The verdict is per RUN: a reused definition must not stay "exhausted" forever.</summary>
        [DataTestMethod]
        [DataRow("BLS")]
        [DataRow("DIJKSTRA")]
        public void BudgetExhausted_IsResetAtTheStartOfEveryCalculation(String algorithm)
        {
            var (fallen8, ids) = Chain(6);

            try
            {
                var definition = Definition(ids[0], ids[5], 5, 1);
                definition.StepBudget = 1;

                Assert.IsFalse(fallen8.TryCalculateShortestPath(out _, algorithm, definition), "first run trips");
                Assert.IsTrue(definition.BudgetExhausted, "first run reports exhaustion");

                definition.StepBudget = 0;

                Assert.IsTrue(fallen8.TryCalculateShortestPath(out var paths, algorithm, definition),
                    "The same definition without a budget must run to completion.");
                Assert.IsFalse(definition.BudgetExhausted,
                    "The verdict describes ONE run, so the second run must clear the first run's exhaustion.");
                Assert.AreEqual(1, paths.Count, "the chain path");
            }
            finally
            {
                fallen8.Dispose();
            }
        }

        #endregion

        #region REST: 408 instead of a silent empty 200, and an unchanged default

        private static GraphController NewController(Fallen8 fallen8, CancellationToken requestAborted)
        {
            var controller = new GraphController(new UnitTestLogger<GraphController>(), fallen8);

            if (requestAborted.CanBeCanceled)
            {
                controller.ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { RequestAborted = requestAborted }
                };
            }

            return controller;
        }

        private static PathSpecification Specification(String algorithm, Double? timeBudgetSeconds)
        {
            return new PathSpecification
            {
                PathAlgorithmName = algorithm,
                MaxDepth = 5,
                MaxResults = 1,
                TimeBudgetSeconds = timeBudgetSeconds
            };
        }

        [TestMethod]
        public void Controller_WithoutABudget_StillReturnsThePath()
        {
            var (fallen8, ids) = Chain(6);
            var controller = NewController(fallen8, default);

            try
            {
                var result = controller.CalculateShortestPath(ids[0], ids[5], Specification("BLS", null)).Result;

                Assert.IsNotNull(result.Value, "The unbudgeted request must keep returning a 200 body.");
                Assert.AreEqual(1, result.Value.Count, "the chain path");
            }
            finally
            {
                fallen8.Dispose();
            }
        }

        [TestMethod]
        public void Controller_WithAGenerousBudget_StillReturnsThePath()
        {
            var (fallen8, ids) = Chain(6);
            var controller = NewController(fallen8, default);

            try
            {
                var result = controller.CalculateShortestPath(ids[0], ids[5], Specification("BLS", 30d)).Result;

                Assert.IsNotNull(result.Value, "A budget nobody exceeds must not change the response.");
                Assert.AreEqual(1, result.Value.Count, "the chain path");
            }
            finally
            {
                fallen8.Dispose();
            }
        }

        [TestMethod]
        public void Controller_WhenTheClientGaveUp_Answers408_NotAnEmpty200()
        {
            var (fallen8, ids) = Chain(6);
            var watch = Stopwatch.StartNew();

            using (var source = new CancellationTokenSource())
            {
                source.Cancel();
                var controller = NewController(fallen8, source.Token);

                try
                {
                    var result = controller.CalculateShortestPath(ids[0], ids[5], Specification("BLS", null)).Result;

                    Assert.IsNull(result.Value, "An aborted traversal must not report a 200 body.");
                    var problem = ProblemAssert.AssertProblem(result.Result, StatusCodes.Status408RequestTimeout);
                    Assert.IsNotNull(problem.Detail, "the 408 must explain what happened and how to retry");
                    Assert.IsTrue(watch.ElapsedMilliseconds < WallClockCeilingMilliseconds, "wall-clock ceiling");
                }
                finally
                {
                    fallen8.Dispose();
                }
            }
        }

        [TestMethod]
        public void Controller_WithADeadlineTheTraversalCannotMeet_Answers408()
        {
            var (fallen8, ids) = Chain(6);
            var watch = Stopwatch.StartNew();
            var controller = NewController(fallen8, default);

            try
            {
                // One tick (100 ns) of budget: building even a single frontier level outlives it, so
                // the level-boundary check stops the traversal deterministically.
                var result = controller.CalculateShortestPath(ids[0], ids[5], Specification("BLS", 0.0000001d)).Result;

                Assert.IsNull(result.Value, "A traversal stopped by its deadline must not report a 200 body.");
                ProblemAssert.AssertProblem(result.Result, StatusCodes.Status408RequestTimeout);
                Assert.IsTrue(watch.ElapsedMilliseconds < WallClockCeilingMilliseconds, "wall-clock ceiling");
            }
            finally
            {
                fallen8.Dispose();
            }
        }

        [DataTestMethod]
        [DataRow(0d)]
        [DataRow(-1d)]
        [DataRow(Double.NaN)]
        [DataRow(Double.PositiveInfinity)]
        [DataRow(3_601d)]
        public void Controller_WithAnUnusableTimeBudget_Answers400(Double timeBudgetSeconds)
        {
            var (fallen8, ids) = Chain(6);
            var controller = NewController(fallen8, default);

            try
            {
                var result = controller.CalculateShortestPath(
                    ids[0], ids[5], Specification("BLS", timeBudgetSeconds)).Result;

                Assert.IsNull(result.Value, "An unusable budget is a malformed request, not a 200.");
                ProblemAssert.AssertProblem(result.Result, StatusCodes.Status400BadRequest, "timeBudgetSeconds");
            }
            finally
            {
                fallen8.Dispose();
            }
        }

        [TestMethod]
        public void Controller_WithTheBudgetCeilingItself_IsAccepted()
        {
            var (fallen8, ids) = Chain(6);
            var controller = NewController(fallen8, default);

            try
            {
                var result = controller.CalculateShortestPath(
                    ids[0], ids[5], Specification("BLS", PathSpecification.MaxTimeBudgetSeconds)).Result;

                Assert.IsNotNull(result.Value, "The ceiling is inclusive.");
                Assert.AreEqual(1, result.Value.Count, "the chain path");
            }
            finally
            {
                fallen8.Dispose();
            }
        }

        #endregion
    }
}
