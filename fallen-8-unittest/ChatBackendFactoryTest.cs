// MIT License
//
// ChatBackendFactoryTest.cs
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
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Chat;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Helper;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The chat selector's factory (features instance-config, nahil-backend and model-providers):
    ///   which backend a configured name really builds, from which option block, and which names are
    ///   refused before anything is built.
    ///
    ///   <para>This is the counterpart of <c>EmbeddingProviderTest.BackendFactory_*</c>, and it exists
    ///   because every other chat test hands the provider a fake backend or replaces it in DI - so
    ///   nothing was executing this switch at all. Aiming an arm at the wrong option block (the
    ///   Anthropic arm reading <c>OpenAI.Endpoint</c>, say) or dropping the token ceiling would then be
    ///   a green suite and a deployment dialling the wrong provider.</para>
    /// </summary>
    [TestClass]
    public class ChatBackendFactoryTest
    {
        private const String OpenAIModel = "gpt-4o-mini";
        private const String AnthropicModel = "claude-opus-5";

        /// <summary>
        ///   Every unusable selector is refusable BEFORE anything is constructed, and the sentence the
        ///   startup warning reads is byte-for-byte the one the 503 reads: two spellings of one fault
        ///   are two things an operator has to reconcile.
        /// </summary>
        [TestMethod]
        public void ValidateIsTheOneHome_SoTheBootWarningAndThe503Agree()
        {
            var unusable = new[]
            {
                new Fallen8ChatOptions { Backend = "Nope" },
                // Casing is load-bearing: the switch is ordinal, so a lower-case selector is a name
                // this app does not have rather than a spelling it forgives.
                new Fallen8ChatOptions { Backend = "openai" },
                new Fallen8ChatOptions { Backend = "anthropic" },
                // Selected, and missing each of the things it needs in turn.
                OpenAI(null, OpenAIModel, "sk-key"),
                OpenAI("https://api.openai.com/v1", OpenAIModel, "sk-key"),
                OpenAI("https://api.openai.com", null, "sk-key"),
                OpenAI("https://api.openai.com", OpenAIModel, null),
                OpenAI("https://api.openai.com", OpenAIModel, "   "),
                Anthropic(null, AnthropicModel, "k"),
                Anthropic("https://api.anthropic.com", null, "k"),
                Anthropic("https://api.anthropic.com", AnthropicModel, null),
                // The Ollama-protocol arm, so the shared branch is covered by the same claim.
                new Fallen8ChatOptions
                {
                    Backend = "Nahil",
                    Nahil = new Fallen8ChatOptions.NahilOptions { Endpoint = null, Model = "m", ApiKey = "k" }
                }
            };

            foreach (var options in unusable)
            {
                var problem = Validate(options);
                Assert.IsNotNull(problem,
                    "an unusable backend must be refusable before it is built: " + options.Backend);

                var thrown = Assert.ThrowsException<InvalidOperationException>(() => Create(options));
                Assert.AreEqual(thrown.Message, problem,
                    "the startup line and the 503 must be the same sentence, or they can drift");
                Assert.IsFalse(problem.Contains("sk-key", StringComparison.Ordinal), problem);
            }

            Assert.IsNull(Validate(OpenAI("https://api.openai.com", OpenAIModel, "sk-key")),
                "a fully configured provider has nothing to warn about at boot");
            Assert.IsNull(Validate(Anthropic("https://api.anthropic.com", AnthropicModel, "k")));
            Assert.IsNull(Validate(new Fallen8ChatOptions()), "the shipped Ollama default is usable");
        }

        /// <summary>
        ///   Each selector builds ITS backend, from ITS own block. The model is read back off the
        ///   built object because that is the value a request would carry: an arm pointed at the
        ///   neighbouring block would still construct something, and construction succeeding is not
        ///   the claim.
        /// </summary>
        [TestMethod]
        public void EachSelector_BuildsItsOwnBackend_FromItsOwnBlock()
        {
            using (var backend = Disposable(OpenAI("https://api.openai.com", OpenAIModel, "sk-key")))
            {
                Assert.IsInstanceOfType(backend, typeof(OpenAIChatBackend));
                Assert.AreEqual(OpenAIModel, Field<String>(backend, "_model"));
                Assert.AreEqual("OpenAI", Field<String>(backend, "_providerName"));
            }

            using (var backend = Disposable(Anthropic("https://api.anthropic.com", AnthropicModel, "k")))
            {
                Assert.IsInstanceOfType(backend, typeof(AnthropicChatBackend));
                Assert.AreEqual(AnthropicModel, Field<String>(backend, "_model"),
                    "the Anthropic arm must read the Anthropic block, not its neighbour");
                Assert.AreEqual("Anthropic", Field<String>(backend, "_providerName"));
            }

            using (var backend = Disposable(new Fallen8ChatOptions()))
            {
                Assert.IsInstanceOfType(backend, typeof(OllamaChatBackend),
                    "Ollama and Nahil share one protocol and therefore one backend type");
            }
        }

        /// <summary>
        ///   <c>Fallen8:Chat:Anthropic:MaxTokens</c> reaches the backend that requires it, and an
        ///   unconfigured block does not become a zero ceiling. The Messages API rejects a missing
        ///   <c>max_tokens</c> and answers exactly one token for a ceiling of one, so a knob that
        ///   silently defaulted would look like a model that lost the ability to finish a sentence.
        /// </summary>
        [TestMethod]
        public void TheAnthropicTokenCeiling_ComesFromItsOwnSetting_AndAnAbsentBlockKeepsTheDefault()
        {
            var configured = Anthropic("https://api.anthropic.com", AnthropicModel, "k");
            configured.Anthropic.MaxTokens = 1234;
            using (var backend = Disposable(configured))
            {
                Assert.AreEqual(1234, Field<Int32>(backend, "_maxTokens"));
            }

            var shipped = new Fallen8ChatOptions.AnthropicOptions().MaxTokens;
            Assert.AreEqual(4096, shipped, "the shipped default is documented on the option");

            var bare = Anthropic("https://api.anthropic.com", AnthropicModel, "k");
            using (var backend = Disposable(bare))
            {
                Assert.AreEqual(shipped, Field<Int32>(backend, "_maxTokens"),
                    "an operator who configured no ceiling gets the documented one, never 0");
            }
        }

        /// <summary>
        ///   Which block each remote selector resolves is pinned field by field. This is the one home
        ///   <c>Create</c> and <c>Validate</c> both read, and it is the edit that goes unnoticed
        ///   otherwise: an arm handed its neighbour's endpoint still builds a backend, still validates
        ///   clean, and dials a provider the operator never configured with a key meant for another
        ///   one.
        /// </summary>
        [TestMethod]
        public void EachRemoteSelector_ResolvesItsOwnEndpointModelAndCredential()
        {
            var openAi = ResolveRemoteTarget(OpenAI("https://gw.openai.example", OpenAIModel, "sk-key"));
            Assert.AreEqual("Fallen8:Chat:OpenAI", openAi.SectionKey);
            Assert.AreEqual("https://gw.openai.example", openAi.Endpoint);
            Assert.AreEqual(OpenAIModel, openAi.Model);
            Assert.AreEqual("sk-key", openAi.ApiKey);
            Assert.AreEqual("OpenAI", openAi.ProviderName);

            var anthropic = ResolveRemoteTarget(
                Anthropic("https://gw.anthropic.example", AnthropicModel, "ant-key"));
            Assert.AreEqual("Fallen8:Chat:Anthropic", anthropic.SectionKey);
            Assert.AreEqual("https://gw.anthropic.example", anthropic.Endpoint,
                "the Anthropic arm must read the Anthropic endpoint, not the OpenAI block's default");
            Assert.AreEqual(AnthropicModel, anthropic.Model);
            Assert.AreEqual("ant-key", anthropic.ApiKey);
            Assert.AreEqual("Anthropic", anthropic.ProviderName);

            foreach (var backend in new[] { "Ollama", "Nahil", "Nope" })
            {
                Assert.IsNull(ResolveRemoteTarget(new Fallen8ChatOptions { Backend = backend }),
                    backend + " speaks no provider protocol, and a null here is what makes the "
                    + "residency probe skip a backend it cannot ask");
            }
        }

        /// <summary>
        ///   The reported model is read from the block the selector points at, not from the residency
        ///   probe's target - which is null for both new providers, and which is why <c>/status</c>
        ///   used to report <c>model: null</c> on an OpenAI deployment (FR-5.2).
        /// </summary>
        [TestMethod]
        public void TheReportedModel_ComesFromTheSelectedBlock_ForEveryBackend()
        {
            Assert.AreEqual(OpenAIModel, ResolveModel(OpenAI("https://api.openai.com", OpenAIModel, "sk-key")));
            Assert.AreEqual(AnthropicModel,
                ResolveModel(Anthropic("https://api.anthropic.com", AnthropicModel, "k")));
            Assert.AreEqual("phi4-f8-mini:latest", ResolveModel(new Fallen8ChatOptions()));
            Assert.IsNull(ResolveModel(new Fallen8ChatOptions { Backend = "Nope" }),
                "a name this app does not have reports no model rather than a plausible one");
        }

        #region the seam

        /// <summary>The factory is internal (the repository adds no InternalsVisibleTo), so it is
        /// reached the same way the embedding twin's tests reach theirs.</summary>
        private static MethodInfo Method(String name)
        {
            var factory = typeof(OllamaChatBackend).Assembly
                .GetType("NoSQL.GraphDB.App.Chat.ChatBackendFactory");
            Assert.IsNotNull(factory, "the one home of the chat selector");
            var method = factory.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, name + " is what the boot warning and the 503 share");
            return method;
        }

        private static String Validate(Fallen8ChatOptions options)
        {
            return (String)Method("Validate").Invoke(null, new Object[] { options });
        }

        private static String ResolveModel(Fallen8ChatOptions options)
        {
            return (String)Method("ResolveModel").Invoke(null, new Object[] { options });
        }

        private static RemoteModelTarget ResolveRemoteTarget(Fallen8ChatOptions options)
        {
            return (RemoteModelTarget)Method("ResolveRemoteTarget").Invoke(null, new Object[] { options });
        }

        /// <summary>The built backend as the resource it is: every implementation owns an
        /// HttpClient, and IChatBackend deliberately does not carry IDisposable (a backend that
        /// owns nothing should not have to pretend to).</summary>
        private static IDisposable Disposable(Fallen8ChatOptions options)
        {
            return (IDisposable)Create(options);
        }

        private static IChatBackend Create(Fallen8ChatOptions options)
        {
            try
            {
                return (IChatBackend)Method("Create").Invoke(null, new Object[] { options, null });
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException;
            }
        }

        private static T Field<T>(Object instance, String name)
        {
            var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, name + " is what a request would carry");
            return (T)field.GetValue(instance);
        }

        private static Fallen8ChatOptions OpenAI(String endpoint, String model, String apiKey)
        {
            return new Fallen8ChatOptions
            {
                Backend = "OpenAI",
                OpenAI = new Fallen8ChatOptions.OpenAIOptions
                {
                    Endpoint = endpoint,
                    Model = model,
                    ApiKey = apiKey
                }
            };
        }

        private static Fallen8ChatOptions Anthropic(String endpoint, String model, String apiKey)
        {
            return new Fallen8ChatOptions
            {
                Backend = "Anthropic",
                Anthropic = new Fallen8ChatOptions.AnthropicOptions
                {
                    Endpoint = endpoint,
                    Model = model,
                    ApiKey = apiKey
                }
            };
        }

        #endregion
    }
}
