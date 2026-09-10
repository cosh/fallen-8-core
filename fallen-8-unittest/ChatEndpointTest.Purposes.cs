// MIT License
//
// ChatEndpointTest.Purposes.cs
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
    ///   Model purposes on <c>POST /chat</c> (feature agent-host): the request says what a
    ///   completion is FOR and the server says which model does that job, so no client ever names
    ///   a model and instance-config decision D8 still holds.
    ///
    ///   <para>Its own file rather than another region in the sibling, because these tests share
    ///   that file's harness (the volatile host, the fake backend seam) and nothing else, and the
    ///   harness is what a reader of either half needs to find first.</para>
    /// </summary>
    public partial class ChatEndpointTest
    {
        /// <summary>A fake that records the model the SERVER chose for it, which is the whole
        /// observable effect of a purpose.</summary>
        private sealed class ModelRecordingBackend : IChatBackend
        {
            public String LastModel
            {
                get; private set;
            }

            public Task<ChatBackendResult> ChatAsync(IReadOnlyList<ChatTurn> messages,
                ChatBackendOptions options, CancellationToken cancellationToken)
            {
                LastModel = options?.Model;
                return Task.FromResult(new ChatBackendResult
                {
                    Content = "ok",
                    Model = options?.Model,
                    PromptTokens = 1,
                    CompletionTokens = 1,
                });
            }
        }

        private static String Body(String purpose)
        {
            var prefix = purpose == null ? String.Empty : "\"purpose\":\"" + purpose + "\",";
            return "{" + prefix + "\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";
        }

        /// <summary>
        ///   A purpose selects the model, and OMITTING it is indistinguishable from
        ///   <c>assist</c> - which is what makes purposes an addition rather than a change to every
        ///   caller that already existed. The observable effect is the model the backend is handed,
        ///   so that is what this reads, plus the model the response echoes back.
        /// </summary>
        [TestMethod]
        public async Task APurposeSelectsTheModel_AndOmittingItIsTheAssistModel()
        {
            var backend = new ModelRecordingBackend();
            using var factory = new ChatFactory(enabled: true, backend);
            using var client = factory.CreateClient();

            var cases = new[]
            {
                (Purpose: (String)null, Model: "fake-model",
                    Why: "no purpose gets the assist model, exactly as before purposes existed"),
                (Purpose: "assist", Model: "fake-model",
                    Why: "naming the default explicitly is the same thing"),
                (Purpose: "agent", Model: "fake-agent-model",
                    Why: "the agent purpose reads the OTHER key, which is the entire point"),
                (Purpose: "AGENT", Model: "fake-agent-model",
                    Why: "case is forgiven on a request field: no other purpose it could mean"),
            };

            foreach (var (purpose, model, why) in cases)
            {
                using var response = await client.PostAsync("/chat", Json(Body(purpose)));

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, why);
                Assert.AreEqual(model, backend.LastModel, why);

                // The response names the model that served it, so a caller can tell which of the two
                // answered without reading the instance's configuration.
                var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
                Assert.AreEqual(model, payload.GetProperty("model").GetString(), why);
            }
        }

        /// <summary>
        ///   A purpose this server does not have is REFUSED rather than defaulted. Defaulting would
        ///   answer a tool-calling agent with a model trained to emit one C# fragment, which looks
        ///   like an answer and is useless, and it is the failure a caller is least able to see.
        /// </summary>
        [TestMethod]
        public async Task AnUnknownPurpose_Is400ListingTheOnesThatExist()
        {
            var backend = new ModelRecordingBackend();
            using var factory = new ChatFactory(enabled: true, backend);
            using var client = factory.CreateClient();

            using var response = await client.PostAsync("/chat", Json(Body("orchestrator")));

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var detail = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
                .RootElement.GetProperty("detail").GetString();
            StringAssert.Contains(detail, "orchestrator", "the refusal quotes what was sent: " + detail);
            StringAssert.Contains(detail, "assist", detail);
            StringAssert.Contains(detail, "agent", detail);
            Assert.IsNull(backend.LastModel, "and the backend is never reached");
        }

        /// <summary>
        ///   A purpose whose model nobody configured answers 503 naming the key to set. This is the
        ///   shipped state of the two metered providers, which carry no model defaults at all.
        ///
        ///   <para>Two things it must NOT do, and both were live hazards while this was written: it
        ///   must not fall back to the other purpose's model, and it must not take the capability
        ///   down - the check is deliberately narrow, so assist on the same instance still
        ///   answers.</para>
        /// </summary>
        [TestMethod]
        public async Task APurposeWithNoModel_Is503NamingItsKey_AndTheOtherPurposeStillAnswers()
        {
            var backend = new ModelRecordingBackend();
            using var factory = new ChatFactory(enabled: true, backend, agentModel: null);
            using var client = factory.CreateClient();

            using var refused = await client.PostAsync("/chat", Json(Body("agent")));

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            var detail = JsonDocument.Parse(await refused.Content.ReadAsStringAsync())
                .RootElement.GetProperty("detail").GetString();
            StringAssert.Contains(detail, "Fallen8:Chat:Ollama:Models:Agent",
                "the refusal names the key an operator has to set: " + detail);
            StringAssert.Contains(detail, "agent", "and which purpose asked for it: " + detail);
            Assert.IsNull(backend.LastModel,
                "an unconfigured purpose must not fall through to the other purpose's model");

            using var served = await client.PostAsync("/chat", Json(Body(null)));

            Assert.AreEqual(HttpStatusCode.OK, served.StatusCode,
                "one missing purpose must not take the whole capability down");
            Assert.AreEqual("fake-model", backend.LastModel);
        }

        /// <summary>
        ///   Both purposes' models are reported, because both are configured and whoever is setting
        ///   up an agent host needs to see the one it will use. <c>model</c> keeps its old meaning,
        ///   the assist model, so every existing reader of that field is unaffected.
        /// </summary>
        [TestMethod]
        public async Task TheReportedState_NamesBothPurposesModels()
        {
            using var factory = new ChatFactory(enabled: true, Returns("ok"));
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/status");
            var chat = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
                .RootElement.GetProperty("chat");

            Assert.AreEqual("fake-model", chat.GetProperty("model").GetString(),
                "model still means the assist model");
            Assert.AreEqual("fake-agent-model", chat.GetProperty("agentModel").GetString());
        }
    }
}
