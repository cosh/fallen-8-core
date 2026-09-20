// MIT License
//
// ChatBackendFactory.cs
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Helper;

namespace NoSQL.GraphDB.App.Chat
{
    /// <summary>
    ///   Maps <c>Fallen8:Chat:Backend</c> to an <see cref="IChatBackend" /> (features
    ///   instance-config, nahil-backend and model-providers): the local Ollama sidecar, a remote
    ///   Nahil, which speaks the same protocol, or OpenAI or Anthropic, each of which speaks its own
    ///   and is reached through its own SDK. Called lazily on first use only, so
    ///   nothing is constructed while the capability is off, and a configuration fault raised here
    ///   is latched by that <see cref="Lazy{T}" /> into the permanent 503 this instance answers
    ///   until the configuration changes - the same failure shape a bad backend name has always had.
    /// </summary>
    internal static class ChatBackendFactory
    {
        internal static IChatBackend Create(Fallen8ChatOptions options, ILoggerFactory loggerFactory,
            IConfiguration configuration = null)
        {
            if (Validate(options, configuration: configuration) is { } problem)
            {
                throw new InvalidOperationException(problem);
            }

            switch (options.Backend)
            {
                case "OpenAI":
                    return new OpenAIChatBackend(ResolveRemoteTarget(options), options.Stream,
                        loggerFactory?.CreateLogger<OpenAIChatBackend>());

                case "Anthropic":
                    return new AnthropicChatBackend(ResolveRemoteTarget(options),
                        (options.Anthropic ?? new Fallen8ChatOptions.AnthropicOptions()).MaxTokens,
                        options.Stream, loggerFactory?.CreateLogger<AnthropicChatBackend>());

                default:
                    // Ollama or Nahil, which share one protocol and therefore one backend type.
                    // Nothing else reaches here: Validate refuses every other name above.
                    return new OllamaChatBackend(ResolveConnection(options), options.Stream,
                        loggerFactory?.CreateLogger<OllamaChatBackend>());
            }
        }

        /// <summary>
        ///   THE one home for which endpoint, model and credential a configured OLLAMA-PROTOCOL chat
        ///   backend dials; <c>null</c> both when the selector names no backend this app has AND when
        ///   it names one that speaks another protocol. Shared with the residency
        ///   probe and the config view, so neither can report on a different target than the one a
        ///   completion would actually reach - and the null is what makes the probe skip a backend
        ///   with no residency API to ask. Says nothing about whether the target is USABLE - that
        ///   is <see cref="OllamaConnection.IsValid" />, asked by the callers that need it, and
        ///   <see cref="Validate" /> for the whole question.
        /// </summary>
        internal static OllamaConnection ResolveConnection(Fallen8ChatOptions options,
            ChatPurpose purpose = ChatPurpose.Assist)
        {
            switch (options.Backend)
            {
                case "Ollama":
                    return OllamaConnection.Sidecar("Fallen8:Chat:Ollama",
                        options.Ollama?.Endpoint, ModelFor(options.Ollama?.Models, purpose),
                        ModelKeyFor("Ollama", purpose));

                case "Nahil":
                    return OllamaConnection.Nahil("Fallen8:Chat:Nahil",
                        options.Nahil?.Endpoint, ModelFor(options.Nahil?.Models, purpose),
                        options.Nahil?.ApiKey, ModelKeyFor("Nahil", purpose));

                default:
                    return null;
            }
        }

        /// <summary>
        ///   The model a backend block serves for one purpose; <c>null</c> when the block or the
        ///   purpose is unset, which is what the validators turn into a message naming the key.
        ///   <para>
        ///     BLANK counts as unset, and this is the one place that decides it: the configuration
        ///     write surface accepts an empty string for a string key, so an operator who empties
        ///     the row leaves <c>""</c> behind rather than clearing it. Normalising here rather
        ///     than at each reader is what makes the report and the refusal agree.
        ///     <see cref="TryResolveModel" /> already read a blank value as no model, while
        ///     <c>/status</c> and <c>/config</c> published the empty string, so the one state the
        ///     per-purpose row exists to reveal reached Studio as an empty cell.
        ///   </para>
        /// </summary>
        private static String ModelFor(Fallen8ChatOptions.ModelPurposes models, ChatPurpose purpose)
        {
            if (models == null)
            {
                return null;
            }

            var model = purpose == ChatPurpose.Agent ? models.Agent : models.Assist;
            return String.IsNullOrWhiteSpace(model) ? null : model;
        }

