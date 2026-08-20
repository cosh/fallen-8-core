// MIT License
//
// NahilTransportTest.cs
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
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Helper;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The transport half of the Nahil backend: the credential, the warm-up retry
    ///   schedule, and which of the two ever reaches which client.
    ///
    ///   <para>No test here spends real time. The retry handler takes its wait as an injected
    ///   delegate for exactly that reason (the arithmetic is the behaviour, the sleeping is not), so
    ///   a "waits ten seconds then succeeds" case asserts the two five-second waits it COMPUTED and
    ///   finishes in microseconds. There is no per-test timeout in this suite, so a genuinely
    ///   sleeping test would not fail - it would quietly lengthen the run with nothing pointing at
    ///   it.</para>
    /// </summary>
    [TestClass]
    public class NahilTransportTest
    {
        private const String Key = "gw-secret-key";

        private static OllamaConnection Nahil(String model = "f8-delegate:latest", String apiKey = Key)
        {
            return OllamaConnection.Nahil("Fallen8:Chat:Nahil", "https://models.example", model, apiKey);
        }

        private static OllamaConnection Sidecar(String model = "phi4-f8-mini:latest")
        {
            return OllamaConnection.Sidecar("Fallen8:Chat:Ollama", "http://localhost:11434", model);
        }

        /// <summary>A handler whose responses (and recorded requests) a test controls entirely.</summary>
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<Int32, HttpResponseMessage> _respond;

            public StubHandler(Func<Int32, HttpResponseMessage> respond)
            {
                _respond = respond;
            }

            public List<String> Bodies { get; } = new List<String>();

            public List<String> Authorizations { get; } = new List<String>();

            public List<String> ContentTypes { get; } = new List<String>();

            public Int32 Calls
            {
                get; private set;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Calls++;
                Bodies.Add(request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
                Authorizations.Add(request.Headers.Authorization?.ToString());
                ContentTypes.Add(request.Content?.Headers.ContentType?.ToString());
                return _respond(Calls);
            }
        }

        private static HttpResponseMessage Retryable(HttpStatusCode status, String retryAfter)
        {
            var response = new HttpResponseMessage(status);
            if (retryAfter != null)
            {
                // TryAddWithoutValidation so a test can send a value the typed accessor REFUSES,
                // which is the "unparseable falls back to backoff" case.
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            }

            return response;
        }

        /// <summary>The handler under test, with its waits captured instead of taken.</summary>
        private static (NahilWarmupRetryHandler Handler, List<TimeSpan> Schedule) Retrying(
            StubHandler inner, ILogger logger = null, Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            var schedule = new List<TimeSpan>();
            var handler = new NahilWarmupRetryHandler("f8-delegate:latest", logger,
                delay ?? ((wait, _) =>
                {
                    schedule.Add(wait);
                    return Task.CompletedTask;
                }))
            {
                InnerHandler = inner
            };
            return (handler, schedule);
        }

        #region the credential

        /// <summary>
        ///   FR-3: a Nahil request carries the bearer credential on every route, and a local
        ///   sidecar carries no Authorization header at all - real Ollama authenticates nothing, so
        ///   sending one would be a behaviour change for every existing deployment.
        /// </summary>
        [TestMethod]
        public async Task ANahilRequest_CarriesTheBearerCredential_AndASidecarRequestCarriesNone()
        {
            var nahilStub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            using (var client = OllamaHttpClientFactory.CreateForProvider(Nahil(), logger: null, nahilStub))
            {
                using var response = await client.GetAsync("api/tags");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }

            Assert.AreEqual("Bearer " + Key, nahilStub.Authorizations.Single());

            var sidecarStub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            using (var client = OllamaHttpClientFactory.CreateForProvider(Sidecar(), logger: null, sidecarStub))
            {
                using var response = await client.GetAsync("api/tags");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }

            Assert.IsNull(sidecarStub.Authorizations.Single(), "a local sidecar authenticates nothing");
        }

        /// <summary>
        ///   The chat and embedding clients carry their OWN keys. One shared client would let a
        ///   single configured key silently serve both providers, so a deployment that keys them
        ///   separately (to meter or revoke them separately) would not actually be doing so.
        /// </summary>
        [TestMethod]
        public async Task ChatAndEmbedding_CarryTheirOwnKeys()
        {
            var chatStub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var embeddingStub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

            using (var chat = OllamaHttpClientFactory.CreateForProvider(
                OllamaConnection.Nahil("Fallen8:Chat:Nahil", "https://models.example", "m", "chat-key"),
                logger: null, chatStub))
            using (var embedding = OllamaHttpClientFactory.CreateForProvider(
                OllamaConnection.Nahil("Fallen8:Embedding:Nahil", "https://models.example", "m", "embed-key"),
                logger: null, embeddingStub))
            {
                (await chat.GetAsync("api/tags")).Dispose();
                (await embedding.GetAsync("api/tags")).Dispose();
            }

            Assert.AreEqual("Bearer chat-key", chatStub.Authorizations.Single());
            Assert.AreEqual("Bearer embed-key", embeddingStub.Authorizations.Single());
        }

        /// <summary>
        ///   The residency probe authenticates too. Without the key <c>/api/ps</c> answers 401, the
        ///   probe swallows it to "unknown" by design, and the config page would report residency
        ///   unknown forever with nothing in the logs to say why.
        /// </summary>
        [TestMethod]
        public async Task TheProbeTransport_CarriesTheCredentialToo()
        {
            var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            using var client = OllamaHttpClientFactory.CreateForProbe(Nahil(), TimeSpan.FromSeconds(3), stub);

            (await client.GetAsync("api/ps")).Dispose();

            Assert.AreEqual("Bearer " + Key, stub.Authorizations.Single());
        }

        #endregion

        #region which client gets the retry handler

        /// <summary>
        ///   The warm-up retry exists ONLY on a Nahil provider client. The two hazards it would
        ///   otherwise introduce are why this is pinned structurally rather than by behaviour: on a
        ///   sidecar it would turn a fast honest failure into a wait, and on the probe it would
        ///   install a second deadline inside the one the probe exists to keep.
        /// </summary>
        [TestMethod]
        public void OnlyANahilProviderClient_CarriesTheWarmupRetry()
        {
            using var nahil = OllamaHttpClientFactory.CreateForProvider(Nahil(), logger: null);
            var retry = Outermost(nahil);
            Assert.IsInstanceOfType(retry, typeof(NahilWarmupRetryHandler),
                "Nahil waits out a warm-up 503");
            Assert.IsInstanceOfType(((DelegatingHandler)retry).InnerHandler, typeof(SocketsHttpHandler),
                "and dials through the DNS-recycling handler underneath it");

            using var sidecar = OllamaHttpClientFactory.CreateForProvider(Sidecar(), logger: null);
            Assert.IsInstanceOfType(Outermost(sidecar), typeof(SocketsHttpHandler),
                "a local sidecar never answers 503-warming, so it must keep failing fast");

            using var probe = OllamaHttpClientFactory.CreateForProbe(Nahil(), TimeSpan.FromSeconds(3));
            Assert.IsInstanceOfType(Outermost(probe), typeof(SocketsHttpHandler),
                "the probe's own 3s bound is the whole point of it - a warm-up wait would defeat it");
        }

        /// <summary>
        ///   The deadline rule, pinned: a provider transport carries NO timeout of its own (the
        ///   caller's configured budget is authoritative), the probe carries a finite one.
        /// </summary>
        [TestMethod]
        public void AProviderTransportHasNoDeadline_AndTheProbeHasAFiniteOne()
        {
            using var provider = OllamaHttpClientFactory.CreateForProvider(Nahil(), logger: null);
            Assert.AreEqual(Timeout.InfiniteTimeSpan, provider.Timeout,
                "two deadlines is the bug the two entry points exist to prevent");

            using var probe = OllamaHttpClientFactory.CreateForProbe(Nahil(), TimeSpan.FromSeconds(3));
            Assert.AreEqual(TimeSpan.FromSeconds(3), probe.Timeout);
        }

        /// <summary>Reflects out the first handler in an HttpClient's chain.</summary>
        private static HttpMessageHandler Outermost(HttpClient client)
        {
            for (var type = typeof(HttpClient); type != null; type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    if (typeof(HttpMessageHandler).IsAssignableFrom(field.FieldType)
                        && field.GetValue(client) is HttpMessageHandler handler)
                    {
                        return handler;
                    }
                }
            }

            Assert.Fail("no handler field found on HttpClient - the reflection needs updating");
            return null;
        }

        #endregion

        #region the wait schedule

        /// <summary>
        ///   Retry-After in delta-seconds is honoured verbatim, and the plan's "succeeds after about
        ///   ten seconds of waiting" is asserted as the two five-second waits it computed rather
        ///   than by actually waiting them.
        /// </summary>
        [TestMethod]
        public async Task RetryAfterInSeconds_IsHonoured_AndTheCallSucceedsAfterIt()
        {
            var stub = new StubHandler(call => call <= 2
                ? Retryable(HttpStatusCode.ServiceUnavailable, "5")
                : new HttpResponseMessage(HttpStatusCode.OK));
            var (handler, schedule) = Retrying(stub);

            using var client = new HttpClient(handler);
            using var response = await client.GetAsync("https://models.example/api/chat");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(3, stub.Calls);
            CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5) }, schedule);
            Assert.AreEqual(10d, schedule.Sum(w => w.TotalSeconds), 0.001);
        }

        /// <summary>
        ///   The HTTP-date form of Retry-After is honoured as well - it is the half a "Retry-After: 0"
        ///   shortcut can never exercise, and the reason the computation reads the wall clock at all.
        /// </summary>
        [TestMethod]
        public void RetryAfterAsAnHttpDate_IsHonoured_AndAPastDateFallsBackToBackoff()
        {
            var future = DateTimeOffset.UtcNow.AddSeconds(20);
            using var soon = Retryable(HttpStatusCode.ServiceUnavailable,
                future.ToString("r", CultureInfo.InvariantCulture));
            var wait = NahilWarmupRetryHandler.WaitFor(soon.Headers.RetryAfter, attempt: 1);
            Assert.IsTrue(wait > TimeSpan.FromSeconds(15) && wait <= TimeSpan.FromSeconds(21),
                "an HTTP-date is honoured as the distance to it, got " + wait);

            // A date in the PAST must not mean "retry immediately": a server repeating a stale date
            // would turn the retry into a hot loop that spends the whole budget on round-trips.
            using var stale = Retryable(HttpStatusCode.ServiceUnavailable,
                DateTimeOffset.UtcNow.AddMinutes(-5).ToString("r", CultureInfo.InvariantCulture));
            var fallback = NahilWarmupRetryHandler.WaitFor(stale.Headers.RetryAfter, attempt: 1);
            Assert.IsTrue(fallback >= TimeSpan.FromSeconds(NahilWarmupRetryHandler.FirstBackoffSeconds),
                "a stale date falls back to the backoff, got " + fallback);
        }

        /// <summary>Every Retry-After a server can get wrong, and what each falls back to.</summary>
        [TestMethod]
        public void AnAbsentOrHostileRetryAfter_FallsBackOrIsClamped()
        {
            foreach (var value in new[] { null, "soon", "0", "-1", "" })
            {
                using var response = Retryable(HttpStatusCode.ServiceUnavailable, value);
                var wait = NahilWarmupRetryHandler.WaitFor(response.Headers.RetryAfter, attempt: 1);
                Assert.IsTrue(wait >= TimeSpan.FromSeconds(NahilWarmupRetryHandler.FirstBackoffSeconds),
                    "'" + value + "' must fall back to the backoff, not to an immediate retry; got " + wait);
                Assert.IsTrue(wait <= TimeSpan.FromSeconds(NahilWarmupRetryHandler.MaxBackoffSeconds), "got " + wait);
            }

            // A hostile value cannot park a request: the per-wait clamp is what the caller's
            // wall-clock budget cannot express.
            using var hostile = Retryable(HttpStatusCode.ServiceUnavailable, "86400");
            Assert.AreEqual(TimeSpan.FromSeconds(NahilWarmupRetryHandler.MaxWaitSeconds),
                NahilWarmupRetryHandler.WaitFor(hostile.Headers.RetryAfter, attempt: 1));
        }

        /// <summary>
        ///   The backoff grows and then stops growing. Asserted as a RANGE per attempt because the
        ///   jitter is real randomness - a fleet that all started waiting on the same cold model
        ///   must not return in lockstep.
        /// </summary>
        [TestMethod]
        public void TheBackoff_GrowsWithJitter_AndStopsAtItsCeiling()
        {
            var previousFloor = 0d;
            for (var attempt = 1; attempt <= 8; attempt++)
            {
                var floor = Math.Min(NahilWarmupRetryHandler.FirstBackoffSeconds * Math.Pow(2d, attempt - 1),
                    NahilWarmupRetryHandler.MaxBackoffSeconds);
                var ceiling = Math.Min(floor * 1.25d, NahilWarmupRetryHandler.MaxBackoffSeconds);

                for (var sample = 0; sample < 20; sample++)
                {
                    var wait = NahilWarmupRetryHandler.Backoff(attempt).TotalSeconds;
                    Assert.IsTrue(wait >= floor - 0.001 && wait <= ceiling + 0.001,
                        "attempt " + attempt + " waited " + wait + "s, outside [" + floor + ", " + ceiling + "]");
                }

                Assert.IsTrue(floor >= previousFloor, "the backoff never shrinks");
                previousFloor = floor;
            }

            Assert.AreEqual(NahilWarmupRetryHandler.MaxBackoffSeconds,
                NahilWarmupRetryHandler.Backoff(30).TotalSeconds, 0.001,
                "a long warm-up settles at the ceiling rather than growing without bound");
        }

        /// <summary>
        ///   429 retries like 503 but never READS like it: one means a model is still being pulled,
        ///   the other that a quota is spent, and an operator acts differently on each.
        /// </summary>
        [TestMethod]
        public async Task RateLimiting_RetriesLikeAWarmUpButIsReportedDifferently()
        {
            var sink = new TestLogSink();
            var stub = new StubHandler(call => call == 1
                ? Retryable(HttpStatusCode.TooManyRequests, "3")
                : new HttpResponseMessage(HttpStatusCode.OK));
            var (handler, schedule) = Retrying(stub, sink.CreateFactory().CreateLogger("nahil"));

            using var client = new HttpClient(handler);
            using var response = await client.GetAsync("https://models.example/api/embed");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(3) }, schedule);
            Assert.IsTrue(sink.Contains(LogLevel.Information, "429", "rate limited", "f8-delegate:latest"),
                "the 429 line says rate limited: " + String.Join(" | ", sink.Entries.Select(e => e.Message)));
            Assert.IsFalse(sink.Contains(LogLevel.Trace, "warming up"),
                "a quota limit must not be reported as a model warming up");
        }

        /// <summary>Nothing but 503 and 429 is retryable: every other status is the answer.</summary>
        [TestMethod]
        public async Task NoOtherStatus_IsRetried()
        {
            foreach (var status in new[]
            {
                HttpStatusCode.OK, HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized,
                HttpStatusCode.NotFound, HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway
            })
            {
                var stub = new StubHandler(_ => Retryable(status, "5"));
                var (handler, schedule) = Retrying(stub);

                using var client = new HttpClient(handler);
                using var response = await client.GetAsync("https://models.example/api/chat");

                Assert.AreEqual(status, response.StatusCode);
                Assert.AreEqual(1, stub.Calls, status + " must not be retried");
                Assert.AreEqual(0, schedule.Count, status + " must not wait");
            }
        }

        /// <summary>
        ///   The retry REPLAYS the request: same body, same credential, every attempt. Without the
        ///   buffer-and-clone this is the failure that hides best - the second request goes out
        ///   empty, Nahil answers 400, and the retry looks like it worked.
        /// </summary>
        [TestMethod]
        public async Task ARetriedRequest_ReplaysItsBodyAndItsCredential()
        {
            var stub = new StubHandler(call => call <= 2
                ? Retryable(HttpStatusCode.ServiceUnavailable, "5")
                : new HttpResponseMessage(HttpStatusCode.OK));
            var (handler, schedule) = Retrying(stub);

            // Driven through a real HttpClient with the credential as a DEFAULT header, which is how
            // the factory sets it: that also pins the ordering this depends on, namely that HttpClient
            // merges its default headers onto the request BEFORE the handler chain sees it, so the
            // clone has something to copy. The handler is used directly rather than via the factory
            // only because the factory has no seam for the injected wait.
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://models.example") };
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Key);

            using var response = await client.PostAsync("api/chat",
                new StringContent("{\"model\":\"f8-delegate:latest\"}", System.Text.Encoding.UTF8, "application/json"));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(3, stub.Calls);
            Assert.AreEqual(2, schedule.Count, "and none of it was actually waited");
            foreach (var body in stub.Bodies)
            {
                Assert.AreEqual("{\"model\":\"f8-delegate:latest\"}", body, "every attempt replays the same body");
            }

            foreach (var authorization in stub.Authorizations)
            {
                Assert.AreEqual("Bearer " + Key, authorization, "every attempt carries the credential");
            }

            foreach (var contentType in stub.ContentTypes)
            {
                Assert.AreEqual("application/json; charset=utf-8", contentType,
                    "the clone keeps the content headers: a replayed request Nahil cannot parse "
                    + "would answer 400 and read as the retry having failed on its own merits");
            }
        }

        #endregion

        #region logging and redaction

        /// <summary>
        ///   One line per retry - a cold multi-gigabyte model can take minutes and the operator needs
        ///   progress, not a wall - and the credential appears in NO line. This is the redaction
        ///   assertion for the whole feature: the key is set once on the client, so the retry log is
        ///   the only place a formatted header could plausibly leak into.
        /// </summary>
        [TestMethod]
        public async Task EachRetryLogsExactlyOneLine_AndNeverTheCredential()
        {
            var sink = new TestLogSink();
            var stub = new StubHandler(call => call <= 3
                ? Retryable(HttpStatusCode.ServiceUnavailable, "2")
                : new HttpResponseMessage(HttpStatusCode.OK));
            var (handler, schedule) = Retrying(stub, sink.CreateFactory().CreateLogger("nahil"));

            using var client = new HttpClient(handler);
            using var response = await client.GetAsync("https://models.example/api/chat");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(3, schedule.Count);
            Assert.AreEqual(3, sink.Entries.Count(e => e.Level == LogLevel.Information),
                "one line per retry, no per-poll spam");
            Assert.IsTrue(sink.Contains(LogLevel.Information, "503", "warming up", "f8-delegate:latest"));

            foreach (var entry in sink.Entries)
            {
                Assert.IsFalse(entry.Message?.Contains(Key, StringComparison.Ordinal) == true,
                    "the credential must appear in no log line: " + entry.Message);
            }
        }

        #endregion

        #region giving up

        /// <summary>
        ///   When the caller's budget runs out mid-wait, the failure says the model was not available
        ///   in time and names it, the status Nahil kept answering, and how long we waited.
        ///   "The backend did not respond" alone would send an operator looking for a slow model
        ///   where the truth is that one was never loaded.
        /// </summary>
        [TestMethod]
        public async Task WhenTheBudgetRunsOutMidWait_TheFailureNamesTheModelAndTheTimeWaited()
        {
            using var budget = new CancellationTokenSource();
            var waits = 0;
            var stub = new StubHandler(_ => Retryable(HttpStatusCode.ServiceUnavailable, "7"));
            var (handler, _) = Retrying(stub, logger: null, delay: (wait, token) =>
            {
                // Two waits complete, then the budget expires inside the third - the case a test
                // that only cancelled up front would never reach.
                if (++waits > 2)
                {
                    budget.Cancel();
                    return Task.FromCanceled(token.IsCancellationRequested ? token : budget.Token);
                }

                return Task.CompletedTask;
            });

            using var client = new HttpClient(handler);
            var gaveUp = await Assert.ThrowsExceptionAsync<NahilWarmupTimeoutException>(
                () => client.GetAsync("https://models.example/api/chat", budget.Token));

            StringAssert.Contains(gaveUp.Message, "f8-delegate:latest");
            StringAssert.Contains(gaveUp.Message, "was not available in time");
            StringAssert.Contains(gaveUp.Message, "503");
            StringAssert.Contains(gaveUp.Message, "warming up");
            StringAssert.Contains(gaveUp.Message, "14s", "two completed 7s waits is 14s of waiting");
        }

        /// <summary>
        ///   The give-up report must SURVIVE HttpClient, which is the whole reason it is not an
        ///   OperationCanceledException: HttpClient replaces any cancellation leaving its handler
        ///   chain with a TaskCanceledException of its own, so a subclass would be silently
        ///   discarded and the operator would get "the backend did not respond" with no mention of
        ///   a model that was never loaded. The cancellation it came from is kept, because a caller
        ///   who went away must still get a cancellation rather than a fault report.
        /// </summary>
        [TestMethod]
        public async Task TheGiveUpReport_SurvivesHttpClient_AndKeepsTheCancellationItCameFrom()
        {
            Assert.IsFalse(typeof(OperationCanceledException).IsAssignableFrom(typeof(NahilWarmupTimeoutException)),
                "deriving from OperationCanceledException is what HttpClient throws away");

            using var budget = new CancellationTokenSource();
            var stub = new StubHandler(_ => Retryable(HttpStatusCode.ServiceUnavailable, "4"));
            var (handler, _) = Retrying(stub, logger: null, delay: (_, token) =>
            {
                budget.Cancel();
                return Task.FromCanceled(budget.Token);
            });

            using var client = new HttpClient(handler);
            var gaveUp = await Assert.ThrowsExceptionAsync<NahilWarmupTimeoutException>(
                () => client.GetAsync("https://models.example/api/chat", budget.Token));

            Assert.IsInstanceOfType(gaveUp.InnerException, typeof(OperationCanceledException),
                "the cancellation is kept so a provider can tell a caller walking away from a spent budget");
        }

        #endregion

        #region the connection contract

        /// <summary>
        ///   Every way an endpoint can be unusable, and the key named in each refusal. The host-root
        ///   rule is the one worth a test of its own: HttpClient.BaseAddress DROPS a path prefix
        ///   silently, so accepting one would send every request to the wrong URL and report only a
        ///   404 from somewhere unexpected.
        /// </summary>
        [TestMethod]
        public void AnEndpointThatCannotBeDialled_IsRefusedWithTheKeyToFix()
        {
            Assert.IsTrue(OllamaConnection.Sidecar("S", "http://ollama:11434", "m").IsValid(out _));
            Assert.IsTrue(OllamaConnection.Sidecar("S", "http://ollama:11434/", "m").IsValid(out _),
                "a bare trailing slash IS a host root");
            Assert.IsTrue(OllamaConnection.Nahil("G", "https://models.example", "m", "k").IsValid(out _));

            foreach (var endpoint in new[]
            {
                "https://models.example/v1", "https://models.example/", // the second is fine
                "https://models.example?a=b", "https://models.example/#f",
                "ftp://models.example", "models.example", "", null
            })
            {
                var connection = OllamaConnection.Nahil("G", endpoint, "m", "k");
                var valid = connection.IsValid(out var problem);
                if (endpoint == "https://models.example/")
                {
                    Assert.IsTrue(valid);
                    continue;
                }

                Assert.IsFalse(valid, "'" + endpoint + "' must be refused");
                StringAssert.Contains(problem, "G:Endpoint");
            }

            // Nahil without its credential is refused rather than sent to 401 on every call.
            Assert.IsFalse(OllamaConnection.Nahil("G", "https://models.example", "m", " ").IsValid(out var noKey));
            StringAssert.Contains(noKey, "G:ApiKey");

            // The sidecar is held to the endpoint contract but never asked for a credential.
            Assert.IsTrue(OllamaConnection.Sidecar("S", "http://ollama:11434", "m").IsValid(out _));
            Assert.IsFalse(OllamaConnection.Sidecar("S", "http://ollama:11434", " ").IsValid(out var noModel));
            StringAssert.Contains(noModel, "S:Model");
        }

        /// <summary>
        ///   The model reaches the request VERBATIM. Nothing normalizes a tag, which is the whole
        ///   point of configuring one: the configured string names one thing on both ends.
        /// </summary>
        [TestMethod]
        public void TheConfiguredModel_IsCarriedVerbatim()
        {
            Assert.AreEqual("bge-m3:latest",
                OllamaConnection.Nahil("G", "https://models.example", "bge-m3:latest", "k").Model);
            Assert.AreEqual("bge-m3",
                OllamaConnection.Sidecar("S", "http://ollama:11434", "bge-m3").Model,
                "an untagged name is passed through untouched too - normalizing it is the backend's job");
        }

        #endregion
    }
}
