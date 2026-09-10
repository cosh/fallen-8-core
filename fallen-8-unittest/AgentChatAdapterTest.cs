// MIT License
//
// AgentChatAdapterTest.cs
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
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Agents.Model;
using NoSQL.GraphDB.App.Chat;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The agent host's model adapter (feature agent-host): <see cref="Fallen8ChatClient" />
    ///   against a REAL hosted apiApp with a fake backend behind it, never a mock of the
    ///   controller.
    ///
    ///   <para>
    ///     <b>Why the whole app rather than a stubbed handler.</b> A stub accepts whatever it is
    ///     handed, so it proves nothing about the wire. The one bug Phase 1a's live verification
    ///     found was exactly of that kind: an assistant turn that only calls a tool has null
    ///     content, and the protocol types that field as a string. Every unit test passed. So the
    ///     adapter is driven through the controller, the request model's validation and the
    ///     provider, and what the backend RECEIVED is asserted on the far side.
    ///   </para>
    /// </summary>
    [TestClass]
    public class AgentChatAdapterTest
    {
        [TestMethod]
        public async Task TheAdapterAsksForThePurposeAgentModelAndNotTheAssistOne()
        {
            IReadOnlyList<ChatTurn> seen = null;
            ChatBackendOptions options = null;

            using var factory = Factory((turns, opts) =>
            {
                seen = turns;
                options = opts;
                return new ChatBackendResult { Content = "eight vertices", Model = opts.Model };
            });

            var response = await Client(factory).GetResponseAsync(
                new[] { new ChatMessage(ChatRole.User, "how many vertices?") });

            Assert.AreEqual("agent-model", options.Model,
                "the adapter sends purpose 'agent', so the gateway must resolve Models:Agent");
            Assert.AreEqual("eight vertices", response.Text);
            Assert.AreEqual("agent-model", response.ModelId,
                "the model the instance chose is reported back, because this host configures none");
            Assert.AreEqual(1, seen.Count);
            Assert.AreEqual("user", seen[0].Role);
        }

        [TestMethod]
        public async Task TheInstructionsTravelAsALeadingSystemTurnBecauseTheyAreNotAMessage()
        {
            // THE regression that shipped past every other test in this feature. Microsoft Agent
            // Framework carries an agent's system prompt on ChatOptions.Instructions, NOT in the
            // message list. An adapter that maps only the messages therefore drops the role prompt
            // silently, and the role prompt is what decides whether a small model calls a tool or
            // invents the answer.
            //
            // Measured against the live service before the fix: the request left with nine prompt
            // tokens and the model answered "the default namespace contains 10,000 vertices" without
            // calling the tool it had been handed. The runtime test that was supposed to cover this
            // asserted on the Instructions PROPERTY, which the framework had set correctly, so it
            // passed throughout. This one asserts what the gateway received.
            IReadOnlyList<ChatTurn> seen = null;
            using var factory = Factory((turns, opts) =>
            {
                seen = turns;
                return new ChatBackendResult { Content = "ok", Model = opts.Model };
            });

            await Client(factory).GetResponseAsync(
                new[] { new ChatMessage(ChatRole.User, "how many vertices?") },
                new ChatOptions { Instructions = "You are a Fallen-8 graph analyst. Never invent a result." });

            Assert.AreEqual(2, seen.Count,
                "the instructions have to become a turn of their own; only the user message arrived");
            Assert.AreEqual("system", seen[0].Role);
            StringAssert.Contains(seen[0].Content, "Never invent");
            Assert.AreEqual("user", seen[1].Role);
        }

        [TestMethod]
        public async Task InstructionsComeBeforeACallersOwnSystemMessageAndBothSurvive()
        {
            // The framework's own precedence: instructions are the agent's identity, a system
            // message is part of the conversation. Dropping either would be a silent narrowing.
            IReadOnlyList<ChatTurn> seen = null;
            using var factory = Factory((turns, opts) =>
            {
                seen = turns;
                return new ChatBackendResult { Content = "ok", Model = opts.Model };
            });

            await Client(factory).GetResponseAsync(new List<ChatMessage>
            {
                new ChatMessage(ChatRole.System, "Answer in German."),
                new ChatMessage(ChatRole.User, "hello"),
            }, new ChatOptions { Instructions = "You are a Fallen-8 graph analyst." });

            Assert.AreEqual(3, seen.Count);
            Assert.AreEqual("system", seen[0].Role);
            StringAssert.Contains(seen[0].Content, "graph analyst");
            Assert.AreEqual("system", seen[1].Role);
            Assert.AreEqual("Answer in German.", seen[1].Content);
            Assert.AreEqual("user", seen[2].Role);
        }

        [TestMethod]
        public async Task BlankInstructionsAddNoTurnAtAll()
        {
            // Whitespace is not an instruction. A blank system turn costs tokens and, on a small
            // model, is one more thing in the prompt that means nothing.
            IReadOnlyList<ChatTurn> seen = null;
            using var factory = Factory((turns, opts) =>
            {
                seen = turns;
                return new ChatBackendResult { Content = "ok", Model = opts.Model };
            });

            await Client(factory).GetResponseAsync(
                new[] { new ChatMessage(ChatRole.User, "hello") },
                new ChatOptions { Instructions = "   " });

            Assert.AreEqual(1, seen.Count);
            Assert.AreEqual("user", seen[0].Role);
        }

        [TestMethod]
        public async Task ASystemMessageTravelsAsASystemTurn()
        {
            IReadOnlyList<ChatTurn> seen = null;
            using var factory = Factory((turns, opts) =>
            {
                seen = turns;
                return new ChatBackendResult { Content = "ok", Model = opts.Model };
            });

            await Client(factory).GetResponseAsync(new[]
            {
                new ChatMessage(ChatRole.System, "You are a Fallen-8 graph analyst."),
                new ChatMessage(ChatRole.User, "hello"),
            });

            Assert.AreEqual(2, seen.Count);
            Assert.AreEqual("system", seen[0].Role);
            Assert.AreEqual("You are a Fallen-8 graph analyst.", seen[0].Content);
            Assert.AreEqual("user", seen[1].Role);
        }

        [TestMethod]
        public async Task AToolTravelsWithItsSchemaIntactAndComesBackAsAFunctionCall()
        {
            ChatBackendOptions options = null;
            using var factory = Factory((turns, opts) =>
            {
                options = opts;
                return new ChatBackendResult
                {
                    Content = String.Empty,
                    Model = opts.Model,
                    ToolCalls = new[]
                    {
                        new ChatToolCall
                        {
                            Id = "call-1",
                            Name = "count_vertices",
                            Arguments = JsonDocument.Parse("{\"namespace\":\"default\"}").RootElement.Clone(),
                        },
                    },
                };
            });

            var response = await Client(factory).GetResponseAsync(
                new[] { new ChatMessage(ChatRole.User, "count them") },
                new ChatOptions { Tools = new List<AITool> { CountVertices() } });

            Assert.AreEqual(1, options.Tools.Count, "the tool must reach the backend");
            Assert.AreEqual("count_vertices", options.Tools[0].Name);

            // The caller's schema, not the SDK's re-rendering of it: a required array is exactly
            // what a mapping through a provider's own schema type drops.
            var parameters = options.Tools[0].Parameters;
            Assert.AreEqual(JsonValueKind.Object, parameters.ValueKind);
            Assert.IsTrue(parameters.TryGetProperty("properties", out var properties));
            Assert.IsTrue(properties.TryGetProperty("namespace", out _));

            var call = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single();
            Assert.AreEqual("call-1", call.CallId);
            Assert.AreEqual("count_vertices", call.Name);
            Assert.AreEqual("default", call.Arguments["namespace"].ToString());
            Assert.IsTrue(String.IsNullOrEmpty(response.Text),
                "a reply that only calls a tool carries no text, and that is the ordinary shape");
        }

        [TestMethod]
        public async Task AnAssistantTurnThatOnlyCalledAToolTravelsWithAStringContentAndItsResultFollows()
        {
            // THE regression this whole file exists for. The live gateway answers 422 to a null
            // content, and an assistant turn that only called a tool has no text - so the value has
            // to be an empty STRING on the wire, and the assertion is on the JSON type rather than
            // on emptiness, because "" and null are both falsy and only one of them works.
            IReadOnlyList<ChatTurn> seen = null;
            using var factory = Factory((turns, opts) =>
            {
                seen = turns;
                return new ChatBackendResult { Content = "The graph has 8 vertices.", Model = opts.Model };
            });

            var call = new FunctionCallContent("call-1", "count_vertices",
                new Dictionary<String, Object>(StringComparer.Ordinal) { ["namespace"] = "default" });

            var response = await Client(factory).GetResponseAsync(new List<ChatMessage>
            {
                new ChatMessage(ChatRole.User, "count them"),
                new ChatMessage(ChatRole.Assistant, new List<AIContent> { call }),
                new ChatMessage(ChatRole.Tool, new List<AIContent>
                {
                    new FunctionResultContent("call-1", "8"),
                }),
            }, new ChatOptions { Tools = new List<AITool> { CountVertices() } });

            Assert.AreEqual(3, seen.Count);

            Assert.AreEqual("assistant", seen[1].Role);
            Assert.IsNotNull(seen[1].Content,
                "an assistant turn's content is a string on the wire even when it is empty; null is a 422");
            Assert.AreEqual(String.Empty, seen[1].Content);
            Assert.AreEqual(1, seen[1].ToolCalls.Count);
            Assert.AreEqual("call-1", seen[1].ToolCalls[0].Id);
            Assert.AreEqual("count_vertices", seen[1].ToolCalls[0].Name);

            Assert.AreEqual("tool", seen[2].Role);
            Assert.AreEqual("call-1", seen[2].ToolCallId, "a tool result names the call it answers");
            Assert.AreEqual("8", seen[2].Content);

            Assert.AreEqual("The graph has 8 vertices.", response.Text);
        }

        [TestMethod]
        public async Task TwoToolResultsInOneMessageBecomeTwoTurns()
        {
            // The framework may hand back several results in one message. Each answers a different
            // call, and the wire carries one toolCallId per turn, so one message has to become two.
            IReadOnlyList<ChatTurn> seen = null;
            using var factory = Factory((turns, opts) =>
            {
                seen = turns;
                return new ChatBackendResult { Content = "done", Model = opts.Model };
            });

            await Client(factory).GetResponseAsync(new List<ChatMessage>
            {
                new ChatMessage(ChatRole.User, "count both"),
                new ChatMessage(ChatRole.Tool, new List<AIContent>
                {
                    new FunctionResultContent("call-1", "8"),
                    new FunctionResultContent("call-2", "12"),
                }),
            });

            Assert.AreEqual(3, seen.Count);
            Assert.AreEqual("call-1", seen[1].ToolCallId);
            Assert.AreEqual("8", seen[1].Content);
            Assert.AreEqual("call-2", seen[2].ToolCallId);
            Assert.AreEqual("12", seen[2].Content);
        }

        [TestMethod]
        public async Task ReportedUsageBecomesUsageDetailsAndAnUnreportingBackendReportsNone()
        {
            using (var factory = Factory((turns, opts) => new ChatBackendResult
            {
                Content = "ok",
                Model = opts.Model,
                PromptTokens = 41,
                CompletionTokens = 7,
            }))
            {
                var response = await Client(factory).GetResponseAsync(
                    new[] { new ChatMessage(ChatRole.User, "hi") });

                Assert.IsNotNull(response.Usage);
                Assert.AreEqual(41L, response.Usage.InputTokenCount);
                Assert.AreEqual(7L, response.Usage.OutputTokenCount);
            }

            // Absent stays absent rather than becoming zero HERE: the meter above turns a missing
            // report into a counted zero AND a flag, and it cannot do that if the adapter has
            // already invented the number.
            using (var factory = Factory((turns, opts) => new ChatBackendResult
            {
                Content = "ok",
                Model = opts.Model,
            }))
            {
                var response = await Client(factory).GetResponseAsync(
                    new[] { new ChatMessage(ChatRole.User, "hi") });

                Assert.IsNull(response.Usage,
                    "usage nobody reported is not estimated; the meter flags it instead");
            }
        }

        [TestMethod]
        public async Task TheBackendAndModelTheInstanceReportedAreReadableAsAPair()
        {
            using var factory = Factory((turns, opts) => new ChatBackendResult
            {
                Content = "ok",
                Model = opts.Model,
            });

            var client = Client(factory);
            Assert.IsNull(client.LastSeen, "before a step there is no model to report, and none is invented");

            await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") });

            Assert.IsNotNull(client.LastSeen);
            Assert.AreEqual("Ollama", client.LastSeen.Backend);
            Assert.AreEqual("agent-model", client.LastSeen.Model);
        }

        [TestMethod]
        public async Task AGatewayRefusalCarriesTheInstancesOwnSentence()
        {
            // The deployment that ships this way: a metered provider with no Models:Agent. The
            // gateway's 503 names the key to set, and that sentence has to survive the hop, because
            // a status this layer invented would send an operator looking at the wrong process.
            using var factory = Factory((turns, opts) => new ChatBackendResult
            {
                Content = "unused",
                Model = opts.Model,
            }, agentModel: String.Empty);

            var failure = await Assert.ThrowsExceptionAsync<Fallen8ChatException>(
                () => Client(factory).GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));

            StringAssert.Contains(failure.Message, "503");
            StringAssert.Contains(failure.Message, "Models:Agent",
                "the gateway's own message names the key; the adapter must not replace it");
        }

        [TestMethod]
        public async Task TheStreamedCallYieldsTheBufferedAnswerRatherThanThrowing()
        {
            // POST /chat has no streamed shape, and a framework path that prefers streaming must
            // still get a correct answer. One update is the honest rendering of one completion.
            using var factory = Factory((turns, opts) => new ChatBackendResult
            {
                Content = "eight",
                Model = opts.Model,
            });

            var text = new System.Text.StringBuilder();
            await foreach (var update in Client(factory).GetStreamingResponseAsync(
                new[] { new ChatMessage(ChatRole.User, "hi") }))
            {
                text.Append(update.Text);
            }

            Assert.AreEqual("eight", text.ToString());
        }

        [TestMethod]
        public async Task ACallerCancellationPropagatesAsItselfRatherThanAsAGatewayFailure()
        {
            using var factory = Factory(async (turns, opts, token) =>
            {
                await Task.Delay(TimeSpan.FromMinutes(5), token);
                return new ChatBackendResult { Content = "never", Model = opts.Model };
            });

            using var caller = new CancellationTokenSource();
            var pending = Client(factory).GetResponseAsync(
                new[] { new ChatMessage(ChatRole.User, "hi") }, cancellationToken: caller.Token);

            caller.CancelAfter(TimeSpan.FromMilliseconds(50));

            await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => pending);
        }

        /// <summary>A tool whose schema has a required array, which is the part a mapping through a
        /// provider's own schema type silently drops.</summary>
        private static AIFunction CountVertices()
        {
            return AIFunctionFactory.Create(
                (String @namespace) => "8",
                new AIFunctionFactoryOptions
                {
                    Name = "count_vertices",
                    Description = "Counts the vertices in one namespace.",
                });
        }

        private static Fallen8ChatClient Client(AgentChatFactory factory)
        {
            // The factory's client speaks to the in-memory server, so this is the real controller
            // on the far side of a real HTTP request.
            var http = factory.CreateClient();
            http.BaseAddress = new Uri(http.BaseAddress, "/");
            return new Fallen8ChatClient(http, TimeSpan.FromSeconds(30));
        }

        private static AgentChatFactory Factory(
            Func<IReadOnlyList<ChatTurn>, ChatBackendOptions, ChatBackendResult> chat,
            String agentModel = "agent-model")
        {
            return Factory((turns, options, token) => Task.FromResult(chat(turns, options)), agentModel);
        }

        private static AgentChatFactory Factory(
            Func<IReadOnlyList<ChatTurn>, ChatBackendOptions, CancellationToken, Task<ChatBackendResult>> chat,
            String agentModel = "agent-model")
        {
            return new AgentChatFactory(chat, agentModel);
        }

        /// <summary>
        ///   A hosted apiApp with the Chat capability on, a distinct model per purpose, and a fake
        ///   backend. The two models DIFFER on purpose: an adapter that sent the wrong purpose, or a
        ///   gateway that ignored it, would otherwise pass.
        /// </summary>
        private sealed class AgentChatFactory : VolatileAppFactory
        {
            private readonly Func<IReadOnlyList<ChatTurn>, ChatBackendOptions, CancellationToken, Task<ChatBackendResult>> _chat;
            private readonly String _agentModel;

            public AgentChatFactory(
                Func<IReadOnlyList<ChatTurn>, ChatBackendOptions, CancellationToken, Task<ChatBackendResult>> chat,
                String agentModel)
            {
                _chat = chat;
                _agentModel = agentModel;
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                base.ConfigureWebHost(builder);
                builder.UseSetting("Fallen8:Chat:Enabled", "true");
                builder.UseSetting("Fallen8:Chat:Backend", "Ollama");
                builder.UseSetting("Fallen8:Chat:Ollama:Models:Assist", "assist-model");
                builder.UseSetting("Fallen8:Chat:Ollama:Models:Agent", _agentModel ?? String.Empty);
                builder.ConfigureTestServices(services =>
                    services.AddSingleton<IChatBackend>(new AdapterFakeBackend(_chat)));
            }
        }

        private sealed class AdapterFakeBackend : IChatBackend
        {
            private readonly Func<IReadOnlyList<ChatTurn>, ChatBackendOptions, CancellationToken, Task<ChatBackendResult>> _chat;

            public AdapterFakeBackend(
                Func<IReadOnlyList<ChatTurn>, ChatBackendOptions, CancellationToken, Task<ChatBackendResult>> chat)
            {
                _chat = chat;
            }

            public Task<ChatBackendResult> ChatAsync(IReadOnlyList<ChatTurn> messages,
                ChatBackendOptions options, CancellationToken cancellationToken)
                => _chat(messages, options, cancellationToken);
        }
    }
}
