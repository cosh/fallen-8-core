// MIT License
//
// OllamaConnection.cs
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
    ///   ONE Ollama-protocol target: the local sidecar, or Nahil (nahil.dev), which serves the
    ///   same API for someone else's hardware (feature nahil-backend). It exists because
    ///   three call sites speak that protocol - the chat backend, the embedding client and the
    ///   residency probe - and each would otherwise pair an endpoint with a model and, for a
    ///   Nahil, a credential, on its own.
    ///
    ///   <para><b>The Nahil delta, stated once for all of them.</b> Nahil authenticates
    ///   EVERY route, where real Ollama authenticates none, and answers <c>503</c> with a real
    ///   <c>Retry-After</c> while it pulls a catalogued model onto a worker.
    ///   <see cref="IsNahil"/> is what <see cref="OllamaHttpClientFactory"/> keys both
    ///   behaviours off, which is what keeps them from ever reaching a local sidecar that has
    ///   neither.</para>
    /// </summary>
    /// <remarks>
    ///   Public for the test project rather than because a caller outside this assembly needs it -
    ///   the repository adds no <c>InternalsVisibleTo</c> (the engine does the same for
    ///   <c>DurableFileIo</c> and <c>PluginFactory</c>), and reflecting over an internal type to
    ///   reach an optional-parameter constructor buys nothing but a NullReferenceException when the
    ///   namespace string goes stale.
    /// </remarks>
    public sealed class OllamaConnection
    {
        private OllamaConnection(String sectionKey, String endpoint, String model, String apiKey, Boolean isNahil)
        {
            SectionKey = sectionKey;
            Endpoint = endpoint;
            Model = model;
            ApiKey = apiKey;
            IsNahil = isNahil;
        }

        /// <summary>The configuration section this came from (e.g. <c>Fallen8:Chat:Nahil</c>), so a
        /// rejection can name the key an operator has to fix rather than describe it.</summary>
        public String SectionKey
        {
            get;
        }

        /// <summary>The base URL, host root only - see <see cref="IsValid" />.</summary>
        public String Endpoint
        {
            get;
        }

        /// <summary>The model to name in the request body, VERBATIM: nothing here strips, appends or
        /// normalizes a <c>:tag</c>.</summary>
        public String Model
        {
            get;
        }

        /// <summary>The bearer credential Nahil requires on every route; <c>null</c> for the
        /// local sidecar, which authenticates nothing. NEVER logged - see
        /// <see cref="OllamaHttpClientFactory" />.</summary>
        public String ApiKey
        {
            get;
        }

        /// <summary>Whether this is Nahil rather than the local sidecar.</summary>
        public Boolean IsNahil
        {
            get;
        }

        /// <summary>The local Ollama sidecar: no credential, no warm-up state.</summary>
        public static OllamaConnection Sidecar(String sectionKey, String endpoint, String model)
        {
            return new OllamaConnection(sectionKey, endpoint, model, null, isNahil: false);
        }

        /// <summary>Nahil: bearer-authenticated, and it may answer 503 while it
        /// pulls the model onto a worker.</summary>
        public static OllamaConnection Nahil(String sectionKey, String endpoint, String model, String apiKey)
        {
            return new OllamaConnection(sectionKey, endpoint, model, apiKey, isNahil: true);
        }

        /// <summary>
        ///   Whether this connection can be dialled at all, with the operator-facing reason when it
        ///   cannot. Checked by the backend factories (where a failure latches as the permanent 503
        ///   this instance answers until the configuration is fixed) and reported once at startup, so
        ///   both say the same thing from one place.
        ///
        ///   <para>No message here QUOTES the endpoint. It reaches an operator two ways - a startup
        ///   warning and the problem-detail of the 503 the affected capability answers - and the
        ///   second of those is anonymous on a keyless instance, while the catalog deliberately
        ///   withholds this key's value from the config surface. Echoing it back would undo that,
        ///   and would disclose any credential an operator embedded in the URL. Naming the key is
        ///   enough to fix it.</para>
        ///
        ///   <para>The host-root rule is the one worth stating: <see cref="System.Net.Http.HttpClient.BaseAddress"/>
        ///   DROPS a path prefix as soon as a request URI starts with <c>/</c>, silently, so an
        ///   endpoint carrying a path would dial the wrong URL and report only a 404 from somewhere
        ///   unexpected. It is refused rather than rewritten: guessing which half of
        ///   <c>https://host/prefix</c> the operator meant is how a proxy path becomes unreachable
        ///   without anyone noticing.</para>
        /// </summary>
        public Boolean IsValid(out String problem)
        {
            if (String.IsNullOrWhiteSpace(Endpoint))
            {
                problem = SectionKey + ":Endpoint is required.";
                return false;
            }

            if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                problem = SectionKey + ":Endpoint must be an absolute http:// or https:// URL.";
                return false;
            }

            if (uri.AbsolutePath != "/" || !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment))
            {
                problem = SectionKey + ":Endpoint must be a host root - scheme, host and optional"
                    + " port, nothing more - because HttpClient.BaseAddress drops a path prefix"
                    + " instead of honouring it.";
                return false;
            }

            if (String.IsNullOrWhiteSpace(Model))
            {
                problem = SectionKey + ":Model is required.";
                return false;
            }

            if (IsNahil && String.IsNullOrWhiteSpace(ApiKey))
            {
                problem = SectionKey + ":ApiKey is required: Nahil authenticates every route.";
                return false;
            }

            problem = null;
            return true;
        }
    }
}
