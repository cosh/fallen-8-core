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
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.Configuration;
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
                    Nahil = new Fallen8ChatOptions.NahilOptions
                    {
                        Endpoint = null,
                        Models = new Fallen8ChatOptions.ModelPurposes { Assist = "m" },
                        ApiKey = "k"
                    }
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

            // THE SHIPPED DEFAULT IS REFUSED, ON PURPOSE (feature nahil-default-backend). The
            // default backend is Nahil, whose endpoint and model are defaulted and whose credential
            // never is, so an instance that turns chat on and configures nothing is told which one
            // value it owes - where the previous Ollama default would instead have dialled
            // http://localhost:11434 and reported whatever happened to answer there.
            var shipped = Validate(new Fallen8ChatOptions());
            Assert.IsNotNull(shipped, "an unconfigured chat gateway must be refused, not aimed somewhere");
            StringAssert.Contains(shipped, "Fallen8:Chat:Nahil:ApiKey",
                "the refusal names the key to set, and names ONLY that: " + shipped);
            Assert.IsNull(Validate(new Fallen8ChatOptions
            {
                Nahil = new Fallen8ChatOptions.NahilOptions { ApiKey = "k" }
            }), "and supplying that one value is enough, because the other two are defaulted");
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

            // The shipped default (Nahil) and an explicit sidecar both land on the same type,
            // which is the claim: one protocol, one backend. The default needs its credential
            // supplied because nothing may default that; see the validation test above.
            using (var backend = Disposable(new Fallen8ChatOptions
            {
                Nahil = new Fallen8ChatOptions.NahilOptions { ApiKey = "k" }
            }))
            {
                Assert.IsInstanceOfType(backend, typeof(OllamaChatBackend),
                    "Ollama and Nahil share one protocol and therefore one backend type");
            }

            using (var backend = Disposable(new Fallen8ChatOptions { Backend = "Ollama" }))
            {
                Assert.IsInstanceOfType(backend, typeof(OllamaChatBackend));
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
            // Both Ollama-protocol blocks name the same fine-tune, so the shipped default and an
            // explicit sidecar report the same model from DIFFERENT blocks - which is the thing
            // worth pinning, since a resolver reading the wrong block would look correct here only
            // because the two values agree. The Nahil block is therefore given a distinct value.
            Assert.AreEqual("phi4-f8-mini:latest", ResolveModel(new Fallen8ChatOptions()),
                "the shipped default reads the Nahil block");
            Assert.AreEqual("phi4-f8-mini:latest", ResolveModel(new Fallen8ChatOptions { Backend = "Ollama" }));
            Assert.AreEqual("phi4-f8:latest", ResolveModel(new Fallen8ChatOptions
            {
                Nahil = new Fallen8ChatOptions.NahilOptions
                {
                    Models = new Fallen8ChatOptions.ModelPurposes { Assist = "phi4-f8:latest" },
                    ApiKey = "k"
                }
            }), "and it is really the Nahil block, not its neighbour that happens to match");

            // And a purpose really selects: the same block serves a DIFFERENT model for the agent
            // purpose, which is the whole point of purposes and would pass with a resolver that
            // ignored the argument if both names matched.
            var twoPurposes = new Fallen8ChatOptions
            {
                Nahil = new Fallen8ChatOptions.NahilOptions
                {
                    ApiKey = "k",
                    Models = new Fallen8ChatOptions.ModelPurposes
                    {
                        Assist = "phi4-f8-mini:latest",
                        Agent = "phi4-mini:latest",
                    }
                }
            };
            Assert.AreEqual("phi4-f8-mini:latest", ResolveModel(twoPurposes, ChatPurpose.Assist));
            Assert.AreEqual("phi4-mini:latest", ResolveModel(twoPurposes, ChatPurpose.Agent));

            // The shipped defaults give both purposes a model on both Ollama-protocol backends, and
            // give the two metered providers neither, so a purpose nobody configured is reported as
            // missing rather than guessed.
            Assert.AreEqual("phi4-mini:latest", ResolveModel(new Fallen8ChatOptions(), ChatPurpose.Agent));
            Assert.AreEqual("phi4-mini:latest",
                ResolveModel(new Fallen8ChatOptions { Backend = "Ollama" }, ChatPurpose.Agent));
            Assert.IsNull(ResolveModel(OpenAI("https://api.openai.com", null, "sk-key"), ChatPurpose.Agent));
            Assert.IsNull(ResolveModel(new Fallen8ChatOptions { Backend = "Nope" }),
                "a name this app does not have reports no model rather than a plausible one");
        }

        #region the seam

        /// <summary>The factory is internal (the repository adds no InternalsVisibleTo), so it is
        /// reached the same way the embedding twin's tests reach theirs.</summary>
        [TestMethod]
        public void TheRenamedModelKeyIsRefusedByNameRatherThanQuietlyReplacedByADefault()
        {
            // Model was renamed to Models:Assist with no alias, and the spec promised an instance
            // still carrying the old key "fails closed with a message naming the new one". Nothing
            // kept that promise: configuration binding ignores a key no property claims, silently,
            // and on the two backends whose Models:Assist carries a default the operator's model
            // was replaced by a stock one on every request with nothing said. A fine-tuned assist
            // model swapped for the sidecar's default is the worst version of a silent config
            // fault, because the instance keeps answering.
            foreach (var backend in new[] { "Nahil", "Ollama", "OpenAI", "Anthropic" })
            {
                var options = new Fallen8ChatOptions { Backend = backend };
                var stale = Config((("Fallen8:Chat:" + backend + ":Model"), "phi4-f8-mini:latest"));

                var problem = Validate(options, configuration: stale);
                Assert.IsNotNull(problem, backend + ": a stale model key was not refused at all");
                StringAssert.Contains(problem, "Fallen8:Chat:" + backend + ":Model is no longer read",
                    backend + ": the refusal has to name the key the operator actually set");
                StringAssert.Contains(problem, "Fallen8:Chat:" + backend + ":Models:Assist",
                    backend + ": and the key they should set instead, or the sweep is guesswork");
            }

            // Only the SELECTED block, because that is what a request depends on. A stale key in a
            // block nobody selected refuses the day it is selected, not before.
            var elsewhere = Validate(
                OpenAI("https://api.openai.com", OpenAIModel, "sk-key"),
                configuration: Config(("Fallen8:Chat:Nahil:Model", "something")));
            Assert.IsNull(elsewhere,
                "a stale key in an unselected block refused a working deployment");

            // Models and Model are different sections, so the key this looks for the absence of
            // cannot be what satisfies it.
            var current = Validate(
                OpenAI("https://api.openai.com", OpenAIModel, "sk-key"),
                configuration: Config(("Fallen8:Chat:OpenAI:Models:Assist", OpenAIModel)));
            Assert.IsNull(current, "the NEW key was read as the old one");

            // A caller with no raw configuration still gets the rest of the answer.
            Assert.IsNull(Validate(OpenAI("https://api.openai.com", OpenAIModel, "sk-key")));
        }

        private static MethodInfo Method(String name)
        {
            var factory = typeof(OllamaChatBackend).Assembly
                .GetType("NoSQL.GraphDB.App.Chat.ChatBackendFactory");
            Assert.IsNotNull(factory, "the one home of the chat selector");
            var method = factory.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, name + " is what the boot warning and the 503 share");
            return method;
        }

        // Defaulted the way the production signatures default them, so a call that names neither
        // still exercises the assist path a request without a purpose takes. Every parameter is
        // passed explicitly, because reflection applies no default values: a new optional parameter
        // on the production method turns a short argument array into a runtime failure, not a
        // compile one.
        private static String Validate(Fallen8ChatOptions options,
            ChatPurpose purpose = ChatPurpose.Assist, IConfiguration configuration = null)
        {
            return (String)Method("Validate")
                .Invoke(null, new Object[] { options, purpose, configuration });
        }

        /// <summary>Configuration holding exactly the keys given, as the raw root a stale-key check
        /// reads.</summary>
        private static IConfiguration Config(params (String Key, String Value)[] keys)
        {
            var pairs = new Dictionary<String, String>(StringComparer.Ordinal);
            foreach (var (key, value) in keys)
            {
                pairs[key] = value;
            }

            return new ConfigurationBuilder().AddInMemoryCollection(pairs).Build();
        }

        private static String ResolveModel(Fallen8ChatOptions options,
            ChatPurpose purpose = ChatPurpose.Assist)
        {
            return (String)Method("ResolveModel").Invoke(null, new Object[] { options, purpose });
        }

        private static RemoteModelTarget ResolveRemoteTarget(Fallen8ChatOptions options,
            ChatPurpose purpose = ChatPurpose.Assist)
        {
            return (RemoteModelTarget)Method("ResolveRemoteTarget")
                .Invoke(null, new Object[] { options, purpose });
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
                return (IChatBackend)Method("Create")
                    .Invoke(null, new Object[] { options, null, null });
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
                    Models = new Fallen8ChatOptions.ModelPurposes { Assist = model },
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
                    Models = new Fallen8ChatOptions.ModelPurposes { Assist = model },
                    ApiKey = apiKey
                }
            };
        }

        #endregion
    }
}
