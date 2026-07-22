// MIT License
//
// TransactionFailureReasonTest.cs
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
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.SubGraph;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Tests for the transaction-failure-reasons feature: the structured
    /// <see cref="TransactionFailureReason"/> recorded on a rolled-back transaction's
    /// <see cref="TransactionInformation"/>, and the controller mappings that turn it into the
    /// correct HTTP status. The engine-level tests drive REAL transactions through the single
    /// worker (not a controller decorator), so they pin the production wiring: the reason is set
    /// in place before the task completes, visible under the same happens-before as
    /// <see cref="TransactionState"/> / <see cref="TransactionInformation.Error"/>.
    /// </summary>
    [TestClass]
    public class TransactionFailureReasonTest
    {
        private ILoggerFactory _loggerFactory;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
        }

        private VertexModel[] CreateVertices(Fallen8 fallen8, int count)
        {
            var tx = new CreateVerticesTransaction();
            for (int i = 0; i < count; i++)
            {
                tx.AddVertex(1, "node", new Dictionary<string, object> { { "idx", i } });
            }
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedVertices().ToArray();
        }

        private static CreateEdgeTransaction Edge(int source, int target)
        {
            return new CreateEdgeTransaction
            {
                Definition = new EdgeDefinition
                {
                    CreationDate = 1,
                    SourceVertexId = source,
                    TargetVertexId = target,
                    EdgePropertyId = "knows",
                    Label = "knows"
                }
            };
        }

        private static int StatusCodeOf(IActionResult result)
        {
            var statusResult = result as IStatusCodeActionResult;
            Assert.IsNotNull(statusResult,
                "Expected a status-code action result but got " + (result?.GetType().Name ?? "null") + ".");
            Assert.IsTrue(statusResult.StatusCode.HasValue, "Expected an explicit status code.");
            return statusResult.StatusCode.Value;
        }

        #region engine level - edge endpoint existence sets NotFound (no indexer throw)

        [TestMethod]
        public void CreateEdge_Valid_Commits_WithReasonNone()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 2);

            var txInfo = fallen8.EnqueueTransaction(Edge(v[0].Id, v[1].Id));
            txInfo.WaitUntilFinished();

            Assert.AreEqual(TransactionState.Finished, txInfo.TransactionState, "A valid edge create commits.");
            Assert.AreEqual(TransactionFailureReason.None, txInfo.FailureReason, "A committed transaction records no failure reason.");
            Assert.IsNull(txInfo.Error);
            Assert.AreEqual(1, fallen8.EdgeCount, "The edge was created.");
        }

        [TestMethod]
        public void CreateEdge_MissingTargetVertex_RollsBackWithNotFound_AndDoesNotThrowOrCreate()
        {
            // One real vertex so the source resolves but the (out-of-range) target does not. The
            // engine must NOT let the master-store bounds check throw; it fails the create cleanly.
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 1);

            var txInfo = fallen8.EnqueueTransaction(Edge(v[0].Id, int.MaxValue));
            txInfo.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, txInfo.TransactionState);
            Assert.IsNull(txInfo.Error, "A missing endpoint is a CLEAN rollback, not a thrown fault.");
            Assert.AreEqual(TransactionFailureReason.NotFound, txInfo.FailureReason,
                "A missing referenced vertex must be classified as NotFound.");
            Assert.AreEqual(0, fallen8.EdgeCount, "No edge is created on a missing endpoint.");
        }

        [TestMethod]
        public void CreateEdge_MissingSourceVertex_RollsBackWithNotFound()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 1);

            var txInfo = fallen8.EnqueueTransaction(Edge(int.MaxValue, v[0].Id));
            txInfo.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, txInfo.TransactionState);
            Assert.IsNull(txInfo.Error);
            Assert.AreEqual(TransactionFailureReason.NotFound, txInfo.FailureReason);
            Assert.AreEqual(0, fallen8.EdgeCount);
        }

        [TestMethod]
        public void CreateEdge_RemovedEndpoint_RollsBackWithNotFound()
        {
            // A referenced vertex that exists in-range but has been REMOVED must also be NotFound,
            // not silently wired to a tombstone.
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 2);

            var removeInfo = fallen8.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = v[1].Id });
            removeInfo.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, removeInfo.TransactionState, "Sanity: the vertex removal committed.");

            var txInfo = fallen8.EnqueueTransaction(Edge(v[0].Id, v[1].Id));
            txInfo.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, txInfo.TransactionState);
            Assert.IsNull(txInfo.Error);
            Assert.AreEqual(TransactionFailureReason.NotFound, txInfo.FailureReason,
                "An edge to a removed vertex must be NotFound.");
            Assert.AreEqual(0, fallen8.EdgeCount);
        }

        [TestMethod]
        public void CreateEdges_BatchWithOneMissingVertex_RollsBackAtomicallyWithNotFound()
        {
            // The batch mixes a fully-valid edge with one referencing a missing vertex. The whole
            // batch must roll back atomically (nothing wired) with NotFound - not commit the valid
            // edge and drop the other.
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 2);

            var tx = new CreateEdgesTransaction();
            tx.AddEdge(v[0].Id, "knows", v[1].Id, 1, "knows");         // valid
            tx.AddEdge(v[0].Id, "knows", int.MaxValue, 1, "knows");    // missing target

            var txInfo = fallen8.EnqueueTransaction(tx);
            txInfo.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, txInfo.TransactionState);
            Assert.IsNull(txInfo.Error);
            Assert.AreEqual(TransactionFailureReason.NotFound, txInfo.FailureReason);
            Assert.AreEqual(0, fallen8.EdgeCount, "The batch is atomic: no edge is created when one endpoint is missing.");
        }

        [TestMethod]
        public void CreateEdges_AllValid_Commits_WithReasonNone()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 3);

            var tx = new CreateEdgesTransaction();
            tx.AddEdge(v[0].Id, "knows", v[1].Id, 1, "knows");
            tx.AddEdge(v[1].Id, "knows", v[2].Id, 1, "knows");

            var txInfo = fallen8.EnqueueTransaction(tx);
            txInfo.WaitUntilFinished();

            Assert.AreEqual(TransactionState.Finished, txInfo.TransactionState);
            Assert.AreEqual(TransactionFailureReason.None, txInfo.FailureReason);
            Assert.AreEqual(2, fallen8.EdgeCount, "All valid edges are created with the same ids as before.");
        }

        [TestMethod]
        public void ProcessTransaction_ThrownException_IsClassifiedAsInternalError()
        {
            // A genuine escaped exception (removing an out-of-range id throws inside the worker) must
            // be recorded as Error AND classified as InternalError - never a client-facing reason.
            var fallen8 = new Fallen8(_loggerFactory);

            var txInfo = fallen8.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = int.MaxValue });
            txInfo.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, txInfo.TransactionState);
            Assert.IsInstanceOfType(txInfo.Error, typeof(ArgumentOutOfRangeException), "The thrown exception is preserved on Error (B6).");
            Assert.AreEqual(TransactionFailureReason.InternalError, txInfo.FailureReason,
                "An escaped exception is an internal fault, classified as InternalError.");
        }

        #endregion

        #region engine level - subgraph create/remove reasons

        private static SubGraphDefinition PersonsDefinition(string name)
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

        private Fallen8 CreatePeopleGraph()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var tx = new CreateVerticesTransaction();
            tx.AddVertex(1, "person", new Dictionary<string, object> { { "name", "Alice" } });
            tx.AddVertex(1, "person", new Dictionary<string, object> { { "name", "Bob" } });
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return fallen8;
        }

        [TestMethod]
        public void CreateSubGraph_ElementQuotaExceeded_RollsBackWithQuotaExceeded()
        {
            var fallen8 = CreatePeopleGraph();
            fallen8.SubGraphFactory.Quota = new SubGraphQuota { MaxElementsPerSubGraph = 1 };

            var txInfo = fallen8.EnqueueTransaction(new CreateSubGraphTransaction { Definition = PersonsDefinition("big") });
            txInfo.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, txInfo.TransactionState);
            Assert.AreEqual(TransactionFailureReason.QuotaExceeded, txInfo.FailureReason,
                "A post-materialization element-quota breach is QuotaExceeded.");
        }

        [TestMethod]
        public void CreateSubGraph_CountQuotaExceeded_RollsBackWithQuotaExceeded()
        {
            var fallen8 = CreatePeopleGraph();
            fallen8.SubGraphFactory.Quota = new SubGraphQuota { MaxSubGraphCount = 1 };

            fallen8.EnqueueTransaction(new CreateSubGraphTransaction { Definition = PersonsDefinition("first") }).WaitUntilFinished();

            var txInfo = fallen8.EnqueueTransaction(new CreateSubGraphTransaction { Definition = PersonsDefinition("second") });
            txInfo.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, txInfo.TransactionState);
            Assert.AreEqual(TransactionFailureReason.QuotaExceeded, txInfo.FailureReason,
                "The up-front count ceiling shares the SAME QuotaExceeded reason as the element ceiling.");
        }

        [TestMethod]
        public void CreateSubGraph_TotalElementQuotaExceeded_RollsBackWithQuotaExceeded()
        {
            // The first subgraph (2 persons) fits; a second would push the aggregate over the total
            // ceiling. The aggregate breach shares the SAME QuotaExceeded reason.
            var fallen8 = CreatePeopleGraph();
            fallen8.SubGraphFactory.Quota = new SubGraphQuota { MaxTotalElements = 3 };

            fallen8.EnqueueTransaction(new CreateSubGraphTransaction { Definition = PersonsDefinition("a") }).WaitUntilFinished();

            var txInfo = fallen8.EnqueueTransaction(new CreateSubGraphTransaction { Definition = PersonsDefinition("b") });
            txInfo.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, txInfo.TransactionState);
            Assert.AreEqual(TransactionFailureReason.QuotaExceeded, txInfo.FailureReason,
                "A total-element aggregate breach is QuotaExceeded, like the count and per-subgraph ceilings.");
        }

        [TestMethod]
        public void CreateSubGraph_StructurallyInvalidPattern_RollsBackWithInvalidInput()
        {
            var fallen8 = CreatePeopleGraph();

            // A pattern ending in an edge is structurally invalid (a well-formed path ends at a vertex).
            var definition = new SubGraphDefinition
            {
                Name = "invalid",
                Pattern = new List<APattern>
                {
                    new VertexPattern { PatternName = "v", Vertex = v => v.Label == "person" },
                    new EdgePattern { PatternName = "dangling", Direction = Direction.OutgoingEdge }
                }
            };

            var txInfo = fallen8.EnqueueTransaction(new CreateSubGraphTransaction { Definition = definition });
            txInfo.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, txInfo.TransactionState);
            Assert.AreEqual(TransactionFailureReason.InvalidInput, txInfo.FailureReason,
                "A structurally-invalid pattern is InvalidInput.");
        }

        [TestMethod]
        public void CreateSubGraph_ValidPatternMatchingNothing_Commits_EmptyAndReasonNone()
        {
            // An empty source graph with a valid pattern is a valid EMPTY result, not a failure.
            var fallen8 = new Fallen8(_loggerFactory);

            var tx = new CreateSubGraphTransaction { Definition = PersonsDefinition("empty") };
            var txInfo = fallen8.EnqueueTransaction(tx);
            txInfo.WaitUntilFinished();

            Assert.AreEqual(TransactionState.Finished, txInfo.TransactionState, "A valid pattern matching nothing commits an empty subgraph.");
            Assert.AreEqual(TransactionFailureReason.None, txInfo.FailureReason);
            Assert.IsNotNull(tx.SubGraphCreated, "An empty (but registered) subgraph is produced.");
            Assert.AreEqual(0, tx.SubGraphCreated.SubGraph.VertexCount);
        }

        [TestMethod]
        public void RemoveSubGraph_Missing_RollsBackWithNotFound()
        {
            var fallen8 = new Fallen8(_loggerFactory);

            var txInfo = fallen8.EnqueueTransaction(new RemoveSubGraphTransaction { SubGraphName = "does-not-exist" });
            txInfo.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, txInfo.TransactionState);
            Assert.IsNull(txInfo.Error, "A missing subgraph is a clean rollback, not a fault.");
            Assert.AreEqual(TransactionFailureReason.NotFound, txInfo.FailureReason);
        }

        #endregion

        #region controller level - GraphController.AddEdge maps NotFound to 404, honours fire-and-forget

        [TestMethod]
        public async Task AddEdge_Waited_MissingSourceVertex_Returns404()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var controller = new GraphController(_loggerFactory.CreateLogger<GraphController>(), fallen8);
            var v = CreateVertices(fallen8, 1);

            var edgeSpec = new EdgeSpecification
            {
                CreationDate = 1,
                Label = "knows",
                SourceVertex = int.MaxValue, // non-existent source
                TargetVertex = v[0].Id,
                EdgePropertyId = "knows",
                Properties = new List<PropertySpecification>()
            };

            var result = await controller.AddEdge(edgeSpec, waitForCompletion: true);

            Assert.AreEqual(StatusCodes.Status404NotFound, StatusCodeOf(result),
                "A waited-on edge create to a missing source vertex maps to 404.");
        }

        [TestMethod]
        public async Task AddEdge_Waited_Valid_Returns202()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var controller = new GraphController(_loggerFactory.CreateLogger<GraphController>(), fallen8);
            var v = CreateVertices(fallen8, 2);

            var edgeSpec = new EdgeSpecification
            {
                CreationDate = 1,
                Label = "knows",
                SourceVertex = v[0].Id,
                TargetVertex = v[1].Id,
                EdgePropertyId = "knows",
                Properties = new List<PropertySpecification>()
            };

            var result = await controller.AddEdge(edgeSpec, waitForCompletion: true);

            Assert.AreEqual(StatusCodes.Status202Accepted, StatusCodeOf(result),
                "A successful waited-on edge create returns 202 (Accepted).");
            Assert.AreEqual(1, fallen8.EdgeCount);
        }

        [TestMethod]
        public async Task AddEdge_NotWaiting_MissingVertex_StaysAccepted202()
        {
            // The fire-and-forget path is unchanged: the outcome is unknowable without waiting, so it
            // returns 202 immediately regardless of the eventual rollback.
            var fallen8 = new Fallen8(_loggerFactory);
            var controller = new GraphController(_loggerFactory.CreateLogger<GraphController>(), fallen8);
            var v = CreateVertices(fallen8, 1);

            var edgeSpec = new EdgeSpecification
            {
                CreationDate = 1,
                Label = "knows",
                SourceVertex = v[0].Id,
                TargetVertex = int.MaxValue,
                EdgePropertyId = "knows",
                Properties = new List<PropertySpecification>()
            };

            var result = await controller.AddEdge(edgeSpec, waitForCompletion: false);

            Assert.AreEqual(StatusCodes.Status202Accepted, StatusCodeOf(result),
                "Fire-and-forget must stay 202 even for a create that will roll back.");
        }

        #endregion
    }
}
