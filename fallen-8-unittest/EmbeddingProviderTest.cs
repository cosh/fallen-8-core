// MIT License
//
// EmbeddingProviderTest.cs
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

        /// <summary>The size of each request, in order. Lets a test see the BATCHING a caller did,
        /// not just that it embedded the right number of things in the end.</summary>
        internal readonly System.Collections.Concurrent.ConcurrentQueue<int> BatchSizes =
            new System.Collections.Concurrent.ConcurrentQueue<int>();

        /// <summary>
        ///   From this call onwards (1-based), refuse the way a remote backend does when a key's
        ///   hourly token budget runs out part way through a long run. 0 never refuses.
        /// </summary>
        internal int RefuseFromCall;

        /// <summary>The refusal's wording, which belongs to the backend and is what the provider reads
        /// to decide whether a failure deserves a pointer at the setting behind it.</summary>
        internal string RefusalMessage = "the hourly token budget for this key is spent";

        /// <summary>The per-call options the provider passed, so a test sees what the backend was
        /// actually ASKED to do rather than trusting the wrapper's intent.</summary>
        internal EmbeddingGenerationOptions LastOptions;

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
            LastOptions = options;
            var call = Interlocked.Increment(ref Calls);
            if (RefuseFromCall > 0 && call >= RefuseFromCall)
            {
                throw new InvalidOperationException(RefusalMessage);
            }

            var result = new GeneratedEmbeddings<Embedding<float>>();
            var size = 0;
            foreach (var value in values)
            {
                size++;
                result.Add(new Embedding<float>(VectorFor(value, _dimension)));
            }
            BatchSizes.Enqueue(size);
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

        private sealed class ProviderFactory : VolatileAppFactory
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
                base.ConfigureWebHost(builder);
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
            => factory.Services.GetRequiredService<NoSQL.GraphDB.App.Namespaces.Fallen8Namespaces>().Default.Engine;

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

        [TestMethod]
        public async Task Status_SurfacesProviderState_OnTheCheapDiscoverySurface()
        {
            // /status (anonymous, polled by clients) carries the same block as /statistics,
            // so learning the provider state never requires the budgeted graph-shape pass.
            using (var factory = new ProviderFactory(enabled: false))
            using (var client = factory.CreateClient())
            {
                using var response = await client.GetAsync("/status");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var embedding = (await ReadJson(response)).GetProperty("embedding");
                Assert.IsFalse(embedding.GetProperty("enabled").GetBoolean());
                Assert.IsFalse(embedding.GetProperty("loaded").GetBoolean());
            }

            using (var factory = new ProviderFactory(enabled: true))
            using (var client = factory.CreateClient())
            {
                using var response = await client.GetAsync("/status");
                var embedding = (await ReadJson(response)).GetProperty("embedding");
                Assert.IsTrue(embedding.GetProperty("enabled").GetBoolean());
                Assert.AreEqual("fake-model", embedding.GetProperty("modelName").GetString());
                Assert.AreEqual(Dim, embedding.GetProperty("dimension").GetInt32());
                Assert.AreEqual("Cosine", embedding.GetProperty("intendedMetric").GetString());
                Assert.IsFalse(embedding.GetProperty("loaded").GetBoolean(),
                    "/status must never trigger the lazy load");
            }
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
            var v = TestVertices.Create(engine, 4, "n");
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
        public async Task Wrapper_BackendOwnTransportTimeout_IsUnavailable_NotAnEscapingCancellation()
        {
            // The embedding twin of the chat gateway's 100s-transport defect. The Ollama generator was
            // built by the OllamaApiClient(Uri, model) ctor, whose HttpClient carries the .NET default
            // 100s timeout, and the provider had NO budget of its own at all. The
            // TaskCanceledException it raises does not observe the caller token, so the sole filter
            // here (`when (!(ex is OperationCanceledException))`) missed it: it escaped as an
            // unhandled 500, and on the ingestion path it skipped the failure cleanup and left the
            // Document stuck at "processing" for the life of the process.
            var options = new Fallen8EmbeddingOptions { Enabled = true, ModelName = "m", Dimension = 2 };
            var provider = Provider(options, new TransportTimeoutGenerator());

            var ex = await Assert.ThrowsExceptionAsync<EmbeddingProviderUnavailableException>(
                () => provider.EmbedAsync(new[] { "x" }, default));
            StringAssert.Contains(ex.Message, "Fallen8:Embedding:TimeoutSeconds",
                "a backend timeout must name the budget that governs it");
            Assert.IsTrue(provider.IsLoaded,
                "a slow batch says nothing about model identity, so it must not latch (IsLoaded "
                + "goes false only once a fatal failure is latched)");
        }

        [TestMethod]
        public async Task Wrapper_OwnBudget_ExpiresAsUnavailable()
        {
            // Fallen8:Embedding:TimeoutSeconds is the single deadline and it actually governs: a
            // generator that honours the token but never finishes is cut off by our budget.
            var options = new Fallen8EmbeddingOptions
            {
                Enabled = true, ModelName = "m", Dimension = 2, TimeoutSeconds = 1
            };
            var provider = Provider(options, new NeverFinishingGenerator());

            var ex = await Assert.ThrowsExceptionAsync<EmbeddingProviderUnavailableException>(
                () => provider.EmbedAsync(new[] { "x" }, default));
            StringAssert.Contains(ex.Message, "Fallen8:Embedding:TimeoutSeconds");
        }

        [TestMethod]
        public async Task Wrapper_CallerCancellation_PropagatesInsteadOfBecomingAFault()
        {
            // The caller half: a client that goes away propagates rather than being reported as a
            // backend fault, which is why the timeout filter keys off the caller's token.
            var options = new Fallen8EmbeddingOptions { Enabled = true, ModelName = "m", Dimension = 2 };
            using var callerCts = new CancellationTokenSource();
            var provider = Provider(options, new NeverFinishingGenerator(callerCts));

            Exception caught = null;
            try
            {
                await provider.EmbedAsync(new[] { "x" }, callerCts.Token);
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            Assert.IsInstanceOfType(caught, typeof(OperationCanceledException));
            Assert.IsNotInstanceOfType(caught, typeof(EmbeddingProviderUnavailableException));
        }

        /// <summary>Raises the exception shape HttpClient uses for its OWN timeout: a
        /// TaskCanceledException with an inner TimeoutException, on a token nobody cancelled.</summary>
        private sealed class TransportTimeoutGenerator : IEmbeddingGenerator<string, Embedding<float>>
        {
            public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
                EmbeddingGenerationOptions options = null, CancellationToken cancellationToken = default)
            {
                return Task.FromException<GeneratedEmbeddings<Embedding<float>>>(
                    new TaskCanceledException(
                        "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.",
                        new TimeoutException()));
            }

            public object GetService(Type serviceType, object serviceKey = null) => null;

            public void Dispose()
            {
            }
        }

        /// <summary>Honours the token but never completes, so the provider's own budget is what ends
        /// the call. Optionally cancels a caller source first, to exercise the caller half.</summary>
        private sealed class NeverFinishingGenerator : IEmbeddingGenerator<string, Embedding<float>>
        {
            private readonly CancellationTokenSource _cancelCallerFirst;

            internal NeverFinishingGenerator(CancellationTokenSource cancelCallerFirst = null)
            {
                _cancelCallerFirst = cancelCallerFirst;
            }

            public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
                EmbeddingGenerationOptions options = null, CancellationToken cancellationToken = default)
            {
                _cancelCallerFirst?.Cancel();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }

            public object GetService(Type serviceType, object serviceKey = null) => null;

            public void Dispose()
            {
            }
        }

        [TestMethod]
        public async Task Wrapper_OllamaProtocolBackends_AskTheBackendNotToTruncate()
        {
            // The flag defaults to TRUE on both, and a truncated input comes back as a valid-looking
            // vector that describes only its head - so the request carrying it is the whole mechanism,
            // and nothing else in the pipeline can notice it went missing.
            foreach (var backend in new[] { "Ollama", "Nahil" })
            {
                var fake = new FakeEmbeddingGenerator(2);
                await Provider(EmbeddingOptions(backend), fake).EmbedAsync(new[] { "x" }, default);

                Assert.AreEqual(1, fake.Calls, backend);
                Assert.IsNotNull(fake.LastOptions, backend + ": per-call options must reach the generator");
                Assert.IsTrue(fake.LastOptions.AdditionalProperties.ContainsKey("truncate"),
                    backend + ": the option is carried by NAME, which is what OllamaSharp's mapper binds on");
                Assert.AreEqual((object)false, fake.LastOptions.AdditionalProperties["truncate"],
                    backend + ": a real boolean false, since that is what reaches the request body");
            }

            // Every other backend gets nothing, for two different reasons: the in-process ones read
            // none of this at all, and OpenAI has no key of that name to set - its route carries no
            // truncation knob and refuses an over-long input instead. Sending an option a backend
            // ignores would only suggest the flag governs it too.
            foreach (var backend in new[] { "Onnx", "LLamaSharp", "OpenAI" })
            {
                var fake = new FakeEmbeddingGenerator(2);
                await Provider(EmbeddingOptions(backend), fake).EmbedAsync(new[] { "x" }, default);

                Assert.AreEqual(1, fake.Calls, backend);
                Assert.IsNull(fake.LastOptions, backend + " does not read the truncate flag");
            }
        }

        private static Fallen8EmbeddingOptions EmbeddingOptions(string backend)
        {
            return new Fallen8EmbeddingOptions
            {
                Enabled = true, ModelName = "m", Dimension = 2, Backend = backend
            };
        }

        [TestMethod]
        public async Task Wrapper_AnOverLongRefusal_NamesWhatToChange_AndEveryOtherFailureDoesNot()
        {
            // The backend's sentence as it really arrives: unpunctuated, so the hint has to supply the
            // separator, or the two run together into one unreadable line.
            var refusing = new FakeEmbeddingGenerator(2)
            {
                RefuseFromCall = 1, RefusalMessage = "input length exceeds the context length"
            };
            var hinted = await Assert.ThrowsExceptionAsync<EmbeddingProviderUnavailableException>(
                () => Provider(EmbeddingOptions("Ollama"), refusing).EmbedAsync(new[] { "x" }, default));
            StringAssert.Contains(hinted.Message, "input length exceeds the context length",
                "the backend's own reason is never replaced by the hint");
            StringAssert.Contains(hinted.Message, "lower Fallen8:Ingestion:ChunkMaxChars",
                "a refusal Fallen-8 asked for must name the Fallen-8 setting that produced the input");
            StringAssert.Contains(hinted.Message, "length. One input exceeds");

            // Same sentence, the backend's casing changed: the wording is not ours to depend on exactly.
            var shouty = new FakeEmbeddingGenerator(2)
            {
                RefuseFromCall = 1, RefusalMessage = "The input EXCEEDS THE CONTEXT LENGTH."
            };
            var shoutyHinted = await Assert.ThrowsExceptionAsync<EmbeddingProviderUnavailableException>(
                () => Provider(EmbeddingOptions("Ollama"), shouty).EmbedAsync(new[] { "x" }, default));
            StringAssert.Contains(shoutyHinted.Message, "One input exceeds the model's per-input token ceiling");
            Assert.IsFalse(shoutyHinted.Message.Contains(".."),
                "an already-punctuated reason must not collect a second full stop");

            // Any other failure carries no hint: pointing at a length setting for an unrelated outage
            // sends the operator to fix the one thing that is not wrong.
            var down = new FakeEmbeddingGenerator(2) { RefuseFromCall = 1 };
            var plain = await Assert.ThrowsExceptionAsync<EmbeddingProviderUnavailableException>(
                () => Provider(EmbeddingOptions("Ollama"), down).EmbedAsync(new[] { "x" }, default));
            StringAssert.Contains(plain.Message, "the hourly token budget for this key is spent");
            Assert.IsFalse(plain.Message.Contains("ChunkMaxChars"),
                "the hint belongs to the over-long refusal alone");
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

        #region backend switch (FR-4: swap = config change)

        /// <summary>Reflection into the internal factory (the repo declares no
        /// InternalsVisibleTo); unwraps the reflection TargetInvocationException.</summary>
        private static object CreateBackend(Fallen8EmbeddingOptions options)
        {
            var factory = typeof(Fallen8EmbeddingProvider).Assembly
                .GetType("NoSQL.GraphDB.App.Embedding.EmbeddingBackendFactory");
            var create = factory.GetMethod("Create",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            try
            {
                // The second argument is the ILoggerFactory the Ollama/Nahil branch carries into the
                // transport for Nahil's per-retry lines; null is a valid "log nothing".
                return create.Invoke(null, new object[] { options, null });
            }
            catch (System.Reflection.TargetInvocationException ex)
            {
                throw ex.InnerException;
            }
        }

        [TestMethod]
        public void BackendFactory_MapsEveryConfigValue()
        {
            // Ollama constructs eagerly (an HTTP client, no model memory).
            var options = new Fallen8EmbeddingOptions { Backend = "Ollama" };
            options.Ollama.Endpoint = "http://localhost:11434";
            using var ollama = (IDisposable)CreateBackend(options);
            Assert.IsInstanceOfType(ollama, typeof(OllamaSharp.OllamaApiClient));

            // The in-process backends refuse a missing model path loudly - nothing downloads.
            Assert.ThrowsException<System.IO.FileNotFoundException>(() =>
                CreateBackend(new Fallen8EmbeddingOptions { Backend = "Onnx" }));
            Assert.ThrowsException<System.IO.FileNotFoundException>(() =>
                CreateBackend(new Fallen8EmbeddingOptions { Backend = "LLamaSharp" }));

            // Nahil constructs eagerly too, and needs the whole triple: it is the same protocol
            // over an authenticated remote (feature nahil-backend).
            var nahil = new Fallen8EmbeddingOptions { Backend = "Nahil" };
            nahil.Nahil.Endpoint = "https://models.example";
            nahil.Nahil.Model = "bge-m3:latest";
            nahil.Nahil.ApiKey = "k";
            using var remote = (IDisposable)CreateBackend(nahil);
            Assert.IsInstanceOfType(remote, typeof(OllamaSharp.OllamaApiClient));

            // OpenAI constructs eagerly too, and needs the same triple - but it is a different
            // protocol AND a different embedding function, so it is its own generator rather than
            // another OllamaApiClient.
            var openAi = new Fallen8EmbeddingOptions { Backend = "OpenAI", Dimension = 1536 };
            openAi.OpenAI.Model = "text-embedding-3-small";
            openAi.OpenAI.ApiKey = "k";
            using var openAiGenerator = (IDisposable)CreateBackend(openAi);
            Assert.IsInstanceOfType(openAiGenerator, typeof(IEmbeddingGenerator<string, Embedding<float>>));
            Assert.IsNotInstanceOfType(openAiGenerator, typeof(OllamaSharp.OllamaApiClient));

            // And its triple is validated with the key named, like Nahil's. The endpoint has a
            // default, so a bare selector still needs the model and the credential.
            var incomplete = new Fallen8EmbeddingOptions { Backend = "OpenAI" };
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(
                () => CreateBackend(incomplete)).Message, "Fallen8:Embedding:OpenAI:Model");
            incomplete.OpenAI.Model = "text-embedding-3-small";
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(
                () => CreateBackend(incomplete)).Message, "Fallen8:Embedding:OpenAI:ApiKey");

            Assert.ThrowsException<InvalidOperationException>(() =>
                CreateBackend(new Fallen8EmbeddingOptions { Backend = "Nope" }));
        }

        /// <summary>
        ///   The startup warning and the surface's <c>503</c> read ONE method, so an operator is
        ///   never told two different things about one deployment. Asserted as an equality against
        ///   the thrown message rather than by re-listing the sentences: a second copy of them here
        ///   would be the very duplication the single home exists to prevent.
        ///
        ///   <para>Covers the case that had no boot warning at all before: a selected but
        ///   incompletely configured OpenAI embedding backend. The shipped docs promise the reason is
        ///   logged once at startup, and <c>ResolveConnection</c> answers <c>null</c> for OpenAI (it
        ///   speaks no Ollama protocol), so inferring the problem from that null found nothing to
        ///   warn about.</para>
        ///
        ///   <para>And it pins the deliberate silences: <c>Onnx</c> and <c>LLamaSharp</c> validate
        ///   clean because whether the operator's model FILE is present is their constructor's
        ///   answer, not something a selector check can know. Warning about them at boot would fire
        ///   on every working in-process deployment.</para>
        /// </summary>
        [TestMethod]
        public void BackendFactory_ValidateIsTheOneHome_SoTheBootWarningAndThe503Agree()
        {
            var unusable = new[]
            {
                new Fallen8EmbeddingOptions { Backend = "Anthropic" },
                new Fallen8EmbeddingOptions { Backend = "Nope" },
                new Fallen8EmbeddingOptions { Backend = "openai" },
                // Selected, and missing each of the three things it needs in turn.
                OpenAI(null, "text-embedding-3-small", "sk-key"),
                OpenAI("https://api.openai.com", null, "sk-key"),
                OpenAI("https://api.openai.com", "text-embedding-3-small", null),
                OpenAI("https://api.openai.com", "text-embedding-3-small", "   "),
                // The Ollama-protocol arm, so the shared branch is covered by the same claim.
                Nahil("https://models.example", "bge-m3:latest", null)
            };

            foreach (var options in unusable)
            {
                var problem = Validate(options);
                Assert.IsNotNull(problem, "an unusable backend must be refusable before it is built: "
                    + options.Backend);

                var thrown = Assert.ThrowsException<InvalidOperationException>(() => CreateBackend(options));
                Assert.AreEqual(thrown.Message, problem,
                    "the startup line and the 503 must be the same sentence, or they can drift");
            }

            foreach (var backend in new[] { "Onnx", "LLamaSharp" })
            {
                Assert.IsNull(Validate(new Fallen8EmbeddingOptions { Backend = backend }),
                    backend + " runs in-process on operator files, so a missing file is its "
                    + "constructor's answer and not a name this check can refuse");
            }

            Assert.IsNull(Validate(Nahil("https://models.example", "bge-m3:latest", "k")),
                "a fully configured remote backend has nothing to warn about at boot");
        }

        /// <summary>Whether the configured embedding backend is refusable, through the real factory.</summary>
        private static String Validate(Fallen8EmbeddingOptions options)
        {
            var factory = typeof(Fallen8EmbeddingProvider).Assembly
                .GetType("NoSQL.GraphDB.App.Embedding.EmbeddingBackendFactory");
            var validate = factory.GetMethod("Validate",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(validate, "Validate is the one home the boot warning and the 503 share");
            return (String)validate.Invoke(null, new object[] { options });
        }

        private static Fallen8EmbeddingOptions OpenAI(String endpoint, String model, String apiKey)
        {
            return new Fallen8EmbeddingOptions
            {
                Backend = "OpenAI",
                OpenAI = new Fallen8EmbeddingOptions.OpenAIOptions
                {
                    Endpoint = endpoint,
                    Model = model,
                    ApiKey = apiKey
                }
            };
        }

        private static Fallen8EmbeddingOptions Nahil(String endpoint, String model, String apiKey)
        {
            var options = new Fallen8EmbeddingOptions { Backend = "Nahil" };
            options.Nahil.Endpoint = endpoint;
            options.Nahil.Model = model;
            options.Nahil.ApiKey = apiKey;
            return options;
        }

        /// <summary>
        ///   Anthropic is a name an operator can reasonably have expected to work - it IS a
        ///   configurable chat backend - so it gets its own sentence rather than the typo's. The
        ///   distinction is the operator's next move: pick another embedding backend, versus fix a
        ///   spelling. And the sentence has to say the two capabilities are configured
        ///   independently, or it reads as "Anthropic is not usable on this instance at all".
        /// </summary>
        [TestMethod]
        public void BackendFactory_RefusesAnthropic_WithItsOwnReason_NotAnUnknownName()
        {
            var refused = Assert.ThrowsException<InvalidOperationException>(
                () => CreateBackend(new Fallen8EmbeddingOptions { Backend = "Anthropic" }));

            StringAssert.Contains(refused.Message, "Fallen8:Embedding:Backend",
                "a refusal names the key an operator has to change");
            StringAssert.Contains(refused.Message, "Anthropic");
            StringAssert.Contains(refused.Message, "no embeddings API");
            StringAssert.Contains(refused.Message, "chat may stay on Anthropic");
            StringAssert.Contains(refused.Message, "OpenAI", "and lists what it can be instead");

            var typo = Assert.ThrowsException<InvalidOperationException>(
                () => CreateBackend(new Fallen8EmbeddingOptions { Backend = "Anthropik" }));
            StringAssert.Contains(typo.Message, "is not a supported embedding backend");
            Assert.AreNotEqual(refused.Message, typo.Message);
            Assert.IsFalse(typo.Message.Contains("no embeddings API", StringComparison.Ordinal),
                "a typo is not told a fact about a provider it did not name");

            // Matching is ordinal, so the casing the catalog publishes is the only spelling that
            // works. A backend that half-matched would latch a 503 nobody could explain.
            foreach (var wrongCase in new[] { "openai", "OpenAi", "OPENAI" })
            {
                var rejected = Assert.ThrowsException<InvalidOperationException>(
                    () => CreateBackend(new Fallen8EmbeddingOptions { Backend = wrongCase }));
                StringAssert.Contains(rejected.Message, "is not a supported embedding backend", wrongCase);
            }
        }

        /// <summary>
        ///   Nahil is refused, with the reason, for every way its configuration can be
        ///   unusable - rather than constructing a client that would 401 or dial the wrong URL. The
        ///   throw is what the provider's Lazy latches into a permanent 503, which is how the
        ///   operator sees the reason at all.
        /// </summary>
        [TestMethod]
        public void BackendFactory_RefusesAnUnusableNahil_NamingTheKeyToFix()
        {
            // A path prefix is REFUSED, never rewritten: HttpClient.BaseAddress would drop it and
            // every call would silently go to the wrong URL.
            var path = Assert.ThrowsException<InvalidOperationException>(
                () => CreateBackend(Nahil("https://models.example/v1", "bge-m3:latest", "k")));
            StringAssert.Contains(path.Message, "Fallen8:Embedding:Nahil:Endpoint");
            StringAssert.Contains(path.Message, "host root");

            foreach (var (options, expected) in new[]
            {
                (Nahil(null, "bge-m3:latest", "k"), "Fallen8:Embedding:Nahil:Endpoint"),
                (Nahil("not-a-url", "bge-m3:latest", "k"), "Fallen8:Embedding:Nahil:Endpoint"),
                (Nahil("https://models.example/?a=b", "bge-m3:latest", "k"), "Fallen8:Embedding:Nahil:Endpoint"),
                (Nahil("https://models.example", null, "k"), "Fallen8:Embedding:Nahil:Model"),
                (Nahil("https://models.example", "bge-m3:latest", " "), "Fallen8:Embedding:Nahil:ApiKey")
            })
            {
                var refused = Assert.ThrowsException<InvalidOperationException>(() => CreateBackend(options));
                StringAssert.Contains(refused.Message, expected, refused.Message);
            }

            // The SIDECAR is held to the same endpoint contract, but never asked for a credential:
            // real Ollama authenticates nothing, so requiring one would break every local setup.
            var sidecar = new Fallen8EmbeddingOptions { Backend = "Ollama" };
            sidecar.Ollama.Endpoint = "http://localhost:11434/ollama";
            var prefixed = Assert.ThrowsException<InvalidOperationException>(() => CreateBackend(sidecar));
            StringAssert.Contains(prefixed.Message, "Fallen8:Embedding:Ollama:Endpoint");
        }

        /// <summary>No model paths configured - and no fake: the REAL backend factory runs.</summary>
        private sealed class RealBackendFactory : VolatileAppFactory
        {
            public RealBackendFactory(string backend)
                : base(new Dictionary<string, string>
                {
                    ["Fallen8:Embedding:Enabled"] = "true",
                    ["Fallen8:Embedding:Backend"] = backend,
                    ["Fallen8:Embedding:ModelName"] = "m",
                    ["Fallen8:Embedding:Dimension"] = "2"
                })
            {
            }
        }

        [TestMethod]
        public async Task Boot_PerInProcessBackend_MissingModel_IsALatched503_NeverAStartupFailure()
        {
            // Boots the real Onnx and LLamaSharp configurations (FR-4): the app starts (lazy
            // load), statistics reports the backend without loading, and first use answers a
            // latched 503 naming the initialization failure.
            foreach (var backend in new[] { "Onnx", "LLamaSharp" })
            {
                using var factory = new RealBackendFactory(backend);
                using var client = factory.CreateClient();

                using var statistics = await client.GetAsync("/statistics");
                Assert.AreEqual(HttpStatusCode.OK, statistics.StatusCode);
                var embedding = (await ReadJson(statistics)).GetProperty("embedding");
                Assert.AreEqual(backend, embedding.GetProperty("backend").GetString());
                Assert.IsFalse(embedding.GetProperty("loaded").GetBoolean());

                using var response = await client.PostAsync("/embedding/text", Json("{ \"texts\": [\"x\"] }"));
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, backend);
                StringAssert.Contains(await response.Content.ReadAsStringAsync(), "failed to initialize");
            }
        }

        [TestMethod]
        public async Task Wrapper_UnknownIntendedMetric_LatchesInsteadOfGuessingCosine()
        {
            // A typo'd metric must not silently become Cosine inside the FR-8 identity stamp.
            var options = new Fallen8EmbeddingOptions
            {
                Enabled = true,
                ModelName = "m",
                Dimension = 2,
                IntendedMetric = "Cosin"
            };
            var provider = Provider(options, new FakeEmbeddingGenerator(2));

            var ex = await Assert.ThrowsExceptionAsync<EmbeddingProviderUnavailableException>(
                () => provider.EmbedAsync(new[] { "x" }, default));
            StringAssert.Contains(ex.Message, "IntendedMetric");
        }

        #endregion

        #region durability of stamps

        [TestMethod]
        public void ModelStamp_RoundTripsThroughTheWal()
        {
            using var temp = new TempDirectory("f8_stampwal_");
            var walPath = System.IO.Path.Combine(temp.FullName, "stamp.wal");

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

        #endregion
    }
}
