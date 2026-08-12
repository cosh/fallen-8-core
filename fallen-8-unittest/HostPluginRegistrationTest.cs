// MIT License
//
// HostPluginRegistrationTest.cs
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
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Analytics;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Plugins;
using NoSQL.GraphDB.Core.Serializer;
using NoSQL.GraphDB.Core.Service;
using NoSQL.GraphDB.Core.Transaction;

// Aliased rather than imported: the path-algorithm namespace contains a type named Path, which would
// make every System.IO.Path use in this file ambiguous.
using PathAlgorithms = NoSQL.GraphDB.Core.Algorithms.Path;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Tests for host plugin registration (feature host-plugin-registration): a host registers a
    ///   plugin TYPE compile-free, and name-based resolution then works in a host where
    ///   assembly-scanning discovery cannot see anything.
    ///
    ///   <para>Every plugin type here is <c>internal</c> ON PURPOSE: <c>PluginFactory</c> discovery
    ///   yields only PUBLIC exported types, so an internal type can never be found by a scan - a
    ///   resolution by name therefore proves the per-namespace registry served it, not the test
    ///   assembly sitting in the base directory.</para>
    /// </summary>
    [TestClass]
    public class HostPluginRegistrationTest
    {
        private ILoggerFactory _loggerFactory;
        private Fallen8 _fallen8;
        private string _tempDir;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
            _fallen8 = new Fallen8(_loggerFactory);
            _tempDir = Path.Combine(Path.GetTempPath(), "f8_hostplugin_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            // The activation/invocation counters are static (a plugin is constructed by the engine, so
            // a test cannot hold the instance); resetting them here is what keeps every test
            // independent of execution order.
            HostPathAlgorithm.Reset();
            HostPathAlgorithmV2.Reset();
            HostAnalyticsAlgorithm.Reset();
        }

        [TestCleanup]
        public void TestCleanup()
        {
            _fallen8.Dispose();

            try
            {
                if (_tempDir != null && Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch { /* best-effort cleanup */ }
        }

        #region helpers

        private string SavePath => Path.Combine(_tempDir, "savegame.f8s");

        private string WalPath => Path.Combine(_tempDir, "savegame.f8s.wal");

        private Fallen8 NewEngineWithWal()
        {
            // No compilers: a source-bearing entry then rehydrates as SourceOnly, which is all these
            // tests need (they ask WHETHER an entry survives, never whether Roslyn ran).
            return new Fallen8(_loggerFactory, new WriteAheadLogOptions(WalPath));
        }

        private Fallen8 NewInlineEngine()
        {
            return new Fallen8(_loggerFactory, transactionExecutionMode: TransactionExecutionMode.Inline);
        }

        private static VertexModel[] CreateVertices(Fallen8 engine, int count)
        {
            var tx = new CreateVerticesTransaction();
            for (var i = 0; i < count; i++)
            {
                tx.AddVertex(1u, "v");
            }
            engine.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedVertices().ToArray();
        }

        private static PathAlgorithms.ShortestPathDefinition PathDefinition()
        {
            return new PathAlgorithms.ShortestPathDefinition { SourceVertexId = 0, DestinationVertexId = 0 };
        }

        /// <summary>A source-bearing (therefore persistable) entry, planted the way the REST layer
        /// would after a compile - the control group every non-persistence test compares against.</summary>
        private static PluginEntry SourceEntry(string name)
        {
            return new PluginEntry(new PluginDefinition
            {
                Name = name,
                Category = PluginCategory.Algorithm,
                Contract = PluginContract.Path,
                SourceCode = "// source",
                Description = "planted",
                CreatedAt = DateTime.UtcNow
            }, PluginCompileState.SourceOnly, null);
        }

        /// <summary>A host entry as <c>RegisterPluginType</c> builds one: a compiled artifact type and
        /// NO source.</summary>
        private static PluginEntry HostEntry(string name)
        {
            return new PluginEntry(new PluginDefinition
            {
                Name = name,
                Category = PluginCategory.Algorithm,
                Contract = PluginContract.Path,
                SourceCode = null,
                Description = "host",
                CreatedAt = DateTime.UtcNow
            }, PluginCompileState.Compiled, typeof(HostPathAlgorithm));
        }

        private static void Register(Fallen8 engine, PluginEntry entry)
        {
            var info = engine.EnqueueTransaction(new RegisterPluginTransaction { Entry = entry });
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState,
                "planting the entry must succeed");
        }

        private static void Remove(Fallen8 engine, string name)
        {
            var info = engine.EnqueueTransaction(new RemovePluginTransaction { Name = name });
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState, "the removal must succeed");
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
            var info = engine.EnqueueTransaction(new LoadTransaction { Path = path });
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState, "the load should finish");
        }

        /// <summary>A save point written by a DIFFERENT engine, holding the given plugin entries.</summary>
        private string SavePointWith(ILoggerFactory loggerFactory, params PluginEntry[] entries)
        {
            var source = new Fallen8(loggerFactory);
            try
            {
                foreach (var entry in entries)
                {
                    Register(source, entry);
                }
                return Save(source, SavePath);
            }
            finally
            {
                source.Dispose();
            }
        }

        /// <summary>The write-ahead-log classifier for a transaction. It is internal to the engine
        /// (which declares no InternalsVisibleTo), so - as elsewhere in this suite - the test reflects
        /// rather than widening that visibility.</summary>
        private static bool IsLoggable(ATransaction tx)
        {
            var codec = typeof(Fallen8).Assembly.GetType("NoSQL.GraphDB.Core.Persistency.WalTransactionCodec");
            var method = codec.GetMethod("TryGetEntryType", BindingFlags.NonPublic | BindingFlags.Static);
            var args = new object[] { tx, null };
            return (bool)method.Invoke(null, args);
        }

        private static IReadOnlyList<string> ManifestNames(string savePath)
        {
            var manifestPath = savePath + "_plugins";
            Assert.IsTrue(File.Exists(manifestPath), "the plugin manifest must exist at " + manifestPath);

            var names = new List<string>();
            using (var document = JsonDocument.Parse(File.ReadAllText(manifestPath)))
            {
                foreach (var definition in document.RootElement.GetProperty("definitions").EnumerateArray())
                {
                    names.Add(definition.GetProperty("name").GetString());
                }
            }
            return names;
        }

        #endregion

        #region 1 - resolution through the string-named APIs

        [TestMethod]
        public void HostRegisteredPathType_ResolvesByName_ThroughTheStringNamedApi()
        {
            var info = _fallen8.RegisterPluginType<HostPathAlgorithm>();
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.Finished, info.TransactionState);
            Assert.IsTrue(_fallen8.TryCalculateShortestPath(out _, "Host-Path", PathDefinition()),
                "a host-registered path type must resolve by name, with no assembly scanning");
            Assert.AreEqual(1, HostPathAlgorithm.Calls);
        }

        [TestMethod]
        public void HostRegisteredAnalyticsType_ResolvesByName_ThroughTheStringNamedApi()
        {
            _fallen8.RegisterPluginType<HostAnalyticsAlgorithm>().WaitUntilFinished();

            Assert.IsTrue(_fallen8.TryRunAnalytics(out _, "Host-Analytics", new GraphAnalyticsDefinition()),
                "a host-registered analytics type must resolve by name");
            Assert.AreEqual(1, HostAnalyticsAlgorithm.Calls);
        }

        [TestMethod]
        public void HostRegisteredEntry_CarriesTheInstancesNameAndDescription_AndNoSource()
        {
            _fallen8.RegisterPluginType<HostPathAlgorithm>().WaitUntilFinished();

            Assert.IsTrue(_fallen8.Plugins.TryGet(out var entry, "Host-Path"),
                "the name is taken from the probe instance's PluginName, never from a parameter");
            Assert.AreEqual("a host-registered path algorithm", entry.Definition.Description);
            Assert.AreEqual(PluginContract.Path, entry.Definition.Contract,
                "the contract is derived from the interface the type implements");
            Assert.AreEqual(PluginCategory.Algorithm, entry.Definition.Category);
            Assert.AreEqual(PluginCompileState.Compiled, entry.CompileState);
            Assert.AreEqual(typeof(HostPathAlgorithm), entry.Artifact);
            Assert.IsNull(entry.Definition.SourceCode, "a host entry carries no source");
            Assert.IsFalse(entry.IsPersistable, "no source means nothing to persist");
        }

        [TestMethod]
        public void HostRegisteredType_IsActivatedFreshOnEveryResolution()
        {
            _fallen8.RegisterPluginType<HostPathAlgorithm>().WaitUntilFinished();

            // The registration itself activates ONE probe instance to read the name/description.
            Assert.AreEqual(1, HostPathAlgorithm.Constructions, "registration probes the type exactly once");

            _fallen8.TryCalculateShortestPath(out _, "Host-Path", PathDefinition());
            _fallen8.TryCalculateShortestPath(out _, "Host-Path", PathDefinition());

            Assert.AreEqual(3, HostPathAlgorithm.Constructions,
                "each resolution activates a fresh instance instead of serving a cached one");
        }

        [TestMethod]
        public void RemovedThenReRegisteredUnderTheSameName_IsNeverServedStale()
        {
            _fallen8.RegisterPluginType<HostPathAlgorithm>().WaitUntilFinished();
            Assert.IsTrue(_fallen8.TryCalculateShortestPath(out _, "Host-Path", PathDefinition()));
            Assert.AreEqual(1, HostPathAlgorithm.Calls);

            Remove(_fallen8, "Host-Path");
            Assert.IsFalse(_fallen8.TryCalculateShortestPath(out _, "Host-Path", PathDefinition()),
                "a removed registration stops resolving");

            // A DIFFERENT type registered under the same PluginName: the new type must serve the call.
            _fallen8.RegisterPluginType<HostPathAlgorithmV2>().WaitUntilFinished();
            Assert.IsTrue(_fallen8.TryCalculateShortestPath(out _, "Host-Path", PathDefinition()));

            Assert.AreEqual(1, HostPathAlgorithm.Calls, "the removed type must not be invoked again");
            Assert.AreEqual(1, HostPathAlgorithmV2.Calls, "the re-registered type serves the call");
        }

        [TestMethod]
        public void HostRegistration_IsInvisibleInAnotherEngine()
        {
            _fallen8.RegisterPluginType<HostPathAlgorithm>().WaitUntilFinished();

            var other = new Fallen8(_loggerFactory);
            try
            {
                Assert.IsFalse(other.Plugins.TryGet(out _, "Host-Path"));
                Assert.IsFalse(other.TryCalculateShortestPath(out _, "Host-Path", PathDefinition()),
                    "the registry is per namespace: one engine's registration is invisible in another");
            }
            finally
            {
                other.Dispose();
            }
        }

        #endregion

        #region 2 - index and service through the registry

        [TestMethod]
        public void HostRegisteredIndexType_IsCreatedByName_WithAFreshInstancePerIndex()
        {
            _fallen8.RegisterPluginType<HostBucketIndex>().WaitUntilFinished();

            Assert.IsTrue(_fallen8.Plugins.TryGet(out var entry, HostBucketIndex.RegisteredName),
                "an index type must be registrable: it is the whole point of the Index contract");
            Assert.AreEqual(PluginContract.Index, entry.Definition.Contract,
                "an IIndex implementation derives the Index contract");
            Assert.AreEqual(PluginCategory.Index, entry.Definition.Category);

            Assert.IsTrue(_fallen8.IndexFactory.TryCreateIndex(out var first, "idx-a", HostBucketIndex.RegisteredName),
                "index creation is the browser-critical resolution: it must go through the registry");
            Assert.IsTrue(_fallen8.IndexFactory.TryCreateIndex(out var second, "idx-b", HostBucketIndex.RegisteredName));

            Assert.IsInstanceOfType(first, typeof(HostBucketIndex));
            Assert.AreNotSame(first, second, "each index IS an instance, so resolution activates a fresh one per call");
        }

        [TestMethod]
        public void HostRegisteredIndexType_ShadowsASameNamedDiscoveredPlugin()
        {
            // The control: the name resolves to the built-in when nothing is registered, so what the
            // registered engine returns below is decided by PRECEDENCE, not by availability.
            Assert.IsTrue(_fallen8.IndexFactory.TryCreateIndex(out var builtIn, "plain", "DictionaryIndex"));
            Assert.IsInstanceOfType(builtIn, typeof(DictionaryIndex));

            _fallen8.RegisterPluginType<ShadowingDictionaryIndex>().WaitUntilFinished();

            Assert.IsTrue(_fallen8.IndexFactory.TryCreateIndex(out var shadowed, "shadowed", "DictionaryIndex"));
            Assert.IsInstanceOfType(shadowed, typeof(ShadowingDictionaryIndex),
                "the registry is consulted BEFORE discovery, as it is for every other plugin family");
        }

        [TestMethod]
        public void HostRegisteredIndexType_RehydratesFromACheckpoint_WithItsContent()
        {
            var vertices = CreateVertices(_fallen8, 3);
            _fallen8.RegisterPluginType<HostBucketIndex>().WaitUntilFinished();
            Assert.IsTrue(_fallen8.IndexFactory.TryCreateIndex(out var index, "host-idx", HostBucketIndex.RegisteredName),
                "the index to be checkpointed must be creatable first");
            index.AddOrUpdate("k", vertices[0]);
            index.AddOrUpdate("k", vertices[1]);
            index.AddOrUpdate("other", vertices[2]);

            var savePoint = Save(_fallen8, SavePath);

            // A host re-registers its types on every start; the checkpoint carries the index by PLUGIN
            // NAME, so the load resolves that name through the registry it just re-established.
            var target = new Fallen8(_loggerFactory);
            try
            {
                target.RegisterPluginType<HostBucketIndex>().WaitUntilFinished();
                Load(target, savePoint);

                Assert.IsTrue(target.IndexFactory.TryGetIndex(out var reloaded, "host-idx"),
                    "rehydration resolves the index plugin by name, so it must consult the registry too");
                Assert.IsInstanceOfType(reloaded, typeof(HostBucketIndex));
                Assert.AreEqual(3, reloaded.CountOfValues(), "the index comes back with its content, not empty");
                Assert.IsTrue(reloaded.TryGetValue(out var bucket, "k"));
                Assert.AreEqual(2, bucket.Count);
                CollectionAssert.AreEquivalent(
                    new[] { vertices[0].Id, vertices[1].Id },
                    bucket.Select(element => element.Id).ToList(),
                    "the reloaded bucket holds the elements of the reloaded graph");
            }
            finally
            {
                target.Dispose();
            }
        }

        [TestMethod]
        public void HostRegisteredIndexType_IsListedAmongTheAvailableIndexPlugins()
        {
            var before = new List<string>(_fallen8.IndexFactory.GetAvailableIndexPlugins());
            CollectionAssert.DoesNotContain(before, HostBucketIndex.RegisteredName);

            _fallen8.RegisterPluginType<HostBucketIndex>().WaitUntilFinished();

            var after = new List<string>(_fallen8.IndexFactory.GetAvailableIndexPlugins());
            CollectionAssert.Contains(after, HostBucketIndex.RegisteredName,
                "a registered index is discoverable, not merely creatable by name");
            CollectionAssert.Contains(after, "DictionaryIndex", "the built-ins stay listed");
        }

        [TestMethod]
        public void HostRegisteredServiceType_IsListedAmongTheAvailableServicePlugins()
        {
            var before = new List<string>(_fallen8.ServiceFactory.GetAvailableServicePlugins());
            CollectionAssert.DoesNotContain(before, HostService.RegisteredName);

            _fallen8.RegisterPluginType<HostService>().WaitUntilFinished();

            var after = new List<string>(_fallen8.ServiceFactory.GetAvailableServicePlugins());
            CollectionAssert.Contains(after, HostService.RegisteredName,
                "index and service are one family: a registered service must be DISCOVERABLE, not merely " +
                "addable by name - otherwise a host that registered it sees an enumeration that denies it exists");
        }

        [TestMethod]
        public void HostRegisteredServiceType_IsAddedByName()
        {
            _fallen8.RegisterPluginType<HostService>().WaitUntilFinished();

            Assert.IsTrue(_fallen8.Plugins.TryGet(out var entry, HostService.RegisteredName));
            Assert.AreEqual(PluginContract.Service, entry.Definition.Contract);
            Assert.AreEqual(PluginCategory.Service, entry.Definition.Category);

            Assert.IsTrue(_fallen8.ServiceFactory.TryAddService(out var service, HostService.RegisteredName, "svc", null),
                "services resolve registry-first as well, so the family is not the odd one out");
            Assert.IsInstanceOfType(service, typeof(HostService));
        }

        #endregion

        #region 7 - the browser execution shape: inline mode

        [TestMethod]
        public void InlineEngine_RegistersATypeAndCreatesAnIndex_ThatSurvivesASaveLoadRoundTrip()
        {
            // The browser shape: no writer thread (see InlineTransactionExecutionTest for that
            // contract), no discoverable dll, so registration plus the registry-first resolutions are
            // the whole path from typeof(T) to a working index.
            var writer = NewInlineEngine();
            string savePoint;
            int[] ids;
            try
            {
                var vertices = CreateVertices(writer, 2);
                ids = vertices.Select(vertex => vertex.Id).ToArray();

                var registration = writer.RegisterPluginType<HostBucketIndex>();
                Assert.AreEqual(TransactionState.Finished, registration.TransactionState,
                    "an inline registration is already terminal when it returns");

                Assert.IsTrue(writer.IndexFactory.TryCreateIndex(out var index, "host-idx", HostBucketIndex.RegisteredName),
                    "creating an index on a single-threaded host is what this feature unblocks");
                index.AddOrUpdate("k", vertices[0]);
                index.AddOrUpdate("k", vertices[1]);

                savePoint = Save(writer, SavePath);
            }
            finally
            {
                writer.Dispose();
            }

            var reader = NewInlineEngine();
            try
            {
                reader.RegisterPluginType<HostBucketIndex>().WaitUntilFinished();
                Load(reader, savePoint);

                Assert.IsTrue(reader.IndexFactory.TryGetIndex(out var reloaded, "host-idx"),
                    "the inline load must resolve the index plugin through the re-established registry");
                Assert.IsInstanceOfType(reloaded, typeof(HostBucketIndex));
                Assert.IsTrue(reloaded.TryGetValue(out var bucket, "k"));
                CollectionAssert.AreEquivalent(ids, bucket.Select(element => element.Id).ToList());
            }
            finally
            {
                reader.Dispose();
            }
        }

        #endregion

        #region 3 - non-persistence of host entries

        [TestMethod]
        public void Wal_HostRegistration_CommitsDurable_AndDoesNotReplay_WhileASourceEntryDoes()
        {
            var engine = NewEngineWithWal();
            Register(engine, SourceEntry("Src-Path"));

            var info = engine.RegisterPluginType<HostPathAlgorithm>();
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.Finished, info.TransactionState);
            Assert.IsTrue(info.Durable,
                "nothing needs to reach disk for a host registration, so the commit is honestly durable");

            engine.Dispose(); // no snapshot taken, so the log is unanchored and replays on the next open

            var recovered = NewEngineWithWal();
            try
            {
                Assert.IsTrue(recovered.Plugins.TryGet(out _, "Src-Path"),
                    "the source-bearing entry in the same log still replays");
                Assert.IsFalse(recovered.Plugins.TryGet(out _, "Host-Path"),
                    "a host registration is not logged, so it does not come back: the host re-registers it");
                Assert.AreEqual(1, recovered.Plugins.Count);
            }
            finally
            {
                recovered.Dispose();
            }
        }

        [TestMethod]
        public void WalCodec_ClassifiesAHostRegistration_AsNotLoggable()
        {
            Assert.IsFalse(IsLoggable(new RegisterPluginTransaction { Entry = HostEntry("Host-Path") }),
                "a non-persistable registration has nothing replayable to log");
            Assert.IsTrue(IsLoggable(new RegisterPluginTransaction { Entry = SourceEntry("Src-Path") }),
                "a source-bearing registration stays loggable");
        }

        [TestMethod]
        public void WalCodec_ClassifiesTheRemovalOfAHostEntry_AsNotLoggable()
        {
            // A removal carries only a name, so what it removed is recorded by its execution. The
            // classification must still be readable after the transaction is terminal (the log frame is
            // buffered at commit time, and a logged remove of an entry that was never logged would
            // replay against nothing).
            _fallen8.RegisterPluginType<HostPathAlgorithm>().WaitUntilFinished();
            Register(_fallen8, SourceEntry("Src-Path"));

            var removeHost = new RemovePluginTransaction { Name = "Host-Path" };
            _fallen8.EnqueueTransaction(removeHost).WaitUntilFinished();
            Assert.IsFalse(IsLoggable(removeHost),
                "removing an entry that was never logged must not be logged either");

            var removeSource = new RemovePluginTransaction { Name = "Src-Path" };
            _fallen8.EnqueueTransaction(removeSource).WaitUntilFinished();
            Assert.IsTrue(IsLoggable(removeSource), "removing a persisted entry stays loggable");
        }

        [TestMethod]
        public void Wal_RemovingAHostEntry_WritesNoFrame_WhileRemovingASourceEntryDoes()
        {
            var engine = NewEngineWithWal();
            try
            {
                Register(engine, SourceEntry("Src-Path"));                            // one frame
                engine.RegisterPluginType<HostPathAlgorithm>().WaitUntilFinished();   // none
                Remove(engine, "Host-Path");                                          // must add none
                Remove(engine, "Src-Path");                                           // one frame
            }
            finally
            {
                engine.Dispose(); // no snapshot: the log is unanchored and replays on the next open
            }

            var recovered = NewEngineWithWal();
            try
            {
                Assert.AreEqual(2, recovered.Durability.LastRecoveryReplayedEntries,
                    "the log holds exactly the source entry's register and its remove - the host entry's pair is absent");
                Assert.AreEqual(0, recovered.Plugins.Count);
            }
            finally
            {
                recovered.Dispose();
            }
        }

        [TestMethod]
        public void Checkpoint_PluginManifest_OmitsHostEntries()
        {
            Register(_fallen8, SourceEntry("Src-Path"));
            _fallen8.RegisterPluginType<HostPathAlgorithm>().WaitUntilFinished();

            var actual = Save(_fallen8, SavePath);

            CollectionAssert.AreEquivalent(new[] { "Src-Path" }, new List<string>(ManifestNames(actual)),
                "the manifest persists source; a host entry has none and is skipped");
        }

        [TestMethod]
        public void Checkpoint_WithOnlyHostEntries_LeavesNoManifest()
        {
            _fallen8.RegisterPluginType<HostPathAlgorithm>().WaitUntilFinished();

            var actual = Save(_fallen8, SavePath);

            Assert.IsFalse(File.Exists(actual + "_plugins"),
                "nothing is persistable, so the save writes no plugin manifest at all");
            Assert.IsTrue(_fallen8.Plugins.TryGet(out _, "Host-Path"),
                "skipping an entry for persistence never unregisters it");
        }

        [TestMethod]
        public void Checkpoint_WithOnlyHostEntries_DeletesAStalePluginManifest()
        {
            Register(_fallen8, SourceEntry("Src-Path"));
            _fallen8.RegisterPluginType<HostPathAlgorithm>().WaitUntilFinished();

            var actual = Save(_fallen8, SavePath);
            var manifestPath = actual + "_plugins";
            Assert.IsTrue(File.Exists(manifestPath), "the first save persists the source entry");

            // A save never overwrites a published save point, so reaching the stale-manifest case takes
            // a manifest whose header is gone - the shape a save that crashed before its commit-point
            // rename leaves behind. The next save to that path must not leave the orphan claiming
            // plugins that this graph no longer persists.
            File.Delete(actual);
            Remove(_fallen8, "Src-Path");

            var second = Save(_fallen8, SavePath);

            Assert.AreEqual(actual, second, "with the header gone, the save reuses the same path");
            Assert.IsFalse(File.Exists(manifestPath),
                "only host entries are left, so the stale manifest is deleted rather than kept");
        }

        #endregion

        #region 4 - host entries survive a Load

        [TestMethod]
        public void Load_KeepsHostRegistrations_WhileReplacingThePersistedOnes()
        {
            var savePoint = SavePointWith(_loggerFactory, SourceEntry("Src-Path"));

            _fallen8.RegisterPluginType<HostPathAlgorithm>().WaitUntilFinished();
            Load(_fallen8, savePoint);

            Assert.IsTrue(_fallen8.Plugins.TryGet(out var host, "Host-Path"),
                "no manifest can hold a host registration, so a load must not take it out of the registry");
            Assert.AreEqual(typeof(HostPathAlgorithm), host.Artifact);
            Assert.IsTrue(_fallen8.TryCalculateShortestPath(out _, "Host-Path", PathDefinition()),
                "it survives INVOCABLE - a load's own index rehydration resolves such types by name");
            Assert.IsTrue(_fallen8.Plugins.TryGet(out _, "Src-Path"),
                "the manifest's entries are still rehydrated");
            Assert.AreEqual(2, _fallen8.Plugins.Count);
        }

        [TestMethod]
        public void Load_OfASavePointWithoutAPluginManifest_KeepsHostRegistrations()
        {
            var savePoint = SavePointWith(_loggerFactory);

            _fallen8.RegisterPluginType<HostPathAlgorithm>().WaitUntilFinished();
            Load(_fallen8, savePoint);

            Assert.IsTrue(_fallen8.Plugins.TryGet(out _, "Host-Path"),
                "an empty manifest replaces the persisted entries - of which there are none - and nothing else");
            Assert.AreEqual(1, _fallen8.Plugins.Count);
        }

        [TestMethod]
        public void Load_OfANameThatCollidesWithAHostRegistration_KeepsTheHostTypeAndLogsAnError()
        {
            var sink = new TestLogSink();
            var factory = sink.CreateFactory();

            // The persisted entry carries the SAME name the host type registers under.
            var savePoint = SavePointWith(factory, SourceEntry("Host-Path"));

            var target = new Fallen8(factory);
            try
            {
                target.RegisterPluginType<HostPathAlgorithm>().WaitUntilFinished();
                Load(target, savePoint);

                Assert.IsTrue(target.Plugins.TryGet(out var entry, "Host-Path"));
                Assert.AreEqual(typeof(HostPathAlgorithm), entry.Artifact,
                    "the host's type is what this process can actually execute, so it wins the name");
                Assert.IsNull(entry.Definition.SourceCode,
                    "the persisted definition is discarded, not blended into the surviving entry");
                Assert.AreEqual(1, target.Plugins.Count);
                Assert.IsTrue(target.TryCalculateShortestPath(out _, "Host-Path", PathDefinition()),
                    "the surviving entry is the invocable one");

                Assert.IsTrue(sink.Contains(LogLevel.Error, "Host-Path", "host registration wins"),
                    "two definitions claiming one name is an operator problem: it is reported, never silent");
            }
            finally
            {
                target.Dispose();
            }
        }

        [TestMethod]
        public void Load_OfACollidingName_StopsCarryingThePersistedSourceForward_WhichIsTheAcceptedCost()
        {
            // The accepted consequence of host-wins, recorded on PluginRegistry.ReplacePersistedEntries:
            // the discarded definition is no longer in the registry, so the NEXT checkpoint does not
            // persist its source any more. Pinned because it is a decision, not an accident - and pinned
            // together with what makes it acceptable: the save point that was loaded still holds the
            // source, so an operator who reads the error can rename one of the two and register again.
            var sink = new TestLogSink();
            var factory = sink.CreateFactory();
            var savePoint = SavePointWith(factory, SourceEntry("Host-Path"));
            CollectionAssert.Contains(new List<string>(ManifestNames(savePoint)), "Host-Path",
                "the starting point: the source-bearing definition IS in the loaded save point's manifest");

            var target = new Fallen8(factory);
            try
            {
                target.RegisterPluginType<HostPathAlgorithm>().WaitUntilFinished();
                Load(target, savePoint);
                Assert.IsTrue(sink.Contains(LogLevel.Error, "Host-Path", "host registration wins"));

                var second = Save(target, Path.Combine(_tempDir, "after-collision.f8s"));

                Assert.IsFalse(File.Exists(second + "_plugins"),
                    "the surviving entry is the host's, which has no source: the new checkpoint carries the " +
                    "colliding definition no further");
                CollectionAssert.Contains(new List<string>(ManifestNames(savePoint)), "Host-Path",
                    "and it is not destroyed: a save never overwrites a published save point, so the loaded " +
                    "one still holds the discarded source");
            }
            finally
            {
                target.Dispose();
            }
        }

        #endregion

        #region 6 - the trim surface of the load path

        [TestMethod]
        public void TheCheckpointLoadPath_DeclaresNoDiscoveryTrimRequirement()
        {
            // Since this feature, index and service rehydration resolve a plugin name through the
            // registry first and reach discovery only behind IndexFactory/ServiceFactory's suppressed
            // seams (pinned in TrimSafetyTest). Nothing on this path declares a discovery requirement,
            // so neither may these four: an annotation broader than the truth teaches a consumer that
            // annotations are noise. The still-valid reason on SaveTransaction/LoadTransaction - a
            // checkpoint's reflectively read PROPERTY VALUES - is a different one, and TrimSafetyTest
            // pins that it stays.
            var persistency = typeof(Fallen8).Assembly.GetType("NoSQL.GraphDB.Core.Persistency.PersistencyFactory");
            Assert.IsNotNull(persistency, "PersistencyFactory was renamed or moved");

            foreach (var name in new[] { "LoadIndices", "LoadServices", "LoadAnIndex", "LoadAService" })
            {
                var method = persistency.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(method, name + ": the member to pin was not found (renamed or removed?)");

                Assert.AreEqual(0,
                    method.GetCustomAttributes(typeof(RequiresUnreferencedCodeAttribute), inherit: false).Length,
                    name + " calls nothing that declares a discovery trim requirement, so it must not declare one.");
            }
        }

        #endregion

        #region 5 - registration rules

        [TestMethod]
        public void Register_TypeWithAnInvalidPluginName_IsInvalidInput()
        {
            var info = _fallen8.RegisterPluginType<InvalidlyNamedAlgorithm>();
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.AreEqual(TransactionFailureReason.InvalidInput, info.FailureReason);
            Assert.AreEqual(0, _fallen8.Plugins.Count);
        }

        [TestMethod]
        public void Register_SameTypeTwice_IsConflict()
        {
            _fallen8.RegisterPluginType<HostPathAlgorithm>().WaitUntilFinished();

            var info = _fallen8.RegisterPluginType<HostPathAlgorithm>();
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.AreEqual(TransactionFailureReason.Conflict, info.FailureReason);
            Assert.AreEqual(1, _fallen8.Plugins.Count);
        }

        [TestMethod]
        public void Register_BeyondTheCeiling_IsQuotaExceeded()
        {
            // Host entries count against the same per-namespace registry ceiling: the cap is about
            // registry size.
            _fallen8.Plugins.MaxCount = 1;
            _fallen8.RegisterPluginType<HostPathAlgorithm>().WaitUntilFinished();

            var info = _fallen8.RegisterPluginType<HostAnalyticsAlgorithm>();
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.AreEqual(TransactionFailureReason.QuotaExceeded, info.FailureReason);
            Assert.AreEqual(1, _fallen8.Plugins.Count);
        }

        [TestMethod]
        public void Register_TypeMatchingNoContract_IsInvalidInput()
        {
            var info = _fallen8.RegisterPluginType<ContractlessPlugin>();
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.AreEqual(TransactionFailureReason.InvalidInput, info.FailureReason);
            Assert.AreEqual(0, _fallen8.Plugins.Count);
        }

        [TestMethod]
        public void Register_TypeMatchingTwoContracts_IsInvalidInput()
        {
            var info = _fallen8.RegisterPluginType<AmbiguousContractPlugin>();
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.AreEqual(TransactionFailureReason.InvalidInput, info.FailureReason,
                "an ambiguous contract cannot be guessed: the registration is rejected");
            Assert.AreEqual(0, _fallen8.Plugins.Count);
        }

        [TestMethod]
        public void Register_TypeWhoseConstructorThrows_IsInvalidInput()
        {
            // The name and description come from a PROBE instance, so construction is the first thing
            // that can fail. It must fail the registration as an ordinary rolled-back transaction - the
            // caller inspects one outcome shape - and never escape to the host as a throw.
            var info = _fallen8.RegisterPluginType<ThrowingProbeAlgorithm>();
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.AreEqual(TransactionFailureReason.InvalidInput, info.FailureReason);
            Assert.AreEqual(0, _fallen8.Plugins.Count, "nothing is registered from a type that cannot be constructed");
        }

        [TestMethod]
        public void Register_TypeWhoseDisposeThrows_StillRegisters_AndTheTypeIsInvocable()
        {
            // The probe is disposed after its name is read, and it was never Initialize()d: a Dispose
            // that tears down state it does not have must not cost the host its registration.
            var info = _fallen8.RegisterPluginType<ThrowingDisposeAlgorithm>();
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.Finished, info.TransactionState,
                "a throwing probe Dispose is best-effort, so the registration stands");
            Assert.IsTrue(_fallen8.TryCalculateShortestPath(out _, ThrowingDisposeAlgorithm.RegisteredName, PathDefinition()),
                "and the registration is a working one, not a half-finished entry");
        }

        [TestMethod]
        public void Register_TypeWithANullPluginName_IsInvalidInput()
        {
            var info = _fallen8.RegisterPluginType<NamelessAlgorithm>();
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.AreEqual(TransactionFailureReason.InvalidInput, info.FailureReason,
                "a null PluginName is nothing a caller could ever address the plugin by");
            Assert.AreEqual(0, _fallen8.Plugins.Count);
        }

        [TestMethod]
        public void Register_TypeWithAPluginNameOverTheLengthLimit_IsInvalidInput()
        {
            // The boundary of PluginRegistry.MaxNameLength, from both sides: a name must stay a usable
            // URL path segment, and the limit is the registry's rule rather than the caller's.
            Assert.AreEqual(PluginRegistry.MaxNameLength, LongestNamedAlgorithm.RegisteredName.Length,
                "the control case sits exactly ON the limit");

            var accepted = _fallen8.RegisterPluginType<LongestNamedAlgorithm>();
            accepted.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, accepted.TransactionState, "the limit itself is allowed");

            var rejected = _fallen8.RegisterPluginType<OverlongNamedAlgorithm>();
            rejected.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, rejected.TransactionState);
            Assert.AreEqual(TransactionFailureReason.InvalidInput, rejected.FailureReason,
                "one character past the limit is rejected");
            Assert.AreEqual(1, _fallen8.Plugins.Count, "only the accepted one is in the registry");
        }

        [TestMethod]
        public void Registry_TryActivate_OfAnEntryWhoseConstructorThrows_IsAReportedNotFound()
        {
            // A pinned artifact type can still fail to construct at resolution time (an entry planted by
            // a compile, or a host type whose constructor only throws under some conditions). The
            // resolution seam must answer false - every caller treats that as "no such plugin" and none
            // of them guards the lookup - and must SAY so, since a failing plugin looks exactly like a
            // misspelled name from the outside.
            var sink = new TestLogSink();
            var engine = new Fallen8(sink.CreateFactory());
            try
            {
                Register(engine, new PluginEntry(new PluginDefinition
                {
                    Name = "Throwing-Path",
                    Category = PluginCategory.Algorithm,
                    Contract = PluginContract.Path,
                    SourceCode = null,
                    Description = "an entry whose artifact cannot be constructed",
                    CreatedAt = DateTime.UtcNow
                }, PluginCompileState.Compiled, typeof(ThrowingProbeAlgorithm)));

                Assert.IsFalse(engine.Plugins.TryActivate<PathAlgorithms.IShortestPathAlgorithm>(out var activated, "Throwing-Path"),
                    "an activation that throws is a failed resolution, not an escaping exception");
                Assert.IsNull(activated);
                Assert.IsTrue(sink.Contains(LogLevel.Error, "Throwing-Path"),
                    "and it is reported: a plugin that cannot be constructed is otherwise indistinguishable from a typo");

                Assert.IsFalse(engine.TryCalculateShortestPath(out _, "Throwing-Path", PathDefinition()),
                    "the string-named API degrades to the same clean false");
                Assert.IsTrue(engine.Plugins.TryGet(out _, "Throwing-Path"),
                    "a failed activation never unregisters the entry: the next call resolves it again");
            }
            finally
            {
                engine.Dispose();
            }
        }

        [TestMethod]
        public void Registry_TryActivate_ForTheWrongContractInterface_IsANotFound()
        {
            _fallen8.RegisterPluginType<HostPathAlgorithm>().WaitUntilFinished();

            Assert.IsTrue(_fallen8.Plugins.TryActivate<PathAlgorithms.IShortestPathAlgorithm>(out _, "Host-Path"),
                "the control: the name resolves for the contract the type implements");

            var constructions = HostPathAlgorithm.Constructions;

            Assert.IsFalse(_fallen8.Plugins.TryActivate<IIndex>(out var asIndex, "Host-Path"),
                "one registry holds every family, so a name must not resolve into the wrong one - an index " +
                "created from a path algorithm would fail far from here");
            Assert.IsNull(asIndex);
            Assert.AreEqual(constructions, HostPathAlgorithm.Constructions,
                "the mismatch is decided from the pinned TYPE, so nothing is constructed and thrown away");
        }

        [TestMethod]
        public void Rollback_RemovesAJustRegisteredHostEntry()
        {
            // No public path rolls a SUCCESSFUL registration back, so the rollback branch is driven
            // directly; the engine declares no InternalsVisibleTo, hence the reflection.
            var tx = new RegisterPluginTransaction { Entry = HostEntry("Host-Path") };

            Assert.IsTrue(InvokeInternal<bool>(tx, "TryExecute"));
            Assert.IsTrue(_fallen8.Plugins.TryGet(out _, "Host-Path"));

            InvokeInternal<object>(tx, "Rollback");
            Assert.IsFalse(_fallen8.Plugins.TryGet(out _, "Host-Path"),
                "a rolled-back registration leaves nothing behind");
            Assert.AreEqual(0, _fallen8.Plugins.Count);
        }

        private T InvokeInternal<T>(ATransaction tx, string name)
        {
            var method = tx.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
            return (T)method.Invoke(tx, new object[] { _fallen8 });
        }

        #endregion
    }

    #region plugin types under test (internal, so no scan can ever find them)

    /// <summary>A host-registrable path algorithm. Counts its activations and invocations, because a
    /// test never holds the instance the engine constructs.</summary>
    internal sealed class HostPathAlgorithm : PathAlgorithms.IShortestPathAlgorithm
    {
        internal static int Constructions;
        internal static int Calls;

        internal static void Reset()
        {
            Constructions = 0;
            Calls = 0;
        }

        public HostPathAlgorithm()
        {
            Interlocked.Increment(ref Constructions);
        }

        public string PluginName => "Host-Path";
        public Type PluginCategory => typeof(PathAlgorithms.IShortestPathAlgorithm);
        public string Description => "a host-registered path algorithm";
        public string Manufacturer => "test";
        public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { }
        public void Dispose() { }

        public bool TryCalculateShortestPath(out List<PathAlgorithms.Path> result, PathAlgorithms.ShortestPathDefinition definition)
        {
            Interlocked.Increment(ref Calls);
            result = new List<PathAlgorithms.Path>();
            return true;
        }
    }

    /// <summary>A second path algorithm carrying the SAME <c>PluginName</c>, so a delete plus
    /// re-register can be told apart from a stale resolution.</summary>
    internal sealed class HostPathAlgorithmV2 : PathAlgorithms.IShortestPathAlgorithm
    {
        internal static int Calls;

        internal static void Reset()
        {
            Calls = 0;
        }

        public string PluginName => "Host-Path";
        public Type PluginCategory => typeof(PathAlgorithms.IShortestPathAlgorithm);
        public string Description => "the replacement path algorithm";
        public string Manufacturer => "test";
        public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { }
        public void Dispose() { }

        public bool TryCalculateShortestPath(out List<PathAlgorithms.Path> result, PathAlgorithms.ShortestPathDefinition definition)
        {
            Interlocked.Increment(ref Calls);
            result = new List<PathAlgorithms.Path>();
            return true;
        }
    }

    internal sealed class HostAnalyticsAlgorithm : IGraphAnalyticsAlgorithm
    {
        internal static int Calls;

        internal static void Reset()
        {
            Calls = 0;
        }

        public string PluginName => "Host-Analytics";
        public Type PluginCategory => typeof(IGraphAnalyticsAlgorithm);
        public string Description => "a host-registered analytics algorithm";
        public string Manufacturer => "test";
        public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { }
        public void Dispose() { }

        public bool TryRunAnalytics(out GraphAnalyticsResult result, GraphAnalyticsDefinition definition)
        {
            Interlocked.Increment(ref Calls);
            result = new GraphAnalyticsResult(new Dictionary<int, double>(), null, true, 0, TimeSpan.Zero, false);
            return true;
        }
    }

    /// <summary>A path algorithm that cannot be constructed: it stands in both for a plugin whose
    /// constructor throws at REGISTRATION (the probe) and for one that throws at RESOLUTION (a pinned
    /// artifact activated later).</summary>
    internal sealed class ThrowingProbeAlgorithm : PathAlgorithms.IShortestPathAlgorithm
    {
        public ThrowingProbeAlgorithm()
        {
            throw new InvalidOperationException("this plugin refuses to be constructed");
        }

        public string PluginName => "Throwing-Probe";
        public Type PluginCategory => typeof(PathAlgorithms.IShortestPathAlgorithm);
        public string Description => "d";
        public string Manufacturer => "test";
        public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { }
        public void Dispose() { }

        public bool TryCalculateShortestPath(out List<PathAlgorithms.Path> result, PathAlgorithms.ShortestPathDefinition definition)
        {
            result = new List<PathAlgorithms.Path>();
            return true;
        }
    }

    /// <summary>Constructs fine, reads fine, and throws when disposed - the probe's exit path.</summary>
    internal sealed class ThrowingDisposeAlgorithm : PathAlgorithms.IShortestPathAlgorithm
    {
        internal const string RegisteredName = "Host-ThrowingDispose";

        public string PluginName => RegisteredName;
        public Type PluginCategory => typeof(PathAlgorithms.IShortestPathAlgorithm);
        public string Description => "d";
        public string Manufacturer => "test";
        public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { }

        public void Dispose()
        {
            throw new InvalidOperationException("this plugin refuses to be disposed");
        }

        public bool TryCalculateShortestPath(out List<PathAlgorithms.Path> result, PathAlgorithms.ShortestPathDefinition definition)
        {
            result = new List<PathAlgorithms.Path>();
            return true;
        }
    }

    /// <summary>Its <c>PluginName</c> is null, which no caller could address it by.</summary>
    internal sealed class NamelessAlgorithm : PathAlgorithms.IShortestPathAlgorithm
    {
        public string PluginName => null;
        public Type PluginCategory => typeof(PathAlgorithms.IShortestPathAlgorithm);
        public string Description => "d";
        public string Manufacturer => "test";
        public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { }
        public void Dispose() { }

        public bool TryCalculateShortestPath(out List<PathAlgorithms.Path> result, PathAlgorithms.ShortestPathDefinition definition)
        {
            result = new List<PathAlgorithms.Path>();
            return true;
        }
    }

    /// <summary>A name exactly <see cref="PluginRegistry.MaxNameLength"/> characters long: the longest
    /// one the registry accepts.</summary>
    internal sealed class LongestNamedAlgorithm : PathAlgorithms.IShortestPathAlgorithm
    {
        internal static readonly string RegisteredName = new string('n', PluginRegistry.MaxNameLength);

        public string PluginName => RegisteredName;
        public Type PluginCategory => typeof(PathAlgorithms.IShortestPathAlgorithm);
        public string Description => "d";
        public string Manufacturer => "test";
        public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { }
        public void Dispose() { }

        public bool TryCalculateShortestPath(out List<PathAlgorithms.Path> result, PathAlgorithms.ShortestPathDefinition definition)
        {
            result = new List<PathAlgorithms.Path>();
            return true;
        }
    }

    /// <summary>One character past the limit.</summary>
    internal sealed class OverlongNamedAlgorithm : PathAlgorithms.IShortestPathAlgorithm
    {
        public string PluginName => new string('n', PluginRegistry.MaxNameLength + 1);
        public Type PluginCategory => typeof(PathAlgorithms.IShortestPathAlgorithm);
        public string Description => "d";
        public string Manufacturer => "test";
        public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { }
        public void Dispose() { }

        public bool TryCalculateShortestPath(out List<PathAlgorithms.Path> result, PathAlgorithms.ShortestPathDefinition definition)
        {
            result = new List<PathAlgorithms.Path>();
            return true;
        }
    }

    /// <summary>Its <c>PluginName</c> is not a valid registry name (a space is not allowed).</summary>
    internal sealed class InvalidlyNamedAlgorithm : PathAlgorithms.IShortestPathAlgorithm
    {
        public string PluginName => "not a valid name";
        public Type PluginCategory => typeof(PathAlgorithms.IShortestPathAlgorithm);
        public string Description => "d";
        public string Manufacturer => "test";
        public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { }
        public void Dispose() { }

        public bool TryCalculateShortestPath(out List<PathAlgorithms.Path> result, PathAlgorithms.ShortestPathDefinition definition)
        {
            result = new List<PathAlgorithms.Path>();
            return true;
        }
    }

    /// <summary>A host-registrable index. All behaviour comes from <see cref="ABucketIndex"/>, so a
    /// checkpoint round trip carries real CONTENT rather than an empty shell.</summary>
    internal sealed class HostBucketIndex : ABucketIndex
    {
        internal const string RegisteredName = "Host-Index";

        public override string PluginName => RegisteredName;

        public override string Description => "a host-registered index";
    }

    /// <summary>Carries the PluginName of a BUILT-IN index, so registry-vs-discovery precedence can be
    /// observed against a name that genuinely resolves both ways.</summary>
    internal sealed class ShadowingDictionaryIndex : ABucketIndex
    {
        public override string PluginName => "DictionaryIndex";

        public override string Description => "the host's own take on the dictionary index";
    }

    /// <summary>A host-registrable service; inert apart from its running flag.</summary>
    internal sealed class HostService : IService
    {
        internal const string RegisteredName = "Host-Service";

        public string PluginName => RegisteredName;
        public Type PluginCategory => typeof(IService);
        public string Description => "a host-registered service";
        public string Manufacturer => "test";
        public DateTime StartTime => DateTime.MinValue;

        public bool IsRunning
        {
            get; private set;
        }

        public IDictionary<string, string> Metadata => new Dictionary<string, string>();

        public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { }
        public void Save(SerializationWriter writer) { }
        public void Load(SerializationReader reader, IFallen8 fallen8) { }
        public void OnServiceRestart() { }
        public void Dispose() { }

        public bool TryStart()
        {
            IsRunning = true;
            return true;
        }

        public bool TryStop()
        {
            IsRunning = false;
            return true;
        }
    }

    /// <summary>An <see cref="IPlugin"/> implementing no plugin CONTRACT interface, so no contract can
    /// be derived for it.</summary>
    internal sealed class ContractlessPlugin : IPlugin
    {
        public string PluginName => "Contractless";
        public Type PluginCategory => typeof(IPlugin);
        public string Description => "d";
        public string Manufacturer => "test";
        public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { }
        public void Dispose() { }
    }

    /// <summary>Implements TWO contract interfaces, so the contract is ambiguous.</summary>
    internal sealed class AmbiguousContractPlugin : PathAlgorithms.IShortestPathAlgorithm, IGraphAnalyticsAlgorithm
    {
        public string PluginName => "Ambiguous";
        public Type PluginCategory => typeof(PathAlgorithms.IShortestPathAlgorithm);
        public string Description => "d";
        public string Manufacturer => "test";
        public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { }
        public void Dispose() { }

        public bool TryCalculateShortestPath(out List<PathAlgorithms.Path> result, PathAlgorithms.ShortestPathDefinition definition)
        {
            result = new List<PathAlgorithms.Path>();
            return true;
        }

        public bool TryRunAnalytics(out GraphAnalyticsResult result, GraphAnalyticsDefinition definition)
        {
            result = null;
            return false;
        }
    }

    #endregion
}
