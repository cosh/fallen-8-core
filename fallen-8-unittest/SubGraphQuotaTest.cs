// MIT License
//
// SubGraphQuotaTest.cs
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
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using NoSQL.GraphDB.Core.SubGraph;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Tests for the subgraph resource quotas enforced by <see cref="SubGraphFactory"/>.
    /// </summary>
    [TestClass]
    public class SubGraphQuotaTest
    {
        private static Fallen8 CreatePeopleGraph()
        {
            var fallen8 = new Fallen8(TestLoggerFactory.Create());
            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());
            var tx = new CreateVerticesTransaction();
            tx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", "Alice" } });
            tx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", "Bob" } });
            tx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", "Carol" } });
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return fallen8;
        }

        private static SubGraphDefinition AllPersons(string name)
        {
            return new SubGraphDefinition
            {
                Name = name,
                Pattern = new List<APattern>
                {
                    new VertexPattern { PatternName = "p", Vertex = v => v.Label == "person" }
                }
            };
        }

        [TestMethod]
        public void MaxSubGraphCount_RejectsBeyondLimit_AndRegistersNothingExtra()
        {
            var fallen8 = CreatePeopleGraph();
            fallen8.SubGraphFactory.Quota = new SubGraphQuota { MaxSubGraphCount = 1 };

            Assert.IsTrue(fallen8.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out _, "first", AllPersons("first")), "first create is within the limit");

            var second = fallen8.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out var secondResult, "second", AllPersons("second"));

            Assert.IsFalse(second, "second create exceeds MaxSubGraphCount");
            Assert.IsNull(secondResult, "no result on a quota breach");
            Assert.AreEqual(1, fallen8.SubGraphFactory.SubGraphCount, "only the first subgraph is registered");
        }

        [TestMethod]
        public void MaxElementsPerSubGraph_RejectsOversizedSubgraph_AndLeavesSourceUnchanged()
        {
            var fallen8 = CreatePeopleGraph();
            var sourceVertexCount = fallen8.VertexCount;
            // The subgraph would have 3 person vertices; cap it at 2.
            fallen8.SubGraphFactory.Quota = new SubGraphQuota { MaxElementsPerSubGraph = 2 };

            var created = fallen8.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out var result, "big", AllPersons("big"));

            Assert.IsFalse(created, "a 3-element subgraph exceeds the per-subgraph limit of 2");
            Assert.IsNull(result);
            Assert.AreEqual(0, fallen8.SubGraphFactory.SubGraphCount, "nothing registered");
            Assert.AreEqual(sourceVertexCount, fallen8.VertexCount, "the source graph is untouched");
        }

        [TestMethod]
        public void MaxElementsPerSubGraph_AllowsSubgraphAtLimit()
        {
            var fallen8 = CreatePeopleGraph();
            fallen8.SubGraphFactory.Quota = new SubGraphQuota { MaxElementsPerSubGraph = 3 };

            Assert.IsTrue(fallen8.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out var result, "exact", AllPersons("exact")), "3 elements is within a limit of 3");
            Assert.AreEqual(3, result.SubGraph.VertexCount);
        }

        [TestMethod]
        public void MaxTotalElements_RejectsWhenAggregateWouldBeExceeded()
        {
            var fallen8 = CreatePeopleGraph();
            // Each all-persons subgraph materializes 3 elements. Allow 5 total: the first
            // (3) fits, the second (would make 6) is rejected.
            fallen8.SubGraphFactory.Quota = new SubGraphQuota { MaxTotalElements = 5 };

            Assert.IsTrue(fallen8.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out _, "a", AllPersons("a")));
            Assert.IsFalse(fallen8.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out var b, "b", AllPersons("b")), "second subgraph would exceed the total element limit");
            Assert.IsNull(b);
            Assert.AreEqual(1, fallen8.SubGraphFactory.SubGraphCount);
        }

        [TestMethod]
        public void DefaultQuota_IsGenerousEnoughForOrdinaryUse()
        {
            var fallen8 = CreatePeopleGraph();

            // The default quota (M6) is bounded but generous, so ordinary use - here, many small
            // subgraphs - is unaffected by the ceiling.
            for (int i = 0; i < 10; i++)
            {
                Assert.IsTrue(fallen8.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                    out _, "sg" + i, AllPersons("sg" + i)), "generous default does not block ordinary use");
            }

            Assert.AreEqual(10, fallen8.SubGraphFactory.SubGraphCount);
        }

        [TestMethod]
        public void DefaultQuota_HasDocumentedGenerousButBoundedValues()
        {
            // Pin the shipped default (M6): non-unlimited (not Int32.MaxValue) yet generous.
            var quota = new SubGraphQuota();

            Assert.AreEqual(SubGraphQuota.DefaultMaxSubGraphCount, quota.MaxSubGraphCount);
            Assert.AreEqual(SubGraphQuota.DefaultMaxElementsPerSubGraph, quota.MaxElementsPerSubGraph);
            Assert.AreEqual(SubGraphQuota.DefaultMaxTotalElements, quota.MaxTotalElements);

            Assert.AreNotEqual(int.MaxValue, quota.MaxSubGraphCount, "the default must be bounded, not unlimited");
            Assert.AreNotEqual(int.MaxValue, quota.MaxElementsPerSubGraph, "the default must be bounded, not unlimited");
            Assert.AreNotEqual(int.MaxValue, quota.MaxTotalElements, "the default must be bounded, not unlimited");

            // A factory with no explicit quota reports the same bounded default.
            var fallen8 = CreatePeopleGraph();
            Assert.AreEqual(SubGraphQuota.DefaultMaxSubGraphCount, fallen8.SubGraphFactory.Quota.MaxSubGraphCount);
        }

        [TestMethod]
        public void Controller_CountLimitReached_Returns409()
        {
            var fallen8 = CreatePeopleGraph();
            fallen8.SubGraphFactory.Quota = new SubGraphQuota { MaxSubGraphCount = 1 };
            var controller = new SubGraphController(TestLoggerFactory.Create().CreateLogger<SubGraphController>(), fallen8);

            var spec1 = new SubGraphSpecification
            {
                Name = "one",
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification { Type = "Vertex", PatternName = "p", VertexFilter = "return (v) => v.Label == \"person\";" }
                }
            };
            var spec2 = new SubGraphSpecification
            {
                Name = "two",
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification { Type = "Vertex", PatternName = "p", VertexFilter = "return (v) => v.Label == \"person\";" }
                }
            };

            Assert.IsInstanceOfType(controller.CreateSubGraph(spec1).Result, typeof(CreatedResult));
            Assert.IsInstanceOfType(controller.CreateSubGraph(spec2).Result, typeof(ConflictObjectResult),
                "creating beyond the count quota returns 409");
        }
    }
}
