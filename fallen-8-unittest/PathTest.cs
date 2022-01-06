using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Tests.Helper;
using System;
using System.Collections.Generic;
using System.Linq;

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
            Literal = new LiteralSpecification() { Value = "Alice", FullQualifiedTypeName = "System.String" } , 
            Operator = BinaryOperator.Equals,  ResultType =  ResultTypeSpecification.Vertices 
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
