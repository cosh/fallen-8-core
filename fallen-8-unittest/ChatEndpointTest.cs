// MIT License
//
// ChatEndpointTest.cs
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
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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

            public FakeChatBackend(
                Func<IReadOnlyList<ChatTurn>, ChatBackendOptions, CancellationToken, Task<ChatBackendResult>> chat)
            {
                _chat = chat;
            }

            public Task<ChatBackendResult> ChatAsync(IReadOnlyList<ChatTurn> messages, ChatBackendOptions options,
                CancellationToken cancellationToken) => _chat(messages, options, cancellationToken);
        }

        private const String ApiKey = "chat-test-key";

        private sealed class ChatFactory : VolatileAppFactory
        {
            private readonly Boolean _enabled;
            private readonly IChatBackend _backend;
            private readonly Int32 _timeoutSeconds;
            private readonly Boolean _withApiKey;
            private readonly String _otlpEndpoint;

            public ChatFactory(Boolean enabled, IChatBackend backend = null, Int32 timeoutSeconds = 120,
                Boolean withApiKey = false, String otlpEndpoint = null)
            {
                _enabled = enabled;
                _backend = backend;
                _timeoutSeconds = timeoutSeconds;
                _withApiKey = withApiKey;
                _otlpEndpoint = otlpEndpoint;
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                base.ConfigureWebHost(builder);
                builder.UseSetting("Fallen8:Chat:Enabled", _enabled ? "true" : "false");
                builder.UseSetting("Fallen8:Chat:Backend", "Ollama"); // never constructed: the fake replaces it
                builder.UseSetting("Fallen8:Chat:Ollama:Model", "fake-model");
                builder.UseSetting("Fallen8:Chat:TimeoutSeconds", _timeoutSeconds.ToString());
                if (_withApiKey)
                {
                    builder.UseSetting("Fallen8:Security:ApiKey", ApiKey);
                }
                if (_otlpEndpoint != null)
                {
                    builder.UseSetting("Fallen8:Observability:Otlp:Endpoint", _otlpEndpoint);
                }
                if (_backend != null)
                {
                    builder.ConfigureTestServices(services => services.AddSingleton<IChatBackend>(_backend));
                }
            }
        }

        private const String RemoteKey = "openai-test-key";

        private const String OtherRemoteKey = "anthropic-test-key";

        /// <summary>
        ///   A deployment whose chat selector names a backend with no Ollama-protocol target
        ///   (feature model-providers). Nothing is ever constructed: the tests using it only read
        ///   the reported state, which is exactly where the bug was.
        /// </summary>
        private sealed class RemoteBackendFactory : VolatileAppFactory
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                base.ConfigureWebHost(builder);
                builder.UseSetting("Fallen8:Chat:Enabled", "true");
                builder.UseSetting("Fallen8:Chat:Backend", "OpenAI");
                builder.UseSetting("Fallen8:Chat:OpenAI:ApiKey", RemoteKey);
                builder.UseSetting("Fallen8:Chat:OpenAI:Model", "gpt-4o-mini");
                // Both left set on purpose: an unselected provider's credential is configured on
                // plenty of real deployments, and an earlier selector's model must not be what gets
                // reported once the selector moved.
                builder.UseSetting("Fallen8:Chat:Anthropic:ApiKey", OtherRemoteKey);
                builder.UseSetting("Fallen8:Chat:Ollama:Model", "fake-model");
            }
        }

        private static StringContent Json(String json) => new StringContent(json, Encoding.UTF8, "application/json");

        private static FakeChatBackend Returns(String content, Int64 promptTokens = 3, Int64 completionTokens = 7)
        {
            return new FakeChatBackend((_, _, _) => Task.FromResult(new ChatBackendResult
            {
                Content = content,
                Model = "fake-model",
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                DurationMs = 123.0,
                TokensPerSecond = 42.0
            }));
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
            // The fake REPLACES the backend while the selector still says Ollama, so this value can
            // only have come from the provider and not from the IChatBackend that answered.
            Assert.AreEqual("Ollama", body.GetProperty("backend").GetString());
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
            // OUR budget fires: the backend honours the cancellation token and the provider's 1s
            // timeout wins. This covers only the token-honouring half; a backend whose OWN
            // transport deadline fires is a different shape, pinned by the sibling test below.
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
        public async Task Chat_BackendOwnTransportTimeout_Is504_NotAnUnhandled500()
        {
            // THE REGRESSION THIS PINS. OllamaSharp's OllamaApiClient(Uri, model) ctor built its own
            // HttpClient and left Timeout at the .NET default of 100s - shorter than the shipped
            // Fallen8:Chat:TimeoutSeconds of 120, and not configurable. It therefore always fired
            // first, and the TaskCanceledException it raises does NOT observe our token: the old
            // filters (one requiring the provider's own CTS to have fired, one excluding
            // OperationCanceledException) both missed it, so it escaped the provider AND the
            // controller and became an unhandled 500. The fake reproduces exactly that shape - the
            // transport's own-timeout exception, raised while the 120s proxy budget is nowhere near
            // expiry and the caller is still connected.
            var backend = new FakeChatBackend((_, _, _) => Task.FromException<ChatBackendResult>(
                new TaskCanceledException(
                    "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.",
                    new TimeoutException())));
            using var factory = new ChatFactory(enabled: true, backend: backend, timeoutSeconds: 120);
            using var client = factory.CreateClient();

            using var response = await client.PostAsync("/chat", Json("{ \"messages\": [ { \"role\": \"user\", \"content\": \"hi\" } ] }"));
            Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
        }

        [TestMethod]
        public async Task ChatProvider_CallerCancellation_PropagatesInsteadOfBecomingATimeout()
        {
            // The other half of the widened filter: it keys off the CALLER's token, so a client that
            // goes away still propagates instead of being reported as a backend timeout. Asserted at
            // the provider (a disconnect has no status code to observe over HTTP).
            using var callerCts = new CancellationTokenSource();
            var backend = new FakeChatBackend((_, _, ct) =>
            {
                callerCts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new ChatBackendResult { Content = "unreachable", Model = "fake-model" });
            });
            var provider = new Fallen8ChatProvider(
                Options.Create(new Fallen8ChatOptions { Enabled = true, TimeoutSeconds = 120 }),
                new Lazy<IChatBackend>(() => backend));

            Exception caught = null;
            try
            {
                await provider.ChatAsync(new[] { new ChatTurn("user", "hi") }, null, callerCts.Token);
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            Assert.IsInstanceOfType(caught, typeof(OperationCanceledException),
                "a caller cancellation must propagate, not be remapped to a 504 timeout fault");
            Assert.IsNotInstanceOfType(caught, typeof(ChatProviderTimeoutException));
        }

        [TestMethod]
        public async Task Status_IncludesChatCapabilityBlock_NoResidencyProbe()
        {
            using var factory = new ChatFactory(enabled: true, backend: Returns("x"));
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/status");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            var chat = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("chat");
            Assert.IsTrue(chat.GetProperty("enabled").GetBoolean());
            Assert.AreEqual("Ollama", chat.GetProperty("backend").GetString());
            Assert.AreEqual("fake-model", chat.GetProperty("model").GetString());
            Assert.IsFalse(chat.GetProperty("loaded").GetBoolean(), "no chat call happened, so nothing is loaded");
        }

        [TestMethod]
        public async Task Status_BackendWithoutAnOllamaTarget_ReportsItsOwnModel_AndNoResidency()
        {
            // THE REGRESSION THIS PINS (feature model-providers, FR-5.2). The reported model used to
            // be read off the residency PROBE target, which answers the narrower question of which
            // Ollama-protocol endpoint can be dialled - null for a backend that speaks its own
            // protocol. A perfectly working deployment therefore reported model: null.
            using var factory = new RemoteBackendFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/status");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

            var chat = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("chat");
            Assert.AreEqual("OpenAI", chat.GetProperty("backend").GetString());
            Assert.AreEqual("gpt-4o-mini", chat.GetProperty("model").GetString(),
                "the model comes from the SELECTED backend's own options");
            Assert.AreEqual(JsonValueKind.Null, chat.GetProperty("resident").ValueKind,
                "there is no residency API to probe, so residency stays unknown rather than guessed");
            Assert.AreEqual(JsonValueKind.Null, chat.GetProperty("gpu").ValueKind);
        }

        [TestMethod]
        public async Task Config_BackendWithoutAnOllamaTarget_NeverEchoesTheProviderCredential()
        {
            // GET /config is anonymous on a keyless instance, so the credential of a METERED
            // provider is exactly the thing that must not ride the config view. The R8 tier is what
            // withholds it; this is the same assertion shape the sibling config test uses.
            using var factory = new RemoteBackendFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/config");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            var raw = await response.Content.ReadAsStringAsync();
            Assert.IsFalse(raw.Contains(RemoteKey), "the config view must never echo a provider credential");
            Assert.IsFalse(raw.Contains(OtherRemoteKey),
                "including the credential of a provider this deployment did not select");
            var chat = JsonDocument.Parse(raw).RootElement.GetProperty("semantic").GetProperty("chat");
            Assert.AreEqual("gpt-4o-mini", chat.GetProperty("model").GetString(),
                "/config reports the model the same way /status does");
            Assert.AreEqual(JsonValueKind.Null, chat.GetProperty("resident").ValueKind,
                "the residency probe is skipped for a backend that has no such API, not attempted and failed");
        }

        [TestMethod]
        public async Task Config_ProjectsSemanticAndObservability_Redacts_AndDoesNotFlipLoaded()
        {
            using var factory = new ChatFactory(enabled: true, backend: Returns("x"),
                withApiKey: true, otlpEndpoint: "http://otel-collector:4317");
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);

            using var response = await client.GetAsync("/config");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

            var raw = await response.Content.ReadAsStringAsync();
            var body = JsonDocument.Parse(raw).RootElement;

            var chat = body.GetProperty("semantic").GetProperty("chat");
            Assert.IsTrue(chat.GetProperty("enabled").GetBoolean());
            // THE regression this fix targets: the residency probe uses a TRANSIENT client, so
            // reading config must NOT flip 'loaded'. No chat completion happened, so the lazy
            // backend is still uncreated. (resident/gpu are best-effort and environment-dependent -
            // they reflect whatever the local sidecar reports, so they are not asserted here.)
            Assert.IsFalse(chat.GetProperty("loaded").GetBoolean(),
                "a config read must never load the model or flip 'loaded'");

            var obs = body.GetProperty("observability");
            Assert.IsTrue(obs.GetProperty("otlpEnabled").GetBoolean());
            Assert.AreEqual("http://otel-collector:4317", obs.GetProperty("otlpEndpoint").GetString());
            Assert.IsFalse(obs.GetProperty("prometheusEnabled").GetBoolean());

            Assert.IsTrue(body.GetProperty("apiKeyRequired").GetBoolean());
            Assert.IsFalse(raw.Contains(ApiKey), "the config view must never echo the API key");
        }

        [TestMethod]
        public async Task Config_401_WithoutCredential_WhenKeyConfigured()
        {
            using var factory = new ChatFactory(enabled: true, backend: Returns("x"), withApiKey: true);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/config");
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }
}
