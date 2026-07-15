// MIT License
//
// StoredQueryDurabilityTest.cs
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
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.StoredQueries;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Durability tests for the stored query library (feature stored-query-library Phase 3):
    /// snapshot manifest save/load with eager recompile, keep-and-mark-Failed on recompile
    /// failure, source-only load without a compiler, WAL entries 14/15 with commit-order replay
    /// (unanchored and snapshot-paired), Save-WAL symmetry, manifest corruption loudness, and
    /// collectible-context unload after delete.
    /// </summary>
    [TestClass]
    public class StoredQueryDurabilityTest
    {
        private ILoggerFactory _loggerFactory;
        private string _tempDir;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
            _tempDir = Path.Combine(Path.GetTempPath(), "f8_sqdur_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try
            {
                if (_tempDir != null && Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }

        #region helpers

        private string SavePath => Path.Combine(_tempDir, "savegame.f8s");
        private string WalPath => Path.Combine(_tempDir, "savegame.f8s.wal");

        private Fallen8 NewEngine(bool withCompiler = true)
        {
            var engine = new Fallen8(_loggerFactory);
            if (withCompiler)
            {
                engine.StoredQueryCompiler = new StoredQueryCompiler();
            }
            return engine;
        }

        private Fallen8 NewEngineWithWal(bool withCompiler = true)
        {
            return new Fallen8(_loggerFactory, new WriteAheadLogOptions(WalPath),
                withCompiler ? new RecipeSubGraphCompiler() : null,
                withCompiler ? new StoredQueryCompiler() : null);
        }

        private StoredQueriesController Controller(Fallen8 engine)
        {
            return new StoredQueriesController(_loggerFactory.CreateLogger<StoredQueriesController>(), engine);
        }

        private void RegisterPathQueryViaController(Fallen8 engine, string name, string vertexFilter = "return (v) => v.Label == \"person\";")
        {
            var result = Controller(engine).RegisterStoredQuery(new StoredQuerySpecification
            {
                Name = name,
                Kind = "Path",
                Description = "durability test query",
                Path = new StoredPathQueryBlock
                {
                    Filter = new PathFilterSpecification { Vertex = vertexFilter }
                }
            });
            Assert.AreEqual(201, ((ObjectResult)result).StatusCode, "registration of '" + name + "' must succeed");
        }

        private void RegisterSubGraphQueryViaController(Fallen8 engine, string name)
        {
            var result = Controller(engine).RegisterStoredQuery(new StoredQuerySpecification
            {
                Name = name,
                Kind = "SubGraph",
                SubGraph = new StoredSubGraphQueryBlock { VertexFilter = "return (ge) => ge.Label == \"person\";" }
            });
            Assert.AreEqual(201, ((ObjectResult)result).StatusCode, "registration of '" + name + "' must succeed");
        }

        /// <summary>
        ///   Registers a definition through the transaction pipeline as SourceOnly, bypassing the
        ///   controller's compile-validation - the way to plant a source that will FAIL a later
        ///   rehydration recompile.
        /// </summary>
        private static void RegisterSourceOnlyViaTransaction(Fallen8 engine, string name, string specificationJson)
        {
            var tx = new RegisterStoredQueryTransaction
            {
                Entry = new StoredQueryEntry(
                    new StoredQueryDefinition
                    {
                        Name = name,
                        Kind = StoredQueryKind.Path,
                        SpecificationJson = specificationJson,
                        CreatedAt = DateTime.UtcNow
                    },
                    StoredQueryCompileState.SourceOnly, null)
            };
            var info = engine.EnqueueTransaction(tx);
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState);
        }

        private static string Save(Fallen8 engine, string path)
        {
            var tx = new SaveTransaction { Path = path, SavePartitions = 1 };
            var info = engine.EnqueueTransaction(tx);
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState, "the save should finish");
            return tx.ActualPath;
        }

        private static void Load(Fallen8 engine, string path)
        {
            var tx = new LoadTransaction { Path = path };
            var info = engine.EnqueueTransaction(tx);
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState, "the load should finish");
        }

        private const string BadSourceBlock = "{\"filter\":{\"vertexFilter\":\"return (v) => v.NoSuchMember == 42;\"}}";

        #endregion

        #region snapshot manifest round-trip

        [TestMethod]
        public void SaveLoad_RoundTrip_RecompilesAndStaysInvocable()
        {
            var source = NewEngine();
            RegisterPathQueryViaController(source, "rt-path");
            RegisterSubGraphQueryViaController(source, "rt-subgraph");
            var actualPath = Save(source, SavePath);
            source.Dispose();

            var target = NewEngine();
            Load(target, actualPath);

            Assert.AreEqual(2, target.StoredQueries.Count);

            Assert.IsTrue(target.StoredQueries.TryGet(out var pathEntry, "rt-path"));
            Assert.AreEqual(StoredQueryCompileState.Compiled, pathEntry.CompileState);
            Assert.AreEqual(StoredQueryKind.Path, pathEntry.Definition.Kind);
            Assert.AreEqual("durability test query", pathEntry.Definition.Description);
            Assert.IsInstanceOfType(pathEntry.Artifact, typeof(NoSQL.GraphDB.Core.Algorithms.Path.IPathTraverser));

            Assert.IsTrue(target.StoredQueries.TryGet(out var subGraphEntry, "rt-subgraph"));
            Assert.AreEqual(StoredQueryCompileState.Compiled, subGraphEntry.CompileState);
            Assert.IsInstanceOfType(subGraphEntry.Artifact, typeof(NoSQL.GraphDB.Core.Algorithms.SubGraph.SubGraphDefinition));

            // Invocable end-to-end after the load: a stored path invocation resolves and runs.
            var graphController = new GraphController(_loggerFactory.CreateLogger<GraphController>(), target);
            var outcome = graphController.CalculateShortestPath(0, 1, new PathSpecification { StoredQuery = "rt-path" });
            Assert.IsNotNull(outcome.Value, "the recompiled stored query must be invocable (got " +
                (outcome.Result?.GetType().Name ?? "a value") + ")");

            target.Dispose();
        }

        [TestMethod]
        public void Load_WithoutCompiler_KeepsSourceOnly_AndInvocationReturns409()
        {
            var source = NewEngine();
            RegisterPathQueryViaController(source, "source-only");
            var actualPath = Save(source, SavePath);
            source.Dispose();

            var target = NewEngine(withCompiler: false);
            Load(target, actualPath);

            Assert.IsTrue(target.StoredQueries.TryGet(out var entry, "source-only"),
                "a definition loaded without a compiler must be KEPT (source-only), not dropped");
            Assert.AreEqual(StoredQueryCompileState.SourceOnly, entry.CompileState);
            Assert.IsNull(entry.Artifact);

            var graphController = new GraphController(_loggerFactory.CreateLogger<GraphController>(), target);
            var outcome = graphController.CalculateShortestPath(0, 1, new PathSpecification { StoredQuery = "source-only" });
            Assert.IsInstanceOfType(outcome.Result, typeof(ConflictObjectResult));

            target.Dispose();
        }

        [TestMethod]
        public void Load_RecompileFailure_KeepsEntryAsFailed_OthersStillLoad()
        {
            var source = NewEngine();
            RegisterPathQueryViaController(source, "good-query");
            RegisterSourceOnlyViaTransaction(source, "breaks-on-load", BadSourceBlock);
            var actualPath = Save(source, SavePath);
            source.Dispose();

            var target = NewEngine();
            Load(target, actualPath);

            // The failing entry is kept LOUDLY as Failed with its diagnostics; recovery of the
            // rest continues.
            Assert.IsTrue(target.StoredQueries.TryGet(out var failed, "breaks-on-load"),
                "a definition whose recompile failed must be KEPT as Failed, never silently dropped");
            Assert.AreEqual(StoredQueryCompileState.Failed, failed.CompileState);
            Assert.IsNotNull(failed.CompileDiagnostics);

            Assert.IsTrue(target.StoredQueries.TryGet(out var good, "good-query"));
            Assert.AreEqual(StoredQueryCompileState.Compiled, good.CompileState);

            // The Failed entry surfaces per contract: get shows diagnostics, invoke is 409,
            // delete + re-register recovers.
            var controller = Controller(target);
            var detail = (StoredQueryDetailREST)((ObjectResult)controller.GetStoredQuery("breaks-on-load")).Value;
            Assert.AreEqual("Failed", detail.CompileState);
            Assert.IsNotNull(detail.CompileDiagnostics);

            var graphController = new GraphController(_loggerFactory.CreateLogger<GraphController>(), target);
            var outcome = graphController.CalculateShortestPath(0, 1, new PathSpecification { StoredQuery = "breaks-on-load" });
            Assert.IsInstanceOfType(outcome.Result, typeof(ConflictObjectResult));

            Assert.AreEqual(204, ((StatusCodeResult)controller.DeleteStoredQuery("breaks-on-load")).StatusCode);
            RegisterPathQueryViaController(target, "breaks-on-load");

            target.Dispose();
        }

        [TestMethod]
        public void Load_ReplacesTheExistingLibraryWholesale()
        {
            var source = NewEngine();
            RegisterPathQueryViaController(source, "from-save");
            var actualPath = Save(source, SavePath);
            source.Dispose();

            var target = NewEngine();
            RegisterPathQueryViaController(target, "pre-existing");
            Load(target, actualPath);

            Assert.IsTrue(target.StoredQueries.TryGet(out _, "from-save"));
            Assert.IsFalse(target.StoredQueries.TryGet(out _, "pre-existing"),
                "a load replaces the library wholesale, exactly like the graph itself");
            Assert.AreEqual(1, target.StoredQueries.Count);

            target.Dispose();
        }

        [TestMethod]
        public void SaveWithoutStoredQueries_WritesNoManifest()
        {
            var source = NewEngine();
            var actualPath = Save(source, SavePath);
            source.Dispose();

            Assert.IsFalse(File.Exists(actualPath + "_storedqueries"),
                "an empty library must not leave a manifest sidecar behind");
        }

        [TestMethod]
        public void CorruptManifest_IsLoud_AndTheLoadStillSucceeds()
        {
            var source = NewEngine();
            RegisterPathQueryViaController(source, "will-be-corrupted");
            var actualPath = Save(source, SavePath);
            source.Dispose();

            File.WriteAllText(actualPath + "_storedqueries", "{ not valid json !!", Encoding.UTF8);

            var target = NewEngine();
            Load(target, actualPath);

            // Loud-but-not-fatal: the load finishes (asserted in Load) with an empty library.
            Assert.AreEqual(0, target.StoredQueries.Count);

            target.Dispose();
        }

        #endregion

        #region WAL replay

        [TestMethod]
        public void Wal_UnanchoredCrash_ReplaysRegisterAndRemove_ToTheIdenticalLibrary()
        {
            var engine = NewEngineWithWal();
            RegisterPathQueryViaController(engine, "survives");
            RegisterPathQueryViaController(engine, "removed-again");
            var delete = Controller(engine).DeleteStoredQuery("removed-again");
            Assert.AreEqual(204, ((StatusCodeResult)delete).StatusCode);
            // Simulated crash: no Save, no Dispose - the WAL alone carries the library.

            var recovered = NewEngineWithWal();

            Assert.AreEqual(1, recovered.StoredQueries.Count,
                "register+register+remove must replay to exactly one entry");
            Assert.IsTrue(recovered.StoredQueries.TryGet(out var entry, "survives"));
            Assert.AreEqual(StoredQueryCompileState.Compiled, entry.CompileState);
            Assert.IsFalse(recovered.StoredQueries.TryGet(out _, "removed-again"));

            // The replayed entry is invocable.
            var graphController = new GraphController(_loggerFactory.CreateLogger<GraphController>(), recovered);
            var outcome = graphController.CalculateShortestPath(0, 1, new PathSpecification { StoredQuery = "survives" });
            Assert.IsNotNull(outcome.Value);

            recovered.Dispose();
        }

        [TestMethod]
        public void Wal_SnapshotPairedRecovery_ManifestPlusReplay_Compose()
        {
            var engine = NewEngineWithWal();
            RegisterPathQueryViaController(engine, "in-snapshot");
            var actualPath = Save(engine, SavePath);
            RegisterPathQueryViaController(engine, "after-snapshot");
            // Simulated crash after the post-snapshot registration.

            var recovered = NewEngineWithWal();
            Load(recovered, actualPath);

            Assert.AreEqual(2, recovered.StoredQueries.Count,
                "the manifest entry and the replayed post-snapshot entry must compose");
            Assert.IsTrue(recovered.StoredQueries.TryGet(out var fromManifest, "in-snapshot"));
            Assert.AreEqual(StoredQueryCompileState.Compiled, fromManifest.CompileState);
            Assert.IsTrue(recovered.StoredQueries.TryGet(out var fromWal, "after-snapshot"));
            Assert.AreEqual(StoredQueryCompileState.Compiled, fromWal.CompileState);

            recovered.Dispose();
        }

        [TestMethod]
        public void Wal_ReplayWithoutCompiler_KeepsSourceOnly()
        {
            var engine = NewEngineWithWal();
            RegisterPathQueryViaController(engine, "no-compiler-replay");
            // Simulated crash.

            var recovered = NewEngineWithWal(withCompiler: false);

            Assert.IsTrue(recovered.StoredQueries.TryGet(out var entry, "no-compiler-replay"),
                "a logged registration replayed without a compiler must be KEPT (source-only), not dropped");
            Assert.AreEqual(StoredQueryCompileState.SourceOnly, entry.CompileState);

            recovered.Dispose();
        }

        [TestMethod]
        public void Wal_ReplayRecompileFailure_KeepsFailed_AndRecoveryContinues()
        {
            var engine = NewEngineWithWal();
            RegisterSourceOnlyViaTransaction(engine, "bad-replay", BadSourceBlock);
            RegisterPathQueryViaController(engine, "good-replay");
            // Simulated crash.

            var recovered = NewEngineWithWal();

            Assert.IsTrue(recovered.StoredQueries.TryGet(out var failed, "bad-replay"));
            Assert.AreEqual(StoredQueryCompileState.Failed, failed.CompileState);
            Assert.IsNotNull(failed.CompileDiagnostics);

            Assert.IsTrue(recovered.StoredQueries.TryGet(out var good, "good-replay"),
                "recovery must continue past a failed recompile");
            Assert.AreEqual(StoredQueryCompileState.Compiled, good.CompileState);

            recovered.Dispose();
        }

        [TestMethod]
        public void Wal_SaveResetsTheLog_AndTheManifestCarriesTheLibrary()
        {
            // Save-WAL symmetry: after a Save, recovery composes manifest + (empty) log to the
            // same library a crash-replay would have produced before it.
            var engine = NewEngineWithWal();
            RegisterPathQueryViaController(engine, "symmetric");
            var actualPath = Save(engine, SavePath);

            var recovered = NewEngineWithWal();
            Load(recovered, actualPath);

            Assert.AreEqual(1, recovered.StoredQueries.Count);
            Assert.IsTrue(recovered.StoredQueries.TryGet(out var entry, "symmetric"));
            Assert.AreEqual(StoredQueryCompileState.Compiled, entry.CompileState);

            recovered.Dispose();
        }

        #endregion

        #region collectible-context unload after delete

        // Registers a stored path query with a unique filter (its own compiled assembly) and
        // returns a weak reference to the pinned traverser's generated type, holding no strong
        // reference. NoInlining ensures no compiled artifact is rooted by this frame afterwards.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private WeakReference RegisterAndWeaklyReferenceArtifact(Fallen8 engine, string name)
        {
            RegisterPathQueryViaController(engine, name,
                vertexFilter: "return (v) => v.Label != \"" + name + "-unique-marker\";");

            Assert.IsTrue(engine.StoredQueries.TryGet(out var entry, name));
            return new WeakReference(entry.Artifact.GetType());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void DeleteAndReleaseBookkeeping(Fallen8 engine, string name)
        {
            Assert.AreEqual(204, ((StatusCodeResult)Controller(engine).DeleteStoredQuery(name)).StatusCode);

            // The register/remove transactions retain entry references in the manager's
            // bookkeeping until it trims; force that so the only question left is whether the
            // LIBRARY leaked a reference.
            var trim = new TrimTransaction();
            engine.EnqueueTransaction(trim).WaitUntilFinished();
        }

        [TestMethod]
        public void DeletedStoredQuery_ArtifactContext_Unloads()
        {
            var engine = NewEngine();

            var artifactTypeRef = RegisterAndWeaklyReferenceArtifact(engine, "unload-probe");
            Assert.IsTrue(artifactTypeRef.IsAlive, "the pinned artifact is alive while registered");

            DeleteAndReleaseBookkeeping(engine, "unload-probe");

            for (int i = 0; i < 15 && artifactTypeRef.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            Assert.IsFalse(artifactTypeRef.IsAlive,
                "the stored query's compiled artifact (and its collectible load context) must unload once deleted");

            engine.Dispose();
        }

        #endregion
    }
}
