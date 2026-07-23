// MIT License
//
// NamespaceDurabilityTest.cs
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
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
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

            public DurabilityFactory(IReadOnlyDictionary<string, string> settings)
            {
                _settings = settings;
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                foreach (var kv in _settings)
                {
                    builder.UseSetting(kv.Key, kv.Value);
                }
            }
        }

        private DurabilityFactory NewHost(bool saveOnShutdown = true)
        {
            return new DurabilityFactory(new Dictionary<string, string>
            {
                ["Fallen8:Durability:StorageDirectory"] = _storageDir,
                ["Fallen8:Durability:Volatile"] = "false",
                ["Fallen8:Durability:SaveOnShutdown"] = saveOnShutdown ? "true" : "false",
                ["Fallen8:Metadata:Directory"] = _metaDir,
            });
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

        [TestMethod]
        public async Task ShutdownSave_SpansAllNamespaces_AndTheNextBootRestoresThem()
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

        [TestMethod]
        public async Task TabulaRasa_IsNamespaceScoped_AndTabulaRasaAll_IsTheFactoryReset()
        {
            using var host = NewHost(saveOnShutdown: false);
            using var client = host.CreateClient();
            var namespaces = Collection(host);

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
            }
            await WaitForVertexCount(namespaces.Default.Engine, 0);
            Assert.AreEqual(1, namespaces.Count, "only default remains");
            Assert.IsFalse(namespaces.TryGet("flights", out _));
            Assert.IsFalse(namespaces.TryGet("scratch", out _));
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
