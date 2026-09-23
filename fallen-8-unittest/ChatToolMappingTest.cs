// MIT License
//
// ChatToolMappingTest.cs
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
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Chat;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Tool calling as each of the three providers spells it (feature agent-host). One file for
    ///   all three, because the CLAIM is the same for each and only the spelling differs: the
    ///   caller's tool reaches the wire with its schema intact, a call the model makes comes back
    ///   with an id, a name and its arguments, and a replayed call plus its result go out again in
    ///   that provider's shape.
    ///
    ///   <para>These are wire tests against an injected transport, so no provider is contacted and
    ///   no model is needed. What they cannot prove is that a real model then behaves - that is
    ///   what the recorded live Phase 0 measurement is for, and it is why the prompts that ship
    ///   with the agent host are a tested contract of their own.</para>
    /// </summary>
    [TestClass]
    public class ChatToolMappingTest
    {
        private static JsonElement Schema(String json) => JsonDocument.Parse(json).RootElement.Clone();

        private static ChatBackendOptions WithTool(String parameters =
            "{\"type\":\"object\",\"properties\":{\"namespace\":{\"type\":\"string\"}},\"required\":[\"namespace\"]}")
        {
            return new ChatBackendOptions
            {
                Tools = new[]
                {
                    new ChatTool
                    {
                        Name = "count_vertices",
                        Description = "Count the vertices in one namespace.",
                        Parameters = Schema(parameters),
                    },
                },
            };
        }

        private static IReadOnlyList<ChatTurn> Ask() =>
            new[] { new ChatTurn("user", "how many vertices?") };

        /// <summary>A conversation that already called a tool and is handing back the result, which
        /// is the shape every second turn of an agent run has.</summary>
        private static IReadOnlyList<ChatTurn> Replay()
        {
            return new[]
            {
                new ChatTurn("user", "how many vertices?"),
                new ChatTurn("assistant", null, new[]
                {
                    new ChatToolCall
                    {
                        Id = "call_1",
                        Name = "count_vertices",
                        Arguments = Schema("{\"namespace\":\"default\"}"),
                    },
                }),
                new ChatTurn("tool", "{\"count\":8}", null, "call_1"),
            };
        }

        #region the Ollama protocol, which serves the default backend

        private sealed class OllamaStub : HttpMessageHandler
        {
            private readonly String _ndjson;

            internal OllamaStub(String ndjson)
            {
                _ndjson = ndjson;
            }

            internal String Body
            {
                get; private set;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Body = await request.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_ndjson, Encoding.UTF8, "application/x-ndjson"),
                };
            }
        }

        private const String OllamaToolReply =
            "{\"model\":\"m\",\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":"
            + "[{\"id\":\"call_9\",\"function\":{\"name\":\"count_vertices\","
            + "\"arguments\":{\"namespace\":\"default\"}}}]},\"done\":true,\"done_reason\":\"stop\","
            + "\"prompt_eval_count\":11,\"eval_count\":0,\"eval_duration\":1000000,\"total_duration\":2000000}";

        private static OllamaChatBackend OllamaBackend(OllamaStub stub, Boolean stream = true)
        {
            var connection = NoSQL.GraphDB.App.Helper.OllamaConnection.Sidecar(
                "Fallen8:Chat:Ollama", "http://localhost:11434", "m");
            return new OllamaChatBackend(connection, stream, logger: null, stub);
        }

        /// <summary>
        ///   The tool reaches the wire in this protocol's envelope with its schema BYTE FOR BYTE, and
        ///   the reply's call comes back whole. The schema matters more than it looks: this SDK has a
        ///   typed parameters class that models only part of JSON Schema, so mapping through it would
        ///   drop the parts it does not describe - here, the <c>required</c> array.
        /// </summary>
        [TestMethod]
        public async Task Ollama_SendsTheToolWithItsSchemaIntact_AndReadsTheCallBack()
        {
            var stub = new OllamaStub(OllamaToolReply);
            using var backend = OllamaBackend(stub);

            var result = await backend.ChatAsync(Ask(), WithTool(), CancellationToken.None);

            var sent = JsonDocument.Parse(stub.Body).RootElement;
            var tool = sent.GetProperty("tools")[0];
            Assert.AreEqual("function", tool.GetProperty("type").GetString());
            var function = tool.GetProperty("function");
            Assert.AreEqual("count_vertices", function.GetProperty("name").GetString());
            Assert.AreEqual("Count the vertices in one namespace.", function.GetProperty("description").GetString());
            var schema = function.GetProperty("parameters");
            Assert.AreEqual("object", schema.GetProperty("type").GetString());
            Assert.AreEqual("string",
                schema.GetProperty("properties").GetProperty("namespace").GetProperty("type").GetString());
            Assert.AreEqual("namespace", schema.GetProperty("required")[0].GetString(),
                "the schema travels verbatim, so a member the SDK's typed shape cannot express survives");

            Assert.AreEqual(1, result.ToolCalls.Count);
            Assert.AreEqual("call_9", result.ToolCalls[0].Id);
            Assert.AreEqual("count_vertices", result.ToolCalls[0].Name);
            Assert.AreEqual("default", result.ToolCalls[0].Arguments.GetProperty("namespace").GetString());
            Assert.AreEqual(JsonValueKind.Object, result.ToolCalls[0].Arguments.ValueKind,
                "arguments reach the seam as JSON, so every caller reads them the same way");
        }

        /// <summary>
        ///   A request carrying tools is not streamed, on any backend, for the reason stated once on
        ///   <see cref="ChatBackendOptions" />. Asserted on the WIRE rather than on a flag, because
        ///   what matters is the body the provider receives.
        /// </summary>
        [TestMethod]
        public async Task Ollama_TurnsStreamingOffWhenToolsAreOffered_AndLeavesItOnOtherwise()
        {
            var withTools = new OllamaStub(OllamaToolReply);
            using (var backend = OllamaBackend(withTools, stream: true))
            {
                await backend.ChatAsync(Ask(), WithTool(), CancellationToken.None);
            }

            Assert.IsFalse(JsonDocument.Parse(withTools.Body).RootElement.GetProperty("stream").GetBoolean(),
                "tools require the non-streamed request on this client library");

            var plain = new OllamaStub(
                "{\"model\":\"m\",\"message\":{\"role\":\"assistant\",\"content\":\"hi\"},\"done\":true,"
                + "\"prompt_eval_count\":1,\"eval_count\":1,\"eval_duration\":1000000,\"total_duration\":1000000}");
            using (var backend = OllamaBackend(plain, stream: true))
            {
                await backend.ChatAsync(Ask(), null, CancellationToken.None);
            }

            Assert.IsTrue(JsonDocument.Parse(plain.Body).RootElement.GetProperty("stream").GetBoolean(),
                "and a request without tools is unaffected");
        }

        /// <summary>
        ///   Replaying the conversation: the assistant's call goes back out, and the result is
        ///   attributed by TOOL NAME, because this protocol has no call-id field on a message. The
        ///   name is recovered from the call the id refers to, which is why the whole conversation is
        ///   handed to the mapper.
        /// </summary>
        [TestMethod]
        public async Task Ollama_ReplaysACall_AndAttributesTheResultByName()
        {
            var stub = new OllamaStub(
                "{\"model\":\"m\",\"message\":{\"role\":\"assistant\",\"content\":\"there are 8\"},\"done\":true,"
                + "\"prompt_eval_count\":9,\"eval_count\":4,\"eval_duration\":1000000,\"total_duration\":2000000}");
            using var backend = OllamaBackend(stub);

            var result = await backend.ChatAsync(Replay(), WithTool(), CancellationToken.None);

            Assert.AreEqual("there are 8", result.Content);
            var turns = JsonDocument.Parse(stub.Body).RootElement.GetProperty("messages");
            Assert.AreEqual(3, turns.GetArrayLength());

            var assistant = turns[1];
            Assert.AreEqual("call_1", assistant.GetProperty("tool_calls")[0].GetProperty("id").GetString());
            Assert.AreEqual("count_vertices",
                assistant.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
            Assert.AreEqual("default", assistant.GetProperty("tool_calls")[0]
                .GetProperty("function").GetProperty("arguments").GetProperty("namespace").GetString());

            var toolTurn = turns[2];
            Assert.AreEqual("tool", toolTurn.GetProperty("role").GetString());
            Assert.AreEqual("count_vertices", toolTurn.GetProperty("tool_name").GetString(),
                "this protocol matches a result by name, and the name came from the call the id names");

            // The assistant turn's content is an EMPTY STRING and not null, which is not a nicety:
            // this protocol types content as a string and answers 422 to a null, killing the whole
            // request. A tool-calling turn has no text, so null is exactly what reaches this layer.
            // Found against the live service, because a stub accepts whatever it is handed.
            Assert.AreEqual(JsonValueKind.String, assistant.GetProperty("content").ValueKind,
                "a null content is a 422 from this protocol: " + stub.Body);
            Assert.AreEqual(String.Empty, assistant.GetProperty("content").GetString());
        }

        /// <summary>
        ///   A result is attributed to the round that ASKED, not to the first round that happens to
        ///   share its id. Ids are synthesised per reply in this protocol, so two rounds sharing one
        ///   is not a malformed conversation: it is what the previous scheme produced for every
        ///   reply's first call. Resolving by first match anywhere therefore answered round two's
        ///   result with round one's tool name, and the model was told the wrong tool had run.
        ///   <para>Masked in practice only because the shipped agent model never reaches a second
        ///   round; it appears the moment an operator names a tool-capable one.</para>
        /// </summary>
        [TestMethod]
        public async Task Ollama_AttributesAResultToTheNearestPrecedingCall_WhenTwoRoundsShareAnId()
        {
            var stub = new OllamaStub(
                "{\"model\":\"m\",\"message\":{\"role\":\"assistant\",\"content\":\"8 vertices, 5 edges\"},"
                + "\"done\":true,\"prompt_eval_count\":9,\"eval_count\":6,\"eval_duration\":1000000,"
                + "\"total_duration\":2000000}");
            using var backend = OllamaBackend(stub);

            var conversation = new[]
            {
                new ChatTurn("user", "how many vertices and edges?"),
                new ChatTurn("assistant", null, new[]
                {
                    new ChatToolCall
                    {
                        Id = "call_0",
                        Name = "count_vertices",
                        Arguments = Schema("{\"namespace\":\"default\"}"),
                    },
                }),
                new ChatTurn("tool", "{\"count\":8}", null, "call_0"),
                new ChatTurn("assistant", null, new[]
                {
                    new ChatToolCall
                    {
                        Id = "call_0",
                        Name = "count_edges",
                        Arguments = Schema("{\"namespace\":\"default\"}"),
                    },
                }),
                new ChatTurn("tool", "{\"count\":5}", null, "call_0"),
            };

            await backend.ChatAsync(conversation, WithTool(), CancellationToken.None);

            var turns = JsonDocument.Parse(stub.Body).RootElement.GetProperty("messages");
            Assert.AreEqual(5, turns.GetArrayLength());
            Assert.AreEqual("count_vertices", turns[2].GetProperty("tool_name").GetString(),
                "round one's result still answers round one's call: " + stub.Body);
            Assert.AreEqual("count_edges", turns[4].GetProperty("tool_name").GetString(),
                "round two's result answers the NEAREST preceding call, not the first id match: "
                + stub.Body);
        }

        /// <summary>
        ///   A synthesised id names the round it was made in, so a trace of several rounds does not
        ///   read <c>call_0</c> at every step. It stays DERIVED rather than generated, because the
        ///   client echoes it back on the next request: the same reply to the same conversation has
        ///   to produce the same id, which rules out a counter, a random value and the clock.
        ///   <para>This is the only test that exercises the synthesis branch at all: the shared
        ///   fixture's reply carries an id of its own, so the provider-supplied path is what every
        ///   other test here takes.</para>
        /// </summary>
        [TestMethod]
        public async Task Ollama_SynthesisesAnIdPerRound_AndTheSameRoundTwiceGetsTheSameOne()
        {
            var stub = new OllamaStub(
                "{\"model\":\"m\",\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":"
                + "[{\"function\":{\"name\":\"count_vertices\",\"arguments\":{\"namespace\":\"default\"}}}]},"
                + "\"done\":true,\"done_reason\":\"stop\",\"prompt_eval_count\":11,\"eval_count\":0,"
                + "\"eval_duration\":1000000,\"total_duration\":2000000}");
            using var backend = OllamaBackend(stub);

            // Conversations of DIFFERENT length, because that is what distinguishes the rounds: Ask
            // is one turn and Replay is three. Two calls on the same conversation must agree.
            var first = await backend.ChatAsync(Ask(), WithTool(), CancellationToken.None);
            var second = await backend.ChatAsync(Replay(), WithTool(), CancellationToken.None);
            var again = await backend.ChatAsync(Ask(), WithTool(), CancellationToken.None);

            Assert.AreEqual(1, first.ToolCalls.Count);
            Assert.AreNotEqual(first.ToolCalls[0].Id, second.ToolCalls[0].Id,
                "two rounds must not both be call_0, or a trace cannot tell their steps apart");
            Assert.AreEqual(first.ToolCalls[0].Id, again.ToolCalls[0].Id,
                "and the id is derived, not generated: the client echoes it back, so the same reply "
                + "to the same conversation has to produce the same id");
        }

        #endregion

        #region OpenAI

        private static OpenAIChatBackend OpenAIBackend(RecordingHandler stub) =>
            new OpenAIChatBackend(RemoteModelWire.OpenAITarget(), stream: true, logger: null, stub);

        private const String OpenAIToolReply =
            "{\"id\":\"c1\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"gpt-4o-mini\","
            + "\"choices\":[{\"index\":0,\"finish_reason\":\"tool_calls\",\"message\":{\"role\":\"assistant\","
            + "\"content\":null,\"tool_calls\":[{\"id\":\"call_7\",\"type\":\"function\",\"function\":"
            + "{\"name\":\"count_vertices\",\"arguments\":\"{\\\"namespace\\\":\\\"default\\\"}\"}}]}}],"
            + "\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":0,\"total_tokens\":11}}";

        /// <summary>
        ///   The same claim in this provider's spelling, where a tool is a function tool on the
        ///   options and a call's arguments are a JSON STRING rather than an object. They are parsed
        ///   back into JSON at the seam, so a caller reads arguments the same way whatever served
        ///   the call.
        /// </summary>
        [TestMethod]
        public async Task OpenAI_SendsTheToolAndReadsTheCallBack_ParsingItsStringArguments()
        {
            var stub = new RecordingHandler(_ => RemoteModelWire.Json(OpenAIToolReply));
            using var backend = OpenAIBackend(stub);

            var result = await backend.ChatAsync(Ask(), WithTool(), CancellationToken.None);

            var sent = JsonDocument.Parse(stub.Bodies[0]).RootElement;
            // This SDK OMITS the field rather than sending false, so the claim is "not streaming"
            // rather than "stream is false" - asserting the latter passed only by accident of which
            // provider was being tested.
            Assert.IsTrue(!sent.TryGetProperty("stream", out var streaming) || !streaming.GetBoolean(),
                "tools mean no streaming here too: " + stub.Bodies[0]);
            var function = sent.GetProperty("tools")[0].GetProperty("function");
            Assert.AreEqual("count_vertices", function.GetProperty("name").GetString());
            Assert.AreEqual("namespace",
                function.GetProperty("parameters").GetProperty("required")[0].GetString(),
                "the schema travels verbatim");

            Assert.AreEqual(1, result.ToolCalls.Count);
            Assert.AreEqual("call_7", result.ToolCalls[0].Id);
            Assert.AreEqual("count_vertices", result.ToolCalls[0].Name);
            Assert.AreEqual("default", result.ToolCalls[0].Arguments.GetProperty("namespace").GetString(),
                "the provider's JSON-in-a-string arguments are parsed, so every backend reports the same shape");
        }

        /// <summary>
        ///   Replay in this provider's shape: the assistant turn becomes its tool calls and the
        ///   result becomes a tool message carrying the call id.
        /// </summary>
        [TestMethod]
        public async Task OpenAI_ReplaysACallAndItsResult_KeyedByCallId()
        {
            var stub = new RecordingHandler(_ => RemoteModelWire.Json(
                "{\"id\":\"c2\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"gpt-4o-mini\","
                + "\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\","
                + "\"content\":\"there are 8\"}}],\"usage\":{\"prompt_tokens\":9,\"completion_tokens\":4,"
                + "\"total_tokens\":13}}"));
            using var backend = OpenAIBackend(stub);

            var result = await backend.ChatAsync(Replay(), WithTool(), CancellationToken.None);

            Assert.AreEqual("there are 8", result.Content);
            var turns = JsonDocument.Parse(stub.Bodies[0]).RootElement.GetProperty("messages");
            var assistant = turns.EnumerateArray().Single(m => m.GetProperty("role").GetString() == "assistant");
            Assert.AreEqual("call_1", assistant.GetProperty("tool_calls")[0].GetProperty("id").GetString());
            var toolTurn = turns.EnumerateArray().Single(m => m.GetProperty("role").GetString() == "tool");
            Assert.AreEqual("call_1", toolTurn.GetProperty("tool_call_id").GetString());
        }

        #endregion

        #region Anthropic

        private static AnthropicChatBackend AnthropicBackend(RecordingHandler stub) =>
            new AnthropicChatBackend(RemoteModelWire.AnthropicTarget(), maxTokens: 512, stream: true,
                logger: null, stub);

        private const String AnthropicToolReply =
            "{\"id\":\"m1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-5\","
            + "\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_5\",\"name\":\"count_vertices\","
            + "\"input\":{\"namespace\":\"default\"}}],\"stop_reason\":\"tool_use\","
            + "\"usage\":{\"input_tokens\":11,\"output_tokens\":0}}";

        /// <summary>
        ///   The same claim again, in the provider whose model is content BLOCKS rather than a
        ///   message with fields: a tool is a top-level tool with an input schema, and a call is a
        ///   <c>tool_use</c> block read out of the reply's content.
        /// </summary>
        [TestMethod]
        public async Task Anthropic_SendsTheToolAsAnInputSchema_AndReadsAToolUseBlockBack()
        {
            var stub = new RecordingHandler(_ => RemoteModelWire.Json(AnthropicToolReply));
            using var backend = AnthropicBackend(stub);

            var result = await backend.ChatAsync(Ask(), WithTool(), CancellationToken.None);

            var sent = JsonDocument.Parse(stub.Bodies[0]).RootElement;
            var tool = sent.GetProperty("tools")[0];
            Assert.AreEqual("count_vertices", tool.GetProperty("name").GetString());
            var schema = tool.GetProperty("input_schema");
            Assert.AreEqual("object", schema.GetProperty("type").GetString());
            Assert.AreEqual("namespace", schema.GetProperty("required")[0].GetString(),
                "the schema travels verbatim rather than through the SDK's typed subset");

            Assert.AreEqual(1, result.ToolCalls.Count);
            Assert.AreEqual("toolu_5", result.ToolCalls[0].Id);
            Assert.AreEqual("count_vertices", result.ToolCalls[0].Name);
            Assert.AreEqual("default", result.ToolCalls[0].Arguments.GetProperty("namespace").GetString());
        }

        /// <summary>
        ///   Replay here is the most different of the three: the assistant's call is a
        ///   <c>tool_use</c> block, and the RESULT is a <c>tool_result</c> block on a USER turn,
        ///   because this protocol has no tool role at all.
        /// </summary>
        [TestMethod]
        public async Task Anthropic_ReplaysACallAsABlock_AndItsResultAsAUserTurn()
        {
            var stub = new RecordingHandler(_ => RemoteModelWire.Json(
                "{\"id\":\"m2\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-5\","
                + "\"content\":[{\"type\":\"text\",\"text\":\"there are 8\"}],\"stop_reason\":\"end_turn\","
                + "\"usage\":{\"input_tokens\":9,\"output_tokens\":4}}"));
            using var backend = AnthropicBackend(stub);

            var result = await backend.ChatAsync(Replay(), WithTool(), CancellationToken.None);

            Assert.AreEqual("there are 8", result.Content);
            var turns = JsonDocument.Parse(stub.Bodies[0]).RootElement.GetProperty("messages");
            Assert.AreEqual(3, turns.GetArrayLength());

            var use = turns[1].GetProperty("content")[0];
            Assert.AreEqual("tool_use", use.GetProperty("type").GetString());
            Assert.AreEqual("call_1", use.GetProperty("id").GetString());
            Assert.AreEqual("default", use.GetProperty("input").GetProperty("namespace").GetString());

            Assert.AreEqual("user", turns[2].GetProperty("role").GetString(),
                "a tool result is a user turn here: this protocol has no tool role");
            var resultBlock = turns[2].GetProperty("content")[0];
            Assert.AreEqual("tool_result", resultBlock.GetProperty("type").GetString());
            Assert.AreEqual("call_1", resultBlock.GetProperty("tool_use_id").GetString());
        }

        #endregion

        /// <summary>
        ///   A tool that takes no arguments still goes out with a schema, on all three: providers
        ///   reject a missing one, and "no arguments" is a schema rather than an absence. The
        ///   caller sends no <c>parameters</c> at all here, which is the shape that produced an
        ///   undefined element.
        /// </summary>
        [TestMethod]
        public async Task EveryBackend_SendsAnEmptyObjectSchema_WhenAToolDeclaresNoArguments()
        {
            var noArguments = new ChatBackendOptions
            {
                Tools = new[] { new ChatTool { Name = "ping", Description = "Ping." } },
            };

            var ollama = new OllamaStub(OllamaToolReply);
            using (var backend = OllamaBackend(ollama))
            {
                await backend.ChatAsync(Ask(), noArguments, CancellationToken.None);
            }

            Assert.AreEqual("object", JsonDocument.Parse(ollama.Body).RootElement
                .GetProperty("tools")[0].GetProperty("function").GetProperty("parameters")
                .GetProperty("type").GetString());

            var openAi = new RecordingHandler(_ => RemoteModelWire.Json(OpenAIToolReply));
            using (var backend = OpenAIBackend(openAi))
            {
                await backend.ChatAsync(Ask(), noArguments, CancellationToken.None);
            }

            Assert.AreEqual("object", JsonDocument.Parse(openAi.Bodies[0]).RootElement
                .GetProperty("tools")[0].GetProperty("function").GetProperty("parameters")
                .GetProperty("type").GetString());

            var anthropic = new RecordingHandler(_ => RemoteModelWire.Json(AnthropicToolReply));
            using (var backend = AnthropicBackend(anthropic))
            {
                await backend.ChatAsync(Ask(), noArguments, CancellationToken.None);
            }

            Assert.AreEqual("object", JsonDocument.Parse(anthropic.Bodies[0]).RootElement
                .GetProperty("tools")[0].GetProperty("input_schema").GetProperty("type").GetString());
        }
    }
}