        /// <summary>
        ///   The full key of ONE purpose's model, which a refusal names. It carries the purpose
        ///   because a block now holds two models: told only <c>Fallen8:Chat:Nahil:Models</c>, an
        ///   operator cannot see which of the two is missing, and the two are set by different
        ///   people for different reasons. The block itself stays the section key, because the
        ///   endpoint and the credential are still one per block.
        /// </summary>
        private static String ModelKeyFor(String backend, ChatPurpose purpose)
        {
            return "Fallen8:Chat:" + backend + ":Models:" + ChatPurposes.SettingName(purpose);
        }

        /// <summary>The same, for the backends that speak their own protocol; <c>null</c> for the
        /// Ollama-protocol ones and for a name this app does not have.</summary>
        internal static RemoteModelTarget ResolveRemoteTarget(Fallen8ChatOptions options,
            ChatPurpose purpose = ChatPurpose.Assist)
        {
            switch (options.Backend)
            {
                case "OpenAI":
                    return RemoteModelTarget.OpenAI("Fallen8:Chat:OpenAI",
                        options.OpenAI?.Endpoint, ModelFor(options.OpenAI?.Models, purpose),
                        options.OpenAI?.ApiKey, ModelKeyFor("OpenAI", purpose));

                case "Anthropic":
                    return RemoteModelTarget.Anthropic("Fallen8:Chat:Anthropic",
                        options.Anthropic?.Endpoint, ModelFor(options.Anthropic?.Models, purpose),
                        options.Anthropic?.ApiKey, ModelKeyFor("Anthropic", purpose));

                default:
                    return null;
            }
        }

        /// <summary>
        ///   The server-owned model, whichever backend serves it. Read from the block the selector
        ///   points at rather than from the probe target, which answers the narrower question of
        ///   which Ollama-protocol backend can be asked about residency.
        /// </summary>
        internal static String ResolveModel(Fallen8ChatOptions options,
            ChatPurpose purpose = ChatPurpose.Assist)
        {
            return ResolveConnection(options, purpose)?.Model
                ?? ResolveRemoteTarget(options, purpose)?.Model;
        }

        /// <summary>
        ///   The model one purpose is configured with, or the operator-facing reason there is none.
        ///   <para>
        ///     Narrower than <see cref="Validate" /> ON PURPOSE, and the difference matters. Validate
        ///     answers "can this backend be dialled at all", which belongs to CONSTRUCTION and runs
        ///     once behind the provider's <see cref="Lazy{T}" />; this answers "does the purpose this
        ///     request asked for name a model", which is per request because the construction check
        ///     only ever saw the default purpose. Asking the broader question per request would also
        ///     re-check an endpoint and a credential that the constructed backend may legitimately
        ///     not have - a test that injects its own backend has neither, and must not be refused
        ///     for it.
        ///   </para>
        /// </summary>
        internal static Boolean TryResolveModel(Fallen8ChatOptions options, ChatPurpose purpose,
            out String model, out String problem)
        {
            var key = ResolveConnection(options, purpose) is { } connection
                ? (connection.Model, connection.ModelKey)
                : ResolveRemoteTarget(options, purpose) is { } target
                    ? (target.Model, target.ModelKey)
                    : (null, null);

            model = key.Item1;
            if (!String.IsNullOrWhiteSpace(model))
            {
                problem = null;
                return true;
            }

            // A backend this app does not have has no key to name, and Validate already owns that
            // sentence; here it can only be reported as what it is.
            problem = key.Item2 == null
                ? String.Format(
                    "Fallen8:Chat:Backend is '{0}', which is not a supported chat backend. "
                    + "Expected Ollama, Nahil, OpenAI or Anthropic.", options.Backend)
                : key.Item2 + " is required for purpose '" + ChatPurposes.SettingName(purpose).ToLowerInvariant() + "'.";
            return false;
        }

