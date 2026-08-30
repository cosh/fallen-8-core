// MIT License
//
// RemoteModelTargetTest.cs
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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Helper;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Validation of a remote model provider target (feature model-providers): the gate that runs
    ///   BEFORE any SDK client exists, so an unusable configuration becomes a named key rather than a
    ///   401 on every call.
    ///
    ///   <para>Two properties are worth more than the rest here. The credential is refused when blank
    ///   instead of being left to the SDK, which would resolve one from the ambient environment; and
    ///   no refusal quotes the endpoint, because these sentences reach an anonymous reader through a
    ///   503 problem-detail and an operator can have embedded a credential in the URL.</para>
    /// </summary>
    [TestClass]
    public class RemoteModelTargetTest
    {
        /// <summary>A string no refusal may ever echo, planted inside the endpoint.</summary>
        private const String Sentinel = "secret.example";

        [TestMethod]
        public void AUsableTarget_IsAccepted_WithNoProblemToReport()
        {
            var openAi = RemoteModelTarget.OpenAI("Fallen8:Chat:OpenAI", "https://api.openai.com", "gpt-4o-mini", "k");
            Assert.IsTrue(openAi.IsValid(out var openAiProblem));
            Assert.IsNull(openAiProblem, "an accepted target reports nothing to fix");

            var anthropic = RemoteModelTarget.Anthropic(
                "Fallen8:Chat:Anthropic", "https://api.anthropic.com", "claude-opus-5", "k");
            Assert.IsTrue(anthropic.IsValid(out _));

            Assert.IsTrue(
                RemoteModelTarget.OpenAI("S", "https://api.openai.com/", "m", "k").IsValid(out _),
                "a bare trailing slash IS a host root");
            Assert.IsTrue(
                RemoteModelTarget.OpenAI("S", "http://gateway.internal:8080", "m", "k").IsValid(out _),
                "an OpenAI-compatible gateway on the operator's own network is a host root too");
        }

        /// <summary>
        ///   Every value is carried verbatim. Nothing normalizes a trailing slash, trims a model tag or
        ///   rewrites a host, because each provider's SDK builds the request URL from exactly what it
        ///   is given and a silent rewrite here would be invisible at that call site.
        /// </summary>
        [TestMethod]
        public void EveryValue_ReachesTheTargetVerbatim()
        {
            var target = RemoteModelTarget.OpenAI("Fallen8:Chat:OpenAI", "https://api.openai.com/", " gpt-4o-mini ", "k ");

            Assert.AreEqual("Fallen8:Chat:OpenAI", target.SectionKey);
            Assert.AreEqual("https://api.openai.com/", target.Endpoint);
            Assert.AreEqual(" gpt-4o-mini ", target.Model);
            Assert.AreEqual("k ", target.ApiKey);
        }

        /// <summary>
        ///   The provider name is the selector's spelling, and it is what makes the credential refusal
        ///   readable: the operator learns WHICH provider is waiting for a key, not just that one is.
        /// </summary>
        [TestMethod]
        public void TheProviderName_IsTheSelectorSpelling_AndReachesTheCredentialRefusal()
        {
            Assert.AreEqual("OpenAI", RemoteModelTarget.OpenAI("S", "https://h.example", "m", "k").ProviderName);
            Assert.AreEqual("Anthropic", RemoteModelTarget.Anthropic("S", "https://h.example", "m", "k").ProviderName);

            Assert.IsFalse(RemoteModelTarget.OpenAI("Fallen8:Chat:OpenAI", "https://h.example", "m", null)
                .IsValid(out var openAi));
            StringAssert.Contains(openAi, "OpenAI authenticates every route");
            Assert.IsFalse(RemoteModelTarget.Anthropic("Fallen8:Chat:Anthropic", "https://h.example", "m", null)
                .IsValid(out var anthropic));
            StringAssert.Contains(anthropic, "Anthropic authenticates every route");
        }

        /// <summary>
        ///   Every way an endpoint can be unusable, and the key named in each refusal. The host-root
        ///   rule is the one worth pinning: both SDKs append their own route suffix to this value, so
        ///   an endpoint carrying a path would dial a URL nobody configured.
        /// </summary>
        [TestMethod]
        public void AnEndpointThatCannotBeDialled_IsRefusedWithTheKeyToFix()
        {
            foreach (var endpoint in new[]
            {
                "https://api.openai.com/v1", "https://api.openai.com?a=b", "https://api.openai.com/#f",
                "https://api.openai.com/v1/chat/completions", "ftp://api.openai.com", "api.openai.com",
                "/v1", "   ", "", null
            })
            {
                Assert.IsFalse(RemoteModelTarget.OpenAI("N", endpoint, "m", "k").IsValid(out var problem),
                    "'" + endpoint + "' must be refused");
                StringAssert.Contains(problem, "N:Endpoint");
            }

            Assert.IsFalse(RemoteModelTarget.Anthropic("N", null, "m", "k").IsValid(out var missing));
            Assert.AreEqual("N:Endpoint is required.", missing,
                "an unset endpoint gets the plain sentence, not the host-root explanation");

            Assert.IsFalse(RemoteModelTarget.Anthropic("N", "https://api.anthropic.com/v1", "m", "k")
                .IsValid(out var withPath));
            StringAssert.Contains(withPath, "host root");
        }

        [TestMethod]
        public void AMissingModel_IsRefusedNamingTheModelKey()
        {
            foreach (var model in new String[] { null, "", " ", "\t" })
            {
                Assert.IsFalse(RemoteModelTarget.OpenAI("Fallen8:Chat:OpenAI", "https://api.openai.com", model, "k")
                    .IsValid(out var problem), "'" + model + "' is not a model");
                Assert.AreEqual("Fallen8:Chat:OpenAI:Model is required.", problem);
            }
        }

        /// <summary>
        ///   A blank credential is refused HERE rather than handed to an SDK. The Anthropic client
        ///   resolves one from the ambient environment when it is given none, and the hook that turns
        ///   that off is protected, so a deployment with an empty configured key would silently run on
        ///   whatever key the machine happens to carry.
        /// </summary>
        [TestMethod]
        public void ABlankCredential_IsRefusedBeforeAnyClientExists()
        {
            foreach (var apiKey in new String[] { null, "", " ", "\t", "\r\n" })
            {
                Assert.IsFalse(RemoteModelTarget.Anthropic(
                    "Fallen8:Chat:Anthropic", "https://api.anthropic.com", "m", apiKey).IsValid(out var problem),
                    "'" + apiKey + "' is not a credential");
                StringAssert.Contains(problem, "Fallen8:Chat:Anthropic:ApiKey is required");
            }
        }

        /// <summary>
        ///   The order of the checks is the order an operator fixes things in, and each refusal reports
        ///   ONE fix: an entirely unset section names the endpoint rather than listing three problems,
        ///   and the credential is the last thing asked for.
        /// </summary>
        [TestMethod]
        public void TheFirstUnusableValue_IsTheOneReported()
        {
            Assert.IsFalse(RemoteModelTarget.OpenAI("S", null, null, null).IsValid(out var nothingSet));
            Assert.AreEqual("S:Endpoint is required.", nothingSet);

            Assert.IsFalse(RemoteModelTarget.OpenAI("S", "https://api.openai.com", null, null).IsValid(out var noModel));
            Assert.AreEqual("S:Model is required.", noModel);

            Assert.IsFalse(RemoteModelTarget.OpenAI("S", "https://api.openai.com", "m", null).IsValid(out var noKey));
            StringAssert.Contains(noKey, "S:ApiKey is required");
        }

        /// <summary>
        ///   No refusal quotes the endpoint. It reaches an operator two ways - a startup warning and
        ///   the problem-detail of the 503 the capability answers - and the second of those is
        ///   anonymous on a keyless instance, while a credential embedded in the URL would ride along.
        /// </summary>
        [TestMethod]
        public void NoRefusal_EverQuotesTheEndpoint()
        {
            foreach (var endpoint in new[]
            {
                "https://token:" + Sentinel + "@api.openai.com/v1",
                "https://api.openai.com?key=" + Sentinel,
                "https://api.openai.com/#" + Sentinel,
                "ftp://" + Sentinel,
                Sentinel
            })
            {
                Assert.IsFalse(RemoteModelTarget.OpenAI("S", endpoint, "m", "k").IsValid(out var problem));
                Assert.IsFalse(problem.Contains(Sentinel, StringComparison.OrdinalIgnoreCase),
                    "the refusal for '" + endpoint + "' echoed the endpoint back: " + problem);
            }

            // A usable endpoint carrying the same sentinel must stay out of the model and credential
            // refusals too - those run after the endpoint check has already passed.
            var usable = "https://token:" + Sentinel + "@api.openai.com";
            Assert.IsFalse(RemoteModelTarget.OpenAI("S", usable, null, "k").IsValid(out var noModel));
            Assert.IsFalse(noModel.Contains(Sentinel, StringComparison.OrdinalIgnoreCase), noModel);
            Assert.IsFalse(RemoteModelTarget.OpenAI("S", usable, "m", null).IsValid(out var noKey));
            Assert.IsFalse(noKey.Contains(Sentinel, StringComparison.OrdinalIgnoreCase), noKey);
        }

        /// <summary>
        ///   The credential itself never reaches a refusal either. A key that looks like a problem
        ///   value (whitespace, punctuation) still must not be echoed while being described.
        /// </summary>
        [TestMethod]
        public void NoRefusal_EverQuotesTheCredential()
        {
            var target = RemoteModelTarget.Anthropic("S", "https://api.anthropic.com/v1", null, "sk-" + Sentinel);

            Assert.IsFalse(target.IsValid(out var problem));
            Assert.IsFalse(problem.Contains(Sentinel, StringComparison.OrdinalIgnoreCase), problem);
        }
    }
}
