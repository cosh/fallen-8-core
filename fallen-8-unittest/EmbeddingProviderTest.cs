// MIT License
//
// EmbeddingProviderTest.cs
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
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Embedding;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Deterministic fake backend: text-hash → unit-ish vector, so identical texts embed
    ///   identically (cosine 1) and CI needs no live model. Configurable output length to
    ///   exercise the dimension hard-error.
    /// </summary>
    internal sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly int _dimension;
        internal int Calls;

        internal FakeEmbeddingGenerator(int dimension)
        {
            _dimension = dimension;
        }

        internal static float[] VectorFor(string text, int dimension)
        {
            var vector = new float[dimension];
            var hash = 17;
            foreach (var c in text ?? String.Empty)
            {
                hash = unchecked(hash * 31 + c);
            }

            for (var i = 0; i < dimension; i++)
            {
                // Deterministic, non-zero, finite.
                vector[i] = 0.1f + Math.Abs(unchecked(hash * (i + 3)) % 997) / 1000f;
            }
            return vector;
        }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            var result = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var value in values)
            {
                result.Add(new Embedding<float>(VectorFor(value, _dimension)));
            }
            return Task.FromResult(result);
        }

        public object GetService(Type serviceType, object serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>
    ///   Feature embedding-provider: capability gating, the wrapper's FR-8 validation, the
    ///   text-in endpoints (element/elements/search/text), queryText traversal, stamps, and
    ///   statistics surfacing - all against the deterministic fake (no live model in CI).
    /// </summary>
    [TestClass]
    public class EmbeddingProviderTest
    {
        private const int Dim = 4;

        private const string ApiKey = "embedding-test-key";

        private sealed class ProviderFactory : WebApplicationFactory<Program>
        {
            private readonly bool _enabled;
            private readonly int _fakeDimension;
            private readonly bool _withApiKey;

            public ProviderFactory(bool enabled, int fakeDimension = Dim, bool withApiKey = false)
            {
                _enabled = enabled;
                _fakeDimension = fakeDimension;
                _withApiKey = withApiKey;
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
                builder.UseSetting("Fallen8:Embedding:Enabled", _enabled ? "true" : "false");
                builder.UseSetting("Fallen8:Embedding:Backend", "Onnx"); // never constructed: the fake replaces it
                builder.UseSetting("Fallen8:Embedding:ModelName", "fake-model");
                builder.UseSetting("Fallen8:Embedding:Dimension", Dim.ToString());
                builder.UseSetting("Fallen8:Embedding:MaxBatchSize", "4");
                if (_withApiKey)
                {
                    builder.UseSetting("Fallen8:Security:ApiKey", ApiKey);
                }
                builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
                        new FakeEmbeddingGenerator(_fakeDimension));
                });
            }
        }

        private static Fallen8 EngineOf(WebApplicationFactory<Program> factory)
            => (Fallen8)factory.Services.GetRequiredService<IFallen8>();

        private static int Vertex(Fallen8 engine)
        {
            var tx = new CreateVertexTransaction { Definition = new VertexDefinition { CreationDate = 1u, Label = "p" } };
            engine.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.VertexCreated.Id;
        }

        private static StringContent Json(string json) => new StringContent(json, Encoding.UTF8, "application/json");

        private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
            => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        #region gating & statistics

        [TestMethod]
        public async Task Disabled_EveryEmbeddingSurfaceIs403_AndNothingLoads()
        {
            // The capability answers 403 to an AUTHENTICATED caller (the api-security-boundary
            // posture; unauthenticated on a keyed server is 401 like everywhere else).
            using var factory = new ProviderFactory(enabled: false, withApiKey: true);
            var a = Vertex(EngineOf(factory));
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);

            foreach (var (url, body) in new (string, string)[]
            {
                ("/embedding/element", $"{{ \"graphElementId\": {a}, \"text\": \"x\" }}"),
                ("/embedding/elements", "{ \"items\": [ { \"graphElementId\": 0, \"text\": \"x\" } ] }"),
                ("/embedding/search", "{ \"indexId\": \"i\", \"text\": \"x\", \"k\": 1 }"),
                ("/embedding/text", "{ \"texts\": [\"x\"] }"),
            })
            {
                using var response = await client.PostAsync(url, Json(body));
                Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, url);
            }

            // queryText is gated by the same capability.
            using var path = await client.PostAsync($"/path/{a}/to/{a}",
                Json("{ \"semantic\": { \"queryText\": \"x\" } }"));
            Assert.AreEqual(HttpStatusCode.Forbidden, path.StatusCode);

            // Statistics surfaces the dark provider without loading anything.
            using var statistics = await client.GetAsync("/statistics");
            Assert.AreEqual(HttpStatusCode.OK, statistics.StatusCode);
            var embedding = (await ReadJson(statistics)).GetProperty("embedding");
            Assert.IsFalse(embedding.GetProperty("enabled").GetBoolean());
            Assert.IsFalse(embedding.GetProperty("loaded").GetBoolean(), "nothing may load while disabled");
        }

        [TestMethod]
        public async Task Statistics_SurfacesIdentity_WithoutTriggeringTheLazyLoad()
        {
            using var factory = new ProviderFactory(enabled: true);
            using var client = factory.CreateClient();

            using var statistics = await client.GetAsync("/statistics");
            var embedding = (await ReadJson(statistics)).GetProperty("embedding");
            Assert.IsTrue(embedding.GetProperty("enabled").GetBoolean());
            Assert.AreEqual("fake-model", embedding.GetProperty("modelName").GetString());
            Assert.AreEqual(Dim, embedding.GetProperty("dimension").GetInt32());
            Assert.AreEqual("Cosine", embedding.GetProperty("intendedMetric").GetString());
            Assert.IsFalse(embedding.GetProperty("loaded").GetBoolean(),
                "statistics must never trigger the lazy load");

            // First use loads; statistics then reports it.
            using var _ = await client.PostAsync("/embedding/text", Json("{ \"texts\": [\"x\"] }"));
            using var after = await client.GetAsync("/statistics");
            Assert.IsTrue((await ReadJson(after)).GetProperty("embedding").GetProperty("loaded").GetBoolean());
        }

        #endregion

        #region endpoints

        [TestMethod]
        public async Task EmbedElement_WritesVectorAndStamp_AndProjectsIntoBoundIndex()
        {
            using var factory = new ProviderFactory(enabled: true);
            var engine = EngineOf(factory);
            var a = Vertex(engine);
            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out var index, "emb", "VectorIndex",
                new Dictionary<string, object> { { "dimension", Dim }, { "embeddingName", "default" } }));
            using var client = factory.CreateClient();

            using var response = await client.PostAsync("/embedding/element",
                Json($"{{ \"graphElementId\": {a}, \"text\": \"a red bicycle\" }}"));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

            Assert.IsTrue(engine.TryGetGraphElement(out var element, a));
            Assert.IsTrue(element.TryGetEmbedding(out var vector));
            CollectionAssert.AreEqual(FakeEmbeddingGenerator.VectorFor("a red bicycle", Dim), vector.ToArray());
            Assert.IsTrue(element.TryGetEmbeddingModelStamp(out var stamp));
            Assert.AreEqual("fake-model#4#Cosine", stamp);
            Assert.AreEqual(1, index.CountOfValues(), "the committed write projected into the bound index");
        }

        [TestMethod]
        public async Task EmbedElements_Batch_OneProviderCall_OneTransaction()
        {
            using var factory = new ProviderFactory(enabled: true);
            var engine = EngineOf(factory);
            var a = Vertex(engine);
            var b = Vertex(engine);
            using var client = factory.CreateClient();

            using var response = await client.PostAsync("/embedding/elements",
                Json($"{{ \"items\": [ {{ \"graphElementId\": {a}, \"text\": \"one\" }}, {{ \"graphElementId\": {b}, \"text\": \"two\" }} ] }}"));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

            Assert.IsTrue(engine.TryGetGraphElement(out var elementA, a));
            Assert.IsTrue(elementA.TryGetEmbedding(out _));
            Assert.IsTrue(engine.TryGetGraphElement(out var elementB, b));
            Assert.IsTrue(elementB.TryGetEmbedding(out _));

            // Oversized batch (MaxBatchSize = 4 in this host) → 400.
            var items = String.Join(", ", Enumerable.Range(0, 5).Select(i => $"{{ \"graphElementId\": {a}, \"text\": \"t{i}\" }}"));
            using var tooBig = await client.PostAsync("/embedding/elements", Json($"{{ \"items\": [ {items} ] }}"));
            Assert.AreEqual(HttpStatusCode.BadRequest, tooBig.StatusCode);

            // Unknown element → 404, nothing embedded.
            using var missing = await client.PostAsync("/embedding/elements",
                Json("{ \"items\": [ { \"graphElementId\": 424242, \"text\": \"x\" } ] }"));
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        }

        [TestMethod]
        public async Task SemanticSearch_FindsTheMatchingElement_AndEnforcesTheIdentityContract()
        {
            using var factory = new ProviderFactory(enabled: true);
            var engine = EngineOf(factory);
            var a = Vertex(engine);
            var b = Vertex(engine);
            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out _, "emb", "VectorIndex",
                new Dictionary<string, object> { { "dimension", Dim }, { "embeddingName", "default" } }));
            using var client = factory.CreateClient();

            using var seedA = await client.PostAsync("/embedding/element", Json($"{{ \"graphElementId\": {a}, \"text\": \"a red bicycle\" }}"));
            Assert.AreEqual(HttpStatusCode.OK, seedA.StatusCode);
            using var seedB = await client.PostAsync("/embedding/element", Json($"{{ \"graphElementId\": {b}, \"text\": \"a blue whale\" }}"));
            Assert.AreEqual(HttpStatusCode.OK, seedB.StatusCode);

            // The fake embeds identical text identically → cosine 1 for the exact match.
            using var search = await client.PostAsync("/embedding/search",
                Json("{ \"indexId\": \"emb\", \"text\": \"a red bicycle\", \"k\": 1 }"));
            Assert.AreEqual(HttpStatusCode.OK, search.StatusCode, await search.Content.ReadAsStringAsync());
            var body = await ReadJson(search);
            Assert.AreEqual(a, body.GetProperty("results")[0].GetProperty("graphElementId").GetInt32());
            Assert.AreEqual(1f, body.GetProperty("results")[0].GetProperty("score").GetSingle(), 1e-5f);

            // Dimension contract: an index of another dimension → 409.
            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out _, "other-dim", "VectorIndex",
                new Dictionary<string, object> { { "dimension", Dim + 1 } }));
            using var dimensionClash = await client.PostAsync("/embedding/search",
                Json("{ \"indexId\": \"other-dim\", \"text\": \"x\", \"k\": 1 }"));
            Assert.AreEqual(HttpStatusCode.Conflict, dimensionClash.StatusCode);

            // Model-identity contract: an index declaring a DIFFERENT model → 409.
            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out _, "other-model", "VectorIndex",
                new Dictionary<string, object> { { "dimension", Dim }, { "model", "someone-else#4#Cosine" } }));
            using var modelClash = await client.PostAsync("/embedding/search",
                Json("{ \"indexId\": \"other-model\", \"text\": \"x\", \"k\": 1 }"));
            Assert.AreEqual(HttpStatusCode.Conflict, modelClash.StatusCode);

            // A matching declared identity passes.
            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out _, "same-model", "VectorIndex",
                new Dictionary<string, object> { { "dimension", Dim }, { "model", "fake-model#4#Cosine" }, { "embeddingName", "default" } }));
            using var match = await client.PostAsync("/embedding/search",
                Json("{ \"indexId\": \"same-model\", \"text\": \"a red bicycle\", \"k\": 1 }"));
            Assert.AreEqual(HttpStatusCode.OK, match.StatusCode);
        }

        [TestMethod]
        public async Task EmbedText_ReturnsVectorsAndIdentity()
        {
            using var factory = new ProviderFactory(enabled: true);
            using var client = factory.CreateClient();

            using var response = await client.PostAsync("/embedding/text", Json("{ \"texts\": [\"one\", \"two\"] }"));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var body = await ReadJson(response);
            Assert.AreEqual("fake-model#4#Cosine", body.GetProperty("model").GetString());
            Assert.AreEqual(Dim, body.GetProperty("dimension").GetInt32());
            Assert.AreEqual(2, body.GetProperty("vectors").GetArrayLength());
            Assert.AreEqual(Dim, body.GetProperty("vectors")[0].GetArrayLength());
        }

        [TestMethod]
        public async Task QueryText_DrivesASemanticPath_WithDynamicCodeOff()
        {
            using var factory = new ProviderFactory(enabled: true);
            var engine = EngineOf(factory);
            using var client = factory.CreateClient();

            // Diamond a -> b -> d / a -> c -> d, embedded via the provider: b matches the
            // query text exactly, c does not.
            var vtx = new CreateVerticesTransaction();
            for (var i = 0; i < 4; i++)
            {
                vtx.AddVertex(1u, "n");
            }
            engine.EnqueueTransaction(vtx).WaitUntilFinished();
            var v = vtx.GetCreatedVertices();
            var edges = new CreateEdgesTransaction();
            edges.AddEdge(v[0].Id, "knows", v[1].Id, 1u, "knows");
            edges.AddEdge(v[1].Id, "knows", v[3].Id, 1u, "knows");
            edges.AddEdge(v[0].Id, "knows", v[2].Id, 1u, "knows");
            edges.AddEdge(v[2].Id, "knows", v[3].Id, 1u, "knows");
            engine.EnqueueTransaction(edges).WaitUntilFinished();

            foreach (var (id, text) in new[] { (v[0].Id, "query"), (v[1].Id, "query"), (v[2].Id, "unrelated other thing"), (v[3].Id, "query") })
            {
                using var seed = await client.PostAsync("/embedding/element", Json($"{{ \"graphElementId\": {id}, \"text\": \"{text}\" }}"));
                Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode);
            }

            using var path = await client.PostAsync($"/path/{v[0].Id}/to/{v[3].Id}",
                Json("{ \"semantic\": { \"queryText\": \"query\", \"minScore\": 0.999 } }"));
            Assert.AreEqual(HttpStatusCode.OK, path.StatusCode, await path.Content.ReadAsStringAsync());
            var paths = await ReadJson(path);
            Assert.AreEqual(1, paths.GetArrayLength(), "only the exact-match route survives the threshold");

            // queryText and queryVector together → 400.
            using var both = await client.PostAsync($"/path/{v[0].Id}/to/{v[3].Id}",
                Json("{ \"semantic\": { \"queryText\": \"query\", \"queryVector\": [1, 0, 0, 0] } }"));
            Assert.AreEqual(HttpStatusCode.BadRequest, both.StatusCode);
        }

        #endregion

        #region wrapper contract

        private static Fallen8EmbeddingProvider Provider(Fallen8EmbeddingOptions options,
            IEmbeddingGenerator<string, Embedding<float>> generator)
        {
            return new Fallen8EmbeddingProvider(Options.Create(options),
                new Lazy<IEmbeddingGenerator<string, Embedding<float>>>(() => generator));
        }

        [TestMethod]
        public async Task Wrapper_DimensionContradiction_LatchesAsUnavailable()
        {
            var options = new Fallen8EmbeddingOptions { Enabled = true, ModelName = "m", Dimension = 8 };
            var provider = Provider(options, new FakeEmbeddingGenerator(4)); // produces 4, declares 8

            await Assert.ThrowsExceptionAsync<EmbeddingProviderUnavailableException>(
                () => provider.EmbedAsync(new[] { "x" }, default));

            // Latched: the second call fails the same way without another backend call.
            await Assert.ThrowsExceptionAsync<EmbeddingProviderUnavailableException>(
                () => provider.EmbedAsync(new[] { "x" }, default));
            Assert.IsFalse(provider.IsLoaded);
        }

        private sealed class BrokenGenerator : IEmbeddingGenerator<string, Embedding<float>>
        {
            private readonly float[] _vector;
            internal BrokenGenerator(float[] vector) { _vector = vector; }

            public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
                EmbeddingGenerationOptions options = null, CancellationToken cancellationToken = default)
            {
                var result = new GeneratedEmbeddings<Embedding<float>>();
                foreach (var _ in values)
                {
                    result.Add(new Embedding<float>(_vector));
                }
                return Task.FromResult(result);
            }

            public object GetService(Type serviceType, object serviceKey = null) => null;
            public void Dispose() { }
        }

        [TestMethod]
        public async Task Wrapper_NonFiniteAndZeroNormOutput_AreUpstreamFaults_NotLatched()
        {
            var options = new Fallen8EmbeddingOptions { Enabled = true, ModelName = "m", Dimension = 2 };

            var nanProvider = Provider(options, new BrokenGenerator(new[] { float.NaN, 1f }));
            await Assert.ThrowsExceptionAsync<EmbeddingProviderOutputException>(
                () => nanProvider.EmbedAsync(new[] { "x" }, default));

            var zeroProvider = Provider(options, new BrokenGenerator(new[] { 0f, 0f }));
            await Assert.ThrowsExceptionAsync<EmbeddingProviderOutputException>(
                () => zeroProvider.EmbedAsync(new[] { "x" }, default));
        }

        [TestMethod]
        public async Task Wrapper_Disabled_ThrowsUnavailable_WithoutTouchingTheBackend()
        {
            var fake = new FakeEmbeddingGenerator(2);
            var provider = Provider(new Fallen8EmbeddingOptions { Enabled = false, Dimension = 2 }, fake);

            await Assert.ThrowsExceptionAsync<EmbeddingProviderUnavailableException>(
                () => provider.EmbedAsync(new[] { "x" }, default));
            Assert.AreEqual(0, fake.Calls);
            Assert.IsFalse(provider.IsLoaded);
        }

        [TestMethod]
        public async Task Wrapper_FailedLazyCreation_IsLatchedByConstruction()
        {
            var options = new Fallen8EmbeddingOptions { Enabled = true, ModelName = "m", Dimension = 2 };
            var creations = 0;
            var provider = new Fallen8EmbeddingProvider(Options.Create(options),
                new Lazy<IEmbeddingGenerator<string, Embedding<float>>>(() =>
                {
                    creations++;
                    throw new InvalidOperationException("model file missing");
                }));

            await Assert.ThrowsExceptionAsync<EmbeddingProviderUnavailableException>(
                () => provider.EmbedAsync(new[] { "x" }, default));
            await Assert.ThrowsExceptionAsync<EmbeddingProviderUnavailableException>(
                () => provider.EmbedAsync(new[] { "x" }, default));
            Assert.AreEqual(1, creations, "Lazy(ExecutionAndPublication) caches the creation failure");
        }

        #endregion

        #region durability of stamps

        [TestMethod]
        public void ModelStamp_RoundTripsThroughTheWal()
        {
            var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "f8_stampwal_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(tempDir);
            var walPath = System.IO.Path.Combine(tempDir, "stamp.wal");
            try
            {
                int a;
                using (var writer = new Fallen8(TestLoggerFactory.Create(), new WriteAheadLogOptions(walPath)))
                {
                    var tx = new CreateVertexTransaction { Definition = new VertexDefinition { CreationDate = 1u } };
                    writer.EnqueueTransaction(tx).WaitUntilFinished();
                    a = tx.VertexCreated.Id;
                    writer.EnqueueTransaction(new SetEmbeddingsTransaction()
                            .SetEmbedding(a, "default", new[] { 1f, 2f }, "fake-model#2#Cosine"))
                        .WaitUntilFinished();
                }

                using var recovered = new Fallen8(TestLoggerFactory.Create(), new WriteAheadLogOptions(walPath));
                Assert.IsTrue(recovered.TryGetGraphElement(out var element, a));
                Assert.IsTrue(element.TryGetEmbeddingModelStamp(out var stamp));
                Assert.AreEqual("fake-model#2#Cosine", stamp);

                // A bring-your-own-vector overwrite CLEARS the stamp - it can never lie.
                recovered.EnqueueTransaction(new SetEmbeddingsTransaction().SetEmbedding(a, "default", new[] { 3f, 4f }))
                    .WaitUntilFinished();
                Assert.IsFalse(element.TryGetEmbeddingModelStamp(out _));
            }
            finally
            {
                try { System.IO.Directory.Delete(tempDir, true); } catch { /* best effort */ }
            }
        }

        #endregion
    }
}
