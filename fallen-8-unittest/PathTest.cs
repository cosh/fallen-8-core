// MIT License
//
// PathTest.cs
//
// Copyright (c) 2021 Henning Rauch
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
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Controllers.Sample;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Expression;

namespace NoSQL.GraphDB.Tests
{
    [TestClass]
    public class PathTest
    {
        private readonly Fallen8 _fallen8;

        private readonly GraphController _controller;

        private readonly ILoggerFactory _loggerFactory;

        private readonly ushort NAME = 0;

        private ScanSpecification ALICESPEC = new ScanSpecification()
        {
            Literal = new LiteralSpecification() { Value = "Alice", FullQualifiedTypeName = "System.String" },
            Operator = BinaryOperator.Equals,
            ResultType = ResultTypeSpecification.Vertices
        };

        private ScanSpecification BOBSPEC = new ScanSpecification()
        {
            Literal = new LiteralSpecification() { Value = "Bob", FullQualifiedTypeName = "System.String" },
            Operator = BinaryOperator.Equals,
            ResultType = ResultTypeSpecification.Vertices
        };

        private ScanSpecification MALLORYSPEC = new ScanSpecification()
        {
            Literal = new LiteralSpecification() { Value = "Mallory", FullQualifiedTypeName = "System.String" },
            Operator = BinaryOperator.Equals,
            ResultType = ResultTypeSpecification.Vertices
        };

        private ScanSpecification TRENTSPEC = new ScanSpecification()
        {
            Literal = new LiteralSpecification() { Value = "Trent", FullQualifiedTypeName = "System.String" },
            Operator = BinaryOperator.Equals,
            ResultType = ResultTypeSpecification.Vertices
        };

        public PathTest()
        {
            _loggerFactory = LoggerFactory.Create(builder =>
            {
                builder
                    .AddFilter("Microsoft", LogLevel.Warning)
                    .AddFilter("System", LogLevel.Warning)
                    .AddFilter("NoSQL.GraphDB", LogLevel.Debug)
                    .AddConsole();
            });

            _fallen8 = new Fallen8(_loggerFactory);

            TestGraphGenerator.GenerateSampleGraph(_fallen8);

            _controller = new GraphController(new UnitTestLogger<GraphController>(), _fallen8);
        }


        [TestMethod]
        public void FindPathToTrent()
        {
            var trent = _controller.GraphScan(NAME, TRENTSPEC).First();
            var mallory = _controller.GraphScan(NAME, MALLORYSPEC).First();

            var result = _controller.GetPaths(mallory, trent, null);

            Assert.AreEqual(2, result.Count);

            //testing it a second time to see if the cache works
            result = _controller.GetPaths(mallory, trent, null);

            Assert.AreEqual(2, result.Count);
        }

        [TestMethod]
        public void FindAliceToBob()
        {
            var bob = _controller.GraphScan(NAME, BOBSPEC).First();
            var alice = _controller.GraphScan(NAME, ALICESPEC).First();

            PathSpecification pathSpec = new PathSpecification() { MaxDepth = 2, MaxResults = 2 };

            var result = _controller.GetPaths(alice, bob, pathSpec);

            Assert.AreEqual(1, result.Count);

            //testing it a second time to see if the cache works
            result = _controller.GetPaths(bob, alice, pathSpec);

            Assert.AreEqual(1, result.Count);
        }

        [TestMethod]
        public void MultiHopPaths()
        {
            Fallen8 f8 = new Fallen8(_loggerFactory);

            TestGraphGenerator.GenerateAbcGraph(f8);

            List<Path> paths;
            f8.CalculateShortestPath(out paths, "BLS", 0, 20, maxDepth: 26, maxResults: 2);

            Assert.AreEqual(1, paths.Count);
        }

        [TestMethod]
        public void MultiHopPaths_generic()
        {
            Fallen8 f8 = new Fallen8(_loggerFactory);

            TestGraphGenerator.GenerateAbcGraph(f8);

            List<Path> paths;
            f8.CalculateShortestPath<BidirectionalLevelSynchronousSSSP> (out paths, 0, 20, maxDepth: 26, maxResults: 2);

            Assert.AreEqual(1, paths.Count);

            //test the cache
            f8.CalculateShortestPath<BidirectionalLevelSynchronousSSSP>(out paths, 0, 20, maxDepth: 26, maxResults: 2);
            Assert.AreEqual(1, paths.Count);
        }
    }
}
