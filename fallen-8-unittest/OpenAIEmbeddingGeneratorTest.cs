// MIT License
//
// OpenAIEmbeddingGeneratorTest.cs
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
using System.ClientModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Embedding;
using NoSQL.GraphDB.App.Helper;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The OpenAI embedding transport (feature model-providers, FR-4), driven through the
    ///   generator's <c>handler</c> seam so every assertion is about the real client composition -
    ///   the SDK's request body, its route, its credential header and its error mapping - rather
    ///   than about a stand-in.
    ///
    ///   <para>The first test is the reason this backend has a hand-written adapter at all. A
    ///   response whose <c>index</c> fields are permuted is indistinguishable from a correct one by
    ///   every downstream check: the vectors are the right width, the count matches, nothing throws
    ///   and nothing logs. The only visible symptom is semantic search returning confident answers
    ///   about the wrong elements, weeks later.</para>
    ///
    ///   <para>No test here spends real time or reaches a network. The one cancellation case uses a
    ///   handler that honours the token, so it finishes as soon as the caller's budget trips.</para>
    /// </summary>
    [TestClass]
    public class OpenAIEmbeddingGeneratorTest
    {
        private const String Key = "sk-openai-secret-key";
        private const String Endpoint = "https://models.secret.example";
        private const String Model = "text-embedding-3-small";

        #region the seam

        /// <summary>A handler whose responses (and recorded requests) a test controls entirely.</summary>
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<Int32, String, HttpResponseMessage> _respond;

            internal StubHandler(Func<Int32, String, HttpResponseMessage> respond)
            {
                _respond = respond;
            }

            internal List<String> Bodies { get; } = new List<String>();

            internal List<String> Authorizations { get; } = new List<String>();

            internal List<String> Uris { get; } = new List<String>();

            internal Int32 Calls
            {
                get; private set;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Calls++;
                var body = request.Content == null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                Bodies.Add(body);
                Authorizations.Add(request.Headers.Authorization?.ToString());
                Uris.Add(request.RequestUri?.ToString());
                return _respond(Calls, body);
            }
        }

        /// <summary>Answers only when the token lets it, which is how a test proves whose deadline
        /// ended a call.</summary>
        private sealed class DelayingHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                return Json(HttpStatusCode.OK, "{}");
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode status, String body)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        /// <summary>
        ///   An error answer that echoes BOTH secrets back. A provider can do this (OpenAI's own 401
        ///   quotes the key it was given) and a gateway's body is arbitrary, and the SDK's exception
        ///   message is the status line plus that body VERBATIM - so a fixture whose body carries no
        ///   secret proves nothing about redaction, whatever the code does with it.
        /// </summary>
        private static HttpResponseMessage Leaky(HttpStatusCode status, String code)
        {
            return Json(status,
                "{\"error\":{\"message\":\"rejected the request to " + Endpoint
                + "/v1/embeddings carrying Authorization: Bearer " + Key + "\","
                + "\"type\":\"invalid_request_error\",\"code\":\"" + code + "\"}}");
        }

        private static Type GeneratorType => typeof(Fallen8EmbeddingProvider).Assembly
            .GetType("NoSQL.GraphDB.App.Embedding.OpenAIEmbeddingGenerator");

        /// <summary>OpenAI's per-request input cap, read from the code so the batching test cannot
        /// drift away from the constant it exercises.</summary>
        private static Int32 InputCap => (Int32)GeneratorType
            .GetField("MaxInputsPerRequest", BindingFlags.NonPublic | BindingFlags.Static)
            .GetRawConstantValue();

        /// <summary>Reflection into the internal generator (the repo declares no
        /// InternalsVisibleTo); returns it as the public abstraction every caller sees.</summary>
        private static IEmbeddingGenerator<String, Embedding<Single>> Generator(HttpMessageHandler handler,
            Int32 dimension = 1, ILogger logger = null)
        {
            var target = RemoteModelTarget.OpenAI("Fallen8:Embedding:OpenAI", Endpoint, Model, Key);
            Assert.IsTrue(target.IsValid(out _), "the fixture must be a target the factory would accept");

            var constructor = GeneratorType
                .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Single();
            try
            {
                return (IEmbeddingGenerator<String, Embedding<Single>>)constructor.Invoke(
                    new Object[] { target, dimension, logger, handler });
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException;
            }
        }

        /// <summary>The first component of every returned vector, in the order the generator
        /// produced them.</summary>
        private static Single[] FirstComponents(GeneratedEmbeddings<Embedding<Single>> generated)
        {
            return generated.Select(e => e.Vector.ToArray()[0]).ToArray();
        }

        /// <summary>The <c>input</c> array the SDK actually sent.</summary>
        private static List<String> InputsOf(String requestBody)
        {
            using var document = JsonDocument.Parse(requestBody);
            return document.RootElement.GetProperty("input").EnumerateArray()
                .Select(e => e.GetString()).ToList();
        }

        /// <summary>
        ///   A well-formed response for whatever was asked for, one 1-component vector per input
        ///   whose value is that input's own ordinal - so a mis-pairing anywhere in the batching is
        ///   visible as a number out of place rather than as a vector that merely looks plausible.
        /// </summary>
        private static HttpResponseMessage Echo(String requestBody, Boolean withUsage = true)
        {
            var inputs = InputsOf(requestBody);
            var data = new StringBuilder();
            for (var i = 0; i < inputs.Count; i++)
            {
                if (i > 0)
                {
                    data.Append(',');
                }

                data.Append("{\"object\":\"embedding\",\"index\":").Append(i.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"embedding\":[")
                    .Append(Ordinal(inputs[i]).ToString(CultureInfo.InvariantCulture))
                    .Append("]}");
            }

            var usage = withUsage
                ? ",\"usage\":{\"prompt_tokens\":" + inputs.Count.ToString(CultureInfo.InvariantCulture)
                    + ",\"total_tokens\":" + (inputs.Count * 2).ToString(CultureInfo.InvariantCulture) + "}"
                : String.Empty;

            return Json(HttpStatusCode.OK,
                "{\"object\":\"list\",\"model\":\"" + Model + "\",\"data\":[" + data + "]" + usage + "}");
        }

        /// <summary>Inputs are named <c>t{n}</c>, so the text carries the position it must end up in.</summary>
        private static Int32 Ordinal(String input)
        {
            return Int32.Parse(input.Substring(1), CultureInfo.InvariantCulture);
        }

        private static String[] Inputs(Int32 count)
        {
            var inputs = new String[count];
            for (var i = 0; i < count; i++)
            {
                inputs[i] = "t" + i.ToString(CultureInfo.InvariantCulture);
            }

            return inputs;
        }

        #endregion

        #region pairing a vector with the text it describes

        /// <summary>
        ///   THE test this backend exists for. The response's <c>index</c> is the only thing that
        ///   says which input a vector belongs to, and the wire order is not required to match the
        ///   request's. Pairing by position instead assigns element A element C's vector with no
        ///   exception, no log line and no downstream check that can notice: the width is right, the
        ///   count is right, and semantic traversal then returns the neighbours of the wrong text.
        /// </summary>
        [TestMethod]
        public async Task APermutedResponse_IsReSortedByIndex_SoNoTextGetsAnotherTextsVector()
        {
            var stub = new StubHandler((_, __) => Json(HttpStatusCode.OK,
                "{\"object\":\"list\",\"model\":\"" + Model + "\",\"data\":["
                + "{\"object\":\"embedding\",\"index\":2,\"embedding\":[3]},"
                + "{\"object\":\"embedding\",\"index\":0,\"embedding\":[1]},"
                + "{\"object\":\"embedding\",\"index\":1,\"embedding\":[2]}],"
                + "\"usage\":{\"prompt_tokens\":3,\"total_tokens\":3}}"));

            using var generator = Generator(stub);
            var generated = await generator.GenerateAsync(new[] { "t1", "t2", "t3" });

            CollectionAssert.AreEqual(new[] { "t1", "t2", "t3" }, InputsOf(stub.Bodies.Single()),
                "the inputs go out in the caller's order, which is what index refers back to");
            CollectionAssert.AreEqual(new[] { 1f, 2f, 3f }, FirstComponents(generated),
                "pairing by wire position would have produced 3, 1, 2 and reported success");
        }

        /// <summary>
        ///   Every way a response can be unpairable is refused. A short answer shifts every
        ///   subsequent pairing for a caller that zips by position, and the SDK returns
        ///   <c>Count == 1</c> for three inputs silently; indexes that are not the inputs' own
        ///   positions are the same fault with the count looking right.
        /// </summary>
        [TestMethod]
        public async Task AResponseThatCannotBePairedWithItsInputs_IsRefused_RatherThanShiftingEveryVector()
        {
            async Task<InvalidOperationException> Refused(String data)
            {
                var stub = new StubHandler((_, __) => Json(HttpStatusCode.OK,
                    "{\"object\":\"list\",\"model\":\"" + Model + "\",\"data\":[" + data + "]}"));
                using var generator = Generator(stub);
                return await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                    () => generator.GenerateAsync(new[] { "t0", "t1", "t2" }));
            }

            var tooFew = await Refused("{\"object\":\"embedding\",\"index\":0,\"embedding\":[0]}");
            StringAssert.Contains(tooFew.Message, "3 input(s) with 1 vector(s)", tooFew.Message);

            // A duplicate index: the count matches, so only the index check catches it.
            var duplicated = await Refused(
                "{\"object\":\"embedding\",\"index\":0,\"embedding\":[0]},"
                + "{\"object\":\"embedding\",\"index\":0,\"embedding\":[0]},"
                + "{\"object\":\"embedding\",\"index\":1,\"embedding\":[1]}");
            StringAssert.Contains(duplicated.Message, "where input 1 was expected", duplicated.Message);

            // One-based indexes: a gateway convention that would silently drop the first input's
            // vector and hand every other one to its predecessor.
            var oneBased = await Refused(
                "{\"object\":\"embedding\",\"index\":1,\"embedding\":[0]},"
                + "{\"object\":\"embedding\",\"index\":2,\"embedding\":[1]},"
                + "{\"object\":\"embedding\",\"index\":3,\"embedding\":[2]}");
            StringAssert.Contains(oneBased.Message, "where input 0 was expected", oneBased.Message);
        }

        /// <summary>
        ///   A batch over the provider's per-request cap becomes several requests, and the answer is
        ///   re-joined in the CALLER's order. The cap is the provider's, not
        ///   <c>Fallen8:Embedding:MaxBatchSize</c>: the SDK does not enforce it (an over-cap request
        ///   goes out whole and the service rejects the lot), so this is the only place it holds.
        /// </summary>
        [TestMethod]
        public async Task ABatchOverTheProvidersInputCap_IsSplit_AndRejoinedInInputOrder()
        {
            var cap = InputCap;
            var stub = new StubHandler((_, body) => Echo(body));

            using var generator = Generator(stub);
            var generated = await generator.GenerateAsync(Inputs(cap + 1));

            Assert.AreEqual(2, stub.Calls, "one request over the cap is two requests, never one");
            Assert.AreEqual(cap, InputsOf(stub.Bodies[0]).Count);
            CollectionAssert.AreEqual(new[] { "t" + cap.ToString(CultureInfo.InvariantCulture) },
                InputsOf(stub.Bodies[1]), "the tail is the remainder, in order");

            Assert.AreEqual(cap + 1, generated.Count);
            var components = FirstComponents(generated);
            for (var i = 0; i < components.Length; i++)
            {
                Assert.AreEqual((Single)i, components[i], "position " + i + " must carry its own vector");
            }

            // Exactly at the cap is still ONE request: an off-by-one here doubles the request count
            // and the spend for every ordinary batch.
            var exact = new StubHandler((_, body) => Echo(body));
            using var exactGenerator = Generator(exact);
            await exactGenerator.GenerateAsync(Inputs(cap));
            Assert.AreEqual(1, exact.Calls);
        }

        /// <summary>An empty batch asks the provider nothing: the route rejects an empty
        /// <c>input</c>, and there is nothing to embed.</summary>
        [TestMethod]
        public async Task AnEmptyBatch_AsksTheProviderNothing()
        {
            var stub = new StubHandler((_, body) => Echo(body));

            using var generator = Generator(stub);
            var generated = await generator.GenerateAsync(Array.Empty<String>());

            Assert.AreEqual(0, generated.Count);
            Assert.AreEqual(0, stub.Calls);
        }

        #endregion

        #region what goes on the wire

        /// <summary>
        ///   The declared width is asked for, so a <c>text-embedding-3-*</c> model returns it rather
        ///   than its native size - and an unconfigured one is not sent at all, because
        ///   <c>dimensions: 0</c> is a request the service refuses with a sentence about the wrong
        ///   thing.
        /// </summary>
        [TestMethod]
        public async Task TheDeclaredDimension_ReachesTheWire_AndAnUnconfiguredOneIsNotSent()
        {
            var declared = new StubHandler((_, body) => Echo(body));
            using (var generator = Generator(declared, dimension: 1536))
            {
                await generator.GenerateAsync(new[] { "t0" });
            }

            StringAssert.Contains(declared.Bodies.Single(), "\"dimensions\":1536");

            var unset = new StubHandler((_, body) => Echo(body));
            using (var generator = Generator(unset, dimension: 0))
            {
                await generator.GenerateAsync(new[] { "t0" });
            }

            Assert.IsFalse(unset.Bodies.Single().Contains("dimensions", StringComparison.Ordinal),
                "an unset dimension is an absent field, not a zero: " + unset.Bodies.Single());
        }

        /// <summary>
        ///   The configured endpoint stays a host root and the SDK owns the route it appends. The
        ///   asymmetry is deliberate and load-bearing: this SDK appends the suffix to what it is
        ///   given verbatim, so the <c>/v1</c> is a transport detail added once, here.
        /// </summary>
        [TestMethod]
        public async Task TheRouteIsTheSdksOwn_SoTheConfiguredEndpointStaysAHostRoot()
        {
            var stub = new StubHandler((_, body) => Echo(body));

            using var generator = Generator(stub);
            await generator.GenerateAsync(new[] { "t0" });

            Assert.AreEqual(Endpoint + "/v1/embeddings", stub.Uris.Single());
        }

        /// <summary>
        ///   Token counts are summed across the requests one batch became, and stay NULL when the
        ///   provider omits <c>usage</c> - a zero would read as "this cost nothing", which is a
        ///   different claim. An embeddings route has no output-token concept, so that half is never
        ///   invented either.
        /// </summary>
        [TestMethod]
        public async Task Usage_IsSummedAcrossRequests_AndStaysNullWhenTheProviderOmitsIt()
        {
            var cap = InputCap;
            var counted = new StubHandler((_, body) => Echo(body));
            using (var generator = Generator(counted))
            {
                var generated = await generator.GenerateAsync(Inputs(cap + 1));

                Assert.AreEqual((Int64?)(cap + 1), generated.Usage.InputTokenCount,
                    "the second request's tokens are as real as the first's");
                Assert.AreEqual((Int64?)((cap + 1) * 2), generated.Usage.TotalTokenCount);
                Assert.IsNull(generated.Usage.OutputTokenCount, "an embeddings route produces no output tokens");
            }

            var silent = new StubHandler((_, body) => Echo(body, withUsage: false));
            using (var generator = Generator(silent))
            {
                var generated = await generator.GenerateAsync(new[] { "t0" });
                Assert.IsNull(generated.Usage, "a missing count stays missing");
            }
        }

        #endregion

        #region the credential and the deadline

        /// <summary>
        ///   The credential is attached once, by the SDK, and appears on the request - and in no log
        ///   line and no message any caller can see. The endpoint is held to the same rule, because
        ///   a URL can carry an embedded credential and the 503 built from these messages is
        ///   anonymous on a keyless instance.
        ///   <para>Every fixture here answers with the secrets IN THE BODY, which is the whole point:
        ///   the SDK's own exception message is the status line plus that body verbatim, so a fixture
        ///   with a clean body would pass no matter what the code forwarded. The one deliberate
        ///   exception to the no-body rule - the <c>message</c> of a <c>context_length_exceeded</c>
        ///   refusal, whose token numbers are what tell an operator how much to cut - is covered by
        ///   <see cref="AnOverLongInput_IsRefusedWithWhatToChange_AndOtherBadRequestsAreNotRelabelled" />
        ///   instead, so it is a stated exception rather than a hole.</para>
        /// </summary>
        [TestMethod]
        public async Task TheCredential_IsOnTheRequest_AndInNoLogLineOrErrorMessage()
        {
            var sink = new TestLogSink();
            var ok = new StubHandler((_, body) => Echo(body));
            using (var generator = Generator(ok, logger: sink.CreateFactory().CreateLogger("openai")))
            {
                await generator.GenerateAsync(new[] { "t0", "t1" });
            }

            Assert.AreEqual("Bearer " + Key, ok.Authorizations.Single());

            // Every failure path a caller can be shown, against a provider that echoes both secrets
            // back in its body. The status is what varies, because only ONE of them (a 400 naming
            // context_length_exceeded) has a Fallen-8 sentence of its own: every other one used to
            // reach the caller as the SDK's own exception, body and all.
            var refusals = new List<Exception>();

            foreach (var status in new[]
            {
                HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound,
                HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway,
                HttpStatusCode.BadRequest
            })
            {
                var leaking = new StubHandler((_, __) => Leaky(status, "invalid_api_key"));
                using var generator = Generator(leaking);
                var refused = await Assert.ThrowsExceptionAsync<HttpRequestException>(
                    () => generator.GenerateAsync(new[] { "t0" }));

                StringAssert.Contains(refused.Message, ((Int32)status).ToString(CultureInfo.InvariantCulture),
                    "the status is what an operator can act on, and all this may say");
                refusals.Add(refused);
            }

            var unpairable = new StubHandler((_, __) => Json(HttpStatusCode.OK,
                "{\"object\":\"list\",\"data\":[]}"));
            using (var generator = Generator(unpairable))
            {
                refusals.Add(await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                    () => generator.GenerateAsync(new[] { "t0" })));
            }

            foreach (var refusal in refusals)
            {
                Assert.IsFalse(refusal.Message.Contains(Key, StringComparison.Ordinal),
                    "a credential must reach no message a caller can see: " + refusal.Message);
                Assert.IsFalse(refusal.Message.Contains("secret.example", StringComparison.Ordinal),
                    "and neither must the endpoint, which can carry one: " + refusal.Message);
            }

            // The SDK's own exception is KEPT, as the inner one, and it does carry the body: no
            // surface reads it (every one of them composes ex.Message into its problem-detail or its
            // log line) and a debugger needs the raw refusal. Asserted so this suite cannot quietly
            // become vacuous again if the SDK ever stops embedding the body.
            var wrapped = refusals.OfType<HttpRequestException>().First();
            Assert.IsInstanceOfType(wrapped.InnerException, typeof(ClientResultException));
            StringAssert.Contains(wrapped.InnerException.Message, Key,
                "the fixture must be one where forwarding the SDK's message WOULD leak");

            foreach (var entry in sink.Entries)
            {
                Assert.IsFalse(entry.Message?.Contains(Key, StringComparison.Ordinal) == true,
                    "the credential must appear in no log line: " + entry.Message);
                Assert.IsFalse(entry.Message?.Contains("secret.example", StringComparison.Ordinal) == true,
                    "and neither must the endpoint: " + entry.Message);
            }
        }

        /// <summary>
        ///   The transport carries no deadline of its own, so the caller's budget is the only thing
        ///   that can end a call. Both halves are asserted: the client that would otherwise inherit
        ///   .NET's undocumented 100s, and the behaviour - a caller cancelling at 300ms against a
        ///   handler that would answer in 30s.
        /// </summary>
        [TestMethod]
        public async Task TheTransportCarriesNoDeadline_SoTheCallersTokenIsTheOnlyOne()
        {
            using var generator = Generator(new DelayingHandler());

            var http = (HttpClient)GeneratorType
                .GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(generator);
            Assert.AreEqual(Timeout.InfiniteTimeSpan, http.Timeout,
                "two deadlines is the bug the single-deadline rule exists to prevent");

            using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await generator.GenerateAsync(new[] { "t0" }, null, caller.Token);
                Assert.Fail("the caller's budget must end the call");
            }
            catch (Exception ex)
            {
                Assert.IsInstanceOfType(ex, typeof(OperationCanceledException), ex.ToString());
            }

            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
                "the caller's 300ms is what ended it, not the handler's 30s: " + stopwatch.Elapsed);
        }

        /// <summary>
        ///   The SDK's own retry is disarmed. Not theoretical: its default policy makes four
        ///   attempts against one 503, which multiplies metered spend behind an operator who
        ///   configured one request - and 503 is not in this provider's retryable set anyway, so
        ///   nothing in our chain replays it either.
        /// </summary>
        [TestMethod]
        public async Task TheSdksOwnRetry_IsDisarmed_SoOneRefusalIsOneAttempt()
        {
            var stub = new StubHandler((_, __) => Json(HttpStatusCode.ServiceUnavailable, "{}"));

            using var generator = Generator(stub);
            var failed = await Assert.ThrowsExceptionAsync<HttpRequestException>(
                () => generator.GenerateAsync(new[] { "t0" }));

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.AreEqual(1, stub.Calls, "the SDK's default policy would have made four");
        }

        /// <summary>
        ///   A 429 IS waited out and replayed, honouring <c>Retry-After</c> - the other half of the
        ///   claim above, and the reason the retryable set has to be the same one the chat client
        ///   reads: a set that lived only on the chat backend could gain a status here and never
        ///   reach this transport, and the attempt-count assertion above would still pass.
        /// </summary>
        [TestMethod]
        public async Task ARateLimit_IsWaitedOutAndReplayed_FromTheSameRetryableSetTheChatClientReads()
        {
            var stub = new StubHandler((call, body) =>
            {
                if (call > 1)
                {
                    return Echo(body);
                }

                var limited = Json(HttpStatusCode.TooManyRequests, "{\"error\":{\"message\":\"slow down\"}}");
                limited.Headers.TryAddWithoutValidation("Retry-After", "1");
                return limited;
            });

            using var generator = Generator(stub);
            var generated = await generator.GenerateAsync(new[] { "t0", "t1" });

            Assert.AreEqual(2, stub.Calls, "a 429 is asked again, not handed to the caller");
            CollectionAssert.AreEqual(new[] { 0f, 1f }, FirstComponents(generated),
                "the replayed request carries the same inputs, in the same order");
            Assert.IsTrue(stub.Authorizations.All(header => header == "Bearer " + Key),
                "and the credential, which a re-cloned request must not lose");
        }

        #endregion

        #region refusals

        /// <summary>
        ///   An input over the model's token ceiling is REFUSED, and the refusal names the Fallen-8
        ///   setting that produced the input - the service's own sentence cannot know that. This is
        ///   the never-truncate promise for this backend: the route has no truncation parameter, so
        ///   the service refusing is the mechanism.
        ///   <para>And a 400 that means something else is NOT relabelled: reporting a spent quota or
        ///   a bad model name as "one input is too long" sends an operator to shorten text that is
        ///   fine. It is also the ONE deliberate exception to the no-provider-body rule, which is why
        ///   the other 400s here carry the credential in their bodies: the token numbers travel only
        ///   for the code that means "count the tokens", and everything else says just its
        ///   status.</para>
        /// </summary>
        [TestMethod]
        public async Task AnOverLongInput_IsRefusedWithWhatToChange_AndOtherBadRequestsAreNotRelabelled()
        {
            var overLong = new StubHandler((_, __) => Json(HttpStatusCode.BadRequest,
                "{\"error\":{\"message\":\"This model's maximum context length is 8192 tokens, "
                + "however you requested 20000 tokens.\",\"type\":\"invalid_request_error\","
                + "\"param\":null,\"code\":\"context_length_exceeded\"}}"));
            using (var generator = Generator(overLong))
            {
                var refused = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                    () => generator.GenerateAsync(new[] { "t0" }));

                StringAssert.Contains(refused.Message, "however you requested 20000 tokens",
                    "the service's own numbers are what tell an operator how much to cut");
                StringAssert.Contains(refused.Message, "Fallen8:Ingestion:ChunkMaxChars");
                StringAssert.Contains(refused.Message, "Nothing is half-embedded");
                Assert.IsInstanceOfType(refused.InnerException, typeof(ClientResultException),
                    "the provider's own fault is kept, so a log still shows the raw refusal");
            }

            // A different 400 code, and a body that is not the documented shape at all: none of them
            // is relabelled, and none of them reaches the caller with the body in it either. The
            // second and third bodies are the ones that matter for the redaction: a gateway can put
            // anything in either, so "not the documented shape" must mean "say only the status".
            foreach (var body in new[]
            {
                "{\"error\":{\"message\":\"Incorrect API key " + Key + "\",\"code\":\"invalid_api_key\"}}",
                "{\"error\":{\"message\":\"no code member, but " + Key + " is in here\"}}",
                "{\"error\":\"not even an object, and " + Key + " rode along\"}",
                "<html>a gateway that does not speak JSON, quoting " + Key + "</html>"
            })
            {
                var other = new StubHandler((_, __) => Json(HttpStatusCode.BadRequest, body));
                using var generator = Generator(other);
                var failed = await Assert.ThrowsExceptionAsync<HttpRequestException>(
                    () => generator.GenerateAsync(new[] { "t0" }));
                Assert.AreEqual(HttpStatusCode.BadRequest, failed.StatusCode, body);
                Assert.IsFalse(failed.Message.Contains("ChunkMaxChars", StringComparison.Ordinal), body);
                Assert.IsFalse(failed.Message.Contains(Key, StringComparison.Ordinal), body);
            }
        }

        #endregion
    }
}