        /// <summary>
        ///   Whether the configured backend can be used at all, and the operator-facing reason when
        ///   it cannot; <c>null</c> when it can. THE home for the distinctions a null resolution
        ///   cannot make on its own: a name this app does not have, a supported name whose block is
        ///   incomplete, a block still carrying the key the rename replaced, and a usable target.
        ///   The boot warning and the 503 both read this, so the two can never say different things
        ///   about one deployment.
        /// </summary>
        /// <param name="options">The bound chat options.</param>
        /// <param name="purpose">Which purpose's model must be present.</param>
        /// <param name="configuration">
        ///   The RAW configuration, for the one fault a bound options object cannot see. Optional
        ///   because a caller that has none is still entitled to the rest of the answer; every
        ///   operator-facing path passes it. See <see cref="StaleModelKey" />.
        /// </param>
        internal static String Validate(Fallen8ChatOptions options,
            ChatPurpose purpose = ChatPurpose.Assist, IConfiguration configuration = null)
        {
            if (StaleModelKey(options, configuration) is { } stale)
            {
                // First, because it is the fault that explains the others: a block whose model key
                // is the old spelling reads as complete when it is not.
                return stale;
            }

            if (ResolveConnection(options, purpose) is { } connection)
            {
                return connection.IsValid(out var problem) ? null : problem;
            }

            if (ResolveRemoteTarget(options, purpose) is { } target)
            {
                return target.IsValid(out var problem) ? null : problem;
            }

            return String.Format(
                "Fallen8:Chat:Backend is '{0}', which is not a supported chat backend. "
                + "Expected Ollama, Nahil, OpenAI or Anthropic.", options.Backend);
        }

        /// <summary>
        ///   The one configuration fault a BOUND options object cannot see: a block still carrying
        ///   <c>Model</c>, which <c>Models:Assist</c> replaced with no alias.
        ///
        ///   <para>
        ///     Configuration binding ignores a key no property claims, silently. On Ollama and
        ///     Nahil, whose <c>Models:Assist</c> carries a default, that silence meant an operator
        ///     who missed one key during the rename had their configured model replaced by a stock
        ///     one and was told nothing: a fine-tuned assist model swapped for the sidecar's
        ///     default on every request. The rename's own promise was that such an instance fails
        ///     CLOSED naming the new key, and nothing kept it. Reading the raw configuration is the
        ///     only way to keep it, because the whole problem is that the key binds to nothing.
        ///   </para>
        ///   <para>
        ///     Only the SELECTED backend's block is refused, because that is what the request
        ///     depends on, exactly as its endpoint and credential are. A stale key in a block
        ///     nobody selected refuses the day it is selected.
        ///   </para>
        /// </summary>
        internal static String StaleModelKey(Fallen8ChatOptions options, IConfiguration configuration)
        {
            if (configuration == null || String.IsNullOrWhiteSpace(options?.Backend))
            {
                return null;
            }

            var block = "Fallen8:Chat:" + options.Backend;

            // Exact key, not a prefix: Model and Models are different sections, so this cannot be
            // satisfied by the new key it is looking for the absence of.
            if (String.IsNullOrWhiteSpace(configuration[block + ":Model"]))
            {
                return null;
            }

            return String.Format(
                "{0}:Model is no longer read. It was renamed to {0}:Models:Assist, with no alias, "
                + "and {0}:Models:Agent selects the model for agent runs. Move the value to the "
                + "purpose it belongs to and remove the old key; until then this instance refuses "
                + "rather than quietly serving a different model.", block);
        }
    }
}
