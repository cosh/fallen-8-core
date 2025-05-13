// MIT License
//
// PathTestEdgeCases.cs
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
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Controllers.Sample;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    [TestClass]
    public class PathTestEdgeCases
    {
        private ILoggerFactory CreateLoggerFactory()
        {
            return LoggerFactory.Create(builder =>
            {
                builder
                    .AddFilter("Microsoft", LogLevel.Warning)
                    .AddFilter("System", LogLevel.Warning)
                    .AddFilter("NoSQL.GraphDB", LogLevel.Debug)
                    .AddConsole();
            });
        }

        [TestMethod]
        public void GetPaths_NullSpecification_ShouldNotThrowAndReturnPathsOrEmpty()
        {
            var loggerFactory = CreateLoggerFactory();
            var fallen8 = new Fallen8(loggerFactory);
            TestGraphGenerator.GenerateSampleGraph(fallen8);
            var controller = new GraphController(new UnitTestLogger<GraphController>(), fallen8);

            // Use Alice and Bob from the sample graph
            var alice = controller.GraphScan("name", new ScanSpecification
            {
                Literal = new LiteralSpecification { Value = "Alice", FullQualifiedTypeName = "System.String" },
                Operator = BinaryOperator.Equals,
                ResultType = ResultTypeSpecification.Vertices
            }).First();
            var bob = controller.GraphScan("name", new ScanSpecification
            {
                Literal = new LiteralSpecification { Value = "Bob", FullQualifiedTypeName = "System.String" },
                Operator = BinaryOperator.Equals,
                ResultType = ResultTypeSpecification.Vertices
            }).First();

            // Act
            var result = controller.GetPaths(alice, bob, null);

            // Assert
            Assert.IsNotNull(result, "Result should not be null");
        }

        [TestMethod]
        public void GetPaths_MaxDepthZero_ShouldReturnEmptyList()
        {
            var loggerFactory = CreateLoggerFactory();
            var fallen8 = new Fallen8(loggerFactory);
            TestGraphGenerator.GenerateSampleGraph(fallen8);
            var controller = new GraphController(new UnitTestLogger<GraphController>(), fallen8);

            var alice = controller.GraphScan("name", new ScanSpecification
            {
                Literal = new LiteralSpecification { Value = "Alice", FullQualifiedTypeName = "System.String" },
                Operator = BinaryOperator.Equals,
                ResultType = ResultTypeSpecification.Vertices
            }).First();
            var bob = controller.GraphScan("name", new ScanSpecification
            {
                Literal = new LiteralSpecification { Value = "Bob", FullQualifiedTypeName = "System.String" },
                Operator = BinaryOperator.Equals,
                ResultType = ResultTypeSpecification.Vertices
            }).First();

            var spec = new PathSpecification { MaxDepth = 0, MaxResults = 10 };
            var result = controller.GetPaths(alice, bob, spec);
            Assert.IsNotNull(result, "Result should not be null");
            Assert.AreEqual(0, result.Count, "No path should be found with MaxDepth=0");
        }

        [TestMethod]
        public void GetPaths_NonexistentVertices_ShouldReturnEmptyList()
        {
            var loggerFactory = CreateLoggerFactory();
            var fallen8 = new Fallen8(loggerFactory);
            TestGraphGenerator.GenerateSampleGraph(fallen8);
            var controller = new GraphController(new UnitTestLogger<GraphController>(), fallen8);

            // Use IDs that do not exist in the sample graph
            int nonexistentFrom = 9999;
            int nonexistentTo = 8888;
            var spec = new PathSpecification { MaxDepth = 5, MaxResults = 10 };
            var result = controller.GetPaths(nonexistentFrom, nonexistentTo, spec);
            Assert.IsNotNull(result, "Result should not be null");
            Assert.AreEqual(0, result.Count, "No path should be found between nonexistent vertices");
        }

        [TestMethod]
        public void GetPaths_NoPossiblePath_ShouldReturnEmptyList()
        {
            var loggerFactory = CreateLoggerFactory();
            var fallen8 = new Fallen8(loggerFactory);
            // Create two disconnected subgraphs
            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());
            var vertexTx1 = new CreateVerticesTransaction();
            vertexTx1.AddVertex(creationDate, "subgraph1", new System.Collections.Generic.Dictionary<string, object> { { "name", "A" } });
            vertexTx1.AddVertex(creationDate, "subgraph1", new System.Collections.Generic.Dictionary<string, object> { { "name", "B" } });
            fallen8.EnqueueTransaction(vertexTx1).WaitUntilFinished();
            var vertexTx2 = new CreateVerticesTransaction();
            vertexTx2.AddVertex(creationDate, "subgraph2", new System.Collections.Generic.Dictionary<string, object> { { "name", "X" } });
            vertexTx2.AddVertex(creationDate, "subgraph2", new System.Collections.Generic.Dictionary<string, object> { { "name", "Y" } });
            fallen8.EnqueueTransaction(vertexTx2).WaitUntilFinished();
            var controller = new GraphController(new UnitTestLogger<GraphController>(), fallen8);
            // Get IDs
            var a = controller.GraphScan("name", new ScanSpecification
            {
                Literal = new LiteralSpecification { Value = "A", FullQualifiedTypeName = "System.String" },
                Operator = BinaryOperator.Equals,
                ResultType = ResultTypeSpecification.Vertices
            }).First();
            var x = controller.GraphScan("name", new ScanSpecification
            {
                Literal = new LiteralSpecification { Value = "X", FullQualifiedTypeName = "System.String" },
                Operator = BinaryOperator.Equals,
                ResultType = ResultTypeSpecification.Vertices
            }).First();
            var spec = new PathSpecification { MaxDepth = 5, MaxResults = 10 };
            var result = controller.GetPaths(a, x, spec);
            Assert.IsNotNull(result, "Result should not be null");
            Assert.AreEqual(0, result.Count, "No path should be found between disconnected subgraphs");
        }
    }
}
