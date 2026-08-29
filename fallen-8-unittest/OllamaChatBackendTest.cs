// MIT License
//
// OllamaChatBackendTest.cs
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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Chat;
using NoSQL.GraphDB.App.Helper;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The Ollama-protocol chat backend against a stubbed wire (feature nahil-backend):
    ///   what the outbound request body says, and what a stream that does not finish must NOT be
    ///   allowed to look like.
    ///
    ///   <para>These are the only tests that see the real OllamaSharp serialization, so they are
    ///   where "the configured model reaches the body verbatim" and "the stop sequences actually go
    ///   on the wire" are actually true rather than asserted one layer above where they are decided.</para>
    /// </summary>
    [TestClass]
    public class OllamaChatBackendTest
    {
        /// <summary>Replies with a canned NDJSON chunk sequence and records what was asked.</summary>
        private sealed class WireStub : HttpMessageHandler
        {
            private readonly String _ndjson;
            private readonly Boolean _truncate;

            public WireStub(String ndjson, Boolean truncate = false)
            {
                _ndjson = ndjson;
                _truncate = truncate;
            }

            public String RequestBody
            {
                get; private set;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                RequestBody = request.Content == null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken);

                if (_truncate)
                {
                    // A connection that dies part-way through the body: the client has already seen
                    // real tokens when the read fails.
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new DyingStream(Encoding.UTF8.GetBytes(_ndjson)))
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_ndjson, Encoding.UTF8, "application/x-ndjson")
                };
            }
        }

        /// <summary>Yields its bytes and then faults, the way an aborted response body does.</summary>
        private sealed class DyingStream : System.IO.Stream
        {
            private readonly Byte[] _payload;
            private Int32 _position;

            public DyingStream(Byte[] payload)
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

        private static String Delta(String content)
        {
            return "{\"model\":\"m\",\"message\":{\"role\":\"assistant\",\"content\":\"" + content
                + "\"},\"done\":false}\n";
        }

        private static String Done(Int32 promptTokens = 11, Int32 completionTokens = 5)
        {
            return "{\"model\":\"m\",\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true,"
                + "\"total_duration\":2000000000,\"prompt_eval_count\":" + promptTokens
                + ",\"eval_count\":" + completionTokens + ",\"eval_duration\":1000000000}\n";
        }

        private static OllamaChatBackend Backend(WireStub stub, Boolean stream = true)
        {
            return new OllamaChatBackend(
                OllamaConnection.Sidecar("Fallen8:Chat:Ollama", "http://localhost:11434", "phi4-f8-mini:latest"),
                stream, logger: null, stub);
        }

        private static IReadOnlyList<ChatTurn> Turns()
        {
            return new[] { new ChatTurn("user", "draft a vertex filter for label person") };
        }

        /// <summary>
        ///   The deltas are accumulated into one answer and the terminal chunk supplies the stats the
        ///   NL-assist UX renders. Streaming is the default, so this is the normal path.
        /// </summary>
        [TestMethod]
        public async Task AStreamedCompletion_IsAccumulated_AndItsStatsComeFromTheTerminalChunk()
        {
            var stub = new WireStub(Delta("return (v) =>") + Delta(" v.Label ==") + Delta(" \\\"person\\\";") + Done());
            using var backend = Backend(stub);

            var result = await backend.ChatAsync(Turns(), null, CancellationToken.None);

            Assert.AreEqual("return (v) => v.Label == \"person\";", result.Content);
            Assert.AreEqual("phi4-f8-mini:latest", result.Model);
            Assert.AreEqual(11L, result.PromptTokens);
            Assert.AreEqual(5L, result.CompletionTokens);
            Assert.AreEqual(2000d, result.DurationMs.Value, 0.001, "2e9 nanoseconds is 2000 ms");
            Assert.AreEqual(5d, result.TokensPerSecond.Value, 0.001, "5 tokens in 1e9 nanoseconds");
        }

        /// <summary>Streaming is asked for by default, and the switch really turns it off.</summary>
        [TestMethod]
        public async Task TheStreamSwitch_DecidesWhatTheRequestAsksFor()
        {
            var streaming = new WireStub(Delta("hi") + Done());
            using (var backend = Backend(streaming, stream: true))
            {
                await backend.ChatAsync(Turns(), null, CancellationToken.None);
            }

            StringAssert.Contains(streaming.RequestBody, "\"stream\":true");

            var buffered = new WireStub(Done());
            using (var backend = Backend(buffered, stream: false))
            {
                await backend.ChatAsync(Turns(), null, CancellationToken.None);
            }

            StringAssert.Contains(buffered.RequestBody, "\"stream\":false");
        }

        /// <summary>
        ///   The configured model reaches the body verbatim, tag included. This is the assertion the
        ///   whole tag-pinning decision rests on: if anything normalized the string, configuring
        ///   ":latest" would be theatre.
        /// </summary>
        [TestMethod]
        public async Task TheConfiguredModel_ReachesTheBodyVerbatim()
        {
            var stub = new WireStub(Done());
            using var backend = Backend(stub);

            await backend.ChatAsync(Turns(), null, CancellationToken.None);

            StringAssert.Contains(stub.RequestBody, "\"model\":\"phi4-f8-mini:latest\"");
        }

        /// <summary>
        ///   Stop sequences and temperature travel TOGETHER. Sending one must not drop the other:
        ///   that is the merge the request-options builder exists for, and the failure it prevents is
        ///   silent (a request that keeps its stop tokens but loses temperature 0.1 just answers
        ///   differently).
        /// </summary>
        [TestMethod]
        public async Task StopSequencesAndTemperature_AreBothSent()
        {
            var both = new WireStub(Done());
            using (var backend = Backend(both))
            {
                await backend.ChatAsync(Turns(),
                    new ChatBackendOptions { Temperature = 0.1, Stop = new[] { "<|im_start|>", "<|im_end|>" } },
                    CancellationToken.None);
            }

            var options = JsonDocument.Parse(both.RequestBody).RootElement.GetProperty("options");
            Assert.AreEqual(0.1f, options.GetProperty("temperature").GetSingle(), 0.0001);
            var stop = options.GetProperty("stop");
            Assert.AreEqual(2, stop.GetArrayLength());
            Assert.AreEqual("<|im_start|>", stop[0].GetString());
            Assert.AreEqual("<|im_end|>", stop[1].GetString());

            // Either one alone still travels.
            var stopOnly = new WireStub(Done());
            using (var backend = Backend(stopOnly))
            {
                await backend.ChatAsync(Turns(),
                    new ChatBackendOptions { Stop = new[] { "<|im_end|>" } }, CancellationToken.None);
            }

            StringAssert.Contains(stopOnly.RequestBody, "<|im_end|>");

            // And with neither, no options block is sent at all: every knob stays at the model's own
            // default rather than at whatever this code would have guessed.
            var neither = new WireStub(Done());
            using (var backend = Backend(neither))
            {
                await backend.ChatAsync(Turns(), null, CancellationToken.None);
            }

            Assert.IsFalse(JsonDocument.Parse(neither.RequestBody).RootElement.TryGetProperty("options", out var sent)
                && sent.ValueKind != JsonValueKind.Null,
                "an empty options block would pin knobs the caller never asked about: " + neither.RequestBody);
        }

        /// <summary>
        ///   A stream that ends with no terminal chunk is a TRUNCATION, and it must not be returned as
        ///   a short answer. This is the case that has no exception to notice: the deltas parse, the
        ///   enumeration completes, and only the missing completion marker distinguishes a cut-off
        ///   answer from one the model chose to keep brief.
        /// </summary>
        [TestMethod]
        public async Task AStreamThatNeverCompletes_FailsInsteadOfReturningAShortAnswer()
        {
            var stub = new WireStub(Delta("return (v) =>") + Delta(" v.Label"));
            using var backend = Backend(stub);

            var failure = await Assert.ThrowsExceptionAsync<ChatBackendOutputException>(
                () => backend.ChatAsync(Turns(), null, CancellationToken.None));

            StringAssert.Contains(failure.Message, "without a completion marker");
            StringAssert.Contains(failure.Message, "21 character(s)",
                "the partial length is what lets an operator tell a truncation from an empty answer");
        }

        /// <summary>
        ///   A connection that dies mid-body fails the same way, and says how much had arrived. It
        ///   surfaces as an IOException from the transport, which is emphatically not a completion.
        /// </summary>
        [TestMethod]
        public async Task AStreamThatDiesMidBody_FailsAndReportsWhatArrived()
        {
            var stub = new WireStub(Delta("return (v) =>"), truncate: true);
            using var backend = Backend(stub);

            var failure = await Assert.ThrowsExceptionAsync<ChatBackendOutputException>(
                () => backend.ChatAsync(Turns(), null, CancellationToken.None));

            StringAssert.Contains(failure.Message, "ended early");
            Assert.IsNotNull(failure.InnerException, "the transport fault is kept for the log");
        }

        /// <summary>
        ///   A backend that never answers is NOT a truncation, and must keep reaching the provider's
        ///   503 rather than the truncation's 502. This is the regression a blanket catch caused:
        ///   wrapping every transport fault as "the response ended early" turned a stopped sidecar
        ///   into a 502 that blamed the response for a connection problem, on the DEFAULT path that
        ///   this feature promised not to change.
        /// </summary>
        [TestMethod]
        public async Task AnUnreachableBackend_IsNotReportedAsATruncatedStream()
        {
            using var backend = new OllamaChatBackend(
                OllamaConnection.Sidecar("Fallen8:Chat:Ollama", "http://127.0.0.1:1", "phi4-f8-mini:latest"),
                stream: true, logger: null, new RefusingHandler());

            var failure = await Assert.ThrowsExceptionAsync<HttpRequestException>(
                () => backend.ChatAsync(Turns(), null, CancellationToken.None),
                "a connection that never delivered a token is the provider's 503, not a 502 truncation");
            Assert.IsNotNull(failure);
        }

        /// <summary>A transport that fails before any byte arrives, the way a stopped sidecar does.</summary>
        private sealed class RefusingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                throw new HttpRequestException("Connection refused");
            }
        }

        /// <summary>
        ///   A caller's cancellation stays a cancellation. It must not be re-dressed as a truncated
        ///   response: the provider maps the two to different status codes, and a disconnected client
        ///   is nobody's bad gateway.
        ///
        ///   <para>This is not theoretical. OllamaSharp's iterator ENDS rather than throwing when the
        ///   token trips, so the loop completes with no terminal chunk and looks exactly like a
        ///   truncation - which is what this did until the guard went in. The assertion is on
        ///   <see cref="OperationCanceledException" /> because that is the type both providers' catch
        ///   filters test; which subclass arrives is the runtime's business.</para>
        /// </summary>
        [TestMethod]
        public async Task ACallerCancellation_IsNotReportedAsATruncatedStream()
        {
            var stub = new WireStub(Delta("partial"));
            using var backend = Backend(stub);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            await Assert.ThrowsExceptionAsync<OperationCanceledException>(
                () => backend.ChatAsync(Turns(), null, cancelled.Token),
                "a cancelled call must not be reported as the backend truncating its answer");
        }

        #region live nahil smoke

        /// <summary>
        ///   OPT-IN live smoke against the real Nahil endpoint. Remove [Ignore] and set
        ///   F8_TEST_NAHIL_API_KEY to run. Asserts a non-empty reply arrives and that the model
        ///   name echoed back matches the one sent - the verbatim-model contract on a real wire.
        /// </summary>
        [TestMethod]
        [Ignore("Live-endpoint smoke: set F8_TEST_NAHIL_API_KEY and remove [Ignore] to run.")]
        [TestCategory("LiveModel")]
        public async Task Nahil_Chat_AnswersAPrompt()
        {
            var apiKey = Environment.GetEnvironmentVariable("F8_TEST_NAHIL_API_KEY");
            if (String.IsNullOrEmpty(apiKey))
            {
                Assert.Inconclusive("F8_TEST_NAHIL_API_KEY not set.");
            }

            var endpoint = Environment.GetEnvironmentVariable("F8_TEST_NAHIL_ENDPOINT") ?? "https://api.nahil.dev";
            var model = Environment.GetEnvironmentVariable("F8_TEST_NAHIL_CHAT_MODEL") ?? "phi4-f8-mini:latest";

            var connection = OllamaConnection.Nahil("Fallen8:Chat:Nahil", endpoint, model, apiKey);
            using var backend = new OllamaChatBackend(connection, stream: true, logger: null);
            var result = await backend.ChatAsync(
                new[] { new ChatTurn("user", "Reply with exactly one word: hello.") },
                new ChatBackendOptions(),
                CancellationToken.None);

            Assert.IsFalse(String.IsNullOrWhiteSpace(result.Content), "expected a non-empty reply");
            Assert.AreEqual(model, result.Model, "backend must echo the model name sent verbatim");
        }

        #endregion
    }
}
