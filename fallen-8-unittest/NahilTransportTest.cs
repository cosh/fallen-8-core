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

        #region a refusal that is not a warm-up

        /// <summary>A retryable status carrying a body, which is the only thing that can tell a
        /// warm-up from a refusal.</summary>
        private static HttpResponseMessage Retryable(HttpStatusCode status, String retryAfter, String body)
        {
            var response = Retryable(status, retryAfter);
            response.Content = new StringContent(body);
            return response;
        }

        private const String NoWorker =
            "{\"error\":\"model 'qwen3:4b' is in the catalog but no attached worker serves class S1 "
            + "(2 worker(s) attached). Retrying will not help until a worker subscribes to that class\"}";

        /// <summary>
        ///   A refusal test needs a budget, and that is not incidental: the whole point of the
        ///   refusal is that this handler has NO retry cap, so a stub answering the refusal
        ///   for ever is answering exactly what the live service answers for ever. Without a bound
        ///   a regressed refusal would not fail these tests, it would HANG them - and a hung test
        ///   in a suite with no per-test timeout does not go red, it just never finishes. So every
        ///   refusal case runs under a budget that expires after a few waits, which turns a
        ///   regression into a <see cref="NahilWarmupTimeoutException" /> the assertion rejects.
        /// </summary>
        private static (CancellationTokenSource Budget, Func<TimeSpan, CancellationToken, Task> Delay, List<TimeSpan> Schedule)
            Bounded(Int32 waitsBeforeGivingUp = 3)
        {
            var budget = new CancellationTokenSource();
            var schedule = new List<TimeSpan>();
            Func<TimeSpan, CancellationToken, Task> delay = (wait, _) =>
            {
                schedule.Add(wait);
                if (schedule.Count >= waitsBeforeGivingUp)
                {
                    budget.Cancel();
                    return Task.FromCanceled(budget.Token);
                }

                return Task.CompletedTask;
            };
            return (budget, delay, schedule);
        }

        /// <summary>
        ///   Nahil reuses <c>503</c> for two opposite answers, and the difference is only in the
        ///   body: a model being pulled onto a worker (wait) and a model no attached worker serves
        ///   (never). Measured live on 2026-09-09, the second arrives with NO <c>Retry-After</c>,
        ///   so before this it fell into the backoff and spent the caller's entire budget - about
        ///   ten minutes on the shipped Nahil chat profile - to end with "was not available in
        ///   time", when the very first response had said retrying would not help.
        ///
        ///   <para>So: no waits, one call, and the provider's own sentence in the message, because
        ///   it names the fix and nothing on this side of the request knows it.</para>
        /// </summary>
        [TestMethod]
        public async Task AModelNoWorkerServes_FailsOnTheFirstAnswer_CarryingNahilsOwnReason()
        {
            var stub = new StubHandler(_ => Retryable(HttpStatusCode.ServiceUnavailable, null, NoWorker));
            var bounded = Bounded();
            using var budget = bounded.Budget;
            var (handler, _) = Retrying(stub, logger: null, delay: bounded.Delay);

            using var client = new HttpClient(handler);
            var refused = await Assert.ThrowsExceptionAsync<NahilModelUnservableException>(
                () => client.GetAsync("https://api.nahil.dev/api/chat", budget.Token));

            Assert.AreEqual(1, stub.Calls, "a refusal must not be re-asked");
            Assert.AreEqual(0, bounded.Schedule.Count,
                "a refusal must not be paid for in the caller's budget");
            StringAssert.Contains(refused.Message, "phi4-f8-mini:latest", "the message names the model asked for");
            StringAssert.Contains(refused.Message, "no attached worker serves class S1",
                "the provider's sentence is quoted, because it names what has to change");
            StringAssert.Contains(refused.Message, "retrying will not help");
            Assert.IsFalse(refused.Message.Contains("{\"error\""),
                "the JSON envelope is unwrapped rather than dumped: " + refused.Message);
        }

        /// <summary>
        ///   The three ways a <c>503</c> is still a warm-up, which is what keeps this fix narrow.
        ///   The discriminator is deliberately the BODY and never the missing <c>Retry-After</c>: a
        ///   provider that omits the header is only declining to say how long, and reading that as
        ///   permanence would turn every ordinary warm-up into an immediate failure.
        /// </summary>
        [TestMethod]
        public async Task AWarmUpIsStillWaitedOut_WhateverItsBodySaysAndWhetherItHasOne()
        {
            foreach (var body in new[] { null, "", "{}", "{\"error\":\"pulling manifest\"}" })
            {
                var stub = new StubHandler(call => call == 1
                    ? (body == null
                        ? Retryable(HttpStatusCode.ServiceUnavailable, null)
                        : Retryable(HttpStatusCode.ServiceUnavailable, null, body))
                    : new HttpResponseMessage(HttpStatusCode.OK));
                var (handler, schedule) = Retrying(stub);

                using var client = new HttpClient(handler);
                using var response = await Get(client);

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                    "body '" + body + "' is not the documented refusal, so it must be waited out");
                Assert.AreEqual(2, stub.Calls, "body '" + body + "' must be retried");
                Assert.AreEqual(1, schedule.Count, "body '" + body + "' must cost exactly one wait");
            }
        }

        /// <summary>
        ///   The refusal is bound to the status as well as to the sentence. A <c>429</c> is a spent
        ///   quota, which refills, so the same words in that body must still be waited out; a status
        ///   that was never retryable still reaches the caller untouched and unread.
        /// </summary>
        [TestMethod]
        public async Task TheRefusalIsBoundToTheStatus_SoAQuotaWithTheSameWordsStillWaits()
        {
            var quota = new StubHandler(call => call == 1
                ? Retryable(HttpStatusCode.TooManyRequests, "4", NoWorker)
                : new HttpResponseMessage(HttpStatusCode.OK));
            var (handler, schedule) = Retrying(quota);

            using (var client = new HttpClient(handler))
            {
                using var response = await Get(client);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }

            CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(4) }, schedule,
                "a quota is a wait even when its body borrows the refusal's words");

            var notRetryable = new StubHandler(_ => Retryable(HttpStatusCode.BadRequest, null, NoWorker));
            var (other, otherSchedule) = Retrying(notRetryable);
            using var plain = new HttpClient(other);
            using var passed = await Get(plain);

            Assert.AreEqual(HttpStatusCode.BadRequest, passed.StatusCode,
                "a status this provider never retries is returned, not reclassified");
            Assert.AreEqual(1, notRetryable.Calls);
            Assert.AreEqual(0, otherSchedule.Count);
            Assert.AreEqual(NoWorker, await passed.Content.ReadAsStringAsync(),
                "and its body reaches the caller unread: the probe only touches responses bound for the bin");
        }

        /// <summary>
        ///   The body is read UP TO a bound, and this states the consequence rather than hiding it:
        ///   the sentence is found when it falls inside the bound and missed when it does not, in
        ///   which case the call is waited out exactly as an unrecognised body is. A bound is the
        ///   price of not letting a service on the far side of a credential decide how much memory
        ///   this process spends.
        /// </summary>
        [TestMethod]
        public async Task TheBodyIsClassifiedByABoundedPrefix_AndBeyondItTheCallIsMerelyWaitedOut()
        {
            // 2 KiB is the probe bound. Inside it the phrase is found even behind a lot of padding.
            var padding = new String('x', 1_500);
            var inside = "{\"pad\":\"" + padding + "\",\"error\":\"no attached worker serves class S1\"}";
            var insideStub = new StubHandler(_ => Retryable(HttpStatusCode.ServiceUnavailable, null, inside));
            var bounded = Bounded();
            using (var budget = bounded.Budget)
            {
                var (insideHandler, _) = Retrying(insideStub, logger: null, delay: bounded.Delay);
                using var client = new HttpClient(insideHandler);
                await Assert.ThrowsExceptionAsync<NahilModelUnservableException>(
                    () => client.GetAsync("https://api.nahil.dev/api/chat", budget.Token));
            }

            Assert.AreEqual(0, bounded.Schedule.Count);
            Assert.AreEqual(1, insideStub.Calls);

            // Pushed past the bound, the same sentence is invisible, and the honest fallback is the
            // wait this provider would have taken before it read anything at all.
            var beyond = "{\"pad\":\"" + new String('x', 4_000) + "\",\"error\":\"no attached worker serves class S1\"}";
            var beyondStub = new StubHandler(call => call == 1
                ? Retryable(HttpStatusCode.ServiceUnavailable, null, beyond)
                : new HttpResponseMessage(HttpStatusCode.OK));
            var (beyondHandler, beyondSchedule) = Retrying(beyondStub);
            using var beyondClient = new HttpClient(beyondHandler);
            using var response = await Get(beyondClient);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(1, beyondSchedule.Count, "an unreadable-in-time sentence degrades to a wait");
        }

        /// <summary>
        ///   Reading a body must not break the replay, which is the hazard this whole handler was
        ///   built around: a request message cannot be sent twice and a content stream read once
        ///   cannot be replayed. So a warm-up whose body IS read, followed by a refusal, must still
        ///   have sent the same bytes both times before it fails.
        /// </summary>
        [TestMethod]
        public async Task ReadingAWarmUpsBody_DoesNotCorruptTheReplayedRequest()
        {
            var stub = new StubHandler(call => call == 1
                ? Retryable(HttpStatusCode.ServiceUnavailable, "6", "{\"error\":\"pulling manifest\"}")
                : Retryable(HttpStatusCode.ServiceUnavailable, null, NoWorker));
            // Four allowed waits: one is the warm-up this test wants, and the rest exist only so a
            // regressed refusal ends as a spent budget rather than as a test that never returns.
            var bounded = Bounded(waitsBeforeGivingUp: 4);
            using var budget = bounded.Budget;
            var (handler, _) = Retrying(stub, logger: null, delay: bounded.Delay);

            using var client = new HttpClient(handler);
            var sent = "{\"model\":\"phi4-f8-mini:latest\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";
            await Assert.ThrowsExceptionAsync<NahilModelUnservableException>(
                () => client.PostAsync("https://api.nahil.dev/api/chat",
                    new StringContent(sent, System.Text.Encoding.UTF8, "application/json"), budget.Token));

            Assert.AreEqual(2, stub.Calls);
            CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(6) }, bounded.Schedule,
                "the warm-up was waited out; only the second answer was a refusal");
            CollectionAssert.AreEqual(new[] { sent, sent }, stub.Bodies,
                "the replayed request carried the same bytes, so reading the first response's body cost nothing");
            CollectionAssert.AreEqual(new[] { "application/json; charset=utf-8", "application/json; charset=utf-8" },
                stub.ContentTypes, "and the same content type");
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
