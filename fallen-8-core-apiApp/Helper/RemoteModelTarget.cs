// MIT License
//
// RemoteModelTarget.cs
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

namespace NoSQL.GraphDB.App.Helper
{
    /// <summary>
    ///   ONE remote model provider target: an endpoint, a model name and the credential that
    ///   provider requires on every route. The counterpart of <see cref="OllamaConnection" /> for
    ///   the providers that speak their OWN protocol rather than Ollama's, so the chat and
    ///   embedding factories pair those three values in one place instead of three.
    ///
    ///   <para>It carries no transport and builds no URL. Each provider's SDK owns request-URL
    ///   construction and the credential header, which is why <see cref="Endpoint" /> is a host
    ///   root here and the route suffix never appears in configuration.</para>
    /// </summary>
    /// <remarks>
    ///   Public for the test project rather than because a caller outside this assembly needs it -
    ///   the repository adds no <c>InternalsVisibleTo</c>, the same reason
    ///   <see cref="OllamaConnection" /> is public.
    /// </remarks>
    public sealed class RemoteModelTarget
    {
        private RemoteModelTarget(String sectionKey, String endpoint, String model, String apiKey, String providerName)
        {
            SectionKey = sectionKey;
            Endpoint = endpoint;
            Model = model;
            ApiKey = apiKey;
            ProviderName = providerName;
        }

        /// <summary>The configuration section this came from (e.g. <c>Fallen8:Chat:OpenAI</c>), so a
        /// rejection can name the key an operator has to fix rather than describe it.</summary>
        public String SectionKey
        {
            get;
        }

        /// <summary>The base URL, host root only - see <see cref="EndpointRule" />.</summary>
        public String Endpoint
        {
            get;
        }

        /// <summary>The model to name in the request body, VERBATIM: nothing here strips, appends or
        /// normalizes a suffix.</summary>
        public String Model
        {
            get;
        }

        /// <summary>The credential the provider requires on every route. NEVER logged, and attached
        /// exactly once, by the SDK, at client construction.</summary>
        public String ApiKey
        {
            get;
        }

        /// <summary>The provider's name, as the selector spells it. It reaches an operator through a
        /// validation sentence and a retry log line, so it is a display value, not a switch key.</summary>
        public String ProviderName
        {
            get;
        }

        /// <summary>OpenAI, or an OpenAI-compatible gateway: <c>Authorization: Bearer</c> on every
        /// route.</summary>
        public static RemoteModelTarget OpenAI(String sectionKey, String endpoint, String model, String apiKey)
        {
            return new RemoteModelTarget(sectionKey, endpoint, model, apiKey, "OpenAI");
        }

        /// <summary>Anthropic: <c>x-api-key</c> plus <c>anthropic-version</c> on every route.</summary>
        public static RemoteModelTarget Anthropic(String sectionKey, String endpoint, String model, String apiKey)
        {
            return new RemoteModelTarget(sectionKey, endpoint, model, apiKey, "Anthropic");
        }

        /// <summary>
        ///   Whether this target can be dialled at all, with the operator-facing reason when it
        ///   cannot. Checked by the backend factories (where a failure latches as the permanent 503
        ///   this instance answers until the configuration is fixed) and reported once at startup, so
        ///   both say the same thing from one place. The endpoint half of the answer, and why no
        ///   message quotes the endpoint, lives on <see cref="EndpointRule" />.
        ///
        ///   <para>An empty credential is refused HERE, before any client exists, because the
        ///   Anthropic SDK would otherwise resolve one from the ambient environment on its own: the
        ///   hook that switches that off is <c>protected</c>, so a blank configured key silently
        ///   becomes whatever <c>ANTHROPIC_API_KEY</c> holds on that machine.</para>
        /// </summary>
        public Boolean IsValid(out String problem)
        {
            if (!EndpointRule.Validate(SectionKey, Endpoint, out problem))
            {
                return false;
            }

            if (String.IsNullOrWhiteSpace(Model))
            {
                problem = SectionKey + ":Model is required.";
                return false;
            }

            if (String.IsNullOrWhiteSpace(ApiKey))
            {
                problem = SectionKey + ":ApiKey is required: " + ProviderName + " authenticates every route.";
                return false;
            }

            problem = null;
            return true;
        }
    }
}
