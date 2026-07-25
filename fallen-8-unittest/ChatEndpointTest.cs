// MIT License
//
// ChatEndpointTest.cs
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
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;
using NoSQL.GraphDB.App.Chat;
using NoSQL.GraphDB.App.Configuration;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The chat gateway endpoint (feature instance-config, POST /chat): the capability gate
    ///   (403 off), server-owned model, the fault table (400/502/503/504), the forwarded
    ///   generation stats, and the best-effort GPU probe. The backend is a deterministic fake
    ///   (the same seam the embedding tests use for their generator), so no real model is loaded.
    /// </summary>
    [TestClass]
    public class ChatEndpointTest
    {
        private sealed class FakeChatBackend : IChatBackend
        {
            private readonly Func<IReadOnlyList<ChatTurn>, ChatBackendOptions, CancellationToken, Task<ChatBackendResult>> _chat;
            private readonly Boolean? _gpu;

            public FakeChatBackend(
                Func<IReadOnlyList<ChatTurn>, ChatBackendOptions, CancellationToken, Task<ChatBackendResult>> chat,
                Boolean? gpu = null)
            {
                _chat = chat;
                _gpu = gpu;
            }

            public Task<ChatBackendResult> ChatAsync(IReadOnlyList<ChatTurn> messages, ChatBackendOptions options,
                CancellationToken cancellationToken) => _chat(messages, options, cancellationToken);

            public Task<Boolean?> TryDetectGpuAsync(CancellationToken cancellationToken) => Task.FromResult(_gpu);
        }

        private const String ApiKey = "chat-test-key";

        private sealed class ChatFactory : WebApplicationFactory<Program>
        {
            private readonly Boolean _enabled;
            private readonly IChatBackend _backend;
            private readonly Int32 _timeoutSeconds;
            private readonly Boolean _withApiKey;

            public ChatFactory(Boolean enabled, IChatBackend backend = null, Int32 timeoutSeconds = 120,
                Boolean withApiKey = false)
            {
                _enabled = enabled;
                _backend = backend;
                _timeoutSeconds = timeoutSeconds;
                _withApiKey = withApiKey;
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
                builder.UseSetting("Fallen8:Chat:Enabled", _enabled ? "true" : "false");
                builder.UseSetting("Fallen8:Chat:Backend", "Ollama"); // never constructed: the fake replaces it
                builder.UseSetting("Fallen8:Chat:Ollama:Model", "fake-model");
                builder.UseSetting("Fallen8:Chat:TimeoutSeconds", _timeoutSeconds.ToString());
                if (_withApiKey)
                {
                    builder.UseSetting("Fallen8:Security:ApiKey", ApiKey);
                }
                if (_backend != null)
                {
                    builder.ConfigureTestServices(services => services.AddSingleton<IChatBackend>(_backend));
                }
            }
        }

        private static StringContent Json(String json) => new StringContent(json, Encoding.UTF8, "application/json");

        private static FakeChatBackend Returns(String content, Int64 promptTokens = 3, Int64 completionTokens = 7,
            Boolean? gpu = null)
        {
            return new FakeChatBackend((_, _, _) => Task.FromResult(new ChatBackendResult
            {
                Content = content,
                Model = "fake-model",
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                DurationMs = 123.0,
                TokensPerSecond = 42.0
            }), gpu);
        }

        [TestMethod]
        public async Task Chat_Disabled_403()
        {
            // The capability answers 403 to an AUTHENTICATED caller (the api-security-boundary
            // posture; unauthenticated on a keyed server is 401 like everywhere else).
            using var factory = new ChatFactory(enabled: false, withApiKey: true);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);

            using var response = await client.PostAsync("/chat", Json("{ \"messages\": [ { \"role\": \"user\", \"content\": \"hi\" } ] }"));
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        [TestMethod]
        public async Task Chat_HappyPath_ReturnsContentAndStats()
        {
            using var factory = new ChatFactory(enabled: true, backend: Returns("hello from the model"));
            using var client = factory.CreateClient();

            using var response = await client.PostAsync("/chat",
                Json("{ \"messages\": [ { \"role\": \"user\", \"content\": \"draft a filter\" } ], \"options\": { \"temperature\": 0.1 } }"));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.AreEqual("hello from the model", body.GetProperty("content").GetString());
            Assert.AreEqual("fake-model", body.GetProperty("model").GetString());
            var stats = body.GetProperty("stats");
            Assert.AreEqual(3, stats.GetProperty("promptTokens").GetInt64());
            Assert.AreEqual(7, stats.GetProperty("completionTokens").GetInt64());
            Assert.AreEqual(123.0, stats.GetProperty("durationMs").GetDouble(), 0.001);
            Assert.AreEqual(42.0, stats.GetProperty("tokensPerSecond").GetDouble(), 0.001);
        }

        [TestMethod]
        public async Task Chat_EmptyMessages_400()
        {
            using var factory = new ChatFactory(enabled: true, backend: Returns("unused"));
            using var client = factory.CreateClient();

            using var empty = await client.PostAsync("/chat", Json("{ \"messages\": [] }"));
            Assert.AreEqual(HttpStatusCode.BadRequest, empty.StatusCode);

            using var missingContent = await client.PostAsync("/chat", Json("{ \"messages\": [ { \"role\": \"user\", \"content\": \"\" } ] }"));
            Assert.AreEqual(HttpStatusCode.BadRequest, missingContent.StatusCode);
        }

        [TestMethod]
        public async Task Chat_BackendDown_503()
        {
            var backend = new FakeChatBackend((_, _, _) => throw new InvalidOperationException("sidecar down"));
            using var factory = new ChatFactory(enabled: true, backend: backend);
            using var client = factory.CreateClient();

            using var response = await client.PostAsync("/chat", Json("{ \"messages\": [ { \"role\": \"user\", \"content\": \"hi\" } ] }"));
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        [TestMethod]
        public async Task Chat_EmptyOutput_502()
        {
            using var factory = new ChatFactory(enabled: true, backend: Returns(""));
            using var client = factory.CreateClient();

            using var response = await client.PostAsync("/chat", Json("{ \"messages\": [ { \"role\": \"user\", \"content\": \"hi\" } ] }"));
            Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
        }

        [TestMethod]
        public async Task Chat_Timeout_504()
        {
            // The backend honours the cancellation token; the provider's 1s timeout fires first.
            var backend = new FakeChatBackend(async (_, _, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return new ChatBackendResult { Content = "late", Model = "fake-model" };
            });
            using var factory = new ChatFactory(enabled: true, backend: backend, timeoutSeconds: 1);
            using var client = factory.CreateClient();

            using var response = await client.PostAsync("/chat", Json("{ \"messages\": [ { \"role\": \"user\", \"content\": \"hi\" } ] }"));
            Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
        }

        [TestMethod]
        public async Task Provider_Gpu_NullWhenDisabled_PassesThroughWhenEnabled()
        {
            var disabled = new Fallen8ChatProvider(
                Options.Create(new Fallen8ChatOptions { Enabled = false }),
                new Lazy<IChatBackend>(() => Returns("x", gpu: true)));
            Assert.IsNull(await disabled.TryDetectGpuAsync(CancellationToken.None),
                "a disabled provider never probes the backend");

            var enabled = new Fallen8ChatProvider(
                Options.Create(new Fallen8ChatOptions { Enabled = true }),
                new Lazy<IChatBackend>(() => Returns("x", gpu: true)));
            Assert.AreEqual(true, await enabled.TryDetectGpuAsync(CancellationToken.None));

            var unknown = new Fallen8ChatProvider(
                Options.Create(new Fallen8ChatOptions { Enabled = true }),
                new Lazy<IChatBackend>(() => Returns("x", gpu: null)));
            Assert.IsNull(await unknown.TryDetectGpuAsync(CancellationToken.None),
                "an undeterminable GPU state stays null (the UI shows nothing)");
        }
    }
}
