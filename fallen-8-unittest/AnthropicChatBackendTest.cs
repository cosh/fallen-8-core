// MIT License
//
// AnthropicChatBackendTest.cs
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
using System.Globalization;
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
    ///   The Anthropic chat backend against a stubbed wire (feature model-providers): what the
    ///   outbound request body says - and above all what it does NOT say - and what a stream that does
    ///   not finish must not be allowed to look like.
    ///
    ///   <para>The truncation case here is the sharpest one in the feature: the manual event loop
    ///   ends WITHOUT throwing on an amputated stream, so a backend that did not track the
    ///   message-stop event by hand would return half an answer as if it were whole. The canned wire
    ///   lives in <see cref="RemoteModelWire" />.</para>
    /// </summary>
    [TestClass]
    public class AnthropicChatBackendTest
    {
        private const Int32 MaxTokens = 512;

        private static AnthropicChatBackend Backend(RecordingHandler stub, Boolean stream = true)
        {
            return new AnthropicChatBackend(RemoteModelWire.AnthropicTarget(), MaxTokens, stream,
                logger: null, stub);
        }

        /// <summary>
        ///   The text deltas are accumulated into one answer, the prompt tokens come from the start
        ///   event and the completion tokens from the delta event. Streaming is the default, so this is
        ///   the normal path.
        /// </summary>
        [TestMethod]
        public async Task AStreamedCompletion_IsAccumulated_AndItsStatsComeFromTheStartAndDeltaEvents()
        {
            var stub = new RecordingHandler(_ => RemoteModelWire.Sse(RemoteModelWire.AnthropicStream()));
            using var backend = Backend(stub);

            var result = await backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None);

            Assert.AreEqual("hello", result.Content);
            Assert.AreEqual(RemoteModelWire.AnthropicModel, result.Model);
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
            var stub = new RecordingHandler(_ => RemoteModelWire.Json(RemoteModelWire.AnthropicMessage()));
            using var backend = Backend(stub, stream: false);

            var result = await backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None);

            Assert.AreEqual("hello", result.Content);
            Assert.AreEqual(11L, result.PromptTokens);
            Assert.AreEqual(5L, result.CompletionTokens);
        }

        /// <summary>
        ///   A stream whose delta event never arrived reports NO completion count and invents no rate.
        ///   Deliberately unlike the Ollama backend, whose fields are non-nullable and report an absent
        ///   value as 0: here 0 would read as "it generated nothing" rather than "it did not say".
        /// </summary>
        [TestMethod]
        public async Task AnAbsentUsageEvent_LeavesTheCountNull_AndInventsNoRate()
        {
            var stub = new RecordingHandler(_ => RemoteModelWire.Sse(
                RemoteModelWire.AnthropicStream(messageDelta: false)));
            using var backend = Backend(stub);

            var result = await backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None);

            Assert.AreEqual("hello", result.Content);
            Assert.AreEqual(11L, result.PromptTokens, "the start event still reported the prompt");
            Assert.IsNull(result.CompletionTokens);
            Assert.IsTrue(result.DurationMs > 0d, "the duration is ours and is always honest");
            Assert.IsNull(result.TokensPerSecond,
                "a rate needs a token count; substituting the duration would publish a made-up number");
        }

        /// <summary>
        ///   NO sampling parameter ever reaches the body - not even one the caller set - because
        ///   current Claude models answer 400 to <c>temperature</c>, <c>top_p</c> and <c>top_k</c>, so
        ///   honouring the knob would turn every request carrying one into a failure. What DOES travel
        ///   is the required token ceiling and the caller's stop sequences.
        /// </summary>
        [TestMethod]
        public async Task NoSamplingParameterReachesTheBody_WhileMaxTokensAndStopSequencesDo()
        {
            var stub = new RecordingHandler(_ => RemoteModelWire.Json(RemoteModelWire.AnthropicMessage()));
            using (var backend = Backend(stub, stream: false))
            {
                await backend.ChatAsync(RemoteModelWire.Turns(),
                    new ChatBackendOptions { Temperature = 0.1, Stop = new[] { "<|im_end|>" } },
                    CancellationToken.None);
            }

            var body = JsonDocument.Parse(stub.Bodies[0]).RootElement;
            foreach (var knob in new[] { "temperature", "top_p", "top_k" })
            {
                Assert.IsFalse(body.TryGetProperty(knob, out _),
                    knob + " is refused with a 400 by current models: " + stub.Bodies[0]);
            }

            Assert.AreEqual(MaxTokens, body.GetProperty("max_tokens").GetInt32(),
                "the Messages API requires it per request, which is why this backend has the knob");
            var stop = body.GetProperty("stop_sequences");
            Assert.AreEqual(1, stop.GetArrayLength());
            Assert.AreEqual("<|im_end|>", stop[0].GetString());

            var bare = new RecordingHandler(_ => RemoteModelWire.Json(RemoteModelWire.AnthropicMessage()));
            using (var backend = Backend(bare, stream: false))
            {
                await backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None);
            }

            Assert.IsFalse(JsonDocument.Parse(bare.Bodies[0]).RootElement
                .TryGetProperty("stop_sequences", out _),
                "an empty stop list is not something the caller asked for: " + bare.Bodies[0]);
        }

        /// <summary>
        ///   The configured model reaches the body verbatim, and a system turn is HOISTED out of the
        ///   message list into the field this API takes it in - sending it as a turn would make the
        ///   request either fail or quietly ignore the instructions.
        /// </summary>
        [TestMethod]
        public async Task TheConfiguredModelReachesTheBodyVerbatim_AndSystemTurnsAreHoisted()
        {
            var stub = new RecordingHandler(_ => RemoteModelWire.Json(RemoteModelWire.AnthropicMessage()));
            using var backend = new AnthropicChatBackend(
                RemoteModelWire.AnthropicTarget("claude-sonnet-5"), MaxTokens, stream: false, logger: null, stub);

            await backend.ChatAsync(RemoteModelWire.Turns(system: "you are terse"), null, CancellationToken.None);

            var body = JsonDocument.Parse(stub.Bodies[0]).RootElement;
            Assert.AreEqual("claude-sonnet-5", body.GetProperty("model").GetString());
            Assert.AreEqual("you are terse", body.GetProperty("system").GetString());

            var turns = body.GetProperty("messages");
            Assert.AreEqual(1, turns.GetArrayLength(), "the system turn left the message list");
            Assert.AreEqual("user", turns[0].GetProperty("role").GetString());

            var bare = new RecordingHandler(_ => RemoteModelWire.Json(RemoteModelWire.AnthropicMessage()));
            using (var plain = Backend(bare, stream: false))
            {
                await plain.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None);
            }

            Assert.IsFalse(JsonDocument.Parse(bare.Bodies[0]).RootElement.TryGetProperty("system", out _),
                "an empty system prompt is not something the caller asked for: " + bare.Bodies[0]);
        }

        /// <summary>
        ///   A stream that stops before its message-stop event is a TRUNCATION and must not be returned
        ///   as a short answer. This is the sharpest case in the feature: the event loop ends WITHOUT
        ///   throwing, so nothing but the hand-tracked flag distinguishes an amputated answer from a
        ///   brief one.
        /// </summary>
        [TestMethod]
        public async Task AStreamThatNeverReachesMessageStop_FailsInsteadOfReturningAShortAnswer()
        {
            var stub = new RecordingHandler(_ => RemoteModelWire.Sse(
                RemoteModelWire.AnthropicStart()
                + RemoteModelWire.AnthropicBlockStart()
                + RemoteModelWire.AnthropicTextDelta("return (v) =>")
                + RemoteModelWire.AnthropicTextDelta(" v.Label")));
            using var backend = Backend(stub);

            var failure = await Assert.ThrowsExceptionAsync<ChatBackendOutputException>(
                () => backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None));

            StringAssert.Contains(failure.Message, "without a completion marker");
            StringAssert.Contains(failure.Message, "21 character(s)",
                "the partial length is what lets an operator tell a truncation from an empty answer");
        }

        /// <summary>
        ///   A connection that dies mid-body fails the same way and says how much had arrived, without
        ///   repeating anything the SDK said: its own message embeds the raw response body verbatim.
        /// </summary>
        [TestMethod]
        public async Task AStreamThatDiesMidBody_FailsAndReportsWhatArrived()
        {
            var stub = new RecordingHandler(_ => RemoteModelWire.DyingSse(
                RemoteModelWire.AnthropicStart()
                + RemoteModelWire.AnthropicBlockStart()
                + RemoteModelWire.AnthropicTextDelta("return (v) =>")));
            using var backend = Backend(stub);

            var failure = await Assert.ThrowsExceptionAsync<ChatBackendOutputException>(
                () => backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None));

            StringAssert.Contains(failure.Message, "ended early");
            StringAssert.Contains(failure.Message, "13 character(s)");
            Assert.IsFalse(failure.Message.Contains("anthropic.invalid", StringComparison.Ordinal),
                "no message quotes the endpoint, because a URL can carry a credential");
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
                RemoteModelWire.AnthropicStart() + RemoteModelWire.AnthropicBlockStart()
                + RemoteModelWire.AnthropicTextDelta("partial")));
            using var backend = Backend(stub);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            await RemoteModelWire.AssertCancelled(
                () => backend.ChatAsync(RemoteModelWire.Turns(), null, cancelled.Token),
                "a cancelled call must not be reported as the backend truncating its answer");
        }

        /// <summary>
        ///   A refusal is an output failure naming the category, on BOTH paths. It arrives as
        ///   structured stop details rather than as content, so returning the partial text would hand a
        ///   caller something the model declined to write.
        /// </summary>
        [TestMethod]
        public async Task ARefusal_IsAnOutputFailureNamingTheCategory_OnBothPaths()
        {
            var buffered = new RecordingHandler(_ => RemoteModelWire.Json(
                RemoteModelWire.AnthropicMessage("partial", "refusal", refusalCategory: "harmful_request")));
            using (var backend = Backend(buffered, stream: false))
            {
                var failure = await Assert.ThrowsExceptionAsync<ChatBackendOutputException>(
                    () => backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None));

                StringAssert.Contains(failure.Message, "refused");
                StringAssert.Contains(failure.Message, "harmful_request");
            }

            var streamed = new RecordingHandler(_ => RemoteModelWire.Sse(
                RemoteModelWire.AnthropicStream("partial", refusalCategory: "harmful_request")));
            using (var backend = Backend(streamed))
            {
                var failure = await Assert.ThrowsExceptionAsync<ChatBackendOutputException>(
                    () => backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None));

                StringAssert.Contains(failure.Message, "harmful_request",
                    "streaming is the DEFAULT path, so a refusal missed here is a refusal missed always");
            }
        }

        /// <summary>
        ///   An answer that stopped at <c>Fallen8:Chat:Anthropic:MaxTokens</c> is an INCOMPLETE answer,
        ///   not a short one, and the refusal names that setting - which is the only place the cause is
        ///   still known. Handed on as a draft it reads as complete, and the next thing that fails is
        ///   whatever consumes it. Both paths, because streaming is the default and its stop reason
        ///   arrives on a different event.
        /// </summary>
        [TestMethod]
        public async Task AnAnswerStoppedAtMaxTokens_IsAnOutputFailureNamingTheCeiling_NotAShortDraft()
        {
            var buffered = new RecordingHandler(_ => RemoteModelWire.Json(
                RemoteModelWire.AnthropicMessage("half a filt", "max_tokens")));
            using (var backend = Backend(buffered, stream: false))
            {
                var failure = await Assert.ThrowsExceptionAsync<ChatBackendOutputException>(
                    () => backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None));

                StringAssert.Contains(failure.Message, "Fallen8:Chat:Anthropic:MaxTokens");
                StringAssert.Contains(failure.Message, MaxTokens.ToString(CultureInfo.InvariantCulture),
                    "the configured ceiling is the number to raise, so it is the number to print");
                StringAssert.Contains(failure.Message, "11 character(s)");
            }

            var streamed = new RecordingHandler(_ => RemoteModelWire.Sse(
                RemoteModelWire.AnthropicStream("half a filt", stopReason: "max_tokens")));
            using (var backend = Backend(streamed))
            {
                var failure = await Assert.ThrowsExceptionAsync<ChatBackendOutputException>(
                    () => backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None));

                StringAssert.Contains(failure.Message, "Fallen8:Chat:Anthropic:MaxTokens",
                    "streaming is the DEFAULT path, so a ceiling missed here is a ceiling missed always");
            }

            // A stop reason this SDK's enum does not carry must read as "not the ceiling" rather than
            // faulting: a gateway is free to invent one, and the answer it sent is still an answer.
            var unknown = new RecordingHandler(_ => RemoteModelWire.Json(
                RemoteModelWire.AnthropicMessage("hello", "quota_exhausted")));
            using (var backend = Backend(unknown, stream: false))
            {
                var result = await backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None);
                Assert.AreEqual("hello", result.Content);
            }

            // And one that is not even a string. The SDK's Raw()/Value() accessors THROW on that,
            // inside the response loop, where the only catch that would take it reports a truncated
            // stream - a fault report about the wrong thing entirely.
            var notAString = new RecordingHandler(_ => RemoteModelWire.Json(
                RemoteModelWire.AnthropicMessage("hello", "end_turn")
                    .Replace("\"stop_reason\":\"end_turn\"", "\"stop_reason\":7", StringComparison.Ordinal)));
            using (var backend = Backend(notAString, stream: false))
            {
                var result = await backend.ChatAsync(RemoteModelWire.Turns(), null, CancellationToken.None);
                Assert.AreEqual("hello", result.Content,
                    "an unreadable stop reason is not the ceiling, and not a truncation either");
            }
        }

        #region live anthropic smoke

        /// <summary>
        ///   OPT-IN live smoke against the real Anthropic endpoint. Remove [Ignore] and set
        ///   F8_TEST_ANTHROPIC_API_KEY to run. Deliberately keyed on the F8_TEST_ prefix and not on the
        ///   compose variable: keying it off F8_ANTHROPIC_API_KEY would make `dotnet test` place live
        ///   billed calls from any machine with a working deployment.
        /// </summary>
        [TestMethod]
        [Ignore("Live-endpoint smoke: set F8_TEST_ANTHROPIC_API_KEY and remove [Ignore] to run.")]
        [TestCategory("LiveModel")]
        public async Task Anthropic_Chat_AnswersAPrompt()
        {
            var apiKey = Environment.GetEnvironmentVariable("F8_TEST_ANTHROPIC_API_KEY");
            if (String.IsNullOrEmpty(apiKey))
            {
                Assert.Inconclusive("F8_TEST_ANTHROPIC_API_KEY not set.");
            }

            var endpoint = Environment.GetEnvironmentVariable("F8_TEST_ANTHROPIC_ENDPOINT")
                ?? "https://api.anthropic.com";
            var model = Environment.GetEnvironmentVariable("F8_TEST_ANTHROPIC_CHAT_MODEL") ?? "claude-opus-5";

            var target = RemoteModelTarget.Anthropic("Fallen8:Chat:Anthropic", endpoint, model, apiKey);
            using var backend = new AnthropicChatBackend(target, maxTokens: 64, stream: true, logger: null);
            var result = await backend.ChatAsync(
                new[] { new ChatTurn("user", "Reply with exactly one word: hello.") },
                new ChatBackendOptions(),
                CancellationToken.None);

            Assert.IsFalse(String.IsNullOrWhiteSpace(result.Content), "expected a non-empty reply");
            Assert.AreEqual(model, result.Model, "the configured model is what is reported back");
            Assert.IsTrue(result.CompletionTokens > 0, "the Messages API reports usage on every completion");
        }

        #endregion
    }
}
