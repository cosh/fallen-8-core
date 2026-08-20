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
    ///   The transport for the Nahil backend (feature nahil-backend).
    ///
    ///   <para>Nahil is a third-party service, so this does NOT test Nahil. It tests the four things
    ///   about talking to one that can hurt Fallen-8 quietly: the credential reaching the right
    ///   client and no log line, a retry that replays a request faithfully, a wait schedule that
    ///   cannot spend a caller's whole budget on round-trips, and the warm-up never leaking onto the
    ///   local sidecar or the residency probe.</para>
    ///
    ///   <para>No test here spends real time: the retry handler takes its wait as an injected
    ///   delegate, so a "waits ten seconds" case asserts the two five-second waits it COMPUTED and
    ///   finishes in microseconds. There is no per-test timeout in this suite, so a genuinely
    ///   sleeping test would not fail - it would quietly lengthen the run.</para>
    /// </summary>
    [TestClass]
    public class NahilTransportTest
    {
        private const String Key = "nahil-secret-key";

        private static OllamaConnection Nahil(String model = "phi4-f8-mini:latest", String apiKey = Key)
        {
            return OllamaConnection.Nahil("Fallen8:Chat:Nahil", "https://api.nahil.dev", model, apiKey);
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
            var handler = new NahilWarmupRetryHandler("phi4-f8-mini:latest", logger,
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

        private static async Task<HttpResponseMessage> Get(HttpClient client, String path = "api/chat")
        {
            return await client.GetAsync("https://api.nahil.dev/" + path);
        }

        #region the credential

        /// <summary>
        ///   The credential goes to Nahil on every route including the residency probe, chat and
        ///   embedding carry their OWN keys, and a local sidecar carries no Authorization header at
        ///   all - real Ollama authenticates nothing, so sending one would change behaviour for
        ///   every existing deployment.
        /// </summary>
        [TestMethod]
        public async Task TheCredential_ReachesEveryNahilClient_AndNeverTheSidecar()
        {
            var chat = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var embedding = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var probe = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            var sidecar = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

            using (var client = OllamaHttpClientFactory.CreateForProvider(
                OllamaConnection.Nahil("Fallen8:Chat:Nahil", "https://api.nahil.dev", "m", "chat-key"),
                logger: null, chat))
            using (var embeddingClient = OllamaHttpClientFactory.CreateForProvider(
                OllamaConnection.Nahil("Fallen8:Embedding:Nahil", "https://api.nahil.dev", "m", "embed-key"),
                logger: null, embedding))
            // The probe is the one a keyless implementation would forget: /api/ps is authenticated
            // too, so without the key it 401s, the probe swallows that to "unknown" by design, and
            // the config page reports residency unknown forever with nothing saying why.
            using (var probeClient = OllamaHttpClientFactory.CreateForProbe(Nahil(),
                TimeSpan.FromSeconds(3), probe))
            using (var sidecarClient = OllamaHttpClientFactory.CreateForProvider(Sidecar(),
                logger: null, sidecar))
            {
                (await Get(client, "api/tags")).Dispose();
                (await Get(embeddingClient, "api/embed")).Dispose();
                (await Get(probeClient, "api/ps")).Dispose();
                (await Get(sidecarClient, "api/tags")).Dispose();
            }

            Assert.AreEqual("Bearer chat-key", chat.Authorizations.Single());
            Assert.AreEqual("Bearer embed-key", embedding.Authorizations.Single(),
                "one shared key would silently serve both providers, so metering them apart would not work");
            Assert.AreEqual("Bearer " + Key, probe.Authorizations.Single());
            Assert.IsNull(sidecar.Authorizations.Single(), "a local sidecar authenticates nothing");
        }

        #endregion

        #region which client gets the warm-up retry

        /// <summary>
        ///   The warm-up retry exists ONLY on a Nahil provider client, and the deadline rule holds:
        ///   a provider transport carries no timeout of its own (the caller's configured budget is
        ///   authoritative) while the probe carries a finite one. On a sidecar the retry would turn a
        ///   fast honest failure into a wait; on the probe it would install a second deadline inside
        ///   the very bound the probe exists to keep.
        /// </summary>
        [TestMethod]
        public void TheWarmupRetryAndTheDeadline_ReachOnlyTheClientsThatShouldHaveThem()
        {
            using var nahil = OllamaHttpClientFactory.CreateForProvider(Nahil(), logger: null);
            Assert.IsInstanceOfType(Outermost(nahil), typeof(NahilWarmupRetryHandler));
            Assert.IsInstanceOfType(((DelegatingHandler)Outermost(nahil)).InnerHandler, typeof(SocketsHttpHandler),
                "and it dials through the DNS-recycling handler underneath");
            Assert.AreEqual(Timeout.InfiniteTimeSpan, nahil.Timeout,
                "two deadlines is the bug the two factory entry points exist to prevent");

            using var sidecar = OllamaHttpClientFactory.CreateForProvider(Sidecar(), logger: null);
            Assert.IsInstanceOfType(Outermost(sidecar), typeof(SocketsHttpHandler),
                "a local sidecar never answers 503-warming, so it must keep failing fast");

            using var probe = OllamaHttpClientFactory.CreateForProbe(Nahil(), TimeSpan.FromSeconds(3));
            Assert.IsInstanceOfType(Outermost(probe), typeof(SocketsHttpHandler),
                "the probe's own 3s bound is the whole point of it");
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
        ///   A cold model is waited out for as long as it asks, and the call then succeeds. The
        ///   "about ten seconds" is asserted as the two five-second waits it computed rather than by
        ///   actually waiting them.
        /// </summary>
        [TestMethod]
        public async Task AWarmUp_IsWaitedOut_AndTheCallThenSucceeds()
        {
            var stub = new StubHandler(call => call <= 2
                ? Retryable(HttpStatusCode.ServiceUnavailable, "5")
                : new HttpResponseMessage(HttpStatusCode.OK));
            var (handler, schedule) = Retrying(stub);

            using var client = new HttpClient(handler);
            using var response = await Get(client);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(3, stub.Calls);
            CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5) }, schedule);
        }

        /// <summary>
        ///   No Retry-After a server can get wrong turns the retry into a hot loop or parks a
        ///   request. This is the one that protects the caller's budget: an immediate retry on a
        ///   stale or zero value would spend the whole of it on round-trips, and an unclamped hostile
        ///   value would spend it on one wait.
        /// </summary>
        [TestMethod]
        public void AnUnusableRetryAfter_NeitherHotLoopsNorParksTheRequest()
        {
            foreach (var value in new[] { null, "soon", "0", "-1", "" })
            {
                using var response = Retryable(HttpStatusCode.ServiceUnavailable, value);
                var wait = NahilWarmupRetryHandler.WaitFor(response.Headers.RetryAfter, attempt: 1);
                Assert.IsTrue(wait >= TimeSpan.FromSeconds(NahilWarmupRetryHandler.FirstBackoffSeconds)
                    && wait <= TimeSpan.FromSeconds(NahilWarmupRetryHandler.MaxBackoffSeconds),
                    "'" + value + "' must fall back to the backoff, not to an immediate retry; got " + wait);
            }

            // An HTTP-date in the PAST is the same hazard wearing a valid header's clothes.
            using var stale = Retryable(HttpStatusCode.ServiceUnavailable,
                DateTimeOffset.UtcNow.AddMinutes(-5).ToString("r", CultureInfo.InvariantCulture));
            Assert.IsTrue(NahilWarmupRetryHandler.WaitFor(stale.Headers.RetryAfter, attempt: 1)
                >= TimeSpan.FromSeconds(NahilWarmupRetryHandler.FirstBackoffSeconds));

            // A date in the future IS honoured - that branch is the reason this reads the wall clock.
            using var soon = Retryable(HttpStatusCode.ServiceUnavailable,
                DateTimeOffset.UtcNow.AddSeconds(20).ToString("r", CultureInfo.InvariantCulture));
            var until = NahilWarmupRetryHandler.WaitFor(soon.Headers.RetryAfter, attempt: 1);
            Assert.IsTrue(until > TimeSpan.FromSeconds(15) && until <= TimeSpan.FromSeconds(21), "got " + until);

            using var hostile = Retryable(HttpStatusCode.ServiceUnavailable, "86400");
            Assert.AreEqual(TimeSpan.FromSeconds(NahilWarmupRetryHandler.MaxWaitSeconds),
                NahilWarmupRetryHandler.WaitFor(hostile.Headers.RetryAfter, attempt: 1),
                "one hostile value must not park a request for a day");

            // The backoff grows and then stops growing. A range, not a value: the jitter is real
            // randomness, so a fleet that all started waiting on one cold model does not return in
            // lockstep.
            Assert.IsTrue(NahilWarmupRetryHandler.Backoff(1).TotalSeconds >= 2d);
            Assert.IsTrue(NahilWarmupRetryHandler.Backoff(3) > NahilWarmupRetryHandler.Backoff(1));
            Assert.AreEqual(NahilWarmupRetryHandler.MaxBackoffSeconds,
                NahilWarmupRetryHandler.Backoff(30).TotalSeconds, 0.001);
        }

        /// <summary>
        ///   Only 503 and 429 are retried, and the two never READ the same: one means a model is
        ///   still being pulled, the other that a quota is spent, and an operator acts differently
        ///   on each.
        /// </summary>
        [TestMethod]
        public async Task OnlyAWarmUpAndAQuotaAreRetried_AndTheyReadDifferently()
        {
            var sink = new TestLogSink();
            var limited = new StubHandler(call => call == 1
                ? Retryable(HttpStatusCode.TooManyRequests, "3")
                : new HttpResponseMessage(HttpStatusCode.OK));
            var (handler, schedule) = Retrying(limited, sink.CreateFactory().CreateLogger("nahil"));

            using (var client = new HttpClient(handler))
            {
                using var response = await Get(client, "api/embed");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }

            CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(3) }, schedule);
            Assert.IsTrue(sink.Contains(LogLevel.Information, "429", "rate limited"),
                "a quota limit must not be reported as a model warming up: "
                + String.Join(" | ", sink.Entries.Select(e => e.Message)));
            Assert.IsFalse(sink.Contains(LogLevel.Trace, "warming up"));

            foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.InternalServerError })
            {
                var stub = new StubHandler(_ => Retryable(status, "5"));
                var (other, otherSchedule) = Retrying(stub);
                using var client = new HttpClient(other);
                using var response = await Get(client);

                Assert.AreEqual(status, response.StatusCode);
                Assert.AreEqual(1, stub.Calls, status + " must not be retried");
                Assert.AreEqual(0, otherSchedule.Count, status + " must not wait");
            }
        }

        #endregion

        #region replay, redaction and giving up

        /// <summary>
        ///   The retry REPLAYS the request: same body, same content type, same credential, every
        ///   attempt. Without the buffer-and-clone this is the failure that hides best - the second
        ///   request goes out empty, Nahil answers 400, and the retry looks like it worked on its own
        ///   merits.
        ///
        ///   <para>Driven through a real HttpClient with the credential as a DEFAULT header, the way
        ///   the factory sets it, which also pins the ordering this depends on: HttpClient merges its
        ///   default headers onto the request BEFORE the handler chain sees it, so the clone has
        ///   something to copy.</para>
        /// </summary>
        [TestMethod]
        public async Task ARetriedRequest_ReplaysItsBodyAndItsCredential()
        {
            var stub = new StubHandler(call => call <= 2
                ? Retryable(HttpStatusCode.ServiceUnavailable, "5")
                : new HttpResponseMessage(HttpStatusCode.OK));
            var (handler, schedule) = Retrying(stub);

            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.nahil.dev") };
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Key);

            using var response = await client.PostAsync("api/chat",
                new StringContent("{\"model\":\"phi4-f8-mini:latest\"}", System.Text.Encoding.UTF8, "application/json"));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(3, stub.Calls);
            Assert.AreEqual(2, schedule.Count, "and none of it was actually waited");
            foreach (var body in stub.Bodies)
            {
                Assert.AreEqual("{\"model\":\"phi4-f8-mini:latest\"}", body, "every attempt replays the same body");
            }

            foreach (var authorization in stub.Authorizations)
            {
                Assert.AreEqual("Bearer " + Key, authorization, "every attempt carries the credential");
            }

            foreach (var contentType in stub.ContentTypes)
            {
                Assert.AreEqual("application/json; charset=utf-8", contentType,
                    "the clone keeps the content headers, or a replayed request Nahil cannot parse "
                    + "answers 400 and reads as the retry having failed on its merits");
            }
        }

        /// <summary>
        ///   One line per retry - a cold multi-gigabyte model can take minutes and the operator needs
        ///   progress, not a wall - and the credential appears in NO line. The key is set once on the
        ///   client, so this log is the only place a formatted header could plausibly leak into.
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
            using var response = await Get(client);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(3, schedule.Count);
            Assert.AreEqual(3, sink.Entries.Count(e => e.Level == LogLevel.Information),
                "one line per retry, no per-poll spam");
            Assert.IsTrue(sink.Contains(LogLevel.Information, "503", "warming up", "phi4-f8-mini:latest"));

            foreach (var entry in sink.Entries)
            {
                Assert.IsFalse(entry.Message?.Contains(Key, StringComparison.Ordinal) == true,
                    "the credential must appear in no log line: " + entry.Message);
            }
        }

        /// <summary>
        ///   When the caller's budget runs out mid-wait, the failure says the model was not available
        ///   in time and names it, the status Nahil kept answering, and how long we waited. "The
        ///   backend did not respond" alone would send an operator looking for a slow model where the
        ///   truth is that one was never loaded.
        ///
        ///   <para>It must also SURVIVE HttpClient, which is why it is not an
        ///   OperationCanceledException: HttpClient replaces any cancellation leaving its handler
        ///   chain with a TaskCanceledException of its own, so a subclass would be silently
        ///   discarded. The cancellation it came from is kept, because a caller who went away must
        ///   still get a cancellation rather than a fault report.</para>
        /// </summary>
        [TestMethod]
        public async Task WhenTheBudgetRunsOutMidWait_TheFailureNamesTheModelAndSurvivesHttpClient()
        {
            Assert.IsFalse(typeof(OperationCanceledException).IsAssignableFrom(typeof(NahilWarmupTimeoutException)),
                "deriving from OperationCanceledException is what HttpClient throws away");

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
                    return Task.FromCanceled(budget.Token);
                }

                return Task.CompletedTask;
            });

            using var client = new HttpClient(handler);
            var gaveUp = await Assert.ThrowsExceptionAsync<NahilWarmupTimeoutException>(
                () => client.GetAsync("https://api.nahil.dev/api/chat", budget.Token));

            StringAssert.Contains(gaveUp.Message, "phi4-f8-mini:latest");
            StringAssert.Contains(gaveUp.Message, "was not available in time");
            StringAssert.Contains(gaveUp.Message, "warming up");
            StringAssert.Contains(gaveUp.Message, "14s", "two completed 7s waits is 14s of waiting");
            Assert.IsInstanceOfType(gaveUp.InnerException, typeof(OperationCanceledException),
                "the cancellation is kept so a provider can tell a caller walking away from a spent budget");
        }

        #endregion

        #region the connection contract

        /// <summary>
        ///   Every way an endpoint can be unusable, and the key named in each refusal. The host-root
        ///   rule is the one worth pinning: HttpClient.BaseAddress DROPS a path prefix silently, so
        ///   accepting one would send every request to the wrong URL and report only a 404 from
        ///   somewhere unexpected.
        /// </summary>
        [TestMethod]
        public void AnEndpointThatCannotBeDialled_IsRefusedWithTheKeyToFix()
        {
            Assert.IsTrue(OllamaConnection.Sidecar("S", "http://ollama:11434", "m").IsValid(out _));
            Assert.IsTrue(OllamaConnection.Sidecar("S", "http://ollama:11434/", "m").IsValid(out _),
                "a bare trailing slash IS a host root");
            Assert.IsTrue(OllamaConnection.Nahil("N", "https://api.nahil.dev", "m", "k").IsValid(out _));

            foreach (var endpoint in new[]
            {
                "https://api.nahil.dev/v1", "https://api.nahil.dev?a=b", "https://api.nahil.dev/#f",
                "ftp://api.nahil.dev", "api.nahil.dev", "", null
            })
            {
                Assert.IsFalse(OllamaConnection.Nahil("N", endpoint, "m", "k").IsValid(out var problem),
                    "'" + endpoint + "' must be refused");
                StringAssert.Contains(problem, "N:Endpoint");
            }

            // Nahil without its credential is refused rather than sent to 401 on every call, and the
            // sidecar is held to the endpoint contract but never asked for one.
            Assert.IsFalse(OllamaConnection.Nahil("N", "https://api.nahil.dev", "m", " ").IsValid(out var noKey));
            StringAssert.Contains(noKey, "N:ApiKey");
            Assert.IsFalse(OllamaConnection.Sidecar("S", "http://ollama:11434", " ").IsValid(out var noModel));
            StringAssert.Contains(noModel, "S:Model");
        }

        #endregion
    }
}
