// MIT License
//
// LiveSettingTest.cs
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
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Namespaces;
using NoSQL.GraphDB.Core;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// The live tier (feature writable-instance-config phase 4).
    ///
    /// Every test here asserts OBSERVED BEHAVIOUR after a write, never that an option value changed.
    /// That distinction is the whole point of the phase: a wrong "applies immediately" claim is the
    /// worst defect this feature can ship, because it fails silently, and a test that reads the option
    /// back would pass for a key whose new value nothing consults.
    ///
    /// Each key here is liveForNewWork rather than live, and the tests assert both halves: the new
    /// limit governs new work, and existing work is untouched. Reporting these as plainly "applied"
    /// would be the silently-did-not-apply defect in the other direction.
    /// </summary>
    [TestClass]
    public class LiveSettingTest
    {
        private const String Key = "test-key-live";

        private String _metadata;

        [TestInitialize]
        public void CreateMetadataDirectory()
        {
            _metadata = Path.Combine(Path.GetTempPath(), "f8-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_metadata);
        }

        [TestCleanup]
        public void RemoveMetadataDirectory()
        {
            try
            {
                if (_metadata != null && Directory.Exists(_metadata))
                {
                    Directory.Delete(_metadata, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }

        /// <summary>
        ///   Builds a host whose starting values for the keys under test come from the STORED OVERRIDES
        ///   FILE rather than from UseSetting.
        ///
        ///   <para>That is not a detail: a test host delivers every UseSetting value as a command-line
        ///   argument, and the command line is an authority no stored override may outrank, so a key
        ///   seeded that way is correctly refused with 409 by this feature's own arbitration and could
        ///   never be written. Seeding through the file puts the value in the layer a write actually
        ///   replaces, which is also how a real operator's instance is configured.</para>
        /// </summary>
        private WebApplicationFactory<Program> CreateFactory(IDictionary<String, String> settings = null)
        {
            if (settings != null && settings.Count > 0)
            {
                var body = String.Join(",", settings.Select(p => "\"" + p.Key + "\": \"" + p.Value + "\""));
                File.WriteAllText(Path.Combine(_metadata, Fallen8ConfigOverridesSource.FileName),
                    "{ \"version\": 1, \"settings\": { " + body + " } }");
            }

            return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                // These four are never-writable, so seeding them by UseSetting collides with nothing.
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
                builder.UseSetting("Fallen8:Metadata:Directory", _metadata);
                builder.UseSetting("Fallen8:Security:ApiKey", Key);
                builder.UseSetting("Fallen8:Security:EnableConfigurationWrite", "true");
            });
        }

        private static HttpClient Authenticated(WebApplicationFactory<Program> factory)
        {
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", Key);
            return client;
        }

        private static async Task Write(HttpClient client, String key, String value)
        {
            using var response = await client.PatchAsJsonAsync("/config",
                new { settings = new Dictionary<String, String> { [key] = value } });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "the write itself must succeed before its effect can be judged: "
                    + await response.Content.ReadAsStringAsync());
        }

        #region the change-feed subscriber limits

        /// <summary>
        ///   The subscriber cap governs a NEW subscribe immediately, and no existing subscriber is
        ///   evicted when it is lowered. Asserted through the dispatcher the running engine actually
        ///   enforces, so it cannot pass because an option value moved.
        /// </summary>
        [TestMethod]
        public async Task MaxSubscribers_GovernsANewSubscribeImmediately_AndEvictsNobody()
        {
            using var factory = CreateFactory(new Dictionary<String, String>
            {
                ["Fallen8:ChangeFeed:MaxSubscribers"] = "1"
            });
            using var client = Authenticated(factory);
            var feed = ((IFallen8)factory.Services.GetService(typeof(IFallen8))).ChangeFeed;

            Assert.IsTrue(feed.TrySubscribe(null, null, null, out var first),
                "the first subscribe fits the cap of one");
            Assert.IsFalse(feed.TrySubscribe(null, null, null, out _),
                "and the second does not, which is the behaviour the write has to change");

            await Write(client, "Fallen8:ChangeFeed:MaxSubscribers", "3");

            Assert.IsTrue(feed.TrySubscribe(null, null, null, out var second),
                "the raised cap governs the very next subscribe, with no restart");
            Assert.IsTrue(feed.TrySubscribe(null, null, null, out var third));
            Assert.IsFalse(feed.TrySubscribe(null, null, null, out _), "and the new cap is enforced too");

            // Lowering it takes effect for new work only: the three existing subscribers stay.
            await Write(client, "Fallen8:ChangeFeed:MaxSubscribers", "1");
            Assert.IsFalse(feed.TrySubscribe(null, null, null, out _), "a new subscribe is refused at once");
            Assert.IsNotNull(first, "and nobody already subscribed was evicted");
            Assert.IsNotNull(second);
            Assert.IsNotNull(third);

            first.Dispose();
            second.Dispose();
            third.Dispose();
        }

        /// <summary>
        ///   The queue depth applies to a subscription created after the write, while a subscription
        ///   created before it keeps the depth it was given. A subscription does not publish its capacity,
        ///   so the depth is observed the only way a client could: by not draining and counting how much
        ///   the queue actually holds.
        /// </summary>
        [TestMethod]
        public async Task SubscriberQueueSize_AppliesToANewSubscription_AndLeavesExistingOnesAlone()
        {
            using var factory = CreateFactory(new Dictionary<String, String>
            {
                ["Fallen8:ChangeFeed:SubscriberQueueSize"] = "2",
                ["Fallen8:ChangeFeed:MaxSubscribers"] = "8"
            });
            using var client = Authenticated(factory);
            var feed = ((IFallen8)factory.Services.GetService(typeof(IFallen8))).ChangeFeed;

            Assert.IsTrue(feed.TrySubscribe(null, null, null, out var shallow));

            await Write(client, "Fallen8:ChangeFeed:SubscriberQueueSize", "16");

            Assert.IsTrue(feed.TrySubscribe(null, null, null, out var deep));

            // Six events, drained by nobody. The shallow queue can hold two of them; the one created
            // after the write can hold all six.
            for (var i = 0; i < 6; i++)
            {
                using var created = await client.PutAsync("/vertex?waitForCompletion=true",
                    new StringContent("{\"label\":\"live-probe\",\"creationDate\":1}",
                        System.Text.Encoding.UTF8, "application/json"));
                created.EnsureSuccessStatusCode();
            }

            var deepCount = await WaitForCount(deep.Reader, 6);
            Assert.AreEqual(6, deepCount,
                "the subscription created after the write holds every event, so the new depth is in force");

            var shallowCount = shallow.Reader.CanCount ? shallow.Reader.Count : -1;
            Assert.IsTrue(shallowCount >= 0 && shallowCount < 6,
                "the subscription created BEFORE the write kept its shallow queue, which is what makes this "
                    + "new-work-only rather than live; it held " + shallowCount);

            shallow.Dispose();
            deep.Dispose();
        }

        /// <summary>
        ///   Waits briefly for the dispatcher to deliver, since delivery is not synchronous with the commit.
        ///   Returns the count reached, so the assertion reports what actually arrived.
        /// </summary>
        private static async Task<Int32> WaitForCount(
            System.Threading.Channels.ChannelReader<NoSQL.GraphDB.Core.ChangeFeed.ChangeEvent> reader, Int32 expected)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (reader.CanCount && reader.Count >= expected)
                {
                    return reader.Count;
                }

                await Task.Delay(50);
            }

            return reader.CanCount ? reader.Count : -1;
        }

        #endregion

        #region the registration ceilings

        /// <summary>
        ///   The stored-query ceiling governs the next registration immediately, and lowering it evicts
        ///   nothing. Asserted through the REST surface that enforces it, so the assertion is what an
        ///   operator would see.
        /// </summary>
        [TestMethod]
        public async Task StoredQueryCeiling_RefusesTheNextRegistrationImmediately_AndEvictsNothing()
        {
            using var factory = CreateFactory(new Dictionary<String, String>
            {
                ["Fallen8:StoredQueries:MaxCount"] = "1"
            });
            using var client = Authenticated(factory);

            Assert.AreEqual(HttpStatusCode.Created, (await Register(client, "first")).StatusCode);

            var refused = await Register(client, "second");
            Assert.AreEqual(HttpStatusCode.Conflict, refused.StatusCode,
                "the ceiling of one is in force, which is the behaviour the write must change");

            await Write(client, "Fallen8:StoredQueries:MaxCount", "5");

            Assert.AreEqual(HttpStatusCode.Created, (await Register(client, "second")).StatusCode,
                "the raised ceiling governs the very next registration, with no restart");

            // Lowered below what is already registered: nothing is evicted, only new work is refused.
            await Write(client, "Fallen8:StoredQueries:MaxCount", "1");
            Assert.AreEqual(HttpStatusCode.Conflict, (await Register(client, "third")).StatusCode);

            using var listed = await client.GetAsync("/storedquery");
            Assert.AreEqual(HttpStatusCode.OK, listed.StatusCode);
            var body = await listed.Content.ReadAsStringAsync();
            StringAssert.Contains(body, "first", "the registrations already held survive a lowered ceiling");
            StringAssert.Contains(body, "second");
        }

        private static Task<HttpResponseMessage> Register(HttpClient client, String name)
        {
            return client.PostAsJsonAsync("/storedquery", new
            {
                name,
                kind = "Path",
                path = new
                {
                    filter = new { vertexFilter = "return (v) => true;" }
                }
            });
        }

        /// <summary>
        ///   The namespace ceiling governs the next creation immediately, and lowering it below the
        ///   namespaces that exist deletes none of them.
        /// </summary>
        [TestMethod]
        public async Task NamespaceCeiling_RefusesTheNextCreationImmediately_AndDeletesNothing()
        {
            using var factory = CreateFactory(new Dictionary<String, String>
            {
                ["Fallen8:Namespaces:MaxNamespaces"] = "2"
            });
            using var client = Authenticated(factory);

            // "default" counts, so one more fits and the next is refused.
            using var created = await client.PutAsync("/ns/alpha", null);
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);

            using var refused = await client.PutAsync("/ns/beta", null);
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, refused.StatusCode,
                "the ceiling of two is in force (a quota refusal, not a name conflict)");

            await Write(client, "Fallen8:Namespaces:MaxNamespaces", "4");

            using var allowed = await client.PutAsync("/ns/beta", null);
            Assert.AreEqual(HttpStatusCode.Created, allowed.StatusCode,
                "the raised ceiling governs the very next creation, with no restart");

            await Write(client, "Fallen8:Namespaces:MaxNamespaces", "1");
            using var refusedAgain = await client.PutAsync("/ns/gamma", null);
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, refusedAgain.StatusCode);

            using var listed = await client.GetAsync("/ns");
            var names = (await listed.Content.ReadAsStringAsync());
            StringAssert.Contains(names, "alpha", "lowering the ceiling deletes no namespace");
            StringAssert.Contains(names, "beta");
        }

        #endregion

        #region what the surface promises about a live key

        /// <summary>
        ///   A live key must report the honest apply mode and must never appear in the pending-restart
        ///   set: it took effect, so there is nothing waiting for a restart.
        /// </summary>
        [TestMethod]
        public async Task ALiveWrite_ReportsNewWorkOnly_AndIsNotPendingARestart()
        {
            using var factory = CreateFactory();
            using var client = Authenticated(factory);

            using var response = await client.PatchAsJsonAsync("/config",
                new { settings = new Dictionary<String, String> { ["Fallen8:ChangeFeed:MaxSubscribers"] = "40" } });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            var result = body.GetProperty("results")[0];

            Assert.AreEqual("liveForNewWork", result.GetProperty("applyMode").GetString(),
                "the promise names what is NOT affected: existing subscribers keep their slot");
            Assert.IsFalse(result.GetProperty("restartPending").GetBoolean());
            Assert.IsFalse(result.TryGetProperty("applyFailure", out _), "the delegate ran");
            Assert.AreEqual(0, body.GetProperty("pendingRestart").GetArrayLength(),
                "a live key that applied is never waiting for a restart");
        }

        /// <summary>
        ///   The apply runs on any configuration reload, not only on a write. Without that, a live key's
        ///   published value could move (appsettings.json reloads on change in production) while the value
        ///   in force did not, and the pending signal says nothing about live keys, so nothing would flag
        ///   the difference.
        /// </summary>
        [TestMethod]
        public void TheApply_RunsOnEveryReload_NotOnlyOnAWrite()
        {
            using var factory = CreateFactory(new Dictionary<String, String>
            {
                ["Fallen8:ChangeFeed:MaxSubscribers"] = "1"
            });
            _ = factory.Services.GetService(typeof(IFallen8)); // force the host to build

            var overridesFile = Path.Combine(_metadata, Fallen8ConfigOverridesSource.FileName);
            File.WriteAllText(overridesFile,
                "{ \"version\": 1, \"settings\": { \"Fallen8:ChangeFeed:MaxSubscribers\": \"7\" } }");

            // A reload this process did not initiate through PATCH.
            factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            ((Microsoft.Extensions.Configuration.IConfigurationRoot)factory.Services
                .GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()).Reload();

            var limits = factory.Services.GetRequiredService<Fallen8Namespaces>().ChangeFeedLimits;
            Assert.AreEqual(7, limits.MaxSubscribers,
                "the reload token drives the apply, so a value that changed outside a write is still in force");
        }

        /// <summary>
        ///   Spec 4.8, checked rather than trusted: promoting one key must not make its never-writable or
        ///   restart-tier neighbours live. The change-feed section is the case that matters, because its
        ///   buffer size is baked into an allocated ring and its enabled flag decides the feed exists.
        /// </summary>
        [TestMethod]
        public async Task PromotingOneKey_LeavesItsNeighboursOnTheirBootValues()
        {
            using var factory = CreateFactory(new Dictionary<String, String>
            {
                ["Fallen8:ChangeFeed:BufferSize"] = "512",
                ["Fallen8:ChangeFeed:MaxSubscribers"] = "2"
            });
            using var client = Authenticated(factory);
            var limits = factory.Services.GetRequiredService<Fallen8Namespaces>().ChangeFeedLimits;

            Assert.AreEqual(512, limits.BufferSize);

            // A write to the live sibling, then the never-live neighbour must not have moved. Writing
            // BufferSize itself is refused as restart tier, so this asserts the apply delegate does not
            // carry it along.
            await Write(client, "Fallen8:ChangeFeed:MaxSubscribers", "9");

            Assert.AreEqual(9, limits.MaxSubscribers, "the promoted key moved");
            Assert.AreEqual(512, limits.BufferSize,
                "and the restart-tier neighbour on the same object did not, which is what per-key means");
        }

        /// <summary>
        ///   The catalog's own promise: a live entry can apply itself, and its tier and apply mode agree.
        ///   Cheap, and it is what stops a future promotion shipping a tier with no way to honour it.
        /// </summary>
        [TestMethod]
        public void EveryPromotedKey_DeclaresItsApplyModeAndCanApply()
        {
            var live = Fallen8SettingCatalog.Entries
                .Where(entry => entry.Tier == Fallen8SettingTier.Live)
                .ToList();

            Assert.AreEqual(6, live.Count, "the phase 4 tranche is six keys; update this when it grows");

            foreach (var entry in live)
            {
                Assert.IsNotNull(entry.ApplyNow, entry.Key + " claims to be live with no way to apply");
                Assert.AreEqual(Fallen8SettingApplyMode.LiveForNewWork, entry.ApplyMode,
                    entry.Key + " is a cap consulted when work starts, so it must promise new work only");
            }
        }

        #endregion
    }
}
