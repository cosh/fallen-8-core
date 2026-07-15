// MIT License
//
// StoredQueryLibraryTest.cs
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
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.StoredQueries;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Tests for the stored query library's Phase 0 surface (feature stored-query-library):
    /// the engine registry (name validation, quota, duplicate handling, transactional
    /// register/remove with structured failure reasons) and the registration REST endpoints
    /// (compile-validated register, list, get, delete) against a real in-memory Fallen8.
    /// </summary>
    [TestClass]
    public class StoredQueryLibraryTest
    {
        private Fallen8 _fallen8;
        private StoredQueriesController _controller;

        [TestInitialize]
        public void TestInitialize()
        {
            var loggerFactory = TestLoggerFactory.Create();
            _fallen8 = new Fallen8(loggerFactory);
            _controller = new StoredQueriesController(loggerFactory.CreateLogger<StoredQueriesController>(), _fallen8);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            _fallen8.Dispose();
        }

        #region helpers

        private static StoredQuerySpecification ValidPathSpecification(string name = "adults-shortest")
        {
            return new StoredQuerySpecification
            {
                Name = name,
                Kind = "Path",
                Description = "test path query",
                Path = new StoredPathQueryBlock
                {
                    Filter = new PathFilterSpecification
                    {
                        Vertex = "return (v) => true;",
                        Edge = "return (e) => true;",
                        EdgeProperty = "return (p) => true;"
                    },
                    Cost = new PathCostSpecification
                    {
                        Vertex = "return (v) => 1.0;",
                        Edge = "return (e) => 1.0;"
                    }
                }
            };
        }

        private static StoredQuerySpecification ValidSubGraphSpecification(string name = "person-net")
        {
            return new StoredQuerySpecification
            {
                Name = name,
                Kind = "SubGraph",
                Description = "test subgraph template",
                SubGraph = new StoredSubGraphQueryBlock
                {
                    VertexFilter = "return (ge) => ge.Label == \"person\";"
                }
            };
        }

        private static int StatusCodeOf(IActionResult result)
        {
            switch (result)
            {
                case ObjectResult objectResult when objectResult.StatusCode.HasValue:
                    return objectResult.StatusCode.Value;
                case StatusCodeResult statusResult:
                    return statusResult.StatusCode;
                default:
                    Assert.Fail($"Unexpected result type {result.GetType().Name}.");
                    return 0;
            }
        }

        #endregion

        #region name validation

        [TestMethod]
        public void IsValidName_AcceptsTheDocumentedAlphabet()
        {
            Assert.IsTrue(StoredQueryLibrary.IsValidName("a"));
            Assert.IsTrue(StoredQueryLibrary.IsValidName("adults-shortest"));
            Assert.IsTrue(StoredQueryLibrary.IsValidName("A_b-9"));
            Assert.IsTrue(StoredQueryLibrary.IsValidName(new string('x', 128)));
        }

        [TestMethod]
        public void IsValidName_RejectsEmptyOversizeAndForbiddenCharacters()
        {
            Assert.IsFalse(StoredQueryLibrary.IsValidName(null));
            Assert.IsFalse(StoredQueryLibrary.IsValidName(""));
            Assert.IsFalse(StoredQueryLibrary.IsValidName(new string('x', 129)));
            Assert.IsFalse(StoredQueryLibrary.IsValidName("with space"));
            Assert.IsFalse(StoredQueryLibrary.IsValidName("slash/name"));
            Assert.IsFalse(StoredQueryLibrary.IsValidName("dot.name"));
            Assert.IsFalse(StoredQueryLibrary.IsValidName("percent%20"));
            Assert.IsFalse(StoredQueryLibrary.IsValidName("ümlaut"));
        }

        [TestMethod]
        public void Register_WithInvalidName_Returns400()
        {
            var spec = ValidPathSpecification("not a valid name!");

            var result = _controller.RegisterStoredQuery(spec);

            Assert.AreEqual(400, StatusCodeOf(result));
            Assert.AreEqual(0, _fallen8.StoredQueries.Count);
        }

        #endregion

        #region kind/block shape validation

        [TestMethod]
        public void Register_WithUnknownKind_Returns400()
        {
            var spec = ValidPathSpecification();
            spec.Kind = "VertexFilter";

            Assert.AreEqual(400, StatusCodeOf(_controller.RegisterStoredQuery(spec)));
        }

        [TestMethod]
        public void Register_KindIsCaseSensitive()
        {
            // The kind is part of the persisted contract; "path" is not a valid kind.
            var spec = ValidPathSpecification();
            spec.Kind = "path";

            Assert.AreEqual(400, StatusCodeOf(_controller.RegisterStoredQuery(spec)));
        }

        [TestMethod]
        public void Register_PathKindWithSubGraphBlock_Returns400()
        {
            var spec = new StoredQuerySpecification
            {
                Name = "mismatched",
                Kind = "Path",
                SubGraph = new StoredSubGraphQueryBlock { VertexFilter = "return (ge) => true;" }
            };

            Assert.AreEqual(400, StatusCodeOf(_controller.RegisterStoredQuery(spec)));
        }

        [TestMethod]
        public void Register_SubGraphKindWithPathBlock_Returns400()
        {
            var spec = new StoredQuerySpecification
            {
                Name = "mismatched",
                Kind = "SubGraph",
                Path = new StoredPathQueryBlock()
            };

            Assert.AreEqual(400, StatusCodeOf(_controller.RegisterStoredQuery(spec)));
        }

        [TestMethod]
        public void Register_WithBothBlocks_Returns400()
        {
            var spec = ValidPathSpecification();
            spec.SubGraph = new StoredSubGraphQueryBlock();

            Assert.AreEqual(400, StatusCodeOf(_controller.RegisterStoredQuery(spec)));
        }

        [TestMethod]
        public void Register_WithNoBlock_Returns400()
        {
            var spec = new StoredQuerySpecification { Name = "no-block", Kind = "Path" };

            Assert.AreEqual(400, StatusCodeOf(_controller.RegisterStoredQuery(spec)));
        }

        [TestMethod]
        public void Register_NullSpecification_Returns400()
        {
            Assert.AreEqual(400, StatusCodeOf(_controller.RegisterStoredQuery(null)));
        }

        #endregion

        #region compile validation

        [TestMethod]
        public void Register_ValidPathQuery_Returns201AndCompiledState()
        {
            var result = _controller.RegisterStoredQuery(ValidPathSpecification());

            Assert.AreEqual(201, StatusCodeOf(result));
            var summary = (StoredQuerySummaryREST)((ObjectResult)result).Value;
            Assert.AreEqual("adults-shortest", summary.Name);
            Assert.AreEqual("Path", summary.Kind);
            Assert.AreEqual("Compiled", summary.CompileState);

            Assert.IsTrue(_fallen8.StoredQueries.TryGet(out var entry, "adults-shortest"));
            Assert.AreEqual(StoredQueryCompileState.Compiled, entry.CompileState);
            Assert.IsInstanceOfType(entry.Artifact, typeof(NoSQL.GraphDB.Core.Algorithms.Path.IPathTraverser));
        }

        [TestMethod]
        public void Register_ValidSubGraphQuery_Returns201AndCompiledState()
        {
            var result = _controller.RegisterStoredQuery(ValidSubGraphSpecification());

            Assert.AreEqual(201, StatusCodeOf(result));

            Assert.IsTrue(_fallen8.StoredQueries.TryGet(out var entry, "person-net"));
            Assert.AreEqual(StoredQueryCompileState.Compiled, entry.CompileState);
            Assert.IsInstanceOfType(entry.Artifact, typeof(NoSQL.GraphDB.Core.Algorithms.SubGraph.SubGraphDefinition));
        }

        [TestMethod]
        public void Register_PathQueryWithSyntaxError_Returns400WithDiagnostics()
        {
            var spec = ValidPathSpecification("broken");
            spec.Path.Filter.Vertex = "return (v) => v.NoSuchMember == 42;";

            var result = _controller.RegisterStoredQuery(spec);

            Assert.AreEqual(400, StatusCodeOf(result));
            var body = ((ObjectResult)result).Value as string;
            Assert.IsNotNull(body);
            // The Roslyn diagnostics shape the inline endpoints use ("ID: CSxxxx, Message: ...").
            StringAssert.Contains(body, "ID:");
            Assert.AreEqual(0, _fallen8.StoredQueries.Count);
        }

        [TestMethod]
        public void Register_SubGraphQueryWithSyntaxError_Returns400WithDiagnostics()
        {
            var spec = ValidSubGraphSpecification("broken-subgraph");
            spec.SubGraph.VertexFilter = "return (ge) => ge.;";

            var result = _controller.RegisterStoredQuery(spec);

            Assert.AreEqual(400, StatusCodeOf(result));
            Assert.AreEqual(0, _fallen8.StoredQueries.Count);
        }

        [TestMethod]
        public void Register_OversizeFragment_IsRejectedBeforeRoslyn()
        {
            // Exceeds MaxFilterFragmentLength (100k), so the pre-Roslyn length guard must fire;
            // the error message names the offending fragment rather than carrying diagnostics.
            var spec = ValidPathSpecification("oversize");
            spec.Path.Filter.Vertex = "return (v) => true; //" + new string('x', 100_001);

            var result = _controller.RegisterStoredQuery(spec);

            Assert.AreEqual(400, StatusCodeOf(result));
            var body = ((ObjectResult)result).Value as string;
            Assert.IsNotNull(body);
            StringAssert.Contains(body, "exceeds the maximum");
            Assert.AreEqual(0, _fallen8.StoredQueries.Count);
        }

        #endregion

        #region duplicate + quota

        [TestMethod]
        public void Register_DuplicateName_Returns409()
        {
            Assert.AreEqual(201, StatusCodeOf(_controller.RegisterStoredQuery(ValidPathSpecification())));

            var result = _controller.RegisterStoredQuery(ValidPathSpecification());

            Assert.AreEqual(409, StatusCodeOf(result));
            Assert.AreEqual(1, _fallen8.StoredQueries.Count);
        }

        [TestMethod]
        public void Register_DuplicateName_AcrossKinds_Returns409()
        {
            Assert.AreEqual(201, StatusCodeOf(_controller.RegisterStoredQuery(ValidPathSpecification("same-name"))));

            Assert.AreEqual(409, StatusCodeOf(_controller.RegisterStoredQuery(ValidSubGraphSpecification("same-name"))));
        }

        [TestMethod]
        public void Register_BeyondQuota_Returns409_AndRecoversAfterDelete()
        {
            _fallen8.StoredQueries.MaxCount = 2;

            Assert.AreEqual(201, StatusCodeOf(_controller.RegisterStoredQuery(ValidPathSpecification("q1"))));
            Assert.AreEqual(201, StatusCodeOf(_controller.RegisterStoredQuery(ValidPathSpecification("q2"))));
            Assert.AreEqual(409, StatusCodeOf(_controller.RegisterStoredQuery(ValidPathSpecification("q3"))));

            Assert.AreEqual(204, StatusCodeOf(_controller.DeleteStoredQuery("q1")));
            Assert.AreEqual(201, StatusCodeOf(_controller.RegisterStoredQuery(ValidPathSpecification("q3"))));
        }

        [TestMethod]
        public void MaxCount_NonPositiveValue_ResetsToDefault()
        {
            _fallen8.StoredQueries.MaxCount = 0;
            Assert.AreEqual(StoredQueryLibrary.DefaultMaxCount, _fallen8.StoredQueries.MaxCount);

            _fallen8.StoredQueries.MaxCount = -5;
            Assert.AreEqual(StoredQueryLibrary.DefaultMaxCount, _fallen8.StoredQueries.MaxCount);
        }

        #endregion

        #region list / get / delete round-trip

        [TestMethod]
        public void ListGetDelete_RoundTrip()
        {
            _controller.RegisterStoredQuery(ValidPathSpecification("path-query"));
            _controller.RegisterStoredQuery(ValidSubGraphSpecification("subgraph-query"));

            // List: both entries, ordered by name.
            var listResult = (ObjectResult)_controller.GetAllStoredQueries();
            var summaries = (List<StoredQuerySummaryREST>)listResult.Value;
            CollectionAssert.AreEqual(new[] { "path-query", "subgraph-query" }, summaries.Select(s => s.Name).ToArray());

            // Get: full detail including the stored source block.
            var getResult = (ObjectResult)_controller.GetStoredQuery("path-query");
            var detail = (StoredQueryDetailREST)getResult.Value;
            Assert.AreEqual("Path", detail.Kind);
            Assert.AreEqual("test path query", detail.Description);
            Assert.AreEqual("Compiled", detail.CompileState);
            Assert.IsNull(detail.CompileDiagnostics);
            // System.Text.Json escapes '>' (>) in the stored document; assert on the
            // stable part of the fragment (deserialization unescapes it for the round-trip).
            StringAssert.Contains(detail.SpecificationJson, "vertexFilter");
            StringAssert.Contains(detail.SpecificationJson, "return (v) =");
            Assert.IsTrue(detail.CreatedAt > DateTime.UtcNow.AddMinutes(-5));

            // Delete: 204, then gone.
            Assert.AreEqual(204, StatusCodeOf(_controller.DeleteStoredQuery("path-query")));
            Assert.AreEqual(404, StatusCodeOf(_controller.GetStoredQuery("path-query")));
            Assert.AreEqual(1, _fallen8.StoredQueries.Count);
        }

        [TestMethod]
        public void Get_UnknownName_Returns404()
        {
            Assert.AreEqual(404, StatusCodeOf(_controller.GetStoredQuery("does-not-exist")));
        }

        [TestMethod]
        public void Delete_UnknownName_Returns404()
        {
            Assert.AreEqual(404, StatusCodeOf(_controller.DeleteStoredQuery("does-not-exist")));
        }

        [TestMethod]
        public void List_EmptyLibrary_ReturnsEmptyList()
        {
            var listResult = (ObjectResult)_controller.GetAllStoredQueries();
            Assert.AreEqual(0, ((List<StoredQuerySummaryREST>)listResult.Value).Count);
        }

        #endregion

        #region transaction failure-reason mapping

        [TestMethod]
        public void RegisterTransaction_DuplicateName_RollsBackWithConflict()
        {
            // Register directly through the pipeline, then race a second registration of the
            // same name: the writer-thread re-check must roll back with Conflict.
            var definition = new StoredQueryDefinition
            {
                Name = "tx-dup",
                Kind = StoredQueryKind.Path,
                SpecificationJson = "{}",
                CreatedAt = DateTime.UtcNow
            };

            var first = new RegisterStoredQueryTransaction
            {
                Entry = new StoredQueryEntry(definition, StoredQueryCompileState.SourceOnly, null)
            };
            var firstInfo = _fallen8.EnqueueTransaction(first);
            firstInfo.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, firstInfo.TransactionState);

            var second = new RegisterStoredQueryTransaction
            {
                Entry = new StoredQueryEntry(definition, StoredQueryCompileState.SourceOnly, null)
            };
            var secondInfo = _fallen8.EnqueueTransaction(second);
            secondInfo.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, secondInfo.TransactionState);
            Assert.AreEqual(TransactionFailureReason.Conflict, secondInfo.FailureReason);
            Assert.AreEqual(1, _fallen8.StoredQueries.Count);
        }

        [TestMethod]
        public void RegisterTransaction_BeyondQuota_RollsBackWithQuotaExceeded()
        {
            _fallen8.StoredQueries.MaxCount = 1;

            var first = new RegisterStoredQueryTransaction
            {
                Entry = new StoredQueryEntry(
                    new StoredQueryDefinition { Name = "q1", Kind = StoredQueryKind.Path, SpecificationJson = "{}" },
                    StoredQueryCompileState.SourceOnly, null)
            };
            _fallen8.EnqueueTransaction(first).WaitUntilFinished();

            var second = new RegisterStoredQueryTransaction
            {
                Entry = new StoredQueryEntry(
                    new StoredQueryDefinition { Name = "q2", Kind = StoredQueryKind.Path, SpecificationJson = "{}" },
                    StoredQueryCompileState.SourceOnly, null)
            };
            var secondInfo = _fallen8.EnqueueTransaction(second);
            secondInfo.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, secondInfo.TransactionState);
            Assert.AreEqual(TransactionFailureReason.QuotaExceeded, secondInfo.FailureReason);
        }

        [TestMethod]
        public void RegisterTransaction_NullEntry_RollsBackWithInvalidInput()
        {
            var tx = new RegisterStoredQueryTransaction { Entry = null };
            var txInfo = _fallen8.EnqueueTransaction(tx);
            txInfo.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, txInfo.TransactionState);
            Assert.AreEqual(TransactionFailureReason.InvalidInput, txInfo.FailureReason);
        }

        [TestMethod]
        public void RemoveTransaction_MissingName_RollsBackWithNotFound()
        {
            var tx = new RemoveStoredQueryTransaction { Name = "never-registered" };
            var txInfo = _fallen8.EnqueueTransaction(tx);
            txInfo.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, txInfo.TransactionState);
            Assert.AreEqual(TransactionFailureReason.NotFound, txInfo.FailureReason);
        }

        [TestMethod]
        public void RemoveTransaction_BlankName_RollsBackWithInvalidInput()
        {
            var tx = new RemoveStoredQueryTransaction { Name = "   " };
            var txInfo = _fallen8.EnqueueTransaction(tx);
            txInfo.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, txInfo.TransactionState);
            Assert.AreEqual(TransactionFailureReason.InvalidInput, txInfo.FailureReason);
        }

        #endregion

        #region entry invariants

        [TestMethod]
        public void StoredQueryEntry_CompiledWithoutArtifact_Throws()
        {
            var definition = new StoredQueryDefinition { Name = "x", Kind = StoredQueryKind.Path };

            Assert.ThrowsExactly<ArgumentException>(() =>
                new StoredQueryEntry(definition, StoredQueryCompileState.Compiled, null));
        }

        [TestMethod]
        public void StoredQueryEntry_NullDefinition_Throws()
        {
            Assert.ThrowsExactly<ArgumentNullException>(() =>
                new StoredQueryEntry(null, StoredQueryCompileState.SourceOnly, null));
        }

        [TestMethod]
        public void StoredQueryEntry_FailedState_KeepsDiagnosticsAndNoArtifact()
        {
            var definition = new StoredQueryDefinition { Name = "x", Kind = StoredQueryKind.Path };

            var entry = new StoredQueryEntry(definition, StoredQueryCompileState.Failed, new object(), "boom");

            Assert.IsNull(entry.Artifact);
            Assert.AreEqual("boom", entry.CompileDiagnostics);
        }

        #endregion
    }
}
