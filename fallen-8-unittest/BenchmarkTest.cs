// MIT License
//
// BenchmarkTest.cs
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
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers.Benchmark;
using NoSQL.GraphDB.App.Controllers.Sample;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    [TestClass]
    public class BenchmarkTest
    {
        [TestMethod]
        public void ScaleFreeNetwork_ShouldCreateExpectedGraph()
        {
            // Arrange - Create a new isolated instance for this test
            var loggerFactory = TestLoggerFactory.Create();
            var fallen8 = new Fallen8(loggerFactory);
            var benchmark = new ScaleFreeNetwork(fallen8);

            // Act
            var generated = benchmark.CreateScaleFreeNetworkAsync(1000, 10).Result;
            var benchRan = benchmark.TryBench(out var result, out var message, 10);

            // Assert
            Assert.AreEqual(1000, fallen8.VertexCount, "Expected 1000 vertices in the scale free network");
            Assert.AreEqual(10000, fallen8.EdgeCount, "Expected 10000 edges in the scale free network");

            // The reported counts are what was written, not an estimate: they must agree with the
            // engine's own totals. (The namespace is stamped by the controller, so it is null here -
            // this builder knows only a graph.)
            Assert.AreEqual(1000, generated.VerticesCreated);
            Assert.AreEqual(10000L, generated.EdgesCreated);
            Assert.AreEqual(fallen8.VertexCount, generated.VertexCountAfter);
            Assert.AreEqual(fallen8.EdgeCount, generated.EdgeCountAfter);
            Assert.AreEqual("uniform", generated.Distribution);
            Assert.IsTrue(generated.ElapsedMilliseconds > 0, "Generating 10k edges takes measurable time");
            Assert.IsNull(generated.Namespace, "The graph builder must not invent a namespace");
            Assert.IsTrue(benchRan, "Benchmark should run on a populated graph: " + message);
            Assert.AreEqual(10, result.Iterations);
            Assert.IsTrue(result.EdgesTraversed > 0, "The traversal must count edges");
            Assert.IsTrue(result.AverageTps > 0, "Average TPS must be positive");
        }

        [TestMethod]
        public void Bench_FollowsEveryOutEdge_RegardlessOfSchema()
        {
            // Regression (feature schema-agnostic-benchmark): the benchmark used to count only
            // out-edges whose edge-property-id was "A" (what the generator writes), so on a graph
            // built with any other schema it reported ZERO edges traversed. It must now follow
            // EVERY out-edge, across every edge-property group, on whatever graph is loaded.
            var loggerFactory = TestLoggerFactory.Create();
            var fallen8 = new Fallen8(loggerFactory);
            const uint creationDate = 1u;

            // Five vertices, four out-edges, NONE labelled "A", split across two distinct edge-
            // property groups ("KNOWS", "LIKES") to prove all groups are summed. v3 and v4 are
            // sinks (no out-edges) so the traversal must tolerate vertices with empty adjacency.
            var vertexTx = new CreateVerticesTransaction();
            for (var i = 0; i < 5; i++)
            {
                vertexTx.AddVertex(creationDate);
            }
            fallen8.EnqueueTransaction(vertexTx).WaitUntilFinished();
            var vertices = vertexTx.GetCreatedVertices();

            var edgeTx = new CreateEdgesTransaction();
            edgeTx.AddEdge(vertices[0].Id, "KNOWS", vertices[1].Id, creationDate);
            edgeTx.AddEdge(vertices[0].Id, "KNOWS", vertices[2].Id, creationDate);
            edgeTx.AddEdge(vertices[1].Id, "LIKES", vertices[2].Id, creationDate);
            edgeTx.AddEdge(vertices[2].Id, "KNOWS", vertices[3].Id, creationDate);
            fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();

            Assert.AreEqual(4, fallen8.EdgeCount, "Precondition: four edges, none of them property 'A'.");

            var benchmark = new ScaleFreeNetwork(fallen8);
            var benchRan = benchmark.TryBench(out var result, out var message, 5);

            Assert.IsTrue(benchRan, "Benchmark should run on a populated graph: " + message);
            // The whole point of the fix: every out-edge is followed regardless of its label, so
            // the count equals the total edge count (4), not the zero the old "A"-only filter gave.
            Assert.AreEqual(4L, result.EdgesTraversed,
                "The benchmark must follow every out-edge ('KNOWS' and 'LIKES'), not only property 'A'.");
            Assert.IsTrue(result.AverageTps > 0, "Average TPS must be positive when edges are traversed.");
        }
    }
}
