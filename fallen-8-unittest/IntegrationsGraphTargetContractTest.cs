// MIT License
//
// IntegrationsGraphTargetContractTest.cs
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
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;
using NoSQL.GraphDB.Integrations.Graph;
using NoSQL.GraphDB.Integrations.Identity;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   THE SHARED CONTRACT SUITE (feature integrations): the same assertions against both implementations of
    ///   <see cref="IGraphTarget"/>, so <c>InMemoryGraphTarget</c> cannot drift stricter or laxer than the
    ///   platform it stands in for.
    ///
    ///   <para>Why it has to exist: the whole conformance suite runs against the in-memory graph, so every
    ///   guarantee an author reads there is only worth what this suite proves. A fake that is HARSHER than the
    ///   platform hides a real failure (an index write the platform silently declines) and one that is LAXER
    ///   invents a guarantee (an id that survives a trim). Both are caught here by asking the same questions
    ///   twice.</para>
    /// </summary>
    public abstract class IntegrationsGraphTargetContractTest
    {
        private const String Instance = "contract-suite";

        /// <summary>A graph with no history, and no indices yet.</summary>
        protected abstract Task<IGraphTarget> CreateTargetAsync();

        /// <summary>
        ///   Drops one index the way three ordinary platform operations do (a tabula rasa, loading a save game, a
        ///   per-index serialization failure), because the loud-missing-index behaviour is the single most
        ///   consequential thing this seam promises.
        /// </summary>
        protected abstract Task DropIndexAsync(IGraphTarget target, String indexId);

        [TestMethod]
        public async Task EnsureIndices_ReportsWhetherItHadToCreate_SoTheCallerKnowsItMustRepair()
        {
            using var target = await CreateTargetAsync();

            Assert.IsTrue(await target.EnsureIndicesAsync(CancellationToken.None),
                "the first ensure creates both indices and must say so: a fresh index is empty, and empty is " +
                "indistinguishable from 'no element carries this claim'");
            Assert.IsFalse(await target.EnsureIndicesAsync(CancellationToken.None),
                "a second ensure created nothing, so it must not oblige another repair");
        }

        [TestMethod]
        public async Task AScanAgainstAMissingIndex_Raises_RatherThanAnsweringEmpty()
        {
            using var target = await CreateTargetAsync();
            await target.EnsureIndicesAsync(CancellationToken.None);
            await DropIndexAsync(target, ClaimSchema.IdentityIndexId);

            await Assert.ThrowsExceptionAsync<GraphIndexMissingException>(
                () => target.ResolveClaimKeysAsync(new[] { "mac:44d244aabbcc" }, Instance, CancellationToken.None),
                "an empty answer would read as 'nothing carries this claim', which duplicates every element on " +
                "the next resolve and leaves the originals claimed by nobody");
        }

        [TestMethod]
        public async Task CreateVertices_ReturnsTheIdsInInputOrder_AndReadElementsSeesWhatWasWritten()
        {
            using var target = await CreateTargetAsync();
            await target.EnsureIndicesAsync(CancellationToken.None);

            var ids = await target.CreateVerticesAsync(new[]
            {
                Vertex("device", ("csv.name", "first")),
                Vertex("device", ("csv.name", "second")),
            }, CancellationToken.None);

            Assert.AreEqual(2, ids.Count);
            Assert.AreNotEqual(ids[0], ids[1]);

            var read = await target.ReadElementsAsync(new[] { ids[0], ids[1] }, CancellationToken.None);
            Assert.AreEqual("first", read[ids[0]].Properties["csv.name"].Text,
                "the ids come back in INPUT order, or a run claims the wrong element");
            Assert.AreEqual("second", read[ids[1]].Properties["csv.name"].Text);
            Assert.AreEqual("device", read[ids[0]].Label);
        }

        [TestMethod]
        public async Task AWriteBiggerThanOneBatch_KeepsTheIdsInInputOrder_AcrossEveryBatch()
        {
            // More than the REST target's write batch (500), so the write is split. It has to be pinned
            // on the CONTRACT rather than on that target, because the ordering promise is what callers
            // build on and the in-memory target must make the same one.
            //
            // Why batching exists at all: a run over a 99 MiB device list produced a single PUT vertices
            // body of about 110 MB, which the graph's own route refuses at Kestrel's 30 MB default - and
            // the runtime could only report it as "the graph did not answer", for a graph that was fine.
            // Splitting the body fixed that; getting the SPLIT wrong instead attaches every edge after
            // the first batch to the wrong vertex, which is plausible nonsense nothing detects later.
            const Int32 Count = 1200;

            using var target = await CreateTargetAsync();
            await target.EnsureIndicesAsync(CancellationToken.None);

            var writes = new List<VertexWrite>(Count);
            for (var i = 0; i < Count; i++)
            {
                writes.Add(Vertex("device", ("csv.name", "device-" + i.ToString(CultureInfo.InvariantCulture))));
            }

            var ids = await target.CreateVerticesAsync(writes, CancellationToken.None);

            Assert.AreEqual(Count, ids.Count, "every vertex sent comes back with an id");
            CollectionAssert.AllItemsAreUnique(ids.ToList(),
                "a repeated id means one batch's answer was counted twice, and two rows then share an element");

            // Spot-checked at the batch SEAMS, which is where an off-by-one lands: the last of one batch
            // and the first of the next.
            var probes = new[] { 0, 499, 500, 501, 999, 1000, Count - 1 };
            var read = await target.ReadElementsAsync(probes.Select(p => ids[p]).ToArray(),
                CancellationToken.None);

            foreach (var probe in probes)
            {
                Assert.AreEqual("device-" + probe.ToString(CultureInfo.InvariantCulture),
                    read[ids[probe]].Properties["csv.name"].Text, String.Format(
                        "the id at input position {0} must name the element written at position {0}. A batch " +
                        "boundary that shifts the ids makes every claim and every edge after it point at the " +
                        "wrong element, and the run reports success", probe));
            }

            // Edges too: the ARXML integration is overwhelmingly edges, so a vertex-only check would leave
            // the half that matters most to it unexercised.
            var edgeWrites = new List<EdgeWrite>(Count);
            for (var i = 0; i < Count; i++)
            {
                edgeWrites.Add(new EdgeWrite(ids[i], ids[(i + 1) % Count], "uplink",
                    new[] { new GraphProperty("csv.hop", "System.String", "hop-" + i.ToString(CultureInfo.InvariantCulture)) }));
            }

            var edgeIds = await target.CreateEdgesAsync(edgeWrites, CancellationToken.None);

            Assert.AreEqual(Count, edgeIds.Count, "every edge sent comes back with an id");
            CollectionAssert.AllItemsAreUnique(edgeIds.ToList(), "no edge id is reported twice");

            var edgeRead = await target.ReadElementsAsync(new[] { edgeIds[0], edgeIds[500], edgeIds[Count - 1] },
                CancellationToken.None);
            Assert.AreEqual("hop-0", edgeRead[edgeIds[0]].Properties["csv.hop"].Text);
            Assert.AreEqual("hop-500", edgeRead[edgeIds[500]].Properties["csv.hop"].Text,
                "the first edge of the second batch, which is where a mis-split shows");
            Assert.AreEqual("hop-" + (Count - 1).ToString(CultureInfo.InvariantCulture),
                edgeRead[edgeIds[Count - 1]].Properties["csv.hop"].Text);
        }

        [TestMethod]
        public async Task ReadElements_OmitsAnIdThatResolvesToNothing_RatherThanInventingAnEmptyElement()
        {
            using var target = await CreateTargetAsync();
            await target.EnsureIndicesAsync(CancellationToken.None);
            var ids = await target.CreateVerticesAsync(new[] { Vertex("device", ("csv.name", "one")) },
                CancellationToken.None);

            var read = await target.ReadElementsAsync(new[] { ids[0], 999_999 }, CancellationToken.None);

            Assert.IsTrue(read.ContainsKey(ids[0]));
            Assert.IsFalse(read.ContainsKey(999_999),
                "'gone' and 'has no properties' are different conclusions, and reconciliation acts differently " +
                "on each: an id the claim index still names but the graph no longer has must be neither " +
                "withdrawn nor deleted");
        }

        [TestMethod]
        public async Task APropertyValueReadBack_IsTheValueThatWasWritten_WhichIsWhatMakesTheDiffMeaningful()
        {
            using var target = await CreateTargetAsync();
            await target.EnsureIndicesAsync(CancellationToken.None);

            var written = new[]
            {
                new GraphProperty("csv.name", "System.String", "Reception printer"),
                new GraphProperty("unifi.uptime", "System.Int64", "12345"),
                new GraphProperty("fronius.pvPower", "System.Double", "8200.5"),
                new GraphProperty("csv.enabled", "System.Boolean", "True"),
            };

            var ids = await target.CreateVerticesAsync(new[] { new VertexWrite("device", written) },
                CancellationToken.None);
            var read = await target.ReadElementsAsync(ids, CancellationToken.None);

            foreach (var property in written)
            {
                Assert.IsTrue(read[ids[0]].Properties.TryGetValue(property.Key, out var stored),
                    property.Key + " survived the write");
                Assert.IsFalse(property.DiffersFrom(stored), String.Format(
                    "'{0}' was written as ({1}, {2}) and came back as ({3}, {4}). Egress must mirror ingress, or " +
                    "every run sees a difference and every run is a write.",
                    property.Key, property.TypeName, property.Text, stored.TypeName, stored.Text));
            }
        }

        [TestMethod]
        public async Task RemovingAnAbsentProperty_IsANoOp_AndIsWhatMakesAReplayedWithdrawalSafe()
        {
            using var target = await CreateTargetAsync();
            await target.EnsureIndicesAsync(CancellationToken.None);
            var ids = await target.CreateVerticesAsync(new[] { Vertex("device", ("csv.name", "one")) },
                CancellationToken.None);

            await target.ApplyPropertyWritesAsync(new[]
            {
                PropertyWrite.Remove_(ids[0], "$claim:nobody"),
            }, CancellationToken.None);

            var read = await target.ReadElementsAsync(ids, CancellationToken.None);
            Assert.AreEqual("one", read[ids[0]].Properties["csv.name"].Text,
                "removing a property that is not there changes nothing else");
        }

        [TestMethod]
        public async Task RemovingAVertex_CascadesToItsEdges()
        {
            using var target = await CreateTargetAsync();
            await target.EnsureIndicesAsync(CancellationToken.None);

            var vertices = await target.CreateVerticesAsync(new[]
            {
                Vertex("device", ("csv.name", "left")),
                Vertex("device", ("csv.name", "right")),
            }, CancellationToken.None);

            var edges = await target.CreateEdgesAsync(new[]
            {
                new EdgeWrite(vertices[0], vertices[1], "uplink", new[]
                {
                    new GraphProperty(ClaimSchema.IdentityProperty(0), "System.String", "edge:a|uplink|b"),
                }),
            }, CancellationToken.None);

            await target.RemoveElementsAsync(new[] { vertices[0] }, CancellationToken.None);

            var read = await target.ReadElementsAsync(new[] { vertices[0], edges[0] }, CancellationToken.None);
            Assert.IsFalse(read.ContainsKey(vertices[0]));
            Assert.IsFalse(read.ContainsKey(edges[0]),
                "an edge whose endpoint is gone cannot survive, or a later run finds it by its derived key and " +
                "never re-wires");
        }

        [TestMethod]
        public async Task AnIndexWriteForAnElementThatDoesNotExist_IsDECLINED_NotSilentlyAccepted()
        {
            using var target = await CreateTargetAsync();
            await target.EnsureIndicesAsync(CancellationToken.None);

            var outcome = await target.IndexClaimsAsync(new[]
            {
                new IndexEntry(ClaimSchema.IdentityIndexId, "mac:44d244aabbcc", 987_654),
            }, CancellationToken.None);

            Assert.AreEqual(0, outcome.Accepted);
            Assert.AreEqual(1, outcome.Declined.Length,
                "the platform declines with a plain false, and an element findable by none of its claims is " +
                "duplicated on the next resolve, so the refusal must reach the report");
        }

        [TestMethod]
        public async Task AClaimWrittenToTheIndex_IsFoundByTheKey_AndOnlyByTheKey()
        {
            using var target = await CreateTargetAsync();
            await target.EnsureIndicesAsync(CancellationToken.None);

            var ids = await target.CreateVerticesAsync(new[]
            {
                new VertexWrite("device", new[]
                {
                    new GraphProperty(ClaimSchema.IdentityProperty(0), "System.String", "mac:44d244aabbcc"),
                    new GraphProperty(ClaimSchema.ClaimProperty(Instance), "System.String", Instance),
                }),
            }, CancellationToken.None);

            await target.IndexClaimsAsync(new[]
            {
                new IndexEntry(ClaimSchema.IdentityIndexId, "mac:44d244aabbcc", ids[0]),
                new IndexEntry(ClaimSchema.ClaimsIndexId, Instance, ids[0]),
            }, CancellationToken.None);

            var found = await target.ResolveClaimKeysAsync(
                new[] { "mac:44d244aabbcc", "mac:000000000000" }, Instance, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { ids[0] }, new List<Int32>(found.InScope["mac:44d244aabbcc"]));
            Assert.IsFalse(found.InScope.ContainsKey("mac:000000000000"),
                "a key nothing carries is simply absent");

            var claimed = await target.ElementsClaimedByAsync(Instance, CancellationToken.None);
            CollectionAssert.Contains(new List<Int32>(claimed), ids[0]);
        }

        [TestMethod]
        public async Task AnElementAnotherInstanceClaims_IsNotInScope_AndOneWithNoClaimIs()
        {
            using var target = await CreateTargetAsync();
            await target.EnsureIndicesAsync(CancellationToken.None);

            var ids = await target.CreateVerticesAsync(new[]
            {
                // Foreign: claimed by somebody else.
                new VertexWrite("device", new[]
                {
                    new GraphProperty(ClaimSchema.IdentityProperty(0), "System.String", "mac:aaaaaaaaaaaa"),
                    new GraphProperty(ClaimSchema.ClaimProperty("someone-else"), "System.String", "someone-else"),
                }),

                // Orphan: carries identity and no claim, which is what a deferred deletion leaves behind.
                new VertexWrite("device", new[]
                {
                    new GraphProperty(ClaimSchema.IdentityProperty(0), "System.String", "mac:bbbbbbbbbbbb"),
                }),
            }, CancellationToken.None);

            await target.IndexClaimsAsync(new[]
            {
                new IndexEntry(ClaimSchema.IdentityIndexId, "mac:aaaaaaaaaaaa", ids[0]),
                new IndexEntry(ClaimSchema.IdentityIndexId, "mac:bbbbbbbbbbbb", ids[1]),
            }, CancellationToken.None);

            var found = await target.ResolveClaimKeysAsync(
                new[] { "mac:aaaaaaaaaaaa", "mac:bbbbbbbbbbbb" }, Instance, CancellationToken.None);

            Assert.IsTrue(found.ByKey.ContainsKey("mac:aaaaaaaaaaaa"),
                "the un-narrowed answer keeps the foreign hit, which is what lets an edge fall through rather " +
                "than adopting another instance's edge");
            Assert.IsFalse(found.InScope.ContainsKey("mac:aaaaaaaaaaaa"),
                "an element another instance claims is out of scope");
            CollectionAssert.AreEqual(new[] { ids[1] }, new List<Int32>(found.InScope["mac:bbbbbbbbbbbb"]),
                "THE UNCLAIMED ARM IS LOAD-BEARING: without it the orphan is invisible forever and the graph " +
                "gains a duplicate on every run");
        }

        [TestMethod]
        public async Task RepairFromElementState_RestoresEVERYClaimOfAnElement_NotOnlyTheFirst()
        {
            using var target = await CreateTargetAsync();
            await target.EnsureIndicesAsync(CancellationToken.None);

            var ids = await target.CreateVerticesAsync(new[]
            {
                new VertexWrite("device", new[]
                {
                    new GraphProperty(ClaimSchema.IdentityProperty(0), "System.String", "mac:44d244aabbcc"),
                    new GraphProperty(ClaimSchema.IdentityProperty(1), "System.String", "serial:SN-1"),
                    new GraphProperty(ClaimSchema.ClaimProperty(Instance), "System.String", Instance),
                }),
            }, CancellationToken.None);

            // The indices are dropped the way an ordinary platform operation drops them, then repaired from what
            // the elements say.
            await DropIndexAsync(target, ClaimSchema.IdentityIndexId);
            await DropIndexAsync(target, ClaimSchema.ClaimsIndexId);
            await target.EnsureIndicesAsync(CancellationToken.None);
            await target.RepairIndicesAsync(CancellationToken.None);

            var found = await target.ResolveClaimKeysAsync(
                new[] { "mac:44d244aabbcc", "serial:SN-1" }, Instance, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { ids[0] }, new List<Int32>(found.InScope["mac:44d244aabbcc"]));
            CollectionAssert.AreEqual(new[] { ids[0] }, new List<Int32>(found.InScope["serial:SN-1"]),
                "an exact-key repair restores only the FIRST claim of each element, which leaves it findable by " +
                "one identity and invisible by the rest: a repair that looks successful and then duplicates the " +
                "element on the next resolve");

            var claimed = await target.ElementsClaimedByAsync(Instance, CancellationToken.None);
            CollectionAssert.Contains(new List<Int32>(claimed), ids[0],
                "the claim index has to come back too, or reconciliation reads 'this instance claims nothing'");
        }

        [TestMethod]
        public async Task ReadDurability_AnswersWhetherDeletingIsSafe()
        {
            using var target = await CreateTargetAsync();

            var durability = await target.ReadDurabilityAsync(CancellationToken.None);

            Assert.IsNotNull(durability);
            Assert.IsTrue(durability.SafeToDelete, String.Format(
                "a healthy test graph must license deletion, or the deferral path is the only one ever exercised " +
                "({0})", durability.Reason()));
        }

        private static VertexWrite Vertex(String label, params (String Key, String Value)[] properties)
        {
            var rendered = new List<GraphProperty>(properties.Length);
            foreach (var property in properties)
            {
                rendered.Add(new GraphProperty(property.Key, "System.String", property.Value));
            }

            return new VertexWrite(label, rendered);
        }
    }

    /// <summary>The in-memory graph, judged by the shared contract.</summary>
    [TestClass]
    public sealed class InMemoryGraphTargetContractTest : IntegrationsGraphTargetContractTest
    {
        protected override Task<IGraphTarget> CreateTargetAsync()
        {
            return Task.FromResult<IGraphTarget>(new InMemoryGraphTarget());
        }

        protected override Task DropIndexAsync(IGraphTarget target, String indexId)
        {
            ((InMemoryGraphTarget)target).DropIndex(indexId);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    ///   The live graph, judged by the same contract, over an apiApp hosted in process. Each test gets its own
    ///   NAMESPACE rather than its own host, because a namespace is an isolated graph and booting a host per test
    ///   would pay a second of startup for nothing.
    /// </summary>
    [TestClass]
    public sealed class Fallen8RestTargetContractTest : IntegrationsGraphTargetContractTest
    {
        private static WebApplicationFactory<Program> _factory;
        private static Int32 _namespaceCounter;

        /// <summary>
        ///   The namespace this test's target writes into. An INSTANCE field, because MSTest builds one instance
        ///   per test method: reading the shared counter back would tie the drop to whichever test incremented it
        ///   last.
        /// </summary>
        private String _namespace;

        [ClassInitialize]
        public static void StartHost(TestContext context)
        {
            _factory = new HostedApp();
        }

        [ClassCleanup]
        public static void StopHost()
        {
            _factory?.Dispose();
            _factory = null;
        }

        protected override async Task<IGraphTarget> CreateTargetAsync()
        {
            _namespace = "contract-" + Interlocked.Increment(ref _namespaceCounter)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);

            using (var admin = _factory.CreateClient())
            using (var created = await admin.PutAsync("/ns/" + _namespace, new StringContent(
                       "{}", System.Text.Encoding.UTF8, "application/json")))
            {
                Assert.IsTrue(created.IsSuccessStatusCode,
                    "the test namespace was created: " + created.StatusCode);
            }

            return new Fallen8RestTarget(_factory.CreateClient(), _namespace);
        }

        protected override async Task DropIndexAsync(IGraphTarget target, String indexId)
        {
            using var client = _factory.CreateClient();
            using var response = await client.DeleteAsync("/ns/" + _namespace + "/index/" + indexId);
            Assert.IsTrue(response.IsSuccessStatusCode, "the index was dropped: " + response.StatusCode);
        }

        private sealed class HostedApp : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment("Development");

                // Volatile durability: booting the host writes no checkpoint or write-ahead log into the test bin.
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
            }
        }
    }
}
