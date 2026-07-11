// MIT License
//
// SubGraphControllerTest.cs
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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.App.Controllers.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// End-to-end tests for <see cref="SubGraphController"/> against a real in-memory
    /// Fallen8 instance (controller → code generation → transaction → factory → algorithm).
    /// </summary>
    [TestClass]
    public class SubGraphControllerTest
    {
        private Fallen8 _fallen8;
        private SubGraphController _controller;

        [TestInitialize]
        public void TestInitialize()
        {
            var loggerFactory = TestLoggerFactory.Create();
            _fallen8 = new Fallen8(loggerFactory);
            _controller = new SubGraphController(loggerFactory.CreateLogger<SubGraphController>(), _fallen8);

            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());
            var verticesTx = new CreateVerticesTransaction();
            verticesTx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", "Alice" } });
            verticesTx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", "Bob" } });
            verticesTx.AddVertex(creationDate, "company", new Dictionary<string, object>() { { "name", "TechCorp" } });
            _fallen8.EnqueueTransaction(verticesTx).WaitUntilFinished();
            var v = verticesTx.GetCreatedVertices();

            var edgesTx = new CreateEdgesTransaction();
            edgesTx.AddEdge(v[0].Id, "knows", v[1].Id, creationDate, "knows");        // Alice -> Bob
            edgesTx.AddEdge(v[0].Id, "works_at", v[2].Id, creationDate, "works_at");   // Alice -> TechCorp
            _fallen8.EnqueueTransaction(edgesTx).WaitUntilFinished();
        }

        private static SubGraphSpecification PersonKnowsPerson(string name = "people")
        {
            return new SubGraphSpecification
            {
                Name = name,
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification { Type = "Vertex", PatternName = "p1", GraphElementFilter = "return (ge) => ge.Label == \"person\";" },
                    new PatternSpecification { Type = "Edge", PatternName = "knows", Direction = "OutgoingEdge", EdgePropertyFilter = "return (p) => p == \"knows\";" },
                    new PatternSpecification { Type = "Vertex", PatternName = "p2", GraphElementFilter = "return (ge) => ge.Label == \"person\";" }
                }
            };
        }

        private static SubGraphSpecification AllPersons(string name = "persons")
        {
            return new SubGraphSpecification
            {
                Name = name,
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification { Type = "Vertex", PatternName = "p", GraphElementFilter = "return (ge) => ge.Label == \"person\";" }
                }
            };
        }

        [TestMethod]
        public void Create_ValidSpecification_Returns201WithSummary()
        {
            var result = _controller.CreateSubGraph(PersonKnowsPerson());

            var created = result as CreatedResult;
            Assert.IsNotNull(created, "Expected a 201 Created result");
            Assert.AreEqual(StatusCodes.Status201Created, created.StatusCode);

            var summary = created.Value as SubGraphSummary;
            Assert.IsNotNull(summary, "Created result must carry a summary");
            Assert.AreEqual("people", summary.Name);
            Assert.AreEqual(2, summary.VertexCount, "Alice and Bob are on the knows path");
            Assert.AreEqual(1, summary.EdgeCount, "Only the Alice->Bob knows edge is kept");
            Assert.IsTrue(summary.CanRecalculate, "Algorithm-created subgraphs can be recalculated");
        }

        [TestMethod]
        public void Create_DuplicateName_Returns409()
        {
            Assert.IsInstanceOfType(_controller.CreateSubGraph(PersonKnowsPerson()), typeof(CreatedResult));

            var second = _controller.CreateSubGraph(PersonKnowsPerson());

            Assert.IsInstanceOfType(second, typeof(ConflictObjectResult), "Re-using a name must conflict");
        }

        [TestMethod]
        public void Create_NullSpecification_Returns400()
        {
            Assert.IsInstanceOfType(_controller.CreateSubGraph(null), typeof(BadRequestObjectResult));
        }

        [TestMethod]
        public void Create_MissingName_Returns400()
        {
            var spec = PersonKnowsPerson();
            spec.Name = "   ";
            Assert.IsInstanceOfType(_controller.CreateSubGraph(spec), typeof(BadRequestObjectResult));
        }

        [TestMethod]
        public void Create_InvalidFilterCode_Returns400WithDiagnostics()
        {
            var spec = AllPersons("bad");
            spec.Patterns[0].GraphElementFilter = "return (ge) => ge.Nope == 1;"; // will not compile

            var result = _controller.CreateSubGraph(spec);

            var badRequest = result as BadRequestObjectResult;
            Assert.IsNotNull(badRequest, "Uncompilable filter must yield 400, not 500");
            Assert.IsNotNull(badRequest.Value, "The compiler diagnostics should be returned");
        }

        [TestMethod]
        public void GetAllNames_ReturnsRegisteredNames()
        {
            _controller.CreateSubGraph(PersonKnowsPerson("a"));
            _controller.CreateSubGraph(AllPersons("b"));

            var ok = _controller.GetAllSubGraphNames() as OkObjectResult;
            Assert.IsNotNull(ok);
            var names = ((IEnumerable<string>)ok.Value).ToList();
            CollectionAssert.AreEquivalent(new[] { "a", "b" }, names);
        }

        [TestMethod]
        public void GetSubGraph_Existing_Returns200_Missing_Returns404()
        {
            _controller.CreateSubGraph(PersonKnowsPerson());

            var ok = _controller.GetSubGraph("people") as OkObjectResult;
            Assert.IsNotNull(ok, "Existing subgraph should return 200");
            Assert.AreEqual("people", ((SubGraphSummary)ok.Value).Name);

            Assert.IsInstanceOfType(_controller.GetSubGraph("does-not-exist"), typeof(NotFoundObjectResult));
        }

        [TestMethod]
        public void GetSubGraphContents_ReturnsVerticesAndEdges()
        {
            _controller.CreateSubGraph(PersonKnowsPerson());

            var ok = _controller.GetSubGraphContents("people") as OkObjectResult;
            Assert.IsNotNull(ok);
            var graph = ok.Value as Graph;
            Assert.IsNotNull(graph);
            Assert.AreEqual(2, graph.Vertices.Count, "Alice and Bob");
            Assert.AreEqual(1, graph.Edges.Count, "The knows edge");
        }

        [TestMethod]
        public void GetSubGraphContents_Missing_Returns404()
        {
            Assert.IsInstanceOfType(_controller.GetSubGraphContents("nope"), typeof(NotFoundObjectResult));
        }

        [TestMethod]
        public void Recalculate_ReflectsSourceChanges()
        {
            _controller.CreateSubGraph(AllPersons());

            var before = (SubGraphSummary)((OkObjectResult)_controller.GetSubGraph("persons")).Value;
            Assert.AreEqual(2, before.VertexCount, "Alice and Bob");

            // Add another person to the source graph.
            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());
            var tx = new CreateVerticesTransaction();
            tx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", "Carol" } });
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();

            var recalc = _controller.RecalculateSubGraph("persons") as OkObjectResult;
            Assert.IsNotNull(recalc, "Recalculation of an algorithm-created subgraph should succeed");
            Assert.AreEqual(3, ((SubGraphSummary)recalc.Value).VertexCount, "Carol should now be included");
        }

        [TestMethod]
        public void Recalculate_Missing_Returns404()
        {
            Assert.IsInstanceOfType(_controller.RecalculateSubGraph("nope"), typeof(NotFoundObjectResult));
        }

        [TestMethod]
        public void Delete_Existing_Returns204_ThenGoneAnd404OnSecondDelete()
        {
            _controller.CreateSubGraph(PersonKnowsPerson());

            Assert.IsInstanceOfType(_controller.DeleteSubGraph("people"), typeof(NoContentResult));
            Assert.IsInstanceOfType(_controller.GetSubGraph("people"), typeof(NotFoundObjectResult),
                "After deletion the subgraph must be gone");
            Assert.IsInstanceOfType(_controller.DeleteSubGraph("people"), typeof(NotFoundObjectResult),
                "Deleting a non-existent subgraph must be 404");
        }

        [TestMethod]
        public void Create_DoesNotMutateSourceGraph()
        {
            var sourceVertexCount = _fallen8.VertexCount;
            var sourceEdgeCount = _fallen8.EdgeCount;

            _controller.CreateSubGraph(PersonKnowsPerson());

            Assert.AreEqual(sourceVertexCount, _fallen8.VertexCount, "Source graph vertices must be unchanged");
            Assert.AreEqual(sourceEdgeCount, _fallen8.EdgeCount, "Source graph edges must be unchanged");
        }
    }
}
