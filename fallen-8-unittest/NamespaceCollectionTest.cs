// MIT License
//
// NamespaceCollectionTest.cs
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
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Namespaces;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Pins the namespace collection (feature graph-namespaces Phase 1): the reserved default
    ///   namespace, name validation, create/rename/drop semantics with their failure reasons, the
    ///   quota, engine isolation, and the durable-mode on-disk layout keyed by the immutable
    ///   namespace id.
    /// </summary>
    [TestClass]
    public class NamespaceCollectionTest
    {
        #region helpers

        private static Fallen8Namespaces CreateCollection(Int32 maxNamespaces = 10000, String storageDirectory = null)
        {
            var durability = new Fallen8DurabilityOptions
            {
                // No storage directory = volatile collection (no WAL files touched by unit tests);
                // the durable-mode tests pass an explicit temp directory.
                Volatile = storageDirectory == null,
                StorageDirectory = storageDirectory
            };

            return new Fallen8Namespaces(
                TestLoggerFactory.Create(),
                Options.Create(durability),
                Options.Create(new Fallen8StoredQueryOptions()),
                Options.Create(new Fallen8ChangeFeedOptions()),
                Options.Create(new Fallen8NamespacesOptions { MaxNamespaces = maxNamespaces }),
                Options.Create(new Fallen8MetadataOptions
                {
                    // Keep durable-mode catalogs inside the test's temp storage directory; volatile
                    // collections never touch the catalog.
                    Directory = storageDirectory == null ? null : Path.Combine(storageDirectory, "metadata")
                }));
        }

        private static void AddVertex(NoSQL.GraphDB.Core.Fallen8 engine)
        {
            var info = engine.EnqueueTransaction(new CreateVerticesTransaction
            {
                Vertices = new List<VertexDefinition> { new VertexDefinition { CreationDate = 1, Properties = null } }
            });
            info.WaitUntilFinished();
        }

        #endregion

        #region boot

        [TestMethod]
        public void Boot_HoldsExactlyTheReadyDefaultNamespace()
        {
            using var namespaces = CreateCollection();

            Assert.AreEqual(1, namespaces.Count);
            Assert.IsTrue(namespaces.TryGet(Fallen8Namespaces.DefaultName, out var ns));
            Assert.AreSame(namespaces.Default, ns);
            Assert.AreEqual(NamespaceState.Ready, ns.State);
            Assert.IsNotNull(ns.Engine);
            Assert.IsFalse(String.IsNullOrEmpty(ns.Id));

            var snapshot = namespaces.Snapshot();
            Assert.AreEqual(1, snapshot.Count);
            Assert.AreEqual(Fallen8Namespaces.DefaultName, snapshot[0].Name);
        }

        #endregion

        #region name validation

        [TestMethod]
        public void IsValidName_AcceptsLowercaseDigitsHyphensUpTo63()
        {
            Assert.IsTrue(Fallen8Namespaces.IsValidName("a"));
            Assert.IsTrue(Fallen8Namespaces.IsValidName("flights"));
            Assert.IsTrue(Fallen8Namespaces.IsValidName("fraud-q3"));
            Assert.IsTrue(Fallen8Namespaces.IsValidName("0-9"));
            Assert.IsTrue(Fallen8Namespaces.IsValidName(new String('a', 63)));

            // Windows reserved device names ARE valid namespace names: on-disk locations are keyed
            // by the immutable namespace id, never by the user-supplied name.
            Assert.IsTrue(Fallen8Namespaces.IsValidName("con"));
        }

        [TestMethod]
        public void IsValidName_RejectsEverythingElse()
        {
            Assert.IsFalse(Fallen8Namespaces.IsValidName(null));
            Assert.IsFalse(Fallen8Namespaces.IsValidName(""));
            Assert.IsFalse(Fallen8Namespaces.IsValidName(new String('a', 64)));
            Assert.IsFalse(Fallen8Namespaces.IsValidName("Flights"));
            Assert.IsFalse(Fallen8Namespaces.IsValidName("under_score"));
            Assert.IsFalse(Fallen8Namespaces.IsValidName("dot.name"));
            Assert.IsFalse(Fallen8Namespaces.IsValidName("space name"));
            Assert.IsFalse(Fallen8Namespaces.IsValidName("slash/name"));
            Assert.IsFalse(Fallen8Namespaces.IsValidName("ümlaut"));
        }

        #endregion

        #region create

        [TestMethod]
        public void TryCreate_CreatesAnIsolatedEngine()
        {
            using var namespaces = CreateCollection();

            Assert.IsTrue(namespaces.TryCreate("flights", out var flights, out var failure));
            Assert.AreEqual(NamespaceFailure.None, failure);
            Assert.AreEqual(2, namespaces.Count);
            Assert.AreEqual(NamespaceState.Ready, flights.State);
            Assert.AreNotSame(namespaces.Default.Engine, flights.Engine);
            Assert.AreNotEqual(namespaces.Default.Id, flights.Id);

            // Data written to one namespace is invisible to the other.
            AddVertex(flights.Engine);
            Assert.AreEqual(1, flights.Engine.VertexCount);
            Assert.AreEqual(0, namespaces.Default.Engine.VertexCount);
        }

        [TestMethod]
        public void TryCreate_RejectsDuplicatesIncludingDefault()
        {
            using var namespaces = CreateCollection();
            Assert.IsTrue(namespaces.TryCreate("flights", out _, out _));

            Assert.IsFalse(namespaces.TryCreate("flights", out var ns, out var failure));
            Assert.IsNull(ns);
            Assert.AreEqual(NamespaceFailure.Conflict, failure);

            Assert.IsFalse(namespaces.TryCreate(Fallen8Namespaces.DefaultName, out _, out failure));
            Assert.AreEqual(NamespaceFailure.Conflict, failure);
        }

        [TestMethod]
        public void TryCreate_RejectsInvalidNames()
        {
            using var namespaces = CreateCollection();

            Assert.IsFalse(namespaces.TryCreate("Flights", out var ns, out var failure));
            Assert.IsNull(ns);
            Assert.AreEqual(NamespaceFailure.InvalidName, failure);
            Assert.AreEqual(1, namespaces.Count);
        }

        [TestMethod]
        public void TryCreate_EnforcesTheQuota_AndDropReleasesIt()
        {
            // Quota counts every namespace INCLUDING default, so 2 leaves room for exactly one more.
            using var namespaces = CreateCollection(maxNamespaces: 2);

            Assert.IsTrue(namespaces.TryCreate("first", out _, out _));
            Assert.IsFalse(namespaces.TryCreate("second", out _, out var failure));
            Assert.AreEqual(NamespaceFailure.QuotaExceeded, failure);

            Assert.IsTrue(namespaces.TryDrop("first", out _));
            Assert.IsTrue(namespaces.TryCreate("second", out _, out _));
        }

        #endregion

        #region rename

        [TestMethod]
        public void TryRename_IsAPureMetadataOperation()
        {
            using var namespaces = CreateCollection();
            Assert.IsTrue(namespaces.TryCreate("flights", out var created, out _));
            var engine = created.Engine;
            var id = created.Id;

            Assert.IsTrue(namespaces.TryRename("flights", "fl-eu", out var renamed, out var failure));
            Assert.AreEqual(NamespaceFailure.None, failure);
            Assert.AreEqual("fl-eu", renamed.Name);
            Assert.AreSame(engine, renamed.Engine);
            Assert.AreEqual(id, renamed.Id);

            Assert.IsFalse(namespaces.TryGet("flights", out _));
            Assert.IsTrue(namespaces.TryGet("fl-eu", out var resolved));
            Assert.AreSame(renamed, resolved);
            Assert.AreEqual(2, namespaces.Count);
        }

        [TestMethod]
        public void TryRename_FailureMatrix()
        {
            using var namespaces = CreateCollection();
            Assert.IsTrue(namespaces.TryCreate("flights", out _, out _));
            Assert.IsTrue(namespaces.TryCreate("scratch", out _, out _));

            // The reserved default cannot be renamed.
            Assert.IsFalse(namespaces.TryRename(Fallen8Namespaces.DefaultName, "renamed", out _, out var failure));
            Assert.AreEqual(NamespaceFailure.Reserved, failure);

            // Unknown source.
            Assert.IsFalse(namespaces.TryRename("missing", "target", out _, out failure));
            Assert.AreEqual(NamespaceFailure.NotFound, failure);

            // Target name in use - including "default", which always exists.
            Assert.IsFalse(namespaces.TryRename("flights", "scratch", out _, out failure));
            Assert.AreEqual(NamespaceFailure.Conflict, failure);
            Assert.IsFalse(namespaces.TryRename("flights", Fallen8Namespaces.DefaultName, out _, out failure));
            Assert.AreEqual(NamespaceFailure.Conflict, failure);

            // Invalid target name.
            Assert.IsFalse(namespaces.TryRename("flights", "Bad_Name", out _, out failure));
            Assert.AreEqual(NamespaceFailure.InvalidName, failure);

            // The failed attempts changed nothing.
            Assert.IsTrue(namespaces.TryGet("flights", out _));
            Assert.AreEqual(3, namespaces.Count);
        }

        #endregion

        #region drop

        [TestMethod]
        public void TryDrop_RemovesTheNamespace()
        {
            using var namespaces = CreateCollection();
            Assert.IsTrue(namespaces.TryCreate("flights", out _, out _));

            Assert.IsTrue(namespaces.TryDrop("flights", out var failure));
            Assert.AreEqual(NamespaceFailure.None, failure);
            Assert.IsFalse(namespaces.TryGet("flights", out _));
            Assert.AreEqual(1, namespaces.Count);
        }

        [TestMethod]
        public void TryDrop_FailureMatrix()
        {
            using var namespaces = CreateCollection();

            Assert.IsFalse(namespaces.TryDrop(Fallen8Namespaces.DefaultName, out var failure));
            Assert.AreEqual(NamespaceFailure.Reserved, failure);

            Assert.IsFalse(namespaces.TryDrop("missing", out failure));
            Assert.AreEqual(NamespaceFailure.NotFound, failure);
        }

        #endregion

        #region observability

        [TestMethod]
        public void EveryEngineMeter_CarriesItsNamespaceIdAsTheScopeTag()
        {
            // Meter-level tags distinguish the N same-named engine meters (feature graph-namespaces
            // Phase 4); the value is the collection-assigned id, never the user-supplied name. The
            // listener starts FIRST so every engine meter created below publishes into it.
            var scopeIds = new HashSet<String>();
            using var listener = new System.Diagnostics.Metrics.MeterListener();
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == NoSQL.GraphDB.Core.Diagnostics.Fallen8Diagnostics.SourceName
                    && instrument.Meter.Tags != null)
                {
                    foreach (var tag in instrument.Meter.Tags)
                    {
                        if (tag.Key == "fallen8.scope.id" && tag.Value is String id)
                        {
                            lock (scopeIds)
                            {
                                scopeIds.Add(id);
                            }
                        }
                    }
                }
            };
            listener.Start();

            using var namespaces = CreateCollection();
            Assert.IsTrue(namespaces.TryCreate("flights", out var flights, out _));

            lock (scopeIds)
            {
                Assert.IsTrue(scopeIds.Contains(namespaces.Default.Id), "the default engine's meter carries its id");
                Assert.IsTrue(scopeIds.Contains(flights.Id), "a created engine's meter carries its id");
                Assert.IsFalse(scopeIds.Contains("flights"), "the user-supplied NAME never becomes a tag value");
            }
        }

        #endregion

        #region durable-mode on-disk layout

        [TestMethod]
        public void CorruptCatalog_FailsBootLoudly()
        {
            var storage = Path.Combine(Path.GetTempPath(), "f8-ns-test-" + Guid.NewGuid().ToString("N"));
            var metadata = Path.Combine(storage, "metadata");
            try
            {
                Directory.CreateDirectory(metadata);
                File.WriteAllText(Path.Combine(metadata, "namespaces.json"), "{{{ not json");

                var thrown = Assert.ThrowsException<InvalidOperationException>(
                    () => CreateCollection(storageDirectory: storage));
                StringAssert.Contains(thrown.Message, "namespaces.json",
                    "the failure must name the catalog file (never silently overwritten)");
            }
            finally
            {
                if (Directory.Exists(storage))
                {
                    Directory.Delete(storage, recursive: true);
                }
            }
        }

        [TestMethod]
        public void SemanticallyBadCatalogEntries_AreSkippedLoudly_NeverSplitBrainDefault()
        {
            var storage = Path.Combine(Path.GetTempPath(), "f8-ns-test-" + Guid.NewGuid().ToString("N"));
            var metadata = Path.Combine(storage, "metadata");
            try
            {
                Directory.CreateDirectory(metadata);
                // A "default"-named entry (would split-brain the bare alias) and an invalid name.
                File.WriteAllText(Path.Combine(metadata, "namespaces.json"),
                    "{\"schemaVersion\":1,\"namespaces\":[" +
                    "{\"id\":\"ns-x\",\"name\":\"default\",\"createdAt\":\"2026-07-23T10:00:00.000Z\"}," +
                    "{\"id\":\"ns-y\",\"name\":\"Bad_Name\",\"createdAt\":\"2026-07-23T10:00:00.000Z\"}]}");

                using var namespaces = CreateCollection(storageDirectory: storage);

                Assert.AreEqual(1, namespaces.Count, "both bad entries are skipped");
                Assert.IsTrue(namespaces.TryGet("default", out var def));
                Assert.AreSame(namespaces.Default, def, "bare alias and /ns/default stay the SAME engine");
            }
            finally
            {
                if (Directory.Exists(storage))
                {
                    Directory.Delete(storage, recursive: true);
                }
            }
        }

        [TestMethod]
        public void DurableMode_UsesIdKeyedDirectories_AndDropDeletesThem()
        {
            var storage = Path.Combine(Path.GetTempPath(), "f8-ns-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var namespaces = CreateCollection(storageDirectory: storage))
                {
                    // The default namespace lives on the LEGACY paths (zero-migration upgrade).
                    Assert.IsTrue(File.Exists(Path.Combine(storage, "fallen8.wal")));

                    Assert.IsTrue(namespaces.TryCreate("flights", out var flights, out _));
                    var namespaceDir = Path.Combine(storage, "namespaces", flights.Id);
                    Assert.IsTrue(Directory.Exists(namespaceDir));
                    Assert.IsTrue(File.Exists(Path.Combine(namespaceDir, "fallen8.wal")));

                    Assert.IsTrue(namespaces.TryDrop("flights", out _));
                    Assert.IsFalse(Directory.Exists(namespaceDir));
                }
            }
            finally
            {
                if (Directory.Exists(storage))
                {
                    Directory.Delete(storage, recursive: true);
                }
            }
        }

        #endregion
    }
}
