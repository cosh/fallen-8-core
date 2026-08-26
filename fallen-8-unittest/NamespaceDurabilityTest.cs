// MIT License
//
// NamespaceDurabilityTest.cs
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
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;
using NoSQL.GraphDB.App.Namespaces;
using NoSQL.GraphDB.App.Services;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Integration tests for per-namespace durability (feature graph-namespaces, Phase 3): the
    /// namespace catalog makes create/rename/drop survive restarts, per-namespace WALs recover
    /// unsaved data, /save/all + the shutdown auto-save produce one spanning save-game entry, and
    /// PUT /savegames/{id}/load restores exactly the contained namespaces (or one via ?namespace=).
    /// </summary>
    [TestClass]
    public class NamespaceDurabilityTest
    {
        private string _storageDir;
        private string _metaDir;

        [TestInitialize]
        public void TestInitialize()
        {
            _storageDir = Path.Combine(Path.GetTempPath(), "f8_ns_" + Guid.NewGuid().ToString("N"));
            _metaDir = Path.Combine(Path.GetTempPath(), "f8_ns_meta_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_storageDir);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            foreach (var dir in new[] { _storageDir, _metaDir })
            {
                try
                {
                    if (dir != null && Directory.Exists(dir))
                    {
                        Directory.Delete(dir, true);
                    }
                }
                catch
                {
                    // best-effort cleanup
                }
            }
        }

        private sealed class DurabilityFactory : WebApplicationFactory<Program>
        {
            private readonly IReadOnlyDictionary<string, string> _settings;
            private readonly TestLogSink _sink;

            public DurabilityFactory(IReadOnlyDictionary<string, string> settings, TestLogSink sink = null)
            {
                _settings = settings;
                _sink = sink;
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                foreach (var kv in _settings)
                {
                    builder.UseSetting(kv.Key, kv.Value);
                }

                if (_sink != null)
                {
                    builder.ConfigureLogging(logging => logging.AddProvider(_sink));
                }
            }
        }

        private DurabilityFactory NewHost(bool saveOnShutdown = true, TestLogSink sink = null,
            string startupLoadMode = null, bool? loadOnStartup = null, int? maxNamespaces = null)
        {
            var settings = new Dictionary<string, string>
            {
                ["Fallen8:Durability:StorageDirectory"] = _storageDir,
                ["Fallen8:Durability:Volatile"] = "false",
                ["Fallen8:Durability:SaveOnShutdown"] = saveOnShutdown ? "true" : "false",
                ["Fallen8:Metadata:Directory"] = _metaDir,
            };

            // The startup-load selection (feature namespace-startup-load): both keys are
            // startup-only, so a test that changes them boots a new host - exactly as an operator does.
            if (startupLoadMode != null)
            {
                settings["Fallen8:Namespaces:StartupLoadMode"] = startupLoadMode;
            }
            if (loadOnStartup.HasValue)
            {
                settings["Fallen8:Namespaces:LoadOnStartup"] = loadOnStartup.Value ? "true" : "false";
            }

            // The quota is the one lever this suite has for making a namespace CREATE fail on
            // demand, which is how a save-game restore's recreate step can be failed deliberately.
            if (maxNamespaces.HasValue)
            {
                settings["Fallen8:Namespaces:MaxNamespaces"] = maxNamespaces.Value.ToString();
            }

            return new DurabilityFactory(settings, sink);
        }

        #region helpers

        /// <summary>Boots the host (StartAsync runs) and returns the namespace collection.</summary>
        private static Fallen8Namespaces Collection(DurabilityFactory factory)
        {
            return factory.Services.GetRequiredService<Fallen8Namespaces>();
        }

        private static Namespace Create(Fallen8Namespaces namespaces, string name)
        {
            Assert.IsTrue(namespaces.TryCreate(name, out var ns, out var failure), "create " + name + ": " + failure);
            return ns;
        }

        private static void AddVertices(Fallen8 engine, int count)
        {
            var definitions = new List<VertexDefinition>();
            for (var i = 0; i < count; i++)
            {
                definitions.Add(new VertexDefinition { CreationDate = (uint)(i + 1), Properties = null });
            }

            var info = engine.EnqueueTransaction(new CreateVerticesTransaction { Vertices = definitions });
            info.WaitUntilFinished();
            Assert.AreNotEqual(TransactionState.RolledBack, info.TransactionState);
        }

        private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
        {
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        }

        /// <summary>
        ///   Puts a namespace with NO resident engine into the collection (feature
        ///   namespace-startup-load), which is the state a boot produces for a namespace excluded
        ///   from the startup load. Reflection because the apiApp declares no
        ///   <c>InternalsVisibleTo</c> - the same convention this suite already uses elsewhere - and
        ///   because phase 0 lands the guard while nothing can yet be excluded through configuration:
        ///   the dangerous code gets its tests before the feature that reaches it exists.
        /// </summary>
        private static Namespace AddNotLoadedNamespace(Fallen8Namespaces namespaces, string name, string id)
        {
            var ctor = typeof(Namespace).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var ns = (Namespace)ctor.Invoke(new object[] { name, id, null, DateTime.UtcNow });

            var byName = typeof(Fallen8Namespaces)
                .GetField("_byName", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(namespaces);
            byName.GetType().GetMethod("TryAdd").Invoke(byName, new object[] { name, ns });

            Assert.IsFalse(ns.IsLoaded, "the fixture namespace must be non-resident");
            return ns;
        }

        /// <summary>
        ///   Rewrites one catalog entry's startup-load policy on disk, which is what an operator's
        ///   PATCH persists and what the NEXT boot reads. Preferred over the reflection fixture above
        ///   wherever a test can afford a restart: it exercises the real catalog round-trip and the
        ///   real boot decision instead of a hand-made non-resident namespace.
        /// </summary>
        private void SetCatalogLoadPolicy(string name, bool? loadOnStartupEnabled)
        {
            var path = Path.Combine(_metaDir, Fallen8Namespaces.CatalogFileName);
            var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)).AsObject();
            var found = false;
            foreach (var entry in document["namespaces"].AsArray())
            {
                if (entry["name"].GetValue<string>() == name)
                {
                    entry.AsObject()["loadOnStartupEnabled"] = loadOnStartupEnabled;
                    found = true;
                }
            }

            Assert.IsTrue(found, "the catalog must contain \"" + name + "\"");
            File.WriteAllText(path, document.ToJsonString());
        }

        /// <summary>The catalog entries on disk, by name.</summary>
        private Dictionary<string, JsonElement> CatalogEntries()
        {
            var path = Path.Combine(_metaDir, Fallen8Namespaces.CatalogFileName);
            var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var entry in JsonDocument.Parse(File.ReadAllText(path)).RootElement
                .GetProperty("namespaces").EnumerateArray())
            {
                result[entry.GetProperty("name").GetString()] = entry;
            }

            return result;
        }

        /// <summary>
        ///   Every file in a namespace's directory, by file name, as bytes - so "nothing on disk was
        ///   touched" can be asserted as byte identity rather than as an absence of evidence.
        /// </summary>
        private static Dictionary<string, byte[]> DirectorySnapshot(string directory)
        {
            return Directory.GetFiles(directory)
                .ToDictionary(Path.GetFileName, File.ReadAllBytes, StringComparer.Ordinal);
        }

        /// <summary>The index of the first logged line containing every fragment, or -1.</summary>
        private static int LogLineIndex(TestLogSink sink, params string[] fragments)
        {
            var entries = sink.Entries;
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].Message != null
                    && fragments.All(f => entries[i].Message.Contains(f, StringComparison.Ordinal)))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>How many log entries name <paramref name="namespaceName"/> in a startup-load decision.</summary>
        private static int StartupLoadLines(TestLogSink sink, string namespaceName)
        {
            return sink.Entries.Count(e => e.Message != null
                && e.Message.Contains("\"" + namespaceName + "\"", StringComparison.Ordinal)
                && e.Message.Contains("at startup", StringComparison.Ordinal));
        }

        #endregion

        #region feature namespace-startup-load: the data-loss guard (spec section 5)

        /// <summary>
        ///   THE test this feature exists to make safe. A namespace that is cataloged but not loaded
        ///   must never be a member of a save, because saving it is not merely useless but
        ///   destructive: <c>Fallen8.Save</c> resets the write-ahead log to a bare header (so every
        ///   post-checkpoint delta it held is gone, with no other artifact carrying it) and the
        ///   empty-but-complete checkpoint it writes gets registered as the NEWEST entry for that
        ///   id, so the next boot loads the empty one. Both halves are silent.
        /// </summary>
        [TestMethod]
        public async Task NotLoadedNamespace_IsNotSavedOnShutdown_AndItsWalAndCheckpointSurvive()
        {
            string directory;
            string walPath;
            byte[] walBefore;
            int checkpointFilesBefore;

            // A real, populated namespace, checkpointed and then left with post-checkpoint deltas in
            // its write-ahead log - exactly the state that has something to lose. SaveOnShutdown is
            // OFF here so this process's own teardown does not save (which would reset the WAL to a
            // header and rob the assertions below of the very deltas they are protecting).
            using (var host = NewHost(saveOnShutdown: false))
            {
                using var client = host.CreateClient();
                var namespaces = Collection(host);
                var flights = Create(namespaces, "flights");
                AddVertices(flights.Engine, 3);

                using var saved = await client.PutAsync("/ns/flights/save", new StringContent("{}", Encoding.UTF8, "application/json"));
                Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);

                // Post-checkpoint work, so the WAL holds deltas the checkpoint does not.
                AddVertices(flights.Engine, 2);

                directory = namespaces.DirectoryFor(flights);
                walPath = Directory.GetFiles(directory, "fallen8.wal*").Single();
                walBefore = File.ReadAllBytes(walPath);
                checkpointFilesBefore = Directory.GetFiles(directory, "Temp.f8s*").Length;
                Assert.IsTrue(walBefore.Length > 0, "the WAL must hold post-checkpoint deltas");
            }

            // Now a process in which that namespace is present but NOT loaded, shutting down with
            // SaveOnShutdown on - the exact shape that used to destroy it.
            var sink = new TestLogSink();
            using (var host = NewHost(saveOnShutdown: true, sink: sink))
            {
                var namespaces = Collection(host);
                Assert.IsTrue(namespaces.TryGet("flights", out var loaded), "the catalog must still list it");
                var id = loaded.Id;

                // Replace the loaded entry with a non-resident one carrying the SAME id, so the
                // shutdown save would target the same files.
                var byName = typeof(Fallen8Namespaces)
                    .GetField("_byName", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(namespaces);
                ((System.Collections.IDictionary)byName).Remove("flights");
                AddNotLoadedNamespace(namespaces, "flights", id);

                var lifecycle = host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
                    .OfType<DurabilityLifecycleService>().Single();
                await lifecycle.StopAsync(System.Threading.CancellationToken.None);
            }

            // (i) No new checkpoint was written for it.
            Assert.AreEqual(checkpointFilesBefore, Directory.GetFiles(directory, "Temp.f8s*").Length,
                "a not-loaded namespace must not produce a checkpoint");

            // (ii) The write-ahead log is byte-identical, so it was never reset to a header.
            CollectionAssert.AreEqual(walBefore, File.ReadAllBytes(walPath),
                "the WAL of a not-loaded namespace must be untouched (Save resets it to a bare header)");

            // (iii) It was skipped BY DESIGN, on the clean informational path - not by accident via
            //       the throwing engine accessor landing in the per-namespace catch. Without this
            //       assertion the test cannot tell the explicit guard from its absence (verified:
            //       disabling the guard leaves every other assertion here green), and an operator
            //       would see an ERROR line on every clean shutdown for a namespace they
            //       deliberately excluded.
            Assert.IsTrue(sink.Contains(LogLevel.Information, "Skipping the shutdown save", "flights"),
                "the skip must be announced as a normal, expected condition");
            Assert.IsFalse(sink.Contains(LogLevel.Error, "flights"),
                "skipping a not-loaded namespace must not surface as an error");

            // (iv) The data comes back in full on a boot that loads it again - the end-to-end proof
            //      that nothing was lost, not merely that no file changed.
            using (var host = NewHost(saveOnShutdown: false))
            {
                var namespaces = Collection(host);
                Assert.IsTrue(namespaces.TryGet("flights", out var flights));
                Assert.AreEqual(5, flights.Engine.VertexCount,
                    "the checkpoint plus the replayed WAL deltas must restore every vertex");
            }
        }

        /// <summary>
        ///   PUT /save/all skips a not-loaded namespace and says so, rather than counting it as a
        ///   failure: without the guard an engine-less namespace would land in the failure list and
        ///   turn an otherwise correct sweep into a 500.
        /// </summary>
        [TestMethod]
        public async Task SaveAll_SkipsNotLoadedNamespaces_AndDoesNotFail()
        {
            using var host = NewHost(saveOnShutdown: false);
            using var client = host.CreateClient();
            var namespaces = Collection(host);
            AddVertices(namespaces.Default.Engine, 1);
            AddNotLoadedNamespace(namespaces, "archived", "ns-archived-fixture");

            using var response = await client.PutAsync("/save/all", new StringContent("", Encoding.UTF8, "application/json"));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "a skipped namespace is not a failure");
            var entry = await ReadJson(response);
            var members = entry.GetProperty("namespaces").EnumerateArray()
                .Select(m => m.GetProperty("name").GetString()).ToList();
            CollectionAssert.DoesNotContain(members, "archived",
                "the spanning entry must not claim to cover a namespace it never saved");
            CollectionAssert.Contains(members, "default");

            // The skip reaches the CALLER, not only the server log: this entry spans a strict subset
            // of the Fallen-8, and from the members alone that is indistinguishable from a Fallen-8
            // that holds nothing else.
            var skipped = entry.GetProperty("skippedNamespaces").EnumerateArray()
                .Select(n => n.GetString()).ToList();
            CollectionAssert.AreEquivalent(new[] { "archived" }, skipped);
        }

        /// <summary>
        ///   PUT /save/all over a fully loaded Fallen-8 reports NO skip field at all - the transient
        ///   member must not appear on a save game that spans everything, and must never be persisted
        ///   into the registry document.
        /// </summary>
        [TestMethod]
        public async Task SaveAll_WithEveryNamespaceLoaded_ReportsNoSkippedNamespaces()
        {
            using var host = NewHost(saveOnShutdown: false);
            using var client = host.CreateClient();
            AddVertices(Create(Collection(host), "flights").Engine, 1);

            using var response = await client.PutAsync("/save/all", null);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsFalse((await ReadJson(response)).TryGetProperty("skippedNamespaces", out _),
                "a save that spans the whole Fallen-8 carries no skip list");

            using var listed = await client.GetAsync("/savegames");
            foreach (var registered in (await ReadJson(listed)).EnumerateArray())
            {
                Assert.IsFalse(registered.TryGetProperty("skippedNamespaces", out _),
                    "the skip list describes ONE operation, so it is never persisted in the registry");
            }
        }

        /// <summary>
        ///   Spec decision 8.3: restoring an entry that contains a not-loaded namespace ACTIVATES it
        ///   and flips its persisted policy to enabled, reporting both - because activating without
        ///   the flip would let the data this restore just wrote go invisible again at the next boot,
        ///   and refusing would dead-end a legitimate recovery behind "change policy, restart,
        ///   restore".
        ///   <para>The no-partial-restore guarantee is kept: the activation happens in its own pass
        ///   after every member resolved and BEFORE any load is enqueued, so no member is left half
        ///   restored. The names are chosen, not incidental - members are enumerated in ORDINAL NAME
        ///   ORDER, so "zz-archived" is the LAST one, the position from which a lazily activated
        ///   member would already have had the earlier members' loads in flight behind it.</para>
        ///   <para>The policy-write-before-activation half is pinned here on the LOG ORDER, because
        ///   the end state cannot tell the two orders apart; the pass-after-the-resolve-loop half is
        ///   pinned by <see cref="EntryRestore_WhenARecreateFails_ActivatesNothing_AndFlipsNoPolicy"/>,
        ///   which needs a failing member and therefore its own fixture.</para>
        /// </summary>
        [TestMethod]
        public async Task EntryRestore_ContainingANotLoadedNamespace_ActivatesIt_AndFlipsThePolicy()
        {
            String entryId;
            using (var host = NewHost(saveOnShutdown: false))
            {
                using var client = host.CreateClient();
                var namespaces = Collection(host);
                AddVertices(namespaces.Default.Engine, 1);
                AddVertices(Create(namespaces, "aaa-live").Engine, 2);
                AddVertices(Create(namespaces, "zz-archived").Engine, 3);

                using var saved = await client.PutAsync("/save/all", null);
                Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
                var entry = await ReadJson(saved);
                entryId = entry.GetProperty("id").GetString();
                var members = entry.GetProperty("namespaces").EnumerateArray()
                    .Select(m => m.GetProperty("name").GetString()).ToList();
                CollectionAssert.AreEqual(new List<string> { "aaa-live", "default", "zz-archived" }, members,
                    "the excluded member must come LAST, or this test cannot see a late activation");
            }

            SetCatalogLoadPolicy("zz-archived", false);

            var sink = new TestLogSink();
            using (var host = NewHost(saveOnShutdown: false, sink: sink))
            {
                using var client = host.CreateClient();
                var namespaces = Collection(host);
                Assert.IsFalse(namespaces.Snapshot().Single(n => n.Name == "zz-archived").IsLoaded);

                // Diverge the loaded members AFTER the save, so their state is measurable rather than
                // indistinguishable from doing nothing.
                Assert.IsTrue(namespaces.TryGet("aaa-live", out var live));
                AddVertices(live.Engine, 4);
                AddVertices(namespaces.Default.Engine, 4);

                using var restored = await client.PutAsync("/savegames/" + entryId + "/load?waitForCompletion=true", null);

                Assert.AreEqual(HttpStatusCode.OK, restored.StatusCode);
                var body = await ReadJson(restored);
                var activated = body.GetProperty("activatedNamespaces").EnumerateArray()
                    .Select(n => n.GetString()).ToList();
                CollectionAssert.AreEquivalent(new[] { "zz-archived" }, activated,
                    "the response names what it had to load, so the caller learns the policy changed too");
                Assert.IsFalse(body.TryGetProperty("skippedNamespaces", out _),
                    "nothing was skipped: 8.3 replaced the skip with an activation");

                // (i) The activated member holds THIS entry's content and serves requests.
                Assert.IsTrue(namespaces.TryGet("zz-archived", out var archived));
                Assert.IsTrue(archived.IsLoaded);
                Assert.AreEqual(3, archived.Engine.VertexCount);
                using var counted = await client.GetAsync("/ns/zz-archived/vertex/count");
                Assert.AreEqual(HttpStatusCode.OK, counted.StatusCode);

                // (ii) No sibling is left half restored. Both were live, and a live namespace reloaded
                //      from the checkpoint its own write-ahead log is anchored to replays the
                //      post-save commits on top - the engine's crash-consistency pairing, asserted here
                //      so "unchanged" is a measured value rather than an absence of evidence.
                Assert.AreEqual(6, live.Engine.VertexCount);
                Assert.AreEqual(5, namespaces.Default.Engine.VertexCount);

                // (iii) The policy was flipped, not just the process state.
                using var listed = await client.GetAsync("/ns/zz-archived");
                var entry = await ReadJson(listed);
                Assert.AreEqual("ready", entry.GetProperty("state").GetString());
                Assert.IsTrue(entry.GetProperty("loadOnStartupEnabled").GetBoolean());

                // (iv) The POLICY WRITE CAME FIRST, which the end state above cannot distinguish: the
                //      log order is the observable. The order is the argument for it - a cheap catalog
                //      write that fails leaves "nothing was restored" true, while the reverse order can
                //      only ever produce a 200 claiming a policy change that did not persist.
                //      (Verified: moving the write after the activation fails exactly this assertion.)
                var policyLine = LogLineIndex(sink, "Updated namespace \"zz-archived\"", "loadOnStartup=enabled");
                var activationLine = LogLineIndex(sink, "\"zz-archived\"", "was ACTIVATED at runtime");
                Assert.IsTrue(policyLine >= 0, "the policy write must be logged");
                Assert.IsTrue(activationLine >= 0, "the activation must be logged");
                Assert.IsTrue(policyLine < activationLine,
                    "the persisted policy is written BEFORE the engine is loaded, not after");
            }

            Assert.IsTrue(CatalogEntries()["zz-archived"].GetProperty("loadOnStartupEnabled").GetBoolean(),
                "the flip is persisted in the catalog, not only in memory");

            using (var host = NewHost(saveOnShutdown: false))
            {
                var archived = Collection(host).Snapshot().Single(n => n.Name == "zz-archived");
                Assert.IsTrue(archived.IsLoaded, "so the restored data does not go invisible at the next boot");
                Assert.AreEqual(3, archived.Engine.VertexCount);
            }
        }

        /// <summary>
        ///   The other load-bearing ordering of decision 8.3: the activation pass runs AFTER every
        ///   member has been resolved, so every pre-existing failure mode still means literally
        ///   "nothing was restored". Here the entry's dropped member cannot be recreated (the quota is
        ///   full), and the not-loaded member sorts BEFORE it - the position from which an activation
        ///   done inside the resolve loop would already have loaded it and flipped its persisted
        ///   policy under a response that says nothing was restored.
        ///   <para>Verified against the mutation: with the activation moved into the resolve loop,
        ///   the two assertions about "aa-archived" fail.</para>
        /// </summary>
        [TestMethod]
        public async Task EntryRestore_WhenARecreateFails_ActivatesNothing_AndFlipsNoPolicy()
        {
            String entryId;
            using (var host = NewHost(saveOnShutdown: false))
            {
                using var client = host.CreateClient();
                var namespaces = Collection(host);
                AddVertices(namespaces.Default.Engine, 1);
                AddVertices(Create(namespaces, "aa-archived").Engine, 2);
                AddVertices(Create(namespaces, "zz-gone").Engine, 3);

                using var saved = await client.PutAsync("/save/all", null);
                Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
                var entry = await ReadJson(saved);
                entryId = entry.GetProperty("id").GetString();
                CollectionAssert.AreEqual(
                    new List<string> { "aa-archived", "default", "zz-gone" },
                    entry.GetProperty("namespaces").EnumerateArray()
                        .Select(m => m.GetProperty("name").GetString()).ToList(),
                    "the failing member must come LAST, or this test cannot see an early activation");

                // Dropped, so the restore has to recreate it. Its checkpoint file survives the drop
                // (checkpoints belong to save games), so the restore's file pre-flight still passes and
                // the recreate is what fails.
                Assert.IsTrue(namespaces.TryDrop("zz-gone", out _));
            }

            SetCatalogLoadPolicy("aa-archived", false);

            // The quota is already full with "default" + "aa-archived", so recreating "zz-gone" fails.
            using (var host = NewHost(saveOnShutdown: false, maxNamespaces: 2))
            {
                using var client = host.CreateClient();
                var namespaces = Collection(host);
                Assert.AreEqual(2, namespaces.Count, "the fixture must boot AT the quota");

                using var restored = await client.PutAsync("/savegames/" + entryId + "/load?waitForCompletion=true", null);

                Assert.AreEqual(HttpStatusCode.InternalServerError, restored.StatusCode);
                var problem = await ReadJson(restored);
                StringAssert.Contains(problem.GetProperty("detail").GetString(), "could not be recreated");
                StringAssert.Contains(problem.GetProperty("detail").GetString(), "nothing was restored");
                Assert.IsFalse(problem.TryGetProperty("activatedNamespaces", out _),
                    "a restore that restored nothing cannot report an activation");

                Assert.IsFalse(namespaces.Snapshot().Single(n => n.Name == "aa-archived").IsLoaded,
                    "the not-loaded member must not have been activated before the failing member was resolved");
            }

            Assert.IsFalse(CatalogEntries()["aa-archived"].GetProperty("loadOnStartupEnabled").GetBoolean(),
                "and its persisted policy must be untouched, so the next boot still does what it did");
        }

        /// <summary>
        ///   The engine accessor throws rather than returning null, so a dereference site the sweep
        ///   missed fails diagnosably (and, inside the shutdown save's per-namespace catch, means
        ///   "skip") instead of NullReferenceException-ing. This repo has no nullable-reference
        ///   analysis, so this is the only fail-safe default available.
        /// </summary>
        [TestMethod]
        public void NotLoadedNamespace_EngineAccessorThrows_AndTryGetEngineReportsIt()
        {
            using var host = NewHost(saveOnShutdown: false);
            var ns = AddNotLoadedNamespace(Collection(host), "archived", "ns-archived-fixture");

            Assert.IsFalse(ns.TryGetEngine(out var engine));
            Assert.IsNull(engine);
            var thrown = Assert.ThrowsException<NamespaceNotLoadedException>(() => _ = ns.Engine);
            Assert.AreEqual("archived", thrown.NamespaceName);
        }

        #endregion

        #region feature namespace-startup-load: the policy and the boot decision (spec section 4)

        /// <summary>
        ///   The boot decision itself: an excluded namespace gets NO engine, and stays in the
        ///   collection anyway (spec §4.4) so its catalog entry, its name reservation and its
        ///   droppability all keep working.
        /// </summary>
        [TestMethod]
        public void Boot_SkipsAnExcludedNamespace_AndConstructsNoEngineForIt()
        {
            string walPath;
            using (var host = NewHost(saveOnShutdown: false))
            {
                var namespaces = Collection(host);
                var archived = Create(namespaces, "archived");
                AddVertices(archived.Engine, 3);
                walPath = Directory.GetFiles(namespaces.DirectoryFor(archived), "fallen8.wal*").Single();
            }

            SetCatalogLoadPolicy("archived", false);
            var walBefore = File.ReadAllBytes(walPath);

            using (var host = NewHost(saveOnShutdown: false))
            {
                var namespaces = Collection(host);
                Assert.AreEqual(2, namespaces.Count, "the excluded namespace stays in the collection");
                Assert.IsTrue(namespaces.TryGet("archived", out var archived), "it stays addressable for management");
                Assert.IsFalse(archived.IsLoaded, "no engine was constructed for it");
                Assert.IsFalse(archived.TryGetEngine(out var engine));
                Assert.IsNull(engine);
                Assert.ThrowsException<NamespaceNotLoadedException>(() => _ = archived.Engine);
                Assert.AreEqual(NamespaceState.NotLoaded, archived.EffectiveState);
                Assert.IsFalse(archived.LoadOnStartupEnabled.Value, "the policy it was excluded by is readable");
                Assert.IsTrue(namespaces.Default.IsLoaded, "default is unaffected");
            }

            CollectionAssert.AreEqual(walBefore, File.ReadAllBytes(walPath),
                "an excluded namespace's write-ahead log is not even opened, let alone replayed");
        }

        /// <summary>
        ///   Boot is loud (spec §4.3): exactly one line per cataloged namespace, saying loaded or
        ///   skipped AND why, plus a summary that names the configuration keys. A deliberate skip is
        ///   never an error - an operator must not see one on every boot for a choice they made.
        /// </summary>
        [TestMethod]
        public void Boot_LogsOneLinePerLoadedAndSkippedNamespace()
        {
            using (var host = NewHost(saveOnShutdown: false))
            {
                var namespaces = Collection(host);
                Create(namespaces, "archived");
                Create(namespaces, "live");
            }

            SetCatalogLoadPolicy("archived", false);

            var sink = new TestLogSink();
            using (var host = NewHost(saveOnShutdown: false, sink: sink))
            {
                Collection(host);
            }

            Assert.IsTrue(sink.Contains(LogLevel.Information, "\"live\"", "is LOADED at startup",
                "Fallen8:Namespaces:LoadOnStartup=true"), "a loaded namespace says so, and why");
            Assert.IsTrue(sink.Contains(LogLevel.Information, "\"archived\"", "is NOT loaded at startup",
                "loadOnStartupEnabled=false"), "a skipped namespace says so, and why");
            Assert.AreEqual(1, StartupLoadLines(sink, "live"), "exactly ONE line per namespace");
            Assert.AreEqual(1, StartupLoadLines(sink, "archived"), "exactly ONE line per namespace");
            Assert.IsTrue(sink.Contains(LogLevel.Information, "startup load selected 1 of 2 cataloged namespaces"),
                "the selection is summarized, never a silent no-op");
            Assert.IsFalse(sink.Contains(LogLevel.Error, "\"archived\""),
                "a deliberate exclusion is not an error");
        }

        /// <summary>
        ///   A selection that loads nothing but the reserved default is the shape an operator gets
        ///   wrong, so it is a WARNING rather than a note.
        /// </summary>
        [TestMethod]
        public void Boot_WarnsWhenTheSelectionLoadsNothingButDefault()
        {
            using (var host = NewHost(saveOnShutdown: false))
            {
                Create(Collection(host), "archived");
            }

            var sink = new TestLogSink();
            using (var host = NewHost(saveOnShutdown: false, sink: sink, loadOnStartup: false))
            {
                Assert.IsFalse(Collection(host).Snapshot().Single(n => n.Name == "archived").IsLoaded);
            }

            Assert.IsTrue(sink.Contains(LogLevel.Warning, "startup load selected 0 of 1 cataloged namespaces"),
                "loading nothing must be loud");
        }

        /// <summary>
        ///   Save-games FR-9's whole-process abort is scoped to the SELECTED namespaces: files under a
        ///   namespace nobody asked for cannot keep the server down. The counter-boot with
        ///   StartupLoadMode=All proves the abort still exists and was only scoped - without it this
        ///   test would pass just as well against a silently removed guard.
        /// </summary>
        [TestMethod]
        public async Task Boot_DoesNotAbort_WhenAnExcludedNamespacesCheckpointIsMissing()
        {
            string archivedDir;
            using (var host = NewHost(saveOnShutdown: false))
            {
                using var client = host.CreateClient();
                var namespaces = Collection(host);
                var archived = Create(namespaces, "archived");
                AddVertices(archived.Engine, 2);
                archivedDir = namespaces.DirectoryFor(archived);

                using var saved = await client.PutAsync("/ns/archived/save",
                    new StringContent("{}", Encoding.UTF8, "application/json"));
                Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
            }

            // Rot the registered checkpoint (the registry entry still points at it) and exclude it.
            foreach (var file in Directory.GetFiles(archivedDir, "Temp.f8s*"))
            {
                File.Delete(file);
            }
            SetCatalogLoadPolicy("archived", false);

            using (var host = NewHost(saveOnShutdown: false))
            {
                var namespaces = Collection(host);
                Assert.IsTrue(namespaces.TryGet("archived", out var archived));
                Assert.IsFalse(archived.IsLoaded);
                Assert.IsTrue(namespaces.Default.IsLoaded, "the rest of the Fallen-8 came up");
            }

            SetCatalogLoadPolicy("archived", true);
            using (var host = NewHost(saveOnShutdown: false, startupLoadMode: "All"))
            {
                var aborted = false;
                try
                {
                    Collection(host);
                }
                catch (Exception ex)
                {
                    aborted = true;
                    StringAssert.Contains(Flatten(ex), "archived",
                        "the abort must name the namespace whose save is unrestorable");
                }

                Assert.IsTrue(aborted, "a missing checkpoint of a SELECTED namespace still aborts startup (FR-9)");
            }
        }

        /// <summary>Every message in an exception chain (host startup wraps).</summary>
        private static string Flatten(Exception exception)
        {
            var text = new StringBuilder();
            for (var current = exception; current != null; current = current.InnerException)
            {
                text.Append(current.Message).Append(' ');
                if (current is AggregateException aggregate)
                {
                    foreach (var inner in aggregate.InnerExceptions)
                    {
                        text.Append(Flatten(inner)).Append(' ');
                    }
                }
            }

            return text.ToString();
        }

        /// <summary>
        ///   Leaves a namespace behind whose directory holds checkpoint files that NO registered save
        ///   game contains (save-games FR-11), built the way an operator reaches it: a real save, then
        ///   the registry entry removed WITHOUT its files (a restored savegames.json backup, or
        ///   checkpoint files carried onto a fresh instance). The post-checkpoint vertices live only
        ///   in the write-ahead log, so what is on disk beside the registry is strictly more than any
        ///   caller could restore without those files - the concrete loss at stake.
        /// </summary>
        /// <returns>That namespace's directory, so a caller can snapshot it before the next boot.</returns>
        private async Task<string> LeaveUnregisteredCheckpointFiles(string name)
        {
            using var host = NewHost(saveOnShutdown: false);
            using var client = host.CreateClient();
            var namespaces = Collection(host);
            var ns = Create(namespaces, name);
            var directory = namespaces.DirectoryFor(ns);
            AddVertices(ns.Engine, 3);

            using var saved = await client.PutAsync("/ns/" + name + "/save",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
            var entryId = (await ReadJson(saved)).GetProperty("id").GetString();

            AddVertices(ns.Engine, 2);

            using var deleted = await client.DeleteAsync("/savegames/" + entryId);
            Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
            return directory;
        }

        /// <summary>
        ///   The BOOT arm of the same three-valued outcome the activation refusal uses, and the whole
        ///   reason that outcome is three-valued: unregistered checkpoint files (save-games FR-11) must
        ///   NOT abort the process the way an unrestorable registered save does (FR-9). The boot has
        ///   already constructed and published this engine, and the state it comes up in is exactly the
        ///   state the operator adopts those files from with one checkpoint load - so aborting would
        ///   take a whole Fallen-8 down over files that are still fully recoverable, and take the very
        ///   route to recovering them (PUT /load) down with it.
        ///   <para>Verified against the mutation: with the boot's condition broadened to abort on
        ///   <c>UnregisteredCheckpoints</c> as well, the host does not come up and this test fails.</para>
        /// </summary>
        [TestMethod]
        public async Task Boot_DoesNotAbort_WhenASelectedNamespaceHasUnregisteredCheckpointFiles()
        {
            var directory = await LeaveUnregisteredCheckpointFiles("archived");
            var checkpointsBefore = DirectorySnapshot(directory)
                .Where(f => f.Key.StartsWith("Temp.f8s", StringComparison.Ordinal))
                .ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
            Assert.AreNotEqual(0, checkpointsBefore.Count,
                "the fixture must leave UNREGISTERED checkpoint files behind");

            var sink = new TestLogSink();
            using (var host = NewHost(saveOnShutdown: false, sink: sink))
            {
                using var client = host.CreateClient();

                // (a) The process came up - with this namespace SELECTED for load (no policy was
                //     written, so it inherits the default), resident, and serving.
                var namespaces = Collection(host);
                var archived = namespaces.Snapshot().Single(n => n.Name == "archived");
                Assert.IsTrue(archived.IsLoaded, "the boot publishes the engine it had already constructed");
                Assert.IsTrue(namespaces.Default.IsLoaded, "and the rest of the Fallen-8 is up");
                using (var counted = await client.GetAsync("/ns/archived/vertex/count"))
                {
                    Assert.AreEqual(HttpStatusCode.OK, counted.StatusCode, "its data plane answers");
                }

                // Nothing was restored, and the count says so honestly: the write-ahead log is
                // anchored to the very checkpoint that was not restored, so it replays nothing and
                // waits for its paired load. That load is the adoption the warning below names.
                Assert.AreEqual(0, archived.Engine.VertexCount,
                    "an unregistered checkpoint is never restored, so nothing came back");

                // (b) And it is loud, at warning level, naming the cure that is reachable HERE (the
                //     activation path's own is on its 409, because PUT /load is refused while a
                //     namespace is not loaded).
                Assert.IsTrue(sink.Contains(LogLevel.Warning, "\"archived\"", "no registered save game contains",
                    "PUT /ns/archived/load"), "the FR-11 warning must name the situation and the boot's own cure");
                Assert.IsFalse(sink.Contains(LogLevel.Error, "\"archived\""),
                    "files nobody registered are not this server's error");
            }

            // (c) The orphan files are still there, byte for byte, so that adoption is still possible.
            var checkpointsAfter = DirectorySnapshot(directory);
            foreach (var file in checkpointsBefore.Keys)
            {
                Assert.IsTrue(checkpointsAfter.ContainsKey(file),
                    "\"" + file + "\" must survive a boot that did not restore it");
                CollectionAssert.AreEqual(checkpointsBefore[file], checkpointsAfter[file],
                    "\"" + file + "\" must be byte-identical after such a boot");
            }
        }

        /// <summary>
        ///   The R2/R5 regression pin. The catalog writer rebuilds its whole document from the
        ///   collection it is handed, so a not-loaded namespace that had left the collection would be
        ///   ERASED by the next metadata write anywhere in the Fallen-8 - stranding its data directory
        ///   and WAL unreachable and un-droppable, and freeing its name to be re-minted under a second
        ///   id over real data.
        /// </summary>
        [TestMethod]
        public void Catalog_RetainsNotLoadedEntries_AcrossCreateRenameDrop()
        {
            string archivedId;
            using (var host = NewHost(saveOnShutdown: false))
            {
                var namespaces = Collection(host);
                archivedId = Create(namespaces, "archived").Id;
                AddVertices(namespaces.Default.Engine, 1);
                Create(namespaces, "keeper");
            }

            SetCatalogLoadPolicy("archived", false);

            using (var host = NewHost(saveOnShutdown: false))
            {
                var namespaces = Collection(host);

                // Three catalog writes, none of which may drop the entry they were not about.
                Create(namespaces, "third");
                Assert.IsTrue(namespaces.TryRename("keeper", "keeper-eu", out _, out _));
                Assert.IsTrue(namespaces.TryDrop("third", out _));

                var entries = CatalogEntries();
                Assert.IsTrue(entries.ContainsKey("archived"), "the not-loaded entry survived every write");
                Assert.AreEqual(archivedId, entries["archived"].GetProperty("id").GetString(),
                    "with its immutable id, so its on-disk data stays reachable");
                Assert.IsFalse(entries["archived"].GetProperty("loadOnStartupEnabled").GetBoolean(),
                    "and with its policy, so it is not silently re-loaded next boot");
                Assert.IsTrue(entries.ContainsKey("keeper-eu"));
                Assert.IsFalse(entries.ContainsKey("third"));
            }

            using (var host = NewHost(saveOnShutdown: false))
            {
                var namespaces = Collection(host);
                Assert.IsTrue(namespaces.TryGet("archived", out var archived));
                Assert.IsFalse(archived.IsLoaded, "still excluded after all that");
            }
        }

        /// <summary>
        ///   R3: the name of a not-loaded namespace stays reserved. Re-minting it would create a
        ///   SECOND namespace with a fresh id over the first one's directory-and-WAL address space.
        /// </summary>
        [TestMethod]
        public async Task Create_OfANotLoadedNamespacesName_Conflicts()
        {
            using (var host = NewHost(saveOnShutdown: false))
            {
                Create(Collection(host), "archived");
            }

            SetCatalogLoadPolicy("archived", false);

            using (var host = NewHost(saveOnShutdown: false))
            {
                using var client = host.CreateClient();
                var namespaces = Collection(host);

                Assert.IsFalse(namespaces.TryCreate("archived", out var created, out var failure));
                Assert.AreEqual(NamespaceFailure.Conflict, failure);
                Assert.IsNull(created);

                using var response = await client.PutAsync("/ns/archived", null);
                Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
                Assert.AreEqual("Namespace name in use", (await ReadJson(response)).GetProperty("title").GetString());
            }
        }

        /// <summary>
        ///   The cold-boot lever: StartupLoadMode=All loads every cataloged namespace regardless of
        ///   its own policy, and does NOT rewrite that policy (the mode is an override, not an edit).
        /// </summary>
        [TestMethod]
        public void StartupLoadMode_All_IgnoresExclusions()
        {
            using (var host = NewHost(saveOnShutdown: false))
            {
                AddVertices(Create(Collection(host), "archived").Engine, 4);
            }

            SetCatalogLoadPolicy("archived", false);

            using (var host = NewHost(saveOnShutdown: false, startupLoadMode: "All", loadOnStartup: false))
            {
                var namespaces = Collection(host);
                Assert.IsTrue(namespaces.TryGet("archived", out var archived));
                Assert.IsTrue(archived.IsLoaded, "All overrides both the entry policy and the global default");
                Assert.AreEqual(4, archived.Engine.VertexCount, "and its WAL was replayed as usual");
                Assert.IsFalse(archived.LoadOnStartupEnabled.Value, "the persisted policy is untouched");
            }

            Assert.IsFalse(CatalogEntries()["archived"].GetProperty("loadOnStartupEnabled").GetBoolean(),
                "an All boot must not silently re-enable what the operator excluded");
        }

        /// <summary>
        ///   DefaultOnly, for when the selection itself is what is broken: nothing but the reserved
        ///   default is loaded, and every other namespace stays cataloged and droppable.
        /// </summary>
        [TestMethod]
        public void StartupLoadMode_DefaultOnly_LoadsOnlyDefault()
        {
            using (var host = NewHost(saveOnShutdown: false))
            {
                var namespaces = Collection(host);
                Create(namespaces, "one");
                Create(namespaces, "two");
            }

            using (var host = NewHost(saveOnShutdown: false, startupLoadMode: "DefaultOnly"))
            {
                var namespaces = Collection(host);
                Assert.AreEqual(3, namespaces.Count);
                Assert.IsTrue(namespaces.Default.IsLoaded);
                foreach (var name in new[] { "one", "two" })
                {
                    Assert.IsTrue(namespaces.TryGet(name, out var ns));
                    Assert.IsFalse(ns.IsLoaded, name + " must not be loaded under DefaultOnly");
                    Assert.IsNull(ns.LoadOnStartupEnabled, "the mode did not invent a per-entry policy");
                }
            }
        }

        /// <summary>
        ///   Spec §4.9: the reserved default namespace cannot be excluded by ANY route - not the
        ///   global default, not the mode, not a hand-written catalog entry, not PATCH. Every bare URL
        ///   aliases it, so a Fallen-8 without it has no coherent answer for most of its own surface.
        /// </summary>
        [TestMethod]
        public async Task Default_CannotBeExcluded_ByCatalogOrConfig()
        {
            // (i) The global default and the most aggressive mode both leave it loaded.
            using (var host = NewHost(saveOnShutdown: false, loadOnStartup: false, startupLoadMode: "DefaultOnly"))
            {
                var namespaces = Collection(host);
                Assert.IsTrue(namespaces.Default.IsLoaded);
                Assert.IsTrue(namespaces.Default.LoadOnStartupEnabled.Value,
                    "default reports the policy actually in force, not an inherited null");
                AddVertices(namespaces.Default.Engine, 1);
            }

            // (ii) A hand-written catalog entry named "default" is refused as it always was (it would
            //      split-brain the bare alias), so it cannot smuggle a policy in either.
            Directory.CreateDirectory(_metaDir);
            File.WriteAllText(Path.Combine(_metaDir, Fallen8Namespaces.CatalogFileName),
                "{\"schemaVersion\":1,\"namespaces\":[{\"id\":\"ns-smuggled\",\"name\":\"default\"," +
                "\"createdAt\":\"2026-01-01T00:00:00.000Z\",\"loadOnStartupEnabled\":false}]}");

            var sink = new TestLogSink();
            using (var host = NewHost(saveOnShutdown: false, sink: sink))
            {
                using var client = host.CreateClient();
                var namespaces = Collection(host);
                Assert.IsTrue(namespaces.Default.IsLoaded, "the smuggled entry cannot exclude the real default");
                Assert.AreEqual(1, namespaces.Default.Engine.VertexCount, "and it still owns the legacy paths");
                Assert.IsTrue(sink.Contains(LogLevel.Error, "ns-smuggled", "SKIPPED"),
                    "the reserved-name entry is refused loudly, as before");

                // (iii) And PATCH refuses to set the policy at all - there is no slot to store it in.
                using var patched = await client.PatchAsync("/ns/default",
                    new StringContent("{\"loadOnStartup\":\"disabled\"}", Encoding.UTF8, "application/json"));
                Assert.AreEqual(HttpStatusCode.Conflict, patched.StatusCode);
                Assert.AreEqual("Reserved namespace", (await ReadJson(patched)).GetProperty("title").GetString());
            }
        }

        /// <summary>
        ///   The operator's end-to-end path: PATCH the policy, restart, and the namespace is not
        ///   loaded - while its data on disk is untouched and one PATCH back brings it all the way home.
        /// </summary>
        [TestMethod]
        public async Task Patch_LoadOnStartupDisabled_ExcludesTheNextBoot_AndReenablingRestoresTheData()
        {
            using (var host = NewHost(saveOnShutdown: false))
            {
                using var client = host.CreateClient();
                AddVertices(Create(Collection(host), "archived").Engine, 5);

                using var patched = await client.PatchAsync("/ns/archived",
                    new StringContent("{\"loadOnStartup\":\"disabled\"}", Encoding.UTF8, "application/json"));
                Assert.AreEqual(HttpStatusCode.OK, patched.StatusCode);
                var body = await ReadJson(patched);
                Assert.IsFalse(body.GetProperty("loadOnStartupEnabled").GetBoolean());
                Assert.AreEqual("ready", body.GetProperty("state").GetString(),
                    "the policy describes the NEXT boot; this process keeps serving the namespace");
                Assert.AreEqual(5, body.GetProperty("vertexCount").GetInt32());
            }

            using (var host = NewHost(saveOnShutdown: false))
            {
                using var client = host.CreateClient();
                Assert.IsFalse(Collection(host).Snapshot().Single(n => n.Name == "archived").IsLoaded);

                using var patched = await client.PatchAsync("/ns/archived",
                    new StringContent("{\"loadOnStartup\":\"enabled\"}", Encoding.UTF8, "application/json"));
                Assert.AreEqual(HttpStatusCode.OK, patched.StatusCode,
                    "re-configuring a not-loaded namespace must stay possible over REST");
                var body = await ReadJson(patched);
                Assert.IsTrue(body.GetProperty("loadOnStartupEnabled").GetBoolean());
                Assert.AreEqual("notLoaded", body.GetProperty("state").GetString(),
                    "the policy took effect on disk, not on this process");
            }

            using (var host = NewHost(saveOnShutdown: false))
            {
                var archived = Collection(host).Snapshot().Single(n => n.Name == "archived");
                Assert.IsTrue(archived.IsLoaded);
                Assert.AreEqual(5, archived.Engine.VertexCount, "nothing was lost while it sat excluded");
            }
        }

        #endregion

        #region feature namespace-startup-load: activation (spec section 4.8)

        /// <summary>
        ///   Boots a namespace holding a checkpoint AND post-checkpoint write-ahead-log deltas, then
        ///   excludes it. Returns its id; the caller's next host has it cataloged but not loaded.
        ///   <para>Both halves matter for activation: 3 vertices live in the checkpoint and 2 only in
        ///   the WAL, so a count of 5 is the only way to see that the tail was replayed on top rather
        ///   than the checkpoint restored alone.</para>
        /// </summary>
        private async Task<string> ExcludeAPopulatedNamespace(string name)
        {
            string id;
            using (var host = NewHost(saveOnShutdown: false))
            {
                using var client = host.CreateClient();
                var namespaces = Collection(host);
                var ns = Create(namespaces, name);
                id = ns.Id;
                AddVertices(ns.Engine, 3);

                using var saved = await client.PutAsync("/ns/" + name + "/save",
                    new StringContent("{}", Encoding.UTF8, "application/json"));
                Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);

                // Post-checkpoint work: these live ONLY in the write-ahead log.
                AddVertices(ns.Engine, 2);
            }

            SetCatalogLoadPolicy(name, false);
            return id;
        }

        /// <summary>
        ///   The way back from a wrong exclusion without a restart: activation constructs the engine,
        ///   restores the newest registered checkpoint, replays the WAL tail on top, and only then
        ///   starts serving - so the data plane that answered 503 a moment ago answers the real count.
        /// </summary>
        [TestMethod]
        public async Task Activation_OfAnExcludedNamespace_RestoresItsCheckpointAndItsWalTail()
        {
            await ExcludeAPopulatedNamespace("archived");

            using var host = NewHost(saveOnShutdown: false);
            using var client = host.CreateClient();
            var namespaces = Collection(host);
            Assert.IsFalse(namespaces.Snapshot().Single(n => n.Name == "archived").IsLoaded);

            // Before: the data plane refuses (the state this feature made reachable).
            using (var refused = await client.GetAsync("/ns/archived/vertex/count"))
            {
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
                StringAssert.Contains((await ReadJson(refused)).GetProperty("detail").GetString(),
                    "POST /ns/archived/activate", "the refusal must name the way out that now exists");
            }

            using var response = await client.PostAsync("/ns/archived/activate", null);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var body = await ReadJson(response);
            Assert.IsTrue(body.GetProperty("activated").GetBoolean(), "this call is what loaded it");
            var entry = body.GetProperty("namespace");
            Assert.AreEqual("ready", entry.GetProperty("state").GetString());
            Assert.AreEqual(5, entry.GetProperty("vertexCount").GetInt32(),
                "3 vertices come from the checkpoint and 2 only from the write-ahead-log tail");
            StringAssert.Contains(body.GetProperty("detail").GetString(), "Restored from save game",
                "the operator is told what was restored, exactly as the boot log says it");

            // The engine really is resident, and the data plane now answers.
            Assert.IsTrue(namespaces.TryGet("archived", out var archived));
            Assert.IsTrue(archived.IsLoaded);
            Assert.AreEqual(5, archived.Engine.VertexCount);

            using var counted = await client.GetAsync("/ns/archived/vertex/count");
            Assert.AreEqual(HttpStatusCode.OK, counted.StatusCode);
            Assert.AreEqual(5, (await ReadJson(counted)).GetInt32());
        }

        /// <summary>
        ///   THE R5 REGRESSION PIN. Two concurrent activations of one namespace must construct
        ///   EXACTLY ONE engine, because two engines on one write-ahead log both adopt the same
        ///   baseline id, append into it independently, and the first Save rewrites that shared log to
        ///   a bare header - acknowledged commits then become silently non-durable.
        ///   <para>The discriminator is the COUNT of "activated" answers: exactly one caller may do
        ///   the work and every other must be told the namespace was already loaded. Verified to fail
        ///   with the per-namespace gate removed (every caller then passes the residency check,
        ///   constructs its own engine, and reports activated).</para>
        /// </summary>
        [TestMethod]
        public async Task ConcurrentActivation_OfTheSameNamespace_ConstructsExactlyOneEngine()
        {
            await ExcludeAPopulatedNamespace("archived");
            var walPath = Directory.GetFiles(Path.Combine(_storageDir, "namespaces"), "fallen8.wal*",
                SearchOption.AllDirectories).Single();
            var walBefore = File.ReadAllBytes(walPath);

            const int Callers = 8;
            var results = new NamespaceActivation[Callers];

            using (var host = NewHost(saveOnShutdown: false))
            {
                var loader = host.Services.GetRequiredService<NamespaceLoader>();
                var namespaces = Collection(host);
                Assert.IsFalse(namespaces.Snapshot().Single(n => n.Name == "archived").IsLoaded);

                // Dedicated threads plus a release gate, not a plain Task.WhenAll over started tasks:
                // the callers must reach the residency check TOGETHER, and thread-pool scheduling would
                // stagger them enough to hide the very overlap this test is about.
                using var release = new System.Threading.ManualResetEventSlim(false);
                var threads = new System.Threading.Thread[Callers];
                for (var i = 0; i < Callers; i++)
                {
                    var slot = i;
                    threads[slot] = new System.Threading.Thread(() =>
                    {
                        release.Wait();
                        results[slot] = loader.ActivateAsync("archived").GetAwaiter().GetResult();
                    });
                    threads[slot].IsBackground = true;
                    threads[slot].Start();
                }

                release.Set();
                foreach (var thread in threads)
                {
                    Assert.IsTrue(thread.Join(TimeSpan.FromMinutes(1)), "an activation thread deadlocked");
                }

                var outcomes = results.Select(r => r.Outcome).ToList();
                Assert.AreEqual(1, outcomes.Count(o => o == NamespaceActivationOutcome.Activated),
                    "exactly ONE caller may construct and load an engine: " + String.Join(", ", outcomes));
                Assert.AreEqual(Callers - 1, outcomes.Count(o => o == NamespaceActivationOutcome.AlreadyLoaded),
                    "every other caller must be told it was already loaded, not fail: " + String.Join(", ", outcomes));
                Assert.AreEqual(1, results.Select(r => r.Namespace.Engine).Distinct().Count(),
                    "and they all see the SAME engine instance");

                Assert.IsTrue(namespaces.TryGet("archived", out var archived));
                Assert.AreEqual(5, archived.Engine.VertexCount,
                    "one restore, not eight: a replayed-twice log would double the count");
            }

            // The shared log survived byte-identical: no second engine re-anchored or truncated it.
            CollectionAssert.AreEqual(walBefore, File.ReadAllBytes(walPath),
                "activation must not rewrite the write-ahead log it replayed");

            using (var host = NewHost(saveOnShutdown: false, startupLoadMode: "All"))
            {
                Assert.AreEqual(5, Collection(host).Snapshot().Single(n => n.Name == "archived").Engine.VertexCount,
                    "and a later boot still finds every acknowledged commit");
            }
        }

        /// <summary>
        ///   Activation is idempotent: a second call is a 200 saying it did nothing, never a conflict
        ///   and never a second load. The reserved default namespace - always loaded - is the same
        ///   answer from the other direction.
        /// </summary>
        [TestMethod]
        public async Task Activation_OfAnAlreadyLoadedNamespace_IsIdempotent()
        {
            await ExcludeAPopulatedNamespace("archived");

            using var host = NewHost(saveOnShutdown: false);
            using var client = host.CreateClient();

            using var first = await client.PostAsync("/ns/archived/activate", null);
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
            Assert.IsTrue((await ReadJson(first)).GetProperty("activated").GetBoolean());

            using var second = await client.PostAsync("/ns/archived/activate", null);
            Assert.AreEqual(HttpStatusCode.OK, second.StatusCode, "a repeat is a success, not a 409");
            var body = await ReadJson(second);
            Assert.IsFalse(body.GetProperty("activated").GetBoolean(), "the repeat did nothing");
            StringAssert.Contains(body.GetProperty("detail").GetString(), "already loaded");
            Assert.AreEqual(5, body.GetProperty("namespace").GetProperty("vertexCount").GetInt32(),
                "and it did not reload the checkpoint over the live graph");

            using var reserved = await client.PostAsync("/ns/default/activate", null);
            Assert.AreEqual(HttpStatusCode.OK, reserved.StatusCode);
            Assert.IsFalse((await ReadJson(reserved)).GetProperty("activated").GetBoolean(),
                "\"default\" is always loaded, so activating it is a no-op success");

            using var unknown = await client.PostAsync("/ns/never-existed/activate", null);
            Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.AreEqual("Namespace not found", (await ReadJson(unknown)).GetProperty("title").GetString());
        }

        /// <summary>
        ///   THE ORPHAN REFUSAL. A namespace whose directory holds checkpoint files that no
        ///   registered save game contains (save-games FR-11) must NOT be activated, because
        ///   publishing an engine there is the data-loss path this whole feature closes, reached from
        ///   the other end: the namespace would become resident and hold only its write-ahead-log
        ///   state, the §5 guard would correctly stop protecting it, and the next clean shutdown would
        ///   register that graph as its newest checkpoint and reset the log to a bare header.
        ///   <para>Verified against the mutation: with the orphan branch answering
        ///   <c>Ready</c> like the genuinely-empty branch, the activation answers 200 and publishes an
        ///   engine, and every assertion below fails.</para>
        /// </summary>
        [TestMethod]
        public async Task Activation_WithUnregisteredCheckpointFiles_Refuses_AndPublishesNoEngine()
        {
            // The orphan state (the fixture is shared with the boot arm of this outcome), plus the
            // exclusion that makes activation the caller who meets it.
            await LeaveUnregisteredCheckpointFiles("archived");
            SetCatalogLoadPolicy("archived", false);

            using var second = NewHost(saveOnShutdown: false);
            using var secondClient = second.CreateClient();
            var namespaces = Collection(second);
            var archived = namespaces.Snapshot().Single(n => n.Name == "archived");
            Assert.IsFalse(archived.IsLoaded, "the fixture must leave it not loaded");

            var directory = namespaces.DirectoryFor(archived);
            var before = DirectorySnapshot(directory);
            Assert.IsTrue(before.Keys.Any(f => f.StartsWith("Temp.f8s", StringComparison.Ordinal)),
                "the fixture must leave UNREGISTERED checkpoint files behind: " + String.Join(", ", before.Keys));

            using var response = await secondClient.PostAsync("/ns/archived/activate", null);

            // (i) It refuses, and the refusal names the situation and the way to adopt the files.
            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
            var problem = await ReadJson(response);
            Assert.AreEqual("Namespace has unregistered checkpoints", problem.GetProperty("title").GetString());
            Assert.AreEqual("archived", problem.GetProperty("namespace").GetString());
            Assert.AreEqual("notLoaded", problem.GetProperty("namespaceState").GetString());
            var detail = problem.GetProperty("detail").GetString();
            StringAssert.Contains(detail, "no registered save game", "the situation must be named");
            StringAssert.Contains(detail, "PATCH /ns/archived", "the cure starts with the startup-load policy");
            StringAssert.Contains(detail, "PUT /ns/archived/load", "and ends by registering the checkpoint");

            // (ii) NO engine was published: the namespace is still not loaded, in the collection and
            //      on the wire, so the §5 data-loss guard still protects it.
            Assert.IsFalse(namespaces.Snapshot().Single(n => n.Name == "archived").IsLoaded);
            using (var listed = await secondClient.GetAsync("/ns/archived"))
            {
                Assert.AreEqual("notLoaded", (await ReadJson(listed)).GetProperty("state").GetString());
            }
            using (var refused = await secondClient.GetAsync("/ns/archived/vertex/count"))
            {
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, refused.StatusCode,
                    "the data plane must still refuse rather than answer over an empty graph");
            }

            // (iii) And nothing on disk moved: the checkpoint files and the write-ahead log are
            //       byte-identical, so the operator's cure still has everything it needs.
            var after = DirectorySnapshot(directory);
            CollectionAssert.AreEquivalent(before.Keys.ToList(), after.Keys.ToList(),
                "a refused activation must neither add nor remove a file");
            foreach (var file in before.Keys)
            {
                CollectionAssert.AreEqual(before[file], after[file],
                    "\"" + file + "\" must be byte-identical after a refused activation");
            }
        }

        /// <summary>
        ///   A namespace that no registered save game contains AND that has no checkpoint files is
        ///   the genuinely-empty case, and it stays a success: it is exactly what a namespace created
        ///   and never saved looks like, so refusing here would make an ordinary namespace
        ///   unactivatable. The write-ahead log is what carries its data, and it is replayed.
        /// </summary>
        [TestMethod]
        public async Task Activation_WithNothingToRestore_Succeeds_AndReplaysTheWriteAheadLog()
        {
            using (var host = NewHost(saveOnShutdown: false))
            {
                var ns = Create(Collection(host), "scratch");
                AddVertices(ns.Engine, 4);
                Assert.AreEqual(0, Directory.GetFiles(Collection(host).DirectoryFor(ns), "Temp.f8s*").Length,
                    "this namespace was never saved, so it has no checkpoint files at all");
            }

            SetCatalogLoadPolicy("scratch", false);

            using var second = NewHost(saveOnShutdown: false);
            using var client = second.CreateClient();
            Assert.IsFalse(Collection(second).Snapshot().Single(n => n.Name == "scratch").IsLoaded);

            using var response = await client.PostAsync("/ns/scratch/activate", null);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "nothing to restore is not a refusal");
            var body = await ReadJson(response);
            Assert.IsTrue(body.GetProperty("activated").GetBoolean());
            Assert.AreEqual(4, body.GetProperty("namespace").GetProperty("vertexCount").GetInt32(),
                "its committed data comes back from the replayed write-ahead log");
        }

        /// <summary>
        ///   Activation answers for THIS process only: it must not rewrite the persisted policy. The
        ///   next boot honouring the unchanged policy is the assertion that matters - an operator who
        ///   activated a namespace once has not silently changed what the machine does on restart, and
        ///   the response says as much in its remarks.
        /// </summary>
        [TestMethod]
        public async Task Activation_LeavesThePersistedPolicyUnchanged()
        {
            await ExcludeAPopulatedNamespace("archived");

            using (var host = NewHost(saveOnShutdown: false))
            {
                using var client = host.CreateClient();
                using var response = await client.PostAsync("/ns/archived/activate", null);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                var entry = (await ReadJson(response)).GetProperty("namespace");
                Assert.AreEqual("ready", entry.GetProperty("state").GetString());
                Assert.IsFalse(entry.GetProperty("loadOnStartupEnabled").GetBoolean(),
                    "the entry reports a loaded namespace that the next boot still skips");
            }

            Assert.IsFalse(CatalogEntries()["archived"].GetProperty("loadOnStartupEnabled").GetBoolean(),
                "activation writes no catalog change at all");

            using (var host = NewHost(saveOnShutdown: false))
            {
                Assert.IsFalse(Collection(host).Snapshot().Single(n => n.Name == "archived").IsLoaded,
                    "the next boot follows the policy, not the last activation");
            }
        }

        #endregion

        [TestMethod]
        public void CreatedNamespace_SurvivesRestart_ThroughCatalogAndWal_WithoutAnySave()
        {
            string namespaceDir;
            using (var host = NewHost(saveOnShutdown: false))
            {
                var namespaces = Collection(host);
                var flights = Create(namespaces, "flights");
                namespaceDir = Path.Combine(_storageDir, "namespaces", flights.Id);
                AddVertices(flights.Engine, 2);
                AddVertices(namespaces.Default.Engine, 1);
            }

            Assert.IsTrue(File.Exists(Path.Combine(_metaDir, Fallen8Namespaces.CatalogFileName)), "catalog must exist");

            using (var host = NewHost(saveOnShutdown: false))
            {
                var namespaces = Collection(host);
                Assert.IsTrue(namespaces.TryGet("flights", out var flights), "flights must be back from the catalog");
                Assert.AreEqual(2, flights.Engine.VertexCount, "flights data must be back from its WAL");
                Assert.AreEqual(1, namespaces.Default.Engine.VertexCount, "default data must be back from the legacy WAL");
                Assert.IsTrue(Directory.Exists(namespaceDir), "the id-keyed directory persists across restarts");
            }
        }

        [TestMethod]
        public void DroppedAndRenamedNamespaces_KeepTheirCatalogStateAcrossRestarts()
        {
            using (var host = NewHost(saveOnShutdown: false))
            {
                var namespaces = Collection(host);
                Create(namespaces, "doomed");
                var kept = Create(namespaces, "kept");
                AddVertices(kept.Engine, 3);

                Assert.IsTrue(namespaces.TryDrop("doomed", out _));
                Assert.IsTrue(namespaces.TryRename("kept", "kept-eu", out _, out _));
            }

            using (var host = NewHost(saveOnShutdown: false))
            {
                var namespaces = Collection(host);
                Assert.IsFalse(namespaces.TryGet("doomed", out _), "dropped stays gone");
                Assert.IsFalse(namespaces.TryGet("kept", out _), "old name stays gone");
                Assert.IsTrue(namespaces.TryGet("kept-eu", out var kept), "rename survives restart");
                Assert.AreEqual(3, kept.Engine.VertexCount, "rename kept the data (id-keyed directory unmoved)");
            }
        }

        /// <summary>
        ///   Renamed from <c>ShutdownSave_SpansAllNamespaces_…</c>: an INTENTIONAL contract change, not
        ///   a test that broke. Since a namespace can be cataloged without being loaded (feature
        ///   namespace-startup-load §5), the shutdown entry spans every LOADED namespace and is
        ///   therefore a strict subset of the Fallen-8 - so "the newest entry is my whole Fallen-8"
        ///   stopped being true, and the old name asserted it.
        /// </summary>
        [TestMethod]
        public async Task ShutdownSave_SpansEveryLoadedNamespace_AndTheNextBootRestoresThem()
        {
            using (var host = NewHost(saveOnShutdown: true))
            {
                var namespaces = Collection(host);
                var flights = Create(namespaces, "flights");
                AddVertices(flights.Engine, 2);
                AddVertices(namespaces.Default.Engine, 1);

                // Drive the clean-shutdown save deterministically: under WebApplicationFactory,
                // container disposal races the host's own StopAsync, and the dispose gate then
                // (correctly) skips the save. The explicit call runs the real StopAsync logic; the
                // later teardown invocation no-ops via its at-most-once guard.
                var lifecycle = host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
                    .OfType<DurabilityLifecycleService>().Single();
                await lifecycle.StopAsync(System.Threading.CancellationToken.None);
            }

            // The shutdown save registered ONE entry spanning both namespaces.
            using (var host = NewHost())
            {
                using var client = host.CreateClient();
                using var response = await client.GetAsync("/savegames");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var entries = await ReadJson(response);
                Assert.IsTrue(entries.GetArrayLength() >= 1);
                var newest = entries[0];
                Assert.AreEqual("shutdown", newest.GetProperty("trigger").GetString());
                var members = newest.GetProperty("namespaces").EnumerateArray()
                    .Select(m => m.GetProperty("name").GetString()).OrderBy(n => n).ToList();
                CollectionAssert.AreEqual(new List<string> { "default", "flights" }, members);

                // And the boot restored both namespaces' data from that entry.
                var namespaces = Collection(host);
                Assert.IsTrue(namespaces.TryGet("flights", out var flights));
                Assert.AreEqual(2, flights.Engine.VertexCount);
                Assert.AreEqual(1, namespaces.Default.Engine.VertexCount);
            }
        }

        [TestMethod]
        public async Task SaveAll_ThenSingleNamespaceRestore_TouchesOnlyThatNamespace()
        {
            using var host = NewHost(saveOnShutdown: false);
            using var client = host.CreateClient();
            var namespaces = Collection(host);

            var flights = Create(namespaces, "flights");
            var scratch = Create(namespaces, "scratch");
            AddVertices(flights.Engine, 2);
            AddVertices(scratch.Engine, 1);

            using var saved = await client.PutAsync("/save/all", null);
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
            var entry = await ReadJson(saved);
            var id = entry.GetProperty("id").GetString();
            Assert.AreEqual(3, entry.GetProperty("namespaces").GetArrayLength(), "default + flights + scratch");

            // Diverge AFTER the save: drop flights entirely, grow scratch.
            Assert.IsTrue(namespaces.TryDrop("flights", out _));
            AddVertices(scratch.Engine, 1); // now 2

            // ?namespace= restores ONLY flights: it is recreated with its saved content, and the
            // post-save growth of scratch is untouched.
            using var restored = await client.PutAsync("/savegames/" + id + "/load?waitForCompletion=true&namespace=flights", null);
            Assert.AreEqual(HttpStatusCode.OK, restored.StatusCode);

            Assert.IsTrue(namespaces.TryGet("flights", out var flightsBack), "dropped namespace recreated by the restore");
            Assert.AreEqual(2, flightsBack.Engine.VertexCount, "restored to its saved content");
            Assert.IsTrue(namespaces.TryGet("scratch", out var scratchNow));
            Assert.AreEqual(2, scratchNow.Engine.VertexCount, "namespaces outside the ?namespace= selector stay untouched");

            // A namespace the entry does not contain -> 404 problem+json.
            using var missing = await client.PutAsync("/savegames/" + id + "/load?namespace=not-in-entry", null);
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.AreEqual("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
        }

        [TestMethod]
        public async Task EntryRestore_RestoresContainedNamespaces_AndLeavesOthersAlone()
        {
            using var host = NewHost(saveOnShutdown: false);
            using var client = host.CreateClient();
            var namespaces = Collection(host);

            var flights = Create(namespaces, "flights");
            AddVertices(flights.Engine, 2);
            AddVertices(namespaces.Default.Engine, 1);

            using var saved = await client.PutAsync("/save/all", null);
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
            var id = (await ReadJson(saved)).GetProperty("id").GetString();

            // Created AFTER the save: the entry does not contain it, so a full restore keeps it.
            var later = Create(namespaces, "later");
            AddVertices(later.Engine, 4);
            // Drop flights entirely - the restore must bring it back from the entry. (A live
            // namespace restored to the checkpoint its WAL is anchored to deliberately replays the
            // post-save commits - the engine's crash-consistency pairing; a recreated namespace has
            // a fresh WAL, so it restores to the entry's exact content.)
            Assert.IsTrue(namespaces.TryDrop("flights", out _));

            using var restored = await client.PutAsync("/savegames/" + id + "/load?waitForCompletion=true", null);
            Assert.AreEqual(HttpStatusCode.OK, restored.StatusCode);

            Assert.IsTrue(namespaces.TryGet("flights", out var flightsNow), "the dropped namespace is recreated by the restore");
            Assert.AreEqual(2, flightsNow.Engine.VertexCount, "flights restored to the entry's content");
            Assert.AreEqual(1, namespaces.Default.Engine.VertexCount, "default matches the entry's content");
            Assert.IsTrue(namespaces.TryGet("later", out var laterNow), "a namespace the entry does not contain survives");
            Assert.AreEqual(4, laterNow.Engine.VertexCount);
        }

        [TestMethod]
        public async Task PerNamespaceSave_ProducesASingleMemberEntry_UnderTheNamespaceDirectory()
        {
            using var host = NewHost(saveOnShutdown: false);
            using var client = host.CreateClient();
            var namespaces = Collection(host);

            var flights = Create(namespaces, "flights");
            AddVertices(flights.Engine, 2);

            using var saved = await client.PutAsync("/ns/flights/save",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
            var entry = await ReadJson(saved);

            var members = entry.GetProperty("namespaces");
            Assert.AreEqual(1, members.GetArrayLength());
            Assert.AreEqual("flights", members[0].GetProperty("name").GetString());
            StringAssert.Contains(members[0].GetProperty("location").GetString().Replace('\\', '/'),
                "/namespaces/" + flights.Id + "/", "per-namespace saves default into the id-keyed directory");

            // The top level mirrors the single member (v1-shaped entry).
            Assert.AreEqual(members[0].GetProperty("location").GetString(), entry.GetProperty("location").GetString());
            Assert.AreEqual(2, entry.GetProperty("kpis").GetProperty("vertexCount").GetInt32());
        }

        [TestMethod]
        public async Task RenamedNamespace_StillBootsFromItsNewestSave_AfterAnUncleanRestart()
        {
            // The boot chain is keyed by the IMMUTABLE id (council finding): a rename must not
            // orphan the namespace's newest save when no clean-shutdown save re-registers it.
            using (var host = NewHost(saveOnShutdown: false))
            {
                using var client = host.CreateClient();
                var namespaces = Collection(host);
                var flights = Create(namespaces, "flights");
                AddVertices(flights.Engine, 2);

                using var saved = await client.PutAsync("/ns/flights/save",
                    new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
                Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);

                using var renamed = await client.PatchAsync("/ns/flights",
                    new StringContent("{\"name\":\"fl-eu\"}", System.Text.Encoding.UTF8, "application/json"));
                Assert.AreEqual(HttpStatusCode.OK, renamed.StatusCode);
            }

            using (var host = NewHost(saveOnShutdown: false))
            {
                var namespaces = Collection(host);
                Assert.IsTrue(namespaces.TryGet("fl-eu", out var kept));
                Assert.AreEqual(2, kept.Engine.VertexCount, "the save registered under the OLD name must still load (id-keyed)");
            }
        }

        [TestMethod]
        public async Task RecreatedNamesake_DoesNotResurrectTheDroppedNamespacesCheckpoints()
        {
            // Drop keeps checkpoint files (they belong to save games); a fresh namesake has a
            // fresh id, so boot must NOT load the dropped predecessor's newest save over it.
            using (var host = NewHost(saveOnShutdown: false))
            {
                using var client = host.CreateClient();
                var namespaces = Collection(host);
                var flights = Create(namespaces, "flights");
                AddVertices(flights.Engine, 1);

                using var saved = await client.PutAsync("/ns/flights/save",
                    new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
                Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);

                Assert.IsTrue(namespaces.TryDrop("flights", out _));
                var reborn = Create(namespaces, "flights");
                AddVertices(reborn.Engine, 2);
            }

            using (var host = NewHost(saveOnShutdown: false))
            {
                var namespaces = Collection(host);
                Assert.IsTrue(namespaces.TryGet("flights", out var flights));
                Assert.AreEqual(2, flights.Engine.VertexCount,
                    "the reborn namespace must recover its own WAL, not the dropped predecessor's checkpoint");
            }
        }

        [TestMethod]
        public async Task Restore_WithMissingCheckpointFiles_Answers500_AndRecreatesNothing()
        {
            using var host = NewHost(saveOnShutdown: false);
            using var client = host.CreateClient();
            var namespaces = Collection(host);

            var flights = Create(namespaces, "flights");
            var flightsDir = Path.Combine(_storageDir, "namespaces", flights.Id);
            AddVertices(flights.Engine, 1);
            using var saved = await client.PutAsync("/ns/flights/save",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
            var entryId = (await ReadJson(saved)).GetProperty("id").GetString();

            Assert.IsTrue(namespaces.TryDrop("flights", out _));
            foreach (var file in Directory.GetFiles(flightsDir))
            {
                File.Delete(file); // gut the entry: the checkpoint files are gone
            }

            using var restored = await client.PutAsync(
                "/savegames/" + entryId + "/load?waitForCompletion=true&namespace=flights", null);
            Assert.AreEqual(HttpStatusCode.InternalServerError, restored.StatusCode);
            Assert.AreEqual("application/problem+json", restored.Content.Headers.ContentType?.MediaType);
            Assert.IsFalse(namespaces.TryGet("flights", out _),
                "a restore that cannot load anything must not recreate the namespace");
        }

        [TestMethod]
        public async Task PreNamespaceRegistry_V1OnDisk_BootsIntoDefault()
        {
            // A REAL pre-upgrade deployment: strip this build's v2 fields from the on-disk
            // registry (schemaVersion 1, no namespaces manifests) and boot against it.
            using (var host = NewHost(saveOnShutdown: false))
            {
                using var client = host.CreateClient();
                AddVertices(Collection(host).Default.Engine, 2);
                using var saved = await client.PutAsync("/save",
                    new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
                Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
            }

            var registryPath = Path.Combine(_metaDir, "savegames.json");
            var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(registryPath))!.AsObject();
            document["schemaVersion"] = 1;
            foreach (var entry in document["saveGames"]!.AsArray())
            {
                entry!.AsObject().Remove("namespaces");
            }
            File.WriteAllText(registryPath, document.ToJsonString());

            using (var host = NewHost(saveOnShutdown: false))
            {
                var namespaces = Collection(host);
                Assert.AreEqual(2, namespaces.Default.Engine.VertexCount,
                    "a v1 entry must be read forever as a default-only save");
            }
        }

        [TestMethod]
        public void V1Entries_ReadAsDefaultOnly()
        {
            var v1 = new NoSQL.GraphDB.App.Controllers.Model.SaveGameREST
            {
                Id = "sg-legacy",
                Location = "C:/somewhere/Temp.f8s",
                FileCount = 3,
                TotalBytes = 42L,
            };

            var members = SaveGameRegistry.EffectiveNamespaces(v1);

            Assert.AreEqual(1, members.Count);
            Assert.AreEqual(Fallen8Namespaces.DefaultName, members[0].Name);
            Assert.AreEqual(v1.Location, members[0].Location);
            Assert.AreEqual(3, members[0].FileCount);
        }

        /// <summary>
        ///   The factory reset drops a NOT-LOADED namespace too (feature namespace-startup-load, spec
        ///   decision 8.2): a documented reset that silently spared one would leave data the next boot
        ///   resurrects after the operator believes they erased it. It is built for real here - a
        ///   catalog entry, a directory and a write-ahead log holding committed data - and deliberately
        ///   with NO checkpoint, because a drop keeps checkpoint files (they belong to save-game
        ///   entries), so a namespace carrying one would leave its directory behind for that reason
        ///   rather than for a missed drop. Being a HEAD route it answers with no body at all, so what
        ///   was dropped is named in the server log and nowhere else.
        /// </summary>
        [TestMethod]
        public async Task TabulaRasa_IsNamespaceScoped_AndTabulaRasaAll_IsTheFactoryReset()
        {
            string archivedId;
            using (var first = NewHost(saveOnShutdown: false))
            {
                var archived = Create(Collection(first), "archived");
                archivedId = archived.Id;
                AddVertices(archived.Engine, 2);
            }

            SetCatalogLoadPolicy("archived", false);
            var archivedDirectory = Path.Combine(_storageDir, "namespaces", archivedId);
            Assert.AreEqual(1, Directory.GetFiles(archivedDirectory, "fallen8.wal*").Length,
                "the not-loaded namespace must have a write-ahead log for the reset to delete");

            var sink = new TestLogSink();
            using var host = NewHost(saveOnShutdown: false, sink: sink);
            using var client = host.CreateClient();
            var namespaces = Collection(host);
            Assert.IsFalse(namespaces.Snapshot().Single(n => n.Name == "archived").IsLoaded);

            var flights = Create(namespaces, "flights");
            var scratch = Create(namespaces, "scratch");
            AddVertices(flights.Engine, 2);
            AddVertices(scratch.Engine, 1);
            AddVertices(namespaces.Default.Engine, 1);

            // Scoped erase: only flights is emptied; it stays registered.
            using (var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/ns/flights/tabularasa")))
            {
                Assert.IsTrue(response.IsSuccessStatusCode, "HEAD /ns/flights/tabularasa: " + response.StatusCode);
            }
            await WaitForVertexCount(flights.Engine, 0);
            Assert.IsTrue(namespaces.TryGet("flights", out _), "tabula rasa keeps the namespace registered");
            Assert.AreEqual(1, scratch.Engine.VertexCount);
            Assert.AreEqual(1, namespaces.Default.Engine.VertexCount);

            // Factory reset: only an empty default remains.
            using (var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/tabularasa/all")))
            {
                Assert.IsTrue(response.IsSuccessStatusCode, "HEAD /tabularasa/all: " + response.StatusCode);
                Assert.AreEqual(0, (await response.Content.ReadAsByteArrayAsync()).Length,
                    "it is a HEAD route: there is no body, so nothing in the response can name what was dropped");
            }
            await WaitForVertexCount(namespaces.Default.Engine, 0);
            Assert.AreEqual(1, namespaces.Count, "only default remains");
            Assert.IsFalse(namespaces.TryGet("flights", out _));
            Assert.IsFalse(namespaces.TryGet("scratch", out _));

            // The not-loaded namespace went with them, in all three places it existed.
            Assert.IsFalse(namespaces.TryGet("archived", out _), "a reset that spares it is a reset that lies");
            Assert.IsFalse(CatalogEntries().ContainsKey("archived"),
                "its catalog entry is gone, so no later boot resurrects it");
            Assert.IsFalse(Directory.Exists(archivedDirectory),
                "and its write-ahead log and directory are deleted, exactly as for a loaded namespace");
            Assert.IsTrue(sink.Contains(LogLevel.Information, "Dropped namespace", "archived"),
                "the server log is the only place that names it, which is what the docs must say");
        }

        /// <summary>Tabula rasa is enqueued fire-and-forget; poll briefly for the writer thread.</summary>
        private static async Task WaitForVertexCount(Fallen8 engine, int expected)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (engine.VertexCount == expected)
                {
                    return;
                }
                await Task.Delay(20);
            }
            Assert.AreEqual(expected, engine.VertexCount);
        }
    }
}
