// MIT License
//
// WalSubGraphSupportTest.cs
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
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using NoSQL.GraphDB.Core.SubGraph;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Covers the WAL subgraph-support feature: with the opt-in write-ahead log enabled, subgraph
    /// creates (that carry a recipe) and removes are logged and replayed, so a subgraph survives a
    /// crash+replay exactly when it survives a Save+Load. Exercises the snapshot-paired and
    /// unanchored recovery paths, create+remove, nested subgraphs, the delegate-only (no-recipe)
    /// exclusion, the no-compiler skip, and torn-tail safety.
    /// </summary>
    [TestClass]
    public class WalSubGraphSupportTest
    {
        private ILoggerFactory _loggerFactory;
        private string _tempDir;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
            _tempDir = Path.Combine(Path.GetTempPath(), "f8_walsg_" + Guid.NewGuid().ToString("N"));
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

        private Fallen8 NewEngineWithWal()
        {
            return new Fallen8(_loggerFactory, new WriteAheadLogOptions(WalPath));
        }

        private Fallen8 NewEngineWithWalAndCompiler()
        {
            return new Fallen8(_loggerFactory, new WriteAheadLogOptions(WalPath), new RecipeSubGraphCompiler());
        }

        /// <summary>Alice(person, 0) --knows--> Bob(person, 1); Alice --works_at--> TechCorp(company, 2).</summary>
        private static void AddPeopleGraph(Fallen8 f8)
        {
            const uint creationDate = 1u;
            var vtx = new CreateVerticesTransaction();
            vtx.AddVertex(creationDate, "person", new Dictionary<string, object> { { "name", "Alice" } });
            vtx.AddVertex(creationDate, "person", new Dictionary<string, object> { { "name", "Bob" } });
            vtx.AddVertex(creationDate, "company", new Dictionary<string, object> { { "name", "TechCorp" } });
            f8.EnqueueTransaction(vtx).WaitUntilFinished();
            var v = vtx.GetCreatedVertices();

            var etx = new CreateEdgesTransaction();
            etx.AddEdge(v[0].Id, "knows", v[1].Id, creationDate, "knows");
            etx.AddEdge(v[0].Id, "works_at", v[2].Id, creationDate, "works_at");
            f8.EnqueueTransaction(etx).WaitUntilFinished();
        }

        /// <summary>person --knows--> person: a vertex-edge-vertex pattern that pulls in the knows edge.</summary>
        private static SubGraphSpecification PersonKnowsPerson(string name)
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

        private static SubGraphSpecification AllPersons(string name)
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

        private static void CreateSubGraphViaController(Fallen8 f8, SubGraphSpecification spec, string fromSubGraph = null)
        {
            var controller = new SubGraphController(TestLoggerFactory.Create().CreateLogger<SubGraphController>(), f8);
            var result = controller.CreateSubGraph(spec, fromSubGraph).Result;
            Assert.IsInstanceOfType(result, typeof(CreatedResult),
                "Creating subgraph \"" + spec.Name + "\" should return Created; got " + result.GetType().Name);
        }

        private static string Save(Fallen8 f8, string path)
        {
            var tx = new SaveTransaction { Path = path, SavePartitions = 1 };
            var info = f8.EnqueueTransaction(tx);
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState, "The save should finish.");
            return tx.ActualPath;
        }

        private static (TransactionState State, Exception Error) Load(Fallen8 f8, string path)
        {
            var tx = new LoadTransaction { Path = path };
            var info = f8.EnqueueTransaction(tx);
            info.WaitUntilFinished();
            return (info.TransactionState, info.Error);
        }

        private static ILoggerFactory CapturingFactory(CapturingLoggerProvider provider)
        {
            return LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddProvider(provider);
            });
        }

        /// <summary>Records emitted log entries so a test can assert a specific warning fired.</summary>
        private sealed class CapturingLoggerProvider : ILoggerProvider
        {
            private readonly object _gate = new object();
            private readonly List<(LogLevel Level, string Message)> _entries = new List<(LogLevel, string)>();

            public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

            public void Dispose() { }

            public bool HasWarningContaining(string fragment)
            {
                lock (_gate)
                {
                    return _entries.Any(e => e.Level == LogLevel.Warning
                                             && e.Message.IndexOf(fragment, StringComparison.Ordinal) >= 0);
                }
            }

            private void Record(LogLevel level, string message)
            {
                lock (_gate)
                {
                    _entries.Add((level, message));
                }
            }

            private sealed class CapturingLogger : ILogger
            {
                private readonly CapturingLoggerProvider _owner;
                public CapturingLogger(CapturingLoggerProvider owner) => _owner = owner;
                public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;
                public bool IsEnabled(LogLevel logLevel) => true;
                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                    Func<TState, Exception, string> formatter) => _owner.Record(logLevel, formatter(state, exception));
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new NullScope();
                public void Dispose() { }
            }
        }

        /// <summary>A misbehaving custom compiler that THROWS from TryCompile (violates the Try contract).</summary>
        private sealed class ThrowingRecipeCompiler : ISubGraphRecipeCompiler
        {
            public bool TryCompile(SubGraphRecipe recipe, out SubGraphDefinition definition, out string error)
                => throw new InvalidOperationException("boom from a misbehaving custom compiler");
        }

        #endregion

        #region snapshot-paired recovery

        [TestMethod]
        public void SnapshotPaired_SubgraphCreatedAfterSnapshot_IsReplayedOntoTheSnapshot()
        {
            // The documented durability model: Save (snapshot + reset/anchor the log), then create a
            // subgraph (logged, post-snapshot), then crash. Recovery = Load(snapshot); the subgraph is
            // replayed from the WAL onto the loaded snapshot.
            var source = NewEngineWithWalAndCompiler();
            AddPeopleGraph(source);
            var snapshotPath = Save(source, SavePath);           // log now empty + anchored to the snapshot

            CreateSubGraphViaController(source, PersonKnowsPerson("people")); // logged in the WAL, after the snapshot
            source.Dispose();

            // Recover: set the compiler BEFORE Load, since the snapshot-paired log replays during Load.
            var recovered = NewEngineWithWal();
            recovered.SubGraphRecipeCompiler = new RecipeSubGraphCompiler();
            var (state, error) = Load(recovered, snapshotPath);

            Assert.AreEqual(TransactionState.Finished, state, "Recovery load should succeed; instead: " + error);
            Assert.AreEqual(3, recovered.VertexCount, "The base graph loads from the snapshot.");
            Assert.IsTrue(recovered.SubGraphFactory.TryGetSubGraph(out var people, "people"),
                "The post-snapshot subgraph must be replayed from the WAL.");
            Assert.AreEqual(2, people.SubGraph.VertexCount, "It keeps the two persons Alice and Bob.");
            Assert.AreEqual(1, people.SubGraph.EdgeCount, "It keeps the knows edge.");
            Assert.IsNotNull(people.Recipe, "The replayed subgraph re-carries its recipe so a later Save re-persists it.");
            Assert.IsTrue(recovered.SubGraphFactory.CanRecalculateSubGraph("people"),
                "The replayed subgraph is fully wired (source + definition + algorithm) and recalculable.");
            recovered.Dispose();
        }

        [TestMethod]
        public void SnapshotPaired_SnapshotPersistedAndWalReplayedSubgraphs_BothRecover()
        {
            // A subgraph registered BEFORE the snapshot is persisted in the recipe manifest; a second
            // one created AFTER the snapshot lives only in the WAL. Both must come back after recovery -
            // the two durability paths compose.
            var source = NewEngineWithWalAndCompiler();
            AddPeopleGraph(source);
            CreateSubGraphViaController(source, AllPersons("beforeSnapshot")); // will be in the snapshot manifest
            var snapshotPath = Save(source, SavePath);

            CreateSubGraphViaController(source, AllPersons("afterSnapshot"));  // will be in the WAL only
            source.Dispose();

            var recovered = NewEngineWithWal();
            recovered.SubGraphRecipeCompiler = new RecipeSubGraphCompiler();
            var (state, error) = Load(recovered, snapshotPath);

            Assert.AreEqual(TransactionState.Finished, state, "Recovery load should succeed; instead: " + error);
            Assert.IsTrue(recovered.SubGraphFactory.TryGetSubGraph(out _, "beforeSnapshot"),
                "The pre-snapshot subgraph recovers from the snapshot recipe manifest.");
            Assert.IsTrue(recovered.SubGraphFactory.TryGetSubGraph(out _, "afterSnapshot"),
                "The post-snapshot subgraph recovers from the WAL replay.");
            recovered.Dispose();
        }

        #endregion

        #region unanchored recovery

        [TestMethod]
        public void Unanchored_SubgraphCreate_RecoversWhenCompilerGivenAtConstruction()
        {
            // No snapshot ever: the log is unanchored and replays during construction. Since a subgraph
            // needs the recipe compiler to rebuild and the property cannot be set before construction,
            // the compiler is passed to the constructor so the unanchored path recovers subgraphs too.
            var source = NewEngineWithWalAndCompiler();
            AddPeopleGraph(source);
            CreateSubGraphViaController(source, AllPersons("people"));
            source.Dispose();

            var recovered = NewEngineWithWalAndCompiler(); // compiler registered before the constructor replay
            Assert.AreEqual(3, recovered.VertexCount, "The unanchored graph elements recover.");
            Assert.IsTrue(recovered.SubGraphFactory.TryGetSubGraph(out var people, "people"),
                "The unanchored subgraph entry recovers because the compiler was available at construction.");
            Assert.AreEqual(2, people.SubGraph.VertexCount);
            recovered.Dispose();
        }

        [TestMethod]
        public void Unanchored_CreateThenRemove_ReplaysToAbsent()
        {
            // A create followed by a remove within the logged window nets to absent after replay
            // (both are logged; commit-order replay applies create then remove).
            var source = NewEngineWithWalAndCompiler();
            AddPeopleGraph(source);
            // "control" is created and KEPT; "ephemeral" is created then removed. Both creates are
            // logged, so after replay the only difference between them is the logged removal - the
            // control being PRESENT while ephemeral is ABSENT proves the remove entry is what makes
            // ephemeral disappear, not a failure to log/replay the create.
            CreateSubGraphViaController(source, AllPersons("control"));
            CreateSubGraphViaController(source, AllPersons("ephemeral"));
            source.EnqueueTransaction(new RemoveSubGraphTransaction { SubGraphName = "ephemeral" }).WaitUntilFinished();
            Assert.IsTrue(source.SubGraphFactory.TryGetSubGraph(out _, "control"), "Control kept before the crash.");
            Assert.IsFalse(source.SubGraphFactory.TryGetSubGraph(out _, "ephemeral"), "Removed before the crash.");
            source.Dispose();

            var recovered = NewEngineWithWalAndCompiler();
            Assert.AreEqual(3, recovered.VertexCount, "The base graph still recovers.");
            Assert.IsTrue(recovered.SubGraphFactory.TryGetSubGraph(out _, "control"),
                "The kept subgraph's create is logged and replayed - so 'ephemeral' being absent is due to the removal, not a missing create.");
            Assert.IsFalse(recovered.SubGraphFactory.TryGetSubGraph(out _, "ephemeral"),
                "The create+remove pair replays to absent - the removal is logged and replayed too.");
            recovered.Dispose();
        }

        [TestMethod]
        public void Unanchored_NestedSubgraph_ReplaysAfterItsSourceByName()
        {
            // A is a root subgraph; B is nested (sourced from A). Commit-order replay recreates A first,
            // then resolves B's source by its stable name - no id remapping needed.
            var source = NewEngineWithWalAndCompiler();
            AddPeopleGraph(source);
            CreateSubGraphViaController(source, AllPersons("A"));
            CreateSubGraphViaController(source, AllPersons("B"), "A"); // nested: source is A
            Assert.IsTrue(source.SubGraphFactory.TryGetSubGraph(out var aBefore, "A"));
            Assert.IsTrue(source.SubGraphFactory.TryGetSubGraph(out var bBefore, "B"));
            Assert.AreEqual(aBefore.SubGraph.Id, bBefore.SourceFallen8Id, "B is sourced from A before the crash.");
            source.Dispose();

            var recovered = NewEngineWithWalAndCompiler();
            Assert.IsTrue(recovered.SubGraphFactory.TryGetSubGraph(out var a, "A"), "Root A recovers.");
            Assert.IsTrue(recovered.SubGraphFactory.TryGetSubGraph(out var b, "B"), "Nested B recovers.");
            Assert.AreEqual(2, a.SubGraph.VertexCount);
            Assert.AreEqual(2, b.SubGraph.VertexCount);
            Assert.AreEqual(a.SubGraph.Id, b.SourceFallen8Id,
                "B is sourced from the replayed A (resolved by name), not the root graph.");
            recovered.Dispose();
        }

        #endregion

        #region exclusions and robustness

        [TestMethod]
        public void DelegateOnlySubgraph_HasNoRecipe_IsNotLogged_AndAbsentAfterReplay()
        {
            // A subgraph created via a transaction with NO specification (delegate-only) gets no recipe,
            // so - exactly like snapshot save - it is not logged and does not come back after replay.
            var source = NewEngineWithWalAndCompiler();
            AddPeopleGraph(source);

            var definition = new SubGraphDefinition
            {
                Name = "delegate-only",
                Pattern = new List<APattern>
                {
                    new VertexPattern { PatternName = "p", GraphElement = ge => ge.Label == "person" }
                }
            };
            var tx = new CreateSubGraphTransaction { Definition = definition }; // no SpecificationJson
            source.EnqueueTransaction(tx).WaitUntilFinished();
            Assert.IsNotNull(tx.SubGraphCreated, "The delegate-only subgraph is still created in memory.");
            Assert.IsNull(tx.SubGraphCreated.Recipe, "No specification => no recipe attached.");

            // A logged marker after it, to prove the WAL is otherwise active and replaying.
            var probe = new CreateVerticesTransaction();
            probe.AddVertex(1u, "person", new Dictionary<string, object> { { "name", "Zoe" } }); // id 3
            source.EnqueueTransaction(probe).WaitUntilFinished();
            source.Dispose();

            var recovered = NewEngineWithWalAndCompiler(); // compiler present: absence is due to not-logging
            Assert.AreEqual(4, recovered.VertexCount, "The base graph plus the logged probe vertex recover.");
            Assert.IsFalse(recovered.SubGraphFactory.TryGetSubGraph(out _, "delegate-only"),
                "A delegate-only (recipe-less) subgraph is never logged, so it is absent after replay.");
            recovered.Dispose();
        }

        [TestMethod]
        public void NoCompilerAtRecovery_SubgraphEntrySkippedWithWarning_LaterEntriesStillReplay()
        {
            // The source logs a subgraph create (recipe present) followed by another vertex. Recovering
            // WITHOUT a compiler must skip the subgraph entry with a loud warning yet KEEP replaying:
            // the later vertex still recovers, and recovery never throws.
            var source = NewEngineWithWalAndCompiler();
            AddPeopleGraph(source);
            CreateSubGraphViaController(source, AllPersons("people")); // logged (recipe present)
            var after = new CreateVerticesTransaction();
            after.AddVertex(1u, "person", new Dictionary<string, object> { { "name", "Zoe" } }); // id 3, logged AFTER the subgraph
            source.EnqueueTransaction(after).WaitUntilFinished();
            source.Dispose();

            var capture = new CapturingLoggerProvider();
            var recovered = new Fallen8(CapturingFactory(capture), new WriteAheadLogOptions(WalPath)); // NO compiler

            Assert.AreEqual(4, recovered.VertexCount,
                "The vertex logged AFTER the skipped subgraph entry still recovers - replay did not halt.");
            Assert.IsFalse(recovered.SubGraphFactory.TryGetSubGraph(out _, "people"),
                "Without a compiler the subgraph entry is skipped, not applied.");
            Assert.IsTrue(capture.HasWarningContaining("no recipe compiler is registered"),
                "Skipping a subgraph entry for lack of a compiler must be signalled with a warning.");
            recovered.Dispose();
        }

        [TestMethod]
        public void ThrowingCompilerAtRecovery_SubgraphEntrySkipped_RecoveryContinues()
        {
            // A registered ISubGraphRecipeCompiler is third-party code; if it THROWS (violating the
            // Try contract) recovery must SKIP that subgraph and keep going, not abort. Without the
            // guard in ReplaySubGraphCreate the throw would escape the constructor's unanchored replay
            // and the fresh-engine construction below would itself throw. Pins that guard.
            var source = NewEngineWithWalAndCompiler();
            AddPeopleGraph(source);
            CreateSubGraphViaController(source, AllPersons("people")); // logged (recipe present)
            var after = new CreateVerticesTransaction();
            after.AddVertex(1u, "person", new Dictionary<string, object> { { "name", "Zoe" } }); // id 3, logged AFTER
            source.EnqueueTransaction(after).WaitUntilFinished();
            source.Dispose();

            // Unanchored replay happens in the constructor, here with a compiler that throws.
            var recovered = new Fallen8(_loggerFactory, new WriteAheadLogOptions(WalPath), new ThrowingRecipeCompiler());
            Assert.AreEqual(4, recovered.VertexCount,
                "A throwing compiler must not abort recovery - the vertex logged after the subgraph still recovers.");
            Assert.IsFalse(recovered.SubGraphFactory.TryGetSubGraph(out _, "people"),
                "The subgraph whose compile threw is skipped, and recovery continued past it.");
            recovered.Dispose();
        }

        [TestMethod]
        public void TornTail_TruncatedSubgraphEntry_IsIgnored_EarlierEntriesRecover()
        {
            // The subgraph create is the LAST appended entry; truncating a few trailing bytes tears its
            // frame. The WAL framing guard ignores the torn entry, and the earlier (complete) graph
            // entries still recover - subgraph entries participate in the same torn-tail safety.
            var source = NewEngineWithWalAndCompiler();
            AddPeopleGraph(source);
            CreateSubGraphViaController(source, AllPersons("people")); // last entry in the log
            source.Dispose();

            using (var fs = new FileStream(WalPath, FileMode.Open, FileAccess.Write))
            {
                fs.SetLength(fs.Length - 5); // tear the trailing CRC of the last (subgraph) entry
            }

            var recovered = NewEngineWithWalAndCompiler();
            Assert.AreEqual(3, recovered.VertexCount, "The complete graph entries recover.");
            Assert.AreEqual(2, recovered.EdgeCount,
                "Both edge entries (before the subgraph) are intact - proving ONLY the trailing subgraph frame tore, uniquely pinning this as a subgraph-tail test.");
            Assert.IsFalse(recovered.SubGraphFactory.TryGetSubGraph(out _, "people"),
                "The torn trailing subgraph entry is ignored, never half-applied.");
            recovered.Dispose();
        }

        #endregion
    }
}
