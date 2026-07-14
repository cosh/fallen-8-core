// MIT License
//
// ApiErrorContractTest.cs
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

using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Tests for the "api-error-contract" feature: reachable status codes now match the documented
    /// contract. A missing edge/vertex is a 404 (not a thrown WebException / an ambiguous 0), a
    /// malformed scan type name is a 400 (not a 500), a page read is bounded, and an invalid plugin
    /// upload is a 400. (The global ProblemDetails net (E1) is a Program.cs pipeline concern exercised
    /// by the WebApplicationFactory-based OpenAPI E2E test.)
    /// </summary>
    [TestClass]
    public class ApiErrorContractTest
    {
        private ILoggerFactory _loggerFactory;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
        }

        private GraphController NewController(Fallen8 fallen8)
        {
            return new GraphController(new UnitTestLogger<GraphController>(), fallen8);
        }

        private (Fallen8 fallen8, int v0, int v1) TwoVertices()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var tx = new CreateVerticesTransaction();
            tx.AddVertex(1u, "person");
            tx.AddVertex(1u, "person");
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            var v = tx.GetCreatedVertices();
            return (fallen8, v[0].Id, v[1].Id);
        }

        // ---- E4: missing edge -> 404 --------------------------------------------------------------

        [TestMethod]
        public void EdgeGetters_ForAMissingEdge_Return404()
        {
            var (fallen8, _, _) = TwoVertices();
            var controller = NewController(fallen8);

            Assert.IsInstanceOfType(controller.GetSourceVertexForEdge(999).Result, typeof(NotFoundObjectResult));
            Assert.IsInstanceOfType(controller.GetTargetVertexForEdge(999).Result, typeof(NotFoundObjectResult));
        }

        // ---- E7: degree getters -> 404 for missing vertex, 200+count otherwise --------------------

        [TestMethod]
        public void DegreeGetters_DistinguishMissingVertexFromZeroDegree()
        {
            var (fallen8, v0, v1) = TwoVertices();
            var edgeTx = new CreateEdgesTransaction();
            edgeTx.AddEdge(v0, "knows", v1, 1u, "knows");
            fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();
            var controller = NewController(fallen8);

            // Missing vertex -> 404 (not an ambiguous 0).
            Assert.IsInstanceOfType(controller.GetInDegree(999).Result, typeof(NotFoundObjectResult));
            Assert.IsInstanceOfType(controller.GetOutDegree(999).Result, typeof(NotFoundObjectResult));

            // A live vertex with a real degree -> 200 with the count.
            Assert.AreEqual(1u, controller.GetOutDegree(v0).Value, "v0 has one outgoing edge.");
            Assert.AreEqual(1u, controller.GetInDegree(v1).Value, "v1 has one incoming edge.");

            // A live vertex with zero degree -> 200 with 0 (distinct from the 404 missing case).
            Assert.AreEqual(0u, controller.GetInDegree(v0).Value, "v0 has no incoming edges (live, zero).");

            // Per-edge-type degree: a live vertex with no such group is 200/0, a missing vertex is 404.
            Assert.AreEqual(0u, controller.GetOutEdgeDegree(v0, "no-such-key").Value);
            Assert.IsInstanceOfType(controller.GetOutEdgeDegree(999, "knows").Result, typeof(NotFoundObjectResult));
        }

        // ---- E3: malformed scan type / literal -> 400 ---------------------------------------------

        [TestMethod]
        public void Scans_WithAnUnknownTypeName_Return400()
        {
            var (fallen8, _, _) = TwoVertices();
            var controller = NewController(fallen8);

            var graphScan = controller.GraphScan("name", new ScanSpecification
            {
                Literal = new LiteralSpecification { Value = "x", FullQualifiedTypeName = "Not.A.Real.Type" },
                Operator = BinaryOperator.Equals,
                ResultType = ResultTypeSpecification.Vertices
            });
            Assert.IsInstanceOfType(graphScan.Result, typeof(BadRequestObjectResult), "An unknown type name must be a 400, not a 500.");

            var indexScan = controller.IndexScan(new IndexScanSpecification
            {
                IndexId = "idx",
                Literal = new LiteralSpecification { Value = "x", FullQualifiedTypeName = "Not.A.Real.Type" },
                Operator = BinaryOperator.Equals,
                ResultType = ResultTypeSpecification.Vertices
            });
            Assert.IsInstanceOfType(indexScan.Result, typeof(BadRequestObjectResult));

            var rangeScan = controller.RangeIndexScan(new RangeIndexScanSpecification
            {
                IndexId = "idx",
                LeftLimit = "1",
                RightLimit = "2",
                FullQualifiedTypeName = "Not.A.Real.Type",
                ResultType = ResultTypeSpecification.Vertices
            });
            Assert.IsInstanceOfType(rangeScan.Result, typeof(BadRequestObjectResult));
        }

        [TestMethod]
        public void GraphScan_WithANullLiteral_Returns400()
        {
            var (fallen8, _, _) = TwoVertices();
            var controller = NewController(fallen8);

            var result = controller.GraphScan("name", new ScanSpecification
            {
                Literal = null,
                Operator = BinaryOperator.Equals,
                ResultType = ResultTypeSpecification.Vertices
            });
            Assert.IsInstanceOfType(result.Result, typeof(BadRequestObjectResult));
        }

        // ---- E6: bounded reads --------------------------------------------------------------------

        [TestMethod]
        public void GetGraph_ClampsAndHandlesNegativeMaxElements()
        {
            var (fallen8, _, _) = TwoVertices();
            var controller = NewController(fallen8);

            // A huge maxElements does not throw or hang; it returns the (small) available set, clamped.
            var big = controller.GetGraph(int.MaxValue);
            Assert.AreEqual(2, big.Vertices.Count, "A clamped read still returns every available vertex here.");

            // A negative maxElements yields an empty page, not a silent Take(negative) / crash.
            var negative = controller.GetGraph(-5);
            Assert.AreEqual(0, negative.Vertices.Count);
            Assert.AreEqual(0, negative.Edges.Count);
        }

        // ---- E7: invalid plugin upload -> 400 -----------------------------------------------------

        [TestMethod]
        public void UploadPlugin_WithANullStream_Returns400()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var admin = new AdminController(
                new UnitTestLogger<AdminController>(),
                fallen8,
                Options.Create(new Fallen8SecurityOptions()));

            var result = admin.UploadPlugin(null);
            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult), "A null plugin stream must be a 400.");
        }
    }
}
