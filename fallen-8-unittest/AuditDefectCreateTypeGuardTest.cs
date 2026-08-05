// MIT License
//
// AuditDefectCreateTypeGuardTest.cs
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
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Audit defect B19: the four element-create routes (<c>PUT /vertex</c>, <c>PUT /vertices</c>,
    ///   <c>PUT /edge</c>, <c>PUT /edges</c>) converted caller-supplied property types without a
    ///   guard, so an unknown type name or an unconvertible value escaped as a 500 although every
    ///   one of them documents a 400 for an invalid specification. These tests pin the 400, pin that
    ///   a rejected request writes nothing (the guard runs before the transaction is enqueued), and
    ///   pin the valid conversions the guard must keep letting through.
    /// </summary>
    [TestClass]
    public class AuditDefectCreateTypeGuardTest
    {
        private Fallen8 _fallen8;
        private GraphController _controller;

        [TestInitialize]
        public void TestInitialize()
        {
            var loggerFactory = TestLoggerFactory.Create();
            _fallen8 = new Fallen8(loggerFactory);
            _controller = new GraphController(loggerFactory.CreateLogger<GraphController>(), _fallen8);
        }

        private static PropertySpecification Property(String id, String type, String value)
        {
            return new PropertySpecification
            {
                PropertyId = id,
                FullQualifiedTypeName = type,
                PropertyValue = value
            };
        }

        private static VertexSpecification VertexSpec(params PropertySpecification[] properties)
        {
            return new VertexSpecification
            {
                Label = "person",
                CreationDate = 0,
                Properties = properties.ToList()
            };
        }

        private static EdgeSpecification EdgeSpec(Int32 source, Int32 target, params PropertySpecification[] properties)
        {
            return new EdgeSpecification
            {
                Label = "friendship",
                EdgePropertyId = "knows",
                CreationDate = 0,
                SourceVertex = source,
                TargetVertex = target,
                Properties = properties.ToList()
            };
        }

        /// <summary>Two bare vertices to hang an edge on, created straight through the engine.</summary>
        private (Int32 source, Int32 target) TwoVertices()
        {
            var tx = new CreateVerticesTransaction();
            tx.AddVertex(1u, "person");
            tx.AddVertex(1u, "person");
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            var created = tx.GetCreatedVertices();
            return (created[0].Id, created[1].Id);
        }

        private String StoredValue(Int32 vertexId, String propertyId)
        {
            return _controller.GetVertex(vertexId).Properties.Single(p => p.PropertyId == propertyId).PropertyValue;
        }

        private String StoredType(Int32 vertexId, String propertyId)
        {
            return _controller.GetVertex(vertexId).Properties.Single(p => p.PropertyId == propertyId).FullQualifiedTypeName;
        }

        // ---- PUT /vertex ---------------------------------------------------------------------------

        [TestMethod]
        public async Task AddVertex_WithAnUnknownTypeName_Returns400_AndWritesNothing()
        {
            var result = await _controller.AddVertex(
                VertexSpec(Property("name", "Not.A.Real.Type", "John Doe")), waitForCompletion: true);

            var problem = ProblemAssert.AssertProblem(result, StatusCodes.Status400BadRequest, "Not.A.Real.Type");
            StringAssert.Contains(problem.Detail, "name", "the problem detail must name the offending property");
            Assert.AreEqual(0, _fallen8.GetAllVertices().Count, "a rejected create must not write a vertex");
        }

        [TestMethod]
        public async Task AddVertex_WithAnUnconvertibleValue_Returns400_AndWritesNothing()
        {
            var result = await _controller.AddVertex(
                VertexSpec(Property("age", "System.Int32", "abc")), waitForCompletion: true);

            ProblemAssert.AssertProblem(result, StatusCodes.Status400BadRequest, "age");
            Assert.AreEqual(0, _fallen8.GetAllVertices().Count, "a rejected create must not write a vertex");
        }

        [TestMethod]
        public async Task AddVertex_WithTheDefaultTypeName_AndANonNumericValue_Returns400()
        {
            // The plainest client mistake: only propertyValue is sent, so fullQualifiedTypeName keeps
            // its "System.Int32" default and the text cannot be parsed. This was the 500.
            var result = await _controller.AddVertex(
                VertexSpec(new PropertySpecification { PropertyId = "name", PropertyValue = "John Doe" }),
                waitForCompletion: true);

            ProblemAssert.AssertProblem(result, StatusCodes.Status400BadRequest, "System.Int32");
            Assert.AreEqual(0, _fallen8.GetAllVertices().Count);
        }

        [TestMethod]
        public async Task AddVertex_WithAnOutOfRangeValue_Returns400()
        {
            // OverflowException, a different throw than the FormatException above, must map to the
            // same 400 rather than slipping past the catch set into a 500.
            var result = await _controller.AddVertex(
                VertexSpec(Property("small", "System.Byte", "999")), waitForCompletion: true);

            ProblemAssert.AssertProblem(result, StatusCodes.Status400BadRequest, "small");
            Assert.AreEqual(0, _fallen8.GetAllVertices().Count);
        }

        [TestMethod]
        public async Task AddVertex_WithANullPropertyEntry_Returns400_AndWritesNothing()
        {
            var result = await _controller.AddVertex(
                new VertexSpecification
                {
                    Label = "person",
                    CreationDate = 0,
                    Properties = new List<PropertySpecification> { null }
                },
                waitForCompletion: true);

            ProblemAssert.AssertProblem(result, StatusCodes.Status400BadRequest);
            Assert.AreEqual(0, _fallen8.GetAllVertices().Count);
        }

        [TestMethod]
        public async Task AddVertex_ReportsTheOffendingProperty_NotTheFirstOne()
        {
            var result = await _controller.AddVertex(
                VertexSpec(
                    Property("name", "System.String", "Alice"),
                    Property("weight", "System.Double", "heavy")),
                waitForCompletion: true);

            var problem = ProblemAssert.AssertProblem(result, StatusCodes.Status400BadRequest, "weight");
            Assert.IsFalse(problem.Detail.Contains("'name'"),
                "the detail must point at the property that failed, not at a valid one");
        }

        [TestMethod]
        public async Task AddVertex_WithValidProperties_Still202_AndRoundTrips()
        {
            var result = await _controller.AddVertex(
                VertexSpec(
                    Property("name", "System.String", "Alice"),
                    Property("age", "System.Int32", "30"),
                    Property("weight", "System.Double", "0.5")),
                waitForCompletion: true);

            Assert.IsInstanceOfType(result, typeof(AcceptedResult), "a valid create is still 202 Accepted");
            var id = _fallen8.GetAllVertices().Single().Id;
            Assert.AreEqual("Alice", StoredValue(id, "name"));
            Assert.AreEqual("30", StoredValue(id, "age"));
            Assert.AreEqual("System.Int32", StoredType(id, "age"), "the guard must still convert, not store the raw text");
            // Invariant parse preserved (feature property-ingestion-culture): 0.5, never 5.
            Assert.AreEqual("0.5", StoredValue(id, "weight"));
        }

        [TestMethod]
        public async Task AddVertex_WithNoProperties_Still202()
        {
            var withNullList = await _controller.AddVertex(
                new VertexSpecification { Label = "person", CreationDate = 0, Properties = null },
                waitForCompletion: true);
            Assert.IsInstanceOfType(withNullList, typeof(AcceptedResult), "an absent property list is valid");

            var withEmptyList = await _controller.AddVertex(VertexSpec(), waitForCompletion: true);
            Assert.IsInstanceOfType(withEmptyList, typeof(AcceptedResult), "an empty property list is valid");

            Assert.AreEqual(2, _fallen8.GetAllVertices().Count);
            Assert.AreEqual(0, _controller.GetVertex(_fallen8.GetAllVertices()[0].Id).Properties.Count);
        }

        [TestMethod]
        public async Task AddVertex_WithNoTypeName_StoresTheRawValue()
        {
            // The documented pass-through: no type name means "store the wire text as it came".
            var result = await _controller.AddVertex(
                VertexSpec(Property("raw", null, "42")), waitForCompletion: true);

            Assert.IsInstanceOfType(result, typeof(AcceptedResult));
            var id = _fallen8.GetAllVertices().Single().Id;
            Assert.AreEqual("42", StoredValue(id, "raw"));
            Assert.AreEqual("System.String", StoredType(id, "raw"), "no type name must not silently coerce to Int32");
        }

        // ---- PUT /vertices -------------------------------------------------------------------------

        [TestMethod]
        public async Task AddVertices_WithABadPropertyInALaterSpec_Returns400_AndWritesNothing()
        {
            var result = await _controller.AddVertices(
                new List<VertexSpecification>
                {
                    VertexSpec(Property("name", "System.String", "Alice")),
                    VertexSpec(Property("age", "System.Int32", "abc"))
                },
                waitForCompletion: true);

            ProblemAssert.AssertProblem(result, StatusCodes.Status400BadRequest, "age");
            Assert.AreEqual(0, _fallen8.GetAllVertices().Count,
                "the batch is rejected before it is enqueued, so the valid leading spec is not written either");
        }

        [TestMethod]
        public async Task AddVertices_WithValidProperties_ReturnsTheAssignedIds()
        {
            var result = await _controller.AddVertices(
                new List<VertexSpecification>
                {
                    VertexSpec(Property("age", "System.Int32", "30")),
                    VertexSpec(Property("age", "System.Int32", "31"))
                },
                waitForCompletion: true);

            var ok = result as OkObjectResult;
            Assert.IsNotNull(ok, "a valid waited-on batch returns 200 with the assigned ids");
            Assert.AreEqual(2, ((IEnumerable<Int32>)ok.Value).Count());
            Assert.AreEqual(2, _fallen8.GetAllVertices().Count);
        }

        // ---- PUT /edge -----------------------------------------------------------------------------

        [TestMethod]
        public async Task AddEdge_WithAnUnknownTypeName_Returns400_AndWritesNoEdge()
        {
            var (source, target) = TwoVertices();

            var result = await _controller.AddEdge(
                EdgeSpec(source, target, Property("since", "Not.A.Real.Type", "2024-01-01")),
                waitForCompletion: true);

            ProblemAssert.AssertProblem(result, StatusCodes.Status400BadRequest, "Not.A.Real.Type");
            Assert.AreEqual(0, _fallen8.GetAllEdges().Count, "a rejected create must not wire an edge");
            Assert.AreEqual(2, _fallen8.GetAllVertices().Count, "the endpoints stay untouched");
        }

        [TestMethod]
        public async Task AddEdge_WithAnUnconvertibleValue_Returns400_AndWritesNoEdge()
        {
            var (source, target) = TwoVertices();

            var result = await _controller.AddEdge(
                EdgeSpec(source, target, Property("since", "System.DateTime", "not-a-date")),
                waitForCompletion: true);

            ProblemAssert.AssertProblem(result, StatusCodes.Status400BadRequest, "since");
            Assert.AreEqual(0, _fallen8.GetAllEdges().Count);
        }

        [TestMethod]
        public async Task AddEdge_WithABadPropertyAndAMissingVertex_Returns400_BeforeTheTransaction()
        {
            // The property guard runs before the enqueue, so the invalid specification is a 400 even
            // though the referenced vertex would also have failed the transaction.
            var (source, _) = TwoVertices();

            var result = await _controller.AddEdge(
                EdgeSpec(source, source + 1000, Property("since", "Not.A.Real.Type", "2024-01-01")),
                waitForCompletion: true);

            ProblemAssert.AssertProblem(result, StatusCodes.Status400BadRequest);
            Assert.AreEqual(0, _fallen8.GetAllEdges().Count);
        }

        [TestMethod]
        public async Task AddEdge_WithValidPropertiesAndAMissingVertex_StillAnswers404()
        {
            // Guard must not swallow the transaction-level outcome: a well-typed property with a
            // missing endpoint is still the documented 404, not a 400.
            var (source, _) = TwoVertices();

            var result = await _controller.AddEdge(
                EdgeSpec(source, source + 1000, Property("since", "System.DateTime", "2024-01-15T00:00:00")),
                waitForCompletion: true);

            ProblemAssert.AssertProblem(result, StatusCodes.Status404NotFound);
            Assert.AreEqual(0, _fallen8.GetAllEdges().Count);
        }

        [TestMethod]
        public async Task AddEdge_WithValidProperties_Still202_AndRoundTrips()
        {
            var (source, target) = TwoVertices();

            var result = await _controller.AddEdge(
                EdgeSpec(source, target,
                    Property("since", "System.DateTime", "2024-01-15T08:30:00"),
                    Property("strength", "System.Double", "0.85")),
                waitForCompletion: true);

            Assert.IsInstanceOfType(result, typeof(AcceptedResult), "a valid create is still 202 Accepted");
            var edgeId = _fallen8.GetAllEdges().Single().Id;
            var edge = _controller.GetEdge(edgeId);
            Assert.AreEqual("System.DateTime", edge.Properties.Single(p => p.PropertyId == "since").FullQualifiedTypeName);
            Assert.AreEqual("0.85", edge.Properties.Single(p => p.PropertyId == "strength").PropertyValue);
        }

        // ---- PUT /edges ----------------------------------------------------------------------------

        [TestMethod]
        public async Task AddEdges_WithABadPropertyInALaterSpec_Returns400_AndWritesNothing()
        {
            var (source, target) = TwoVertices();

            var result = await _controller.AddEdges(
                new List<EdgeSpecification>
                {
                    EdgeSpec(source, target, Property("since", "System.DateTime", "2024-01-15T08:30:00")),
                    EdgeSpec(target, source, Property("strength", "System.Double", "very"))
                },
                waitForCompletion: true);

            ProblemAssert.AssertProblem(result, StatusCodes.Status400BadRequest, "strength");
            Assert.AreEqual(0, _fallen8.GetAllEdges().Count,
                "the batch is rejected before it is enqueued, so the valid leading edge is not wired either");
        }

        [TestMethod]
        public async Task AddEdges_WithValidProperties_ReturnsTheAssignedIds()
        {
            var (source, target) = TwoVertices();

            var result = await _controller.AddEdges(
                new List<EdgeSpecification>
                {
                    EdgeSpec(source, target, Property("strength", "System.Double", "0.85")),
                    EdgeSpec(target, source, Property("strength", "System.Double", "0.15"))
                },
                waitForCompletion: true);

            var ok = result as OkObjectResult;
            Assert.IsNotNull(ok, "a valid waited-on batch returns 200 with the assigned ids");
            Assert.AreEqual(2, ((IEnumerable<Int32>)ok.Value).Count());
            Assert.AreEqual(2, _fallen8.GetAllEdges().Count);
        }
    }
}
