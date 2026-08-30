// MIT License
//
// OpenAIChatBackendTest.cs
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
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Chat;
using NoSQL.GraphDB.App.Helper;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The OpenAI chat backend against a stubbed wire (feature model-providers): what the outbound
    ///   request body says, what the stats mean when the provider omits them, and what a stream that
    ///   does not finish must NOT be allowed to look like.
    ///
    ///   <para>These are the only tests that see the real SDK serialization, so this is where "the
    ///   configured model reaches the body verbatim" and "nothing the caller did not ask for travels"
    ///   are actually true rather than asserted one layer above where they are decided. The canned
    ///   wire lives in <see cref="RemoteModelWire" />.</para>
    /// </summary>
    [TestClass]
    public class OpenAIChatBackendTest
    {
        private static OpenAIChatBackend Backend(RecordingHandler stub, Boolean stream = true)
        {
            return new OpenAIChatBackend(RemoteModelWire.OpenAITarget(), stream, logger: null, stub);
        }

        /// <summary>
        ///   The deltas are accumulated into one answer and the trailing usage frame supplies the
        ///   stats the NL-assist UX renders. Streaming is the default, so this is the normal path.
        /// </summary>
        [TestMethod]
        public async Task AStreamedCompletion_IsAccumulated_AndItsStatsComeFromTheUsageFrame()
        {
            var stub = new RecordingHandler(_ => RemoteModelWire.Sse(RemoteModelWire.OpenAIStream()));
            using var backend = Backend(stub);

            var result = await backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None);

            Assert.AreEqual("hello", result.Content);
            Assert.AreEqual(RemoteModelWire.OpenAIModel, result.Model);
            Assert.AreEqual(11L, result.PromptTokens);
            Assert.AreEqual(5L, result.CompletionTokens);
            Assert.IsTrue(result.DurationMs > 0d,
                "neither wire format carries a duration, so the only honest one is measured locally");
            Assert.AreEqual(5d / (result.DurationMs.Value / 1000d), result.TokensPerSecond.Value, 0.001);
        }

        /// <summary>A buffered completion carries the same content and the same stats.</summary>
        [TestMethod]
        public async Task ABufferedCompletion_CarriesTheSameContentAndStats()
        {
            var stub = new RecordingHandler(_ => RemoteModelWire.Json(RemoteModelWire.OpenAICompletion()));
            using var backend = Backend(stub, stream: false);

            var result = await backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None);

            Assert.AreEqual("hello", result.Content);
            Assert.AreEqual(11L, result.PromptTokens);
            Assert.AreEqual(5L, result.CompletionTokens);
        }

        /// <summary>
        ///   A provider that reports no usage leaves the counts NULL, and no tokens-per-second is
        ///   invented from a duration alone. Deliberately unlike the Ollama backend, whose fields are
        ///   non-nullable and report an absent value as 0: here 0 would read as "it generated nothing"
        ///   rather than "it did not say".
        /// </summary>
        [TestMethod]
        public async Task AnAbsentUsageObject_LeavesTheCountsNull_AndInventsNoRate()
        {
            var stub = new RecordingHandler(_ => RemoteModelWire.Sse(
                RemoteModelWire.OpenAIStream(usage: false)));
            using var backend = Backend(stub);

            var result = await backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None);

            Assert.AreEqual("hello", result.Content);
            Assert.IsNull(result.PromptTokens);
            Assert.IsNull(result.CompletionTokens);
            Assert.IsTrue(result.DurationMs > 0d, "the duration is ours and is always honest");
            Assert.IsNull(result.TokensPerSecond,
                "a rate needs a token count; substituting the duration would publish a made-up number");
        }

        /// <summary>
        ///   Streaming is asked for by default, and the switch really turns it off.
        ///   <para>The streamed request must also ASK for the trailing usage frame. The SDK sets
        ///   <c>stream_options.include_usage</c> by itself, which is why nothing in this repository
        ///   does - so a version bump that stopped doing it would make every streamed draft's token
        ///   counts null in production while a stub that emits the frame unconditionally kept the
        ///   parsing tests green.</para>
        /// </summary>
        [TestMethod]
        public async Task TheStreamSwitch_DecidesWhatTheRequestAsksFor()
        {
            var streaming = new RecordingHandler(_ => RemoteModelWire.Sse(RemoteModelWire.OpenAIStream()));
            using (var backend = Backend(streaming, stream: true))
            {
                await backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None);
            }

            StringAssert.Contains(streaming.Bodies[0], "\"stream\":true");
            StringAssert.Contains(streaming.Bodies[0], "\"include_usage\":true",
                "the trailing usage frame only arrives if the request asked for it: " + streaming.Bodies[0]);

            var buffered = new RecordingHandler(_ => RemoteModelWire.Json(RemoteModelWire.OpenAICompletion()));
            using (var backend = Backend(buffered, stream: false))
            {
                await backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None);
            }

            Assert.IsFalse(buffered.Bodies[0].Contains("\"stream\":true", StringComparison.Ordinal),
                "a buffered call must not ask for a stream: " + buffered.Bodies[0]);
        }

        /// <summary>
        ///   The configured model reaches the body verbatim, and the turns reach it in their own roles.
        ///   If anything normalized the model string, configuring a dated snapshot name would be
        ///   theatre.
        /// </summary>
        [TestMethod]
        public async Task TheConfiguredModelAndTheTurnRoles_ReachTheBodyVerbatim()
        {
            var stub = new RecordingHandler(_ => RemoteModelWire.Json(RemoteModelWire.OpenAICompletion()));
            using var backend = new OpenAIChatBackend(
                RemoteModelWire.OpenAITarget("gpt-4o-mini-2024-07-18"), stream: false, logger: null, stub);

            await backend.ChatAsync(RemoteModelWire.Turns(system: "you are terse"), null, CancellationToken.None);

            var body = JsonDocument.Parse(stub.Bodies[0]).RootElement;
            Assert.AreEqual("gpt-4o-mini-2024-07-18", body.GetProperty("model").GetString());

            var turns = body.GetProperty("messages");
            Assert.AreEqual(2, turns.GetArrayLength(), "a system turn stays a turn on this protocol");
            Assert.AreEqual("system", turns[0].GetProperty("role").GetString());
            Assert.AreEqual("user", turns[1].GetProperty("role").GetString());
        }

        /// <summary>
        ///   Nothing the caller did not ask for reaches the body: an unset temperature must not travel
        ///   as a <c>0</c>, which would pin a knob at a value the caller never chose. Both knobs travel
        ///   when they are set, and either one alone still does.
        /// </summary>
        [TestMethod]
        public async Task NothingTheCallerDidNotAskFor_ReachesTheBody()
        {
            var neither = new RecordingHandler(_ => RemoteModelWire.Json(RemoteModelWire.OpenAICompletion()));
            using (var backend = Backend(neither, stream: false))
            {
                await backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None);
            }

            var body = JsonDocument.Parse(neither.Bodies[0]).RootElement;
            Assert.IsFalse(body.TryGetProperty("temperature", out _),
                "an unset temperature must be absent, not 0: " + neither.Bodies[0]);
            Assert.IsFalse(body.TryGetProperty("stop", out _), neither.Bodies[0]);

            var both = new RecordingHandler(_ => RemoteModelWire.Json(RemoteModelWire.OpenAICompletion()));
            using (var backend = Backend(both, stream: false))
            {
                await backend.ChatAsync(RemoteModelWire.Turns(),
                    new ChatBackendOptions { Temperature = 0.1, Stop = new[] { "<|im_start|>", "<|im_end|>" } },
                    CancellationToken.None);
            }

            var sent = JsonDocument.Parse(both.Bodies[0]).RootElement;
            Assert.AreEqual(0.1, sent.GetProperty("temperature").GetDouble(), 0.0001);
            var stop = sent.GetProperty("stop");
            Assert.AreEqual(2, stop.GetArrayLength());
            Assert.AreEqual("<|im_start|>", stop[0].GetString());

            var stopOnly = new RecordingHandler(_ => RemoteModelWire.Json(RemoteModelWire.OpenAICompletion()));
            using (var backend = Backend(stopOnly, stream: false))
            {
                await backend.ChatAsync(RemoteModelWire.Turns(),
                    new ChatBackendOptions { Stop = new[] { "<|im_end|>" } }, CancellationToken.None);
            }

            var one = JsonDocument.Parse(stopOnly.Bodies[0]).RootElement;
            Assert.IsFalse(one.TryGetProperty("temperature", out _),
                "sending stop sequences must not drag a temperature along: " + stopOnly.Bodies[0]);
            StringAssert.Contains(stopOnly.Bodies[0], "<|im_end|>");
        }

        /// <summary>
        ///   A stream that ends with no finish reason is a TRUNCATION and must not be returned as a
        ///   short answer. This is the case with no exception to notice: the frames parse, the
        ///   enumeration completes, and the SDK cannot see the <c>[DONE]</c> sentinel at all, so the
        ///   missing finish reason is the only thing that distinguishes a cut-off answer from one the
        ///   model chose to keep brief.
        /// </summary>
        [TestMethod]
        public async Task AStreamThatNeverCompletes_FailsInsteadOfReturningAShortAnswer()
        {
            var stub = new RecordingHandler(_ => RemoteModelWire.Sse(
                RemoteModelWire.Data(RemoteModelWire.OpenAIChunk("return (v) =>", null),
                    RemoteModelWire.OpenAIChunk(" v.Label", null))));
            using var backend = Backend(stub);

            var failure = await Assert.ThrowsExceptionAsync<ChatBackendOutputException>(
                () => backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None));

            StringAssert.Contains(failure.Message, "without a completion marker");
            StringAssert.Contains(failure.Message, "21 character(s)",
                "the partial length is what lets an operator tell a truncation from an empty answer");
        }

        /// <summary>
        ///   A connection that dies mid-body fails the same way and says how much had arrived, without
        ///   repeating anything the SDK said: its own message embeds the raw response body.
        /// </summary>
        [TestMethod]
        public async Task AStreamThatDiesMidBody_FailsAndReportsWhatArrived()
        {
            var stub = new RecordingHandler(_ => RemoteModelWire.DyingSse(
                RemoteModelWire.Data(RemoteModelWire.OpenAIChunk("return (v) =>", null))));
            using var backend = Backend(stub);

            var failure = await Assert.ThrowsExceptionAsync<ChatBackendOutputException>(
                () => backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None));

            StringAssert.Contains(failure.Message, "ended early");
            StringAssert.Contains(failure.Message, "13 character(s)");
            Assert.IsNotNull(failure.InnerException, "the transport fault is kept for the log");
        }

        /// <summary>
        ///   A backend that never answered is NOT a truncation: a fault before the first token is a
        ///   connection problem, which the chat provider maps to 503, and wrapping it as "the response
        ///   ended early" would blame the response for it.
        /// </summary>
        [TestMethod]
        public async Task AnUnreachableBackend_IsNotReportedAsATruncatedStream()
        {
            var stub = new RecordingHandler(_ => throw new HttpRequestException("Connection refused"));
            using var backend = Backend(stub);

            await Assert.ThrowsExceptionAsync<HttpRequestException>(
                () => backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None),
                "a connection that never delivered a token is the provider's 503, not a 502 truncation");
        }

        /// <summary>
        ///   A caller's cancellation stays a cancellation. It must not be re-dressed as a truncated
        ///   response: the provider maps the two to different status codes, and a disconnected client
        ///   is nobody's bad gateway.
        /// </summary>
        [TestMethod]
        public async Task ACallerCancellation_IsNotReportedAsATruncatedStream()
        {
            var stub = new RecordingHandler(_ => RemoteModelWire.Sse(
                RemoteModelWire.Data(RemoteModelWire.OpenAIChunk("partial", null))));
            using var backend = Backend(stub);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            await RemoteModelWire.AssertCancelled(
                () => backend.ChatAsync(RemoteModelWire.Turns(), null, cancelled.Token),
                "a cancelled call must not be reported as the backend truncating its answer");
        }

        /// <summary>
        ///   A refused answer is an output failure naming the refusal, not a draft. Returning the
        ///   partial text would hand a caller something the model declined to write.
        /// </summary>
        [TestMethod]
        public async Task AContentFilterFinish_IsARefusal_NotADraft()
        {
            var stub = new RecordingHandler(_ => RemoteModelWire.Sse(
                RemoteModelWire.OpenAIStream("partial", "content_filter")));
            using var backend = Backend(stub);

            var failure = await Assert.ThrowsExceptionAsync<ChatBackendOutputException>(
                () => backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None));

            StringAssert.Contains(failure.Message, "content filter");
            StringAssert.Contains(failure.Message, "refused");
        }

        /// <summary>
        ///   An answer that stopped at an output ceiling is an INCOMPLETE answer, not a short one.
        ///   Handed on as a draft it reads as complete, and the next thing that fails is whatever
        ///   consumes it - a delegate fragment that no longer parses, with nothing anywhere naming the
        ///   ceiling. Both paths, because streaming is the default.
        /// </summary>
        [TestMethod]
        public async Task AnAnswerStoppedAtTheOutputCeiling_IsAnOutputFailure_NotAShortDraft()
        {
            var streamed = new RecordingHandler(_ => RemoteModelWire.Sse(
                RemoteModelWire.OpenAIStream("half a filt", "length")));
            using (var backend = Backend(streamed))
            {
                var failure = await Assert.ThrowsExceptionAsync<ChatBackendOutputException>(
                    () => backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None));

                StringAssert.Contains(failure.Message, "output ceiling");
                StringAssert.Contains(failure.Message, "incomplete");
                StringAssert.Contains(failure.Message, "11 character(s)",
                    "how much arrived is what tells a truncation from a refusal");
            }

            var buffered = new RecordingHandler(_ => RemoteModelWire.Json(
                RemoteModelWire.OpenAICompletion("half a filt", "length")));
            using (var backend = Backend(buffered, stream: false))
            {
                var failure = await Assert.ThrowsExceptionAsync<ChatBackendOutputException>(
                    () => backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None));

                StringAssert.Contains(failure.Message, "output ceiling");
            }

            // And a normal stop still returns its answer: the guard must not swallow the happy path.
            var complete = new RecordingHandler(_ => RemoteModelWire.Sse(RemoteModelWire.OpenAIStream()));
            using (var backend = Backend(complete))
            {
                var result = await backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None);
                Assert.AreEqual("hello", result.Content);
            }
        }

        /// <summary>
        ///   A gateway answering with a finish reason outside OpenAI's own set is a bad RESPONSE, not
        ///   an unhandled fault: the SDK models the field as a closed CLR enum, so the value faults the
        ///   enumeration itself and would otherwise escape as an HTTP 500 rather than a 502.
        /// </summary>
        [TestMethod]
        public async Task AFinishReasonNoClientCanRead_IsAMappedOutputFailure()
        {
            var stub = new RecordingHandler(_ => RemoteModelWire.Sse(
                RemoteModelWire.Data(RemoteModelWire.OpenAIChunk(null, "quota_exhausted"))));
            using var backend = Backend(stub);

            var failure = await Assert.ThrowsExceptionAsync<ChatBackendOutputException>(
                () => backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None));

            StringAssert.Contains(failure.Message, "completion reason this client cannot read");
            StringAssert.Contains(failure.Message, "0 character(s)",
                "the value faults the enumeration before any content arrives");
        }

        #region live openai smoke

        /// <summary>
        ///   OPT-IN live smoke against the real OpenAI endpoint. Remove [Ignore] and set
        ///   F8_TEST_OPENAI_API_KEY to run. Deliberately keyed on the F8_TEST_ prefix and not on the
        ///   compose variable: keying it off F8_OPENAI_API_KEY would make `dotnet test` place live
        ///   billed calls from any machine with a working deployment.
        /// </summary>
        [TestMethod]
        [Ignore("Live-endpoint smoke: set F8_TEST_OPENAI_API_KEY and remove [Ignore] to run.")]
        [TestCategory("LiveModel")]
        public async Task OpenAI_Chat_AnswersAPrompt()
        {
            var apiKey = Environment.GetEnvironmentVariable("F8_TEST_OPENAI_API_KEY");
            if (String.IsNullOrEmpty(apiKey))
            {
                Assert.Inconclusive("F8_TEST_OPENAI_API_KEY not set.");
            }

            var endpoint = Environment.GetEnvironmentVariable("F8_TEST_OPENAI_ENDPOINT")
                ?? "https://api.openai.com";
            var model = Environment.GetEnvironmentVariable("F8_TEST_OPENAI_CHAT_MODEL") ?? "gpt-4o-mini";

            var target = RemoteModelTarget.OpenAI("Fallen8:Chat:OpenAI", endpoint, model, apiKey);
            using var backend = new OpenAIChatBackend(target, stream: true, logger: null);
            var result = await backend.ChatAsync(
                new[] { new ChatTurn("user", "Reply with exactly one word: hello.") },
                new ChatBackendOptions(),
                CancellationToken.None);

            Assert.IsFalse(String.IsNullOrWhiteSpace(result.Content), "expected a non-empty reply");
            Assert.AreEqual(model, result.Model, "the configured model is what is reported back");
            Assert.IsTrue(result.CompletionTokens > 0, "OpenAI reports usage on every completion");
        }

        #endregion
    }
}
