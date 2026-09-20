// MIT License
//
// ChatEndpointTest.Tools.cs
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
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Chat;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Tool calling as <c>POST /chat</c> exposes it (feature agent-host). The provider-specific
    ///   spellings are pinned in <c>ChatToolMappingTest</c>; what this file pins is the CONTRACT a
    ///   client sees: what reaches the backend, what comes back, and which malformed requests are
    ///   refused at the edge rather than sent to a provider that will refuse them less clearly.
    /// </summary>
    public partial class ChatEndpointTest
    {
        /// <summary>A fake that records what it was offered and answers with a tool call.</summary>
        private sealed class ToolRecordingBackend : IChatBackend
        {
            private readonly IReadOnlyList<ChatToolCall> _answer;

            internal ToolRecordingBackend(IReadOnlyList<ChatToolCall> answer = null)
            {
                _answer = answer;
            }

            internal ChatBackendOptions LastOptions
            {
                get; private set;
            }

            internal IReadOnlyList<ChatTurn> LastTurns
            {
                get; private set;
            }

            public Task<ChatBackendResult> ChatAsync(IReadOnlyList<ChatTurn> messages,
                ChatBackendOptions options, CancellationToken cancellationToken)
            {
                LastOptions = options;
                LastTurns = messages;
                return Task.FromResult(new ChatBackendResult
                {
                    Content = _answer == null ? "plain answer" : String.Empty,
                    ToolCalls = _answer,
                    Model = options?.Model,
                    PromptTokens = 1,
                    CompletionTokens = 1,
                });
            }
        }

        private static IReadOnlyList<ChatToolCall> OneCall()
        {
            return new[]
            {
                new ChatToolCall
                {
                    Id = "call_1",
                    Name = "count_vertices",
                    Arguments = JsonDocument.Parse("{\"namespace\":\"default\"}").RootElement.Clone(),
                },
            };
        }

        private const String OneTool =
            "\"tools\":[{\"name\":\"count_vertices\",\"description\":\"Count vertices.\","
            + "\"parameters\":{\"type\":\"object\",\"properties\":{\"namespace\":{\"type\":\"string\"}}}}]";

        /// <summary>
        ///   The round trip an agent's first turn makes: offer a tool, get a call back. The answer
        ///   has NO content, which is the ordinary shape of a tool-calling turn and used to be a 502
        ///   ("the chat backend returned an empty response") before tools existed.
        /// </summary>
        [TestMethod]
        public async Task OfferingATool_ReachesTheBackend_AndACallComesBackWithNoContent()
        {
            var backend = new ToolRecordingBackend(OneCall());
            using var factory = new ChatFactory(enabled: true, backend);
            using var client = factory.CreateClient();

            using var response = await client.PostAsync("/chat", Json(
                "{\"purpose\":\"agent\",\"messages\":[{\"role\":\"user\",\"content\":\"how many?\"}]," + OneTool + "}"));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "a tool call with no text is an answer, not an empty response");

            Assert.AreEqual(1, backend.LastOptions.Tools.Count, "the tool reached the backend");
            Assert.AreEqual("count_vertices", backend.LastOptions.Tools[0].Name);
            Assert.AreEqual("string", backend.LastOptions.Tools[0].Parameters
                .GetProperty("properties").GetProperty("namespace").GetProperty("type").GetString(),
                "with its schema, which is passed through rather than interpreted");

            var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.AreEqual(String.Empty, payload.GetProperty("content").GetString());
            var calls = payload.GetProperty("toolCalls");
            Assert.AreEqual(1, calls.GetArrayLength());
            Assert.AreEqual("call_1", calls[0].GetProperty("id").GetString());
            Assert.AreEqual("count_vertices", calls[0].GetProperty("name").GetString());
            Assert.AreEqual("default",
                calls[0].GetProperty("arguments").GetProperty("namespace").GetString());
        }

        /// <summary>
        ///   The agent's second turn: replay the call the model made and hand back the result. The
        ///   assistant turn has NO content, which the message contract has to admit or the call the
        ///   result answers cannot be replayed at all.
        /// </summary>
        [TestMethod]
        public async Task ReplayingACallAndItsResult_IsAcceptedWithNoContentOnTheAssistantTurn()
        {
            var backend = new ToolRecordingBackend();
            using var factory = new ChatFactory(enabled: true, backend);
            using var client = factory.CreateClient();

            using var response = await client.PostAsync("/chat", Json(
                "{\"purpose\":\"agent\",\"messages\":["
                + "{\"role\":\"user\",\"content\":\"how many?\"},"
                + "{\"role\":\"assistant\",\"toolCalls\":[{\"id\":\"call_1\",\"name\":\"count_vertices\","
                + "\"arguments\":{\"namespace\":\"default\"}}]},"
                + "{\"role\":\"tool\",\"content\":\"{\\\"count\\\":8}\",\"toolCallId\":\"call_1\"}"
                + "]," + OneTool + "}"));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(3, backend.LastTurns.Count);

            var assistant = backend.LastTurns[1];
            Assert.AreEqual(1, assistant.ToolCalls.Count, "the call survives the hop");
            Assert.AreEqual("call_1", assistant.ToolCalls[0].Id);
            Assert.AreEqual("default", assistant.ToolCalls[0].Arguments.GetProperty("namespace").GetString());

            Assert.AreEqual("call_1", backend.LastTurns[2].ToolCallId, "and so does what the result answers");
        }

        /// <summary>
        ///   The malformed requests that are refused HERE rather than forwarded. Each would otherwise
        ///   come back as a provider's own 400 about a request the caller never saw, and two of them
        ///   would be worse than that: a result nobody can match, and a call nobody can route.
        /// </summary>
        [TestMethod]
        public async Task MalformedToolRequests_AreRefusedAtTheEdge_WithWhatIsWrong()
        {
            var backend = new ToolRecordingBackend(OneCall());
            using var factory = new ChatFactory(enabled: true, backend);
            using var client = factory.CreateClient();

            var cases = new[]
            {
                (Body: "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],"
                    + "\"tools\":[{\"description\":\"no name\"}]}",
                    Expect: "name", Why: "a nameless tool cannot be called"),
                (Body: "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],"
                    + "\"tools\":[{\"name\":\"t\"},{\"name\":\"t\"}]}",
                    Expect: "more than once", Why: "two tools under one name is a call that cannot be routed"),
                (Body: "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],"
                    + "\"tools\":[{\"name\":\"t\",\"parameters\":[1,2]}]}",
                    Expect: "JSON Schema object", Why: "an array is not a schema for arguments"),
                (Body: "{\"messages\":[{\"role\":\"tool\",\"content\":\"{}\"}]}",
                    Expect: "toolCallId", Why: "a result nobody can match is a result the model cannot use"),
                (Body: "{\"messages\":[{\"role\":\"assistant\"}]}",
                    Expect: "content", Why: "no content and no calls is not a turn"),
            };

            foreach (var (body, expect, why) in cases)
            {
                using var response = await client.PostAsync("/chat", Json(body));

                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, why);

                // The whole body rather than its detail: a missing tool name is refused by model
                // validation before the controller runs, and that answer carries its reason under
                // "errors" instead. Both are honest 400s naming the field, and which layer catches
                // which shape is not something this test should pin.
                var refusal = await response.Content.ReadAsStringAsync();

                // Case-insensitive because the CLAIM is that the refusal names the offending field,
                // not how it capitalises it: model validation says "Name", the controller's own
                // sentence says "name", and both are the field being named.
                Assert.IsTrue(refusal.IndexOf(expect, StringComparison.OrdinalIgnoreCase) >= 0,
                    why + " -> " + refusal);
            }

            Assert.IsNull(backend.LastOptions, "and none of them reached the backend");
        }

        /// <summary>
        ///   An answer with neither text nor calls is still unusable, and still a 502. Tools widened
        ///   what counts as an answer; they did not remove the floor.
        /// </summary>
        [TestMethod]
        public async Task AnAnswerWithNeitherTextNorCalls_IsStillABadGateway()
        {
            var backend = new FakeChatBackend((_, options, _) => Task.FromResult(new ChatBackendResult
            {
                Content = String.Empty,
                ToolCalls = null,
                Model = options?.Model,
            }));
            using var factory = new ChatFactory(enabled: true, backend);
            using var client = factory.CreateClient();

            using var response = await client.PostAsync("/chat", Json(
                "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]," + OneTool + "}"));

            Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
        }

        /// <summary>
        ///   A request that offers no tools carries nothing tool-shaped to the backend - not an
        ///   empty list - because a provider that sees a tools field can behave differently from one
        ///   that never saw it.
        /// </summary>
        [TestMethod]
        public async Task ARequestWithoutTools_CarriesNoToolsAtAll()
        {
            var backend = new ToolRecordingBackend();
            using var factory = new ChatFactory(enabled: true, backend);
            using var client = factory.CreateClient();

            using var response = await client.PostAsync("/chat", Json(
                "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}"));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(backend.LastOptions == null || backend.LastOptions.Tools == null,
                "no tools offered means no tools on the call");

            var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.IsFalse(payload.TryGetProperty("toolCalls", out _),
                "and an answer with no calls says nothing about them");
        }
    }
}
