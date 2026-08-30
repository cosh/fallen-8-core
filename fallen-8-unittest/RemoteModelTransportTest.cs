// MIT License
//
// RemoteModelTransportTest.cs
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
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Chat;
using NoSQL.GraphDB.App.Helper;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The transport for the OpenAI and Anthropic backends (feature model-providers).
    ///
    ///   <para>Neither provider is under test here. What is under test is the five-point transport
    ///   contract we hold each SDK to, because every point of it has failed silently before: ONE
    ///   deadline (the caller's), NO retry of the SDK's own, our retry in OUR chain rather than in an
    ///   SDK's handler list, the credential reaching every attempt and no log line, and the
    ///   provider's own answer to which statuses mean "ask again".</para>
    ///
    ///   <para>The wait schedule is asserted through <see cref="RemoteModelRetryHandler" /> with the
    ///   delay injected, so a "waits ten seconds" case finishes in microseconds. The two end-to-end
    ///   retry cases go through the real SDK, which owns the delay, so they ask for the smallest
    ///   honourable wait there is (one second) - that is the price of proving that an SDK-built
    ///   request survives being replayed at all.</para>
    /// </summary>
    [TestClass]
    public class RemoteModelTransportTest
    {
        #region the credential

        /// <summary>
        ///   Each SDK attaches its own credential, in its own shape, and neither leaves it to a call
        ///   site: OpenAI a bearer token, Anthropic <c>x-api-key</c> plus the API version it pins.
        ///   Asserted on the wire rather than on the options object, because "we set the property" is
        ///   not the same claim as "the header arrived".
        ///   <para>The REQUEST URL is asserted here for the same reason, and it is the asymmetry that
        ///   makes it worth doing: the OpenAI options carry the <c>/v1</c> we add, while Anthropic's
        ///   <c>BaseUrl</c> is the bare host root and its SDK appends <c>/v1/messages</c> itself.
        ///   "Harmonizing" the two - the natural-looking edit - sends every Anthropic request to
        ///   <c>/v1/v1/messages</c>, which a stub that answers any URI cannot notice.</para>
        /// </summary>
        [TestMethod]
        public async Task EachProviderAttachesItsOwnCredentialShape_OnTheWire()
        {
            var openAi = new RecordingHandler(_ => RemoteModelWire.Json(RemoteModelWire.OpenAICompletion()));
            using (var backend = new OpenAIChatBackend(RemoteModelWire.OpenAITarget(), stream: false,
                logger: null, openAi))
            {
                await backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None);
            }

            Assert.AreEqual("Bearer " + RemoteModelWire.OpenAIKey, openAi.Headers.Single()["Authorization"]);
            Assert.AreEqual(RemoteModelWire.OpenAIHost + "/v1/chat/completions", openAi.Uris.Single(),
                "the configured host root plus the /v1 we add plus the SDK's own route suffix");

            var anthropic = new RecordingHandler(_ => RemoteModelWire.Json(RemoteModelWire.AnthropicMessage()));
            using (var backend = new AnthropicChatBackend(RemoteModelWire.AnthropicTarget(), maxTokens: 512,
                stream: false, logger: null, anthropic))
            {
                await backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None);
            }

            var sent = anthropic.Headers.Single();
            Assert.AreEqual(RemoteModelWire.AnthropicKey, sent["x-api-key"],
                "Anthropic authenticates with x-api-key, not with a bearer token");
            Assert.IsFalse(sent.ContainsKey("Authorization"),
                "sending both would hand the credential to a header nothing reads");
            Assert.IsTrue(sent.TryGetValue("anthropic-version", out var version) && version.Length > 0,
                "the Messages API requires a pinned version on every request");
            Assert.AreEqual(RemoteModelWire.AnthropicHost + "/v1/messages", anthropic.Uris.Single(),
                "the configured host root, with the whole route the SDK's own - no /v1 of ours");
        }

        /// <summary>
        ///   A retried request is REPLAYED faithfully - same body, same credential - through the real
        ///   SDK. This is the case shape B fails: putting our retry in the Anthropic SDK's own handler
        ///   list makes the second attempt throw "the request message was already sent", because that
        ///   list re-sends the same message and HttpClient refuses one twice.
        /// </summary>
        [TestMethod]
        public async Task ARetriedRequest_ReplaysItsBodyAndItsCredential_ThroughTheRealSdk()
        {
            var openAi = new RecordingHandler(call => call == 1
                ? RemoteModelWire.Status(HttpStatusCode.TooManyRequests, "1")
                : RemoteModelWire.Json(RemoteModelWire.OpenAICompletion()));
            using (var backend = new OpenAIChatBackend(RemoteModelWire.OpenAITarget(), stream: false,
                logger: null, openAi))
            {
                var result = await backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None);
                Assert.AreEqual("hello", result.Content);
            }

            Assert.AreEqual(2, openAi.Calls);
            Assert.AreEqual(1, openAi.Bodies.Distinct().Count(), "every attempt replays the same body");
            Assert.IsTrue(openAi.Bodies.All(body => body.Contains(RemoteModelWire.OpenAIModel)));
            Assert.IsTrue(openAi.Headers.All(h => h["Authorization"] == "Bearer " + RemoteModelWire.OpenAIKey),
                "an attempt that lost the credential would answer 401 and read as the retry failing on merit");

            var anthropic = new RecordingHandler(call => call == 1
                ? RemoteModelWire.Status(HttpStatusCode.TooManyRequests, "1")
                : RemoteModelWire.Json(RemoteModelWire.AnthropicMessage()));
            using (var backend = new AnthropicChatBackend(RemoteModelWire.AnthropicTarget(), maxTokens: 512,
                stream: false, logger: null, anthropic))
            {
                var result = await backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None);
                Assert.AreEqual("hello", result.Content);
            }

            Assert.AreEqual(2, anthropic.Calls);
            Assert.AreEqual(1, anthropic.Bodies.Distinct().Count());
            Assert.IsTrue(anthropic.Headers.All(h => h["x-api-key"] == RemoteModelWire.AnthropicKey));
        }

        #endregion

        #region the single deadline and the absent SDK retry

        /// <summary>
        ///   The transport carries NO deadline of its own. Two deadlines is the bug this rule exists to
        ///   prevent: either the SDK's own or HttpClient's undocumented 100s default firing raises a
        ///   TaskCanceledException the provider never asked for, which shipped once as an HTTP 500.
        ///
        ///   <para>BOTH halves are asserted, because each has its own default to hand back and a
        ///   deployment only fails on the one that was missed. The SDK half is read off the setting
        ///   rather than timed: its defaults are 100 seconds (OpenAI) and ten minutes (Anthropic), so a
        ///   test that waits for one to fire either takes that long or proves nothing. The OpenAI half
        ///   is asserted on the composition every OpenAI-protocol client is built from - the chat
        ///   backend AND the embedding generator - so neither can keep the default alone.</para>
        /// </summary>
        [TestMethod]
        public void TheTransport_CarriesNoDeadlineOfItsOwn()
        {
            using var openAi = new OpenAIChatBackend(RemoteModelWire.OpenAITarget(), stream: true, logger: null,
                new RecordingHandler(_ => RemoteModelWire.Json(RemoteModelWire.OpenAICompletion())));
            Assert.AreEqual(Timeout.InfiniteTimeSpan, TransportOf(openAi).Timeout,
                "two deadlines is the bug the single-deadline rule exists to prevent");

            using var anthropic = new AnthropicChatBackend(RemoteModelWire.AnthropicTarget(), maxTokens: 512,
                stream: true, logger: null,
                new RecordingHandler(_ => RemoteModelWire.Json(RemoteModelWire.AnthropicMessage())));
            Assert.AreEqual(Timeout.InfiniteTimeSpan, TransportOf(anthropic).Timeout,
                "two deadlines is the bug the single-deadline rule exists to prevent");

            Assert.AreEqual(Timeout.InfiniteTimeSpan, OpenAINetworkTimeout(),
                "System.ClientModel's own 100s network deadline is the one that shipped as a 500");

            var client = ClientOf(anthropic);
            Assert.AreEqual(Timeout.InfiniteTimeSpan,
                client.GetType().GetProperty("Timeout").GetValue(client),
                "the Anthropic SDK's own deadline defaults to ten minutes and pre-empts the caller's budget");
            Assert.AreEqual(0, client.GetType().GetProperty("MaxRetries").GetValue(client),
                "an SDK retrying underneath us multiplies metered spend behind the operator's back");
        }

        /// <summary>
        ///   The caller's token is the only thing that cancels: it reaches the transport, and what
        ///   comes back out is a cancellation rather than a fault report blaming the backend.
        /// </summary>
        [TestMethod]
        public async Task TheCallersToken_IsTheOnlyThingThatCancels()
        {
            var stub = new RecordingHandler(async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return RemoteModelWire.Json(RemoteModelWire.OpenAICompletion());
            });

            using var backend = new OpenAIChatBackend(RemoteModelWire.OpenAITarget(), stream: false,
                logger: null, stub);
            using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

            await RemoteModelWire.AssertCancelled(
                () => backend.ChatAsync(RemoteModelWire.Turns(), null, budget.Token),
                "a request the caller abandoned is a cancellation, not a bad gateway");
        }

        /// <summary>
        ///   A status we do NOT retry produces exactly ONE transport attempt. Not theoretical: the
        ///   OpenAI SDK's default policy makes four attempts against one failure and logs nothing,
        ///   which on a metered provider is spend the operator never asked for.
        /// </summary>
        [TestMethod]
        public async Task AnSdkRetry_IsOff_SoOneRequestIsOneAttempt()
        {
            var openAi = new RecordingHandler(_ => RemoteModelWire.Status(HttpStatusCode.ServiceUnavailable));
            using (var backend = new OpenAIChatBackend(RemoteModelWire.OpenAITarget(), stream: false,
                logger: null, openAi))
            {
                await Assert.ThrowsExceptionAsync<HttpRequestException>(
                    () => backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None));
            }

            Assert.AreEqual(1, openAi.Calls, "the SDK's own retry policy would have made four");

            // 529 is Anthropic's overloaded status and is NOT OpenAI's, so this provider must not
            // wait it out either: the retryable set is per provider, not a shared guess.
            var overloaded = new RecordingHandler(_ => RemoteModelWire.Status((HttpStatusCode)529, "1"));
            using (var backend = new OpenAIChatBackend(RemoteModelWire.OpenAITarget(), stream: false,
                logger: null, overloaded))
            {
                await Assert.ThrowsExceptionAsync<HttpRequestException>(
                    () => backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None));
            }

            Assert.AreEqual(1, overloaded.Calls);

            var anthropic = new RecordingHandler(_ => RemoteModelWire.Status(HttpStatusCode.ServiceUnavailable, "1"));
            using (var backend = new AnthropicChatBackend(RemoteModelWire.AnthropicTarget(), maxTokens: 512,
                stream: false, logger: null, anthropic))
            {
                await Assert.ThrowsExceptionAsync<HttpRequestException>(
                    () => backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None));
            }

            Assert.AreEqual(1, anthropic.Calls,
                "503-while-warming is Nahil's contract; on Anthropic it is an honest failure");
        }

        /// <summary>
        ///   Anthropic's non-standard <c>529</c> IS waited out, which is the whole reason its
        ///   retryable set is not OpenAI's.
        /// </summary>
        [TestMethod]
        public async Task AnthropicsOverloadedStatus_IsWaitedOut()
        {
            var stub = new RecordingHandler(call => call == 1
                ? RemoteModelWire.Status((HttpStatusCode)529, "1")
                : RemoteModelWire.Json(RemoteModelWire.AnthropicMessage()));

            using var backend = new AnthropicChatBackend(RemoteModelWire.AnthropicTarget(), maxTokens: 512,
                stream: false, logger: null, stub);
            var result = await backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None);

            Assert.AreEqual("hello", result.Content);
            Assert.AreEqual(2, stub.Calls);
        }

        #endregion

        #region the wait schedule, bounded by the caller's budget

        /// <summary>
        ///   A 429 is waited out for as long as the provider asked, once per retry in the log, and a
        ///   400 is not waited at all. The schedule is asserted as the waits it COMPUTED rather than
        ///   by spending them.
        /// </summary>
        [TestMethod]
        public async Task A429IsWaitedOutAndLoggedOncePerRetry_WhileA400FailsAtOnce()
        {
            var sink = new TestLogSink();
            var limited = new RecordingHandler(call => call <= 2
                ? RemoteModelWire.Status(HttpStatusCode.TooManyRequests, "5")
                : new HttpResponseMessage(HttpStatusCode.OK));
            var (handler, schedule) = Retrying(limited, RemoteModelWire.OpenAIRetryable,
                sink.CreateFactory().CreateLogger("openai"));

            using (var client = new HttpClient(handler))
            {
                using var response = await client.GetAsync(RemoteModelWire.OpenAIHost + "/v1/chat/completions");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }

            CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5) }, schedule,
                "the provider's own Retry-After is honoured rather than second-guessed");
            Assert.AreEqual(2, sink.Entries.Count(e => e.Level == LogLevel.Information),
                "one line per retry, no per-poll spam: "
                + String.Join(" | ", sink.Entries.Select(e => e.Message)));
            Assert.IsTrue(sink.Contains(LogLevel.Information, "OpenAI", "429", "rate limited",
                RemoteModelWire.OpenAIModel));

            foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized })
            {
                var stub = new RecordingHandler(_ => RemoteModelWire.Status(status, "5"));
                var (other, otherSchedule) = Retrying(stub, RemoteModelWire.OpenAIRetryable);
                using var client = new HttpClient(other);
                using var response = await client.GetAsync(RemoteModelWire.OpenAIHost + "/v1/chat/completions");

                Assert.AreEqual(status, response.StatusCode);
                Assert.AreEqual(1, stub.Calls, status + " must not be retried");
                Assert.AreEqual(0, otherSchedule.Count, status + " must not wait");
            }
        }

        /// <summary>
        ///   The retry is bounded by the CALLER's budget and by nothing else - there is no second
        ///   deadline and no attempt cap - and when that budget runs out mid-wait the failure names
        ///   the provider, the status it kept answering, the model and how long we waited.
        ///
        ///   <para>It must also SURVIVE HttpClient, which is why it is not an
        ///   OperationCanceledException: HttpClient replaces any cancellation leaving its handler
        ///   chain with a TaskCanceledException of its own, so a subclass would be silently
        ///   discarded.</para>
        /// </summary>
        [TestMethod]
        public async Task WhenTheBudgetRunsOutMidWait_TheFailureNamesTheProviderAndSurvivesHttpClient()
        {
            Assert.IsFalse(
                typeof(OperationCanceledException).IsAssignableFrom(typeof(RemoteModelRetryTimeoutException)),
                "deriving from OperationCanceledException is what HttpClient throws away");

            using var budget = new CancellationTokenSource();
            var waits = 0;
            var stub = new RecordingHandler(_ => RemoteModelWire.Status((HttpStatusCode)529, "7"));
            var (handler, _) = Retrying(stub, RemoteModelWire.AnthropicRetryable, logger: null,
                model: RemoteModelWire.AnthropicModel, provider: "Anthropic", delay: (wait, token) =>
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
            var gaveUp = await Assert.ThrowsExceptionAsync<RemoteModelRetryTimeoutException>(
                () => client.GetAsync(RemoteModelWire.AnthropicHost + "/v1/messages", budget.Token));

            StringAssert.Contains(gaveUp.Message, "Anthropic answered 529 (overloaded)");
            StringAssert.Contains(gaveUp.Message, RemoteModelWire.AnthropicModel);
            StringAssert.Contains(gaveUp.Message, "14s", "two completed 7s waits is 14s of waiting");
            Assert.IsInstanceOfType(gaveUp.InnerException, typeof(OperationCanceledException),
                "the cancellation is kept so a provider can tell a caller walking away from a spent budget");
            Assert.IsInstanceOfType(gaveUp, typeof(ModelRetryTimeoutException),
                "both providers' give-ups share one base, or the chat provider's 504 mapping would miss one");
        }

        /// <summary>
        ///   A hostile or unusable <c>Retry-After</c> neither parks a request nor hot-loops it. The
        ///   arithmetic is shared with Nahil's warm-up, so this pins that the generalization did not
        ///   change it.
        /// </summary>
        [TestMethod]
        public void AnUnusableRetryAfter_NeitherHotLoopsNorParksTheRequest()
        {
            foreach (var value in new[] { null, "soon", "0", "-1", "" })
            {
                using var response = RemoteModelWire.Status(HttpStatusCode.TooManyRequests, value);
                var wait = RemoteModelRetryHandler.WaitFor(response.Headers.RetryAfter, attempt: 1);
                Assert.IsTrue(wait >= TimeSpan.FromSeconds(RemoteModelRetryHandler.FirstBackoffSeconds)
                    && wait <= TimeSpan.FromSeconds(RemoteModelRetryHandler.MaxBackoffSeconds),
                    "'" + value + "' must fall back to the backoff, not to an immediate retry; got " + wait);
            }

            using var stale = RemoteModelWire.Status(HttpStatusCode.TooManyRequests,
                DateTimeOffset.UtcNow.AddMinutes(-5).ToString("r", CultureInfo.InvariantCulture));
            Assert.IsTrue(RemoteModelRetryHandler.WaitFor(stale.Headers.RetryAfter, attempt: 1)
                >= TimeSpan.FromSeconds(RemoteModelRetryHandler.FirstBackoffSeconds));

            using var hostile = RemoteModelWire.Status(HttpStatusCode.TooManyRequests, "86400");
            Assert.AreEqual(TimeSpan.FromSeconds(RemoteModelRetryHandler.MaxWaitSeconds),
                RemoteModelRetryHandler.WaitFor(hostile.Headers.RetryAfter, attempt: 1),
                "one hostile value must not park a request for a day");
        }

        /// <summary>
        ///   The give-up survives the SDK too, not just HttpClient - which is a separate claim, because
        ///   each SDK sits ABOVE our handler chain and each of them converts a transport fault into an
        ///   exception of its own. If it did not arrive intact, the chat provider's 504 branch would
        ///   never match for these two backends and a spent retry budget would report a 503 inviting
        ///   the identical retry that just failed.
        /// </summary>
        [TestMethod]
        public async Task TheGiveUp_ReachesTheCallerThroughEachSdk_NotRewrittenAsATransportFault()
        {
            var openAi = new RecordingHandler(_ => RemoteModelWire.Status(HttpStatusCode.TooManyRequests, "30"));
            using (var backend = new OpenAIChatBackend(RemoteModelWire.OpenAITarget(), stream: false,
                logger: null, openAi))
            using (var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
            {
                var gaveUp = await Assert.ThrowsExceptionAsync<RemoteModelRetryTimeoutException>(
                    () => backend.ChatAsync(RemoteModelWire.Turns(), null, budget.Token));
                StringAssert.Contains(gaveUp.Message, "OpenAI answered 429 (rate limited)");
            }

            var anthropic = new RecordingHandler(_ => RemoteModelWire.Status((HttpStatusCode)529, "30"));
            using (var backend = new AnthropicChatBackend(RemoteModelWire.AnthropicTarget(), maxTokens: 512,
                stream: true, logger: null, anthropic))
            using (var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
            {
                var gaveUp = await Assert.ThrowsExceptionAsync<RemoteModelRetryTimeoutException>(
                    () => backend.ChatAsync(RemoteModelWire.Turns(), null, budget.Token));
                StringAssert.Contains(gaveUp.Message, "Anthropic answered 529 (overloaded)");
            }
        }

        #endregion

        #region redaction

        /// <summary>
        ///   Neither the credential nor the endpoint appears in a log line or in any message a caller
        ///   can see. The endpoint matters as much as the key: a URL can carry an embedded credential,
        ///   and each SDK's own exception message embeds the raw response body verbatim - which is why
        ///   none of them is ever forwarded.
        /// </summary>
        [TestMethod]
        public async Task NeitherTheCredentialNorTheEndpoint_ReachesALogLineOrAMessage()
        {
            var sink = new TestLogSink();
            var messages = new List<String>();

            foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests })
            {
                var openAi = new RecordingHandler(_ => RemoteModelWire.Leaky(status, RemoteModelWire.OpenAIKey));
                using (var backend = new OpenAIChatBackend(RemoteModelWire.OpenAITarget(), stream: true,
                    logger: sink.CreateFactory().CreateLogger("openai"), openAi))
                using (var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
                {
                    messages.Add(await FailureMessage(backend, budget.Token));
                }

                var anthropic = new RecordingHandler(_ => RemoteModelWire.Leaky(status, RemoteModelWire.AnthropicKey));
                using (var backend = new AnthropicChatBackend(RemoteModelWire.AnthropicTarget(), maxTokens: 512,
                    stream: true, logger: sink.CreateFactory().CreateLogger("anthropic"), anthropic))
                using (var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
                {
                    messages.Add(await FailureMessage(backend, budget.Token));
                }
            }

            Assert.AreEqual(4, messages.Count);
            foreach (var message in messages)
            {
                Assert.IsFalse(message.Contains(RemoteModelWire.OpenAIKey, StringComparison.Ordinal),
                    "the credential must appear in no message: " + message);
                Assert.IsFalse(message.Contains(RemoteModelWire.AnthropicKey, StringComparison.Ordinal),
                    "the credential must appear in no message: " + message);
                Assert.IsFalse(message.Contains("openai.invalid", StringComparison.Ordinal),
                    "no message quotes the endpoint, because a URL can carry a credential: " + message);
                Assert.IsFalse(message.Contains("anthropic.invalid", StringComparison.Ordinal),
                    "no message quotes the endpoint, because a URL can carry a credential: " + message);
            }

            foreach (var entry in sink.Entries)
            {
                Assert.IsFalse(entry.Message?.Contains(RemoteModelWire.OpenAIKey, StringComparison.Ordinal) == true,
                    "the credential must appear in no log line: " + entry.Message);
                Assert.IsFalse(entry.Message?.Contains(RemoteModelWire.AnthropicKey, StringComparison.Ordinal) == true,
                    "the credential must appear in no log line: " + entry.Message);
            }
        }

        /// <summary>Runs a call that is expected to fail and returns the message a caller would see.</summary>
        private static async Task<String> FailureMessage(IChatBackend backend, CancellationToken cancellationToken)
        {
            try
            {
                var result = await backend.ChatAsync(RemoteModelWire.Turns(), null, cancellationToken);
                Assert.Fail("expected a failure, got " + result.Content);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        #endregion

        private static HttpClient TransportOf(Object backend)
        {
            var field = backend.GetType().GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field,
                "the backend's owned transport is what carries (or does not carry) the deadline");
            return (HttpClient)field.GetValue(backend);
        }

        /// <summary>The SDK client the backend built, so its own budget can be read back off it.</summary>
        private static Object ClientOf(Object backend)
        {
            var field = backend.GetType().GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "the SDK client carries the SDK's own deadline and retry count");
            return field.GetValue(backend);
        }

        /// <summary>
        ///   The network deadline on the options every OpenAI-protocol client is composed from. Reached
        ///   by reflection because <c>RemoteModelHttpClient</c> is internal and the repository adds no
        ///   <c>InternalsVisibleTo</c>; the alternative is reading it back off the SDK client, whose
        ///   endpoint and options accessors are <c>[Experimental]</c> and therefore build errors here.
        /// </summary>
        private static TimeSpan? OpenAINetworkTimeout()
        {
            var composition = typeof(RemoteModelTarget).Assembly
                .GetType("NoSQL.GraphDB.App.Helper.RemoteModelHttpClient");
            Assert.IsNotNull(composition, "the one home of the OpenAI client composition");

            var build = composition.GetMethod("OpenAIOptions",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(build, "OpenAIOptions is what both OpenAI-protocol clients are built from");

            using var transport = new HttpClient();
            var options = build.Invoke(null, new Object[] { RemoteModelWire.OpenAITarget(), transport });
            return (TimeSpan?)options.GetType().GetProperty("NetworkTimeout").GetValue(options);
        }

        /// <summary>The retry under test, with its waits captured instead of taken.</summary>
        private static (RemoteModelRetryHandler Handler, List<TimeSpan> Schedule) Retrying(
            HttpMessageHandler inner, IReadOnlyCollection<HttpStatusCode> retryable, ILogger logger = null,
            String model = RemoteModelWire.OpenAIModel, String provider = "OpenAI",
            Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            var schedule = new List<TimeSpan>();
            var handler = new RemoteModelRetryHandler(provider, model, logger, retryable,
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
    }

    /// <summary>
    ///   The canned provider wire, shared by the three model-provider suites
    ///   (<see cref="RemoteModelTransportTest" />, <see cref="OpenAIChatBackendTest" />,
    ///   <see cref="AnthropicChatBackendTest" />) so one answer shape has one spelling.
    /// </summary>
    internal static class RemoteModelWire
    {
        internal const String OpenAIKey = "sk-openai-test-credential";
        internal const String AnthropicKey = "anthropic-test-credential";
        internal const String OpenAIHost = "https://api.openai.invalid";
        internal const String AnthropicHost = "https://api.anthropic.invalid";
        internal const String OpenAIModel = "gpt-4o-mini";
        internal const String AnthropicModel = "claude-opus-5";

        /// <summary>The retryable sets, for driving the retry handler's arithmetic directly. That each
        /// BACKEND really carries these is pinned behaviourally instead, by the attempt counts in
        /// <see cref="RemoteModelTransportTest" />: a set copied here could otherwise agree with itself
        /// while the shipped one was wrong.</summary>
        internal static readonly HttpStatusCode[] OpenAIRetryable = { HttpStatusCode.TooManyRequests };

        internal static readonly HttpStatusCode[] AnthropicRetryable =
        {
            HttpStatusCode.TooManyRequests, (HttpStatusCode)529
        };

        internal static RemoteModelTarget OpenAITarget(String model = OpenAIModel, String apiKey = OpenAIKey)
        {
            return RemoteModelTarget.OpenAI("Fallen8:Chat:OpenAI", OpenAIHost, model, apiKey);
        }

        internal static RemoteModelTarget AnthropicTarget(String model = AnthropicModel, String apiKey = AnthropicKey)
        {
            return RemoteModelTarget.Anthropic("Fallen8:Chat:Anthropic", AnthropicHost, model, apiKey);
        }

        internal static IReadOnlyList<ChatTurn> Turns(String system = null)
        {
            return system == null
                ? new[] { new ChatTurn("user", "draft a vertex filter for label person") }
                : new[] { new ChatTurn("system", system), new ChatTurn("user", "draft a vertex filter") };
        }

        internal static HttpResponseMessage Json(String body)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        /// <summary>
        ///   An event stream. Deliberately NOT a <see cref="StringContent" />: that is seekable, and a
        ///   seekable body an SDK has already read makes it refuse to buffer on dispose - a fault a
        ///   real network stream cannot produce, and one that would otherwise look like a truncation
        ///   in every streaming test here.
        /// </summary>
        internal static HttpResponseMessage Sse(String body)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ForwardOnlyStream(Encoding.UTF8.GetBytes(body)))
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return response;
        }

        /// <summary>A response body that dies part-way, the way an aborted stream does.</summary>
        internal static HttpResponseMessage DyingSse(String body)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new DyingStream(Encoding.UTF8.GetBytes(body)))
            };
        }

        internal static HttpResponseMessage Status(HttpStatusCode status, String retryAfter = null)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent("{\"error\":{\"message\":\"no\",\"type\":\"test_error\"}}",
                    Encoding.UTF8, "application/json")
            };

            if (retryAfter != null)
            {
                // TryAddWithoutValidation so a test can send a value the typed accessor REFUSES,
                // which is the "unparseable falls back to backoff" case.
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            }

            return response;
        }

        /// <summary>An error answer that echoes the credential back, which is exactly what an SDK's own
        /// exception message embeds verbatim.</summary>
        internal static HttpResponseMessage Leaky(HttpStatusCode status, String credential)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    "{\"error\":{\"message\":\"bad key " + credential + "\",\"type\":\"authentication_error\"}}",
                    Encoding.UTF8, "application/json")
            };
        }

        internal static String OpenAICompletion(String text = "hello", String finishReason = "stop",
            Boolean usage = true)
        {
            return "{\"id\":\"chatcmpl-test\",\"object\":\"chat.completion\",\"created\":1700000000,"
                + "\"model\":\"" + OpenAIModel + "\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\","
                + "\"content\":\"" + text + "\"},\"finish_reason\":" + Quoted(finishReason) + "}]"
                + (usage ? ",\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":5,\"total_tokens\":16}" : "")
                + "}";
        }

        internal static String OpenAIChunk(String text, String finishReason)
        {
            return "{\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,"
                + "\"model\":\"" + OpenAIModel + "\",\"choices\":[{\"index\":0,\"delta\":"
                + (text == null ? "{}" : "{\"content\":\"" + text + "\"}")
                + ",\"finish_reason\":" + Quoted(finishReason) + "}]}";
        }

        internal static String OpenAIUsageChunk(Int32 promptTokens = 11, Int32 completionTokens = 5)
        {
            return "{\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,"
                + "\"model\":\"" + OpenAIModel + "\",\"choices\":[],\"usage\":{\"prompt_tokens\":"
                + promptTokens + ",\"completion_tokens\":" + completionTokens + ",\"total_tokens\":"
                + (promptTokens + completionTokens) + "}}";
        }

        /// <summary>Wraps JSON frames as <c>data:</c> events. The <c>[DONE]</c> sentinel is added by
        /// <see cref="Done" /> only where a test wants a stream that really ended.</summary>
        internal static String Data(params String[] frames)
        {
            var sse = new StringBuilder();
            foreach (var frame in frames)
            {
                sse.Append("data: ").Append(frame).Append("\n\n");
            }

            return sse.ToString();
        }

        internal static String Done()
        {
            return "data: [DONE]\n\n";
        }

        internal static String OpenAIStream(String text = "hello", String finishReason = "stop",
            Boolean usage = true)
        {
            var frames = new List<String> { OpenAIChunk(text, null), OpenAIChunk(null, finishReason) };
            if (usage)
            {
                frames.Add(OpenAIUsageChunk());
            }

            return Data(frames.ToArray()) + Done();
        }

        internal static String AnthropicMessage(String text = "hello", String stopReason = "end_turn",
            String refusalCategory = null, Int32 inputTokens = 11, Int32 outputTokens = 5)
        {
            return "{\"id\":\"msg_test\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\""
                + AnthropicModel + "\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}],"
                + "\"stop_reason\":" + Quoted(stopReason) + ",\"stop_sequence\":null,"
                + Refusal(refusalCategory)
                + "\"usage\":{\"input_tokens\":" + inputTokens + ",\"output_tokens\":" + outputTokens + "}}";
        }

        internal static String AnthropicStart(Int32 inputTokens = 11)
        {
            return "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_test\","
                + "\"type\":\"message\",\"role\":\"assistant\",\"model\":\"" + AnthropicModel + "\","
                + "\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":"
                + inputTokens + ",\"output_tokens\":0}}}\n\n";
        }

        internal static String AnthropicBlockStart()
        {
            return "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,"
                + "\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n";
        }

        internal static String AnthropicTextDelta(String text)
        {
            return "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,"
                + "\"delta\":{\"type\":\"text_delta\",\"text\":\"" + text + "\"}}\n\n";
        }

        internal static String AnthropicBlockStop()
        {
            return "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n";
        }

        internal static String AnthropicMessageDelta(Int32 outputTokens = 5, String stopReason = "end_turn",
            String refusalCategory = null)
        {
            return "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":"
                + Quoted(stopReason) + "," + Refusal(refusalCategory)
                + "\"stop_sequence\":null},\"usage\":{\"output_tokens\":" + outputTokens + "}}\n\n";
        }

        internal static String AnthropicStop()
        {
            return "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
        }

        internal static String AnthropicStream(String text = "hello", Boolean stop = true,
            Boolean messageDelta = true, String refusalCategory = null, String stopReason = "end_turn")
        {
            var sse = new StringBuilder()
                .Append(AnthropicStart())
                .Append(AnthropicBlockStart())
                .Append(AnthropicTextDelta(text))
                .Append(AnthropicBlockStop());

            if (messageDelta)
            {
                sse.Append(AnthropicMessageDelta(stopReason: stopReason, refusalCategory: refusalCategory));
            }

            if (stop)
            {
                sse.Append(AnthropicStop());
            }

            return sse.ToString();
        }

        private static String Refusal(String category)
        {
            return category == null
                ? String.Empty
                : "\"stop_details\":{\"type\":\"refusal\",\"category\":\"" + category
                    + "\",\"explanation\":\"declined by policy\"},";
        }

        private static String Quoted(String value)
        {
            return value == null ? "null" : "\"" + value + "\"";
        }

        /// <summary>
        ///   Asserts that <paramref name="call" /> was CANCELLED, whichever cancellation subclass the
        ///   runtime picked. Both SDKs surface a <see cref="TaskCanceledException" /> here, and the
        ///   claim under test is the one the chat provider's catch filters make: a cancellation, not a
        ///   truncated response.
        /// </summary>
        internal static async Task AssertCancelled(Func<Task> call, String because)
        {
            try
            {
                await call();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception other)
            {
                Assert.Fail(because + " - got " + other.GetType().Name + ": " + other.Message);
            }

            Assert.Fail(because + " - the call completed instead");
        }
    }

    /// <summary>A transport whose answers a test controls entirely, recording what was asked.</summary>
    internal sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<Int32, CancellationToken, Task<HttpResponseMessage>> _respond;

        internal RecordingHandler(Func<Int32, HttpResponseMessage> respond)
            : this((call, _) => Task.FromResult(respond(call)))
        {
        }

        internal RecordingHandler(Func<Int32, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        internal List<String> Bodies { get; } = new List<String>();

        internal List<Dictionary<String, String>> Headers { get; } = new List<Dictionary<String, String>>();

        internal List<String> Uris { get; } = new List<String>();

        internal Int32 Calls
        {
            get; private set;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Bodies.Add(request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken));
            Uris.Add(request.RequestUri?.ToString());

            var headers = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
            {
                headers[header.Key] = String.Join(",", header.Value);
            }

            Headers.Add(headers);
            return await _respond(Calls, cancellationToken);
        }
    }

    /// <summary>Yields its bytes once, forward only, the way a network response body does.</summary>
    internal sealed class ForwardOnlyStream : System.IO.Stream
    {
        private readonly Byte[] _payload;
        private Int32 _position;

        internal ForwardOnlyStream(Byte[] payload)
        {
            _payload = payload;
        }

        public override Int32 Read(Byte[] buffer, Int32 offset, Int32 count)
        {
            var take = Math.Min(count, _payload.Length - _position);
            Array.Copy(_payload, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override Boolean CanRead => true;

        public override Boolean CanSeek => false;

        public override Boolean CanWrite => false;

        public override Int64 Length => throw new NotSupportedException();

        public override Int64 Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Int64 Seek(Int64 offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(Int64 value) => throw new NotSupportedException();

        public override void Write(Byte[] buffer, Int32 offset, Int32 count) => throw new NotSupportedException();
    }

    /// <summary>Yields its bytes and then faults, the way an aborted response body does.</summary>
    internal sealed class DyingStream : System.IO.Stream
    {
        private readonly Byte[] _payload;
        private Int32 _position;

        internal DyingStream(Byte[] payload)
        {
            _payload = payload;
        }

        public override Int32 Read(Byte[] buffer, Int32 offset, Int32 count)
        {
            if (_position >= _payload.Length)
            {
                throw new System.IO.IOException("the response body was aborted");
            }

            var take = Math.Min(count, _payload.Length - _position);
            Array.Copy(_payload, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override Boolean CanRead => true;

        public override Boolean CanSeek => false;

        public override Boolean CanWrite => false;

        public override Int64 Length => throw new NotSupportedException();

        public override Int64 Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Int64 Seek(Int64 offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(Int64 value) => throw new NotSupportedException();

        public override void Write(Byte[] buffer, Int32 offset, Int32 count) => throw new NotSupportedException();
    }
}
