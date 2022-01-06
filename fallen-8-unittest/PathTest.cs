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

using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Tests.Helper;

namespace NoSQL.GraphDB.Tests
{
    [TestClass]
    public class PathTest
    {
        private readonly Fallen8 _fallen8 = new Fallen8();

        private readonly GraphController _controller;

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
            TestGraphGenerator.GenerateSampleGraph(_fallen8);

            var serviceProvider = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider();

            var factory = serviceProvider.GetService<ILoggerFactory>();

            var logger = factory.CreateLogger<GraphController>();

            _controller = new GraphController(logger, _fallen8);
        }


        [TestMethod]
        public void FindPathToTrent()
        {
            var trent = _controller.GraphScan(NAME, TRENTSPEC).First();
            var mallory = _controller.GraphScan(NAME, MALLORYSPEC).First();

            var result = _controller.GetPaths(mallory, trent, null);

            Assert.AreEqual(2, result.Count);
        }
    }
}
