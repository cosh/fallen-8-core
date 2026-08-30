// MIT License
//
// EndpointRule.cs
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
    ///   The one rule every configured model endpoint obeys: it is a host root, and a rejection
    ///   never quotes it. Shared by <see cref="OllamaConnection" /> and
    ///   <see cref="RemoteModelTarget" /> so all four backends refuse the same URLs with the same
    ///   sentence.
    ///
    ///   <para>No message here QUOTES the endpoint. It reaches an operator two ways - a startup
    ///   warning and the problem-detail of the 503 the affected capability answers - and the
    ///   second of those is anonymous on a keyless instance, while the catalog deliberately
    ///   withholds this key's value from the config surface. Echoing it back would undo that,
    ///   and would disclose any credential an operator embedded in the URL. Naming the key is
    ///   enough to fix it.</para>
    ///
    ///   <para>The host-root rule is the one worth stating: every client here builds its request URL
    ///   by adding its own route to this value, and none of them treats a path already in it as a
    ///   prefix to keep. <see cref="System.Net.Http.HttpClient.BaseAddress"/> DROPS one, silently, as
    ///   soon as a request URI starts with <c>/</c> - that is the Ollama-protocol mechanism. The
    ///   provider SDKs instead APPEND to whatever they were given, so a configured
    ///   <c>https://host/v1</c> becomes <c>https://host/v1/v1/...</c>. Either way the request goes
    ///   somewhere nobody configured and reports only a 404 from an unexpected place, so a path is
    ///   refused rather than rewritten: guessing which half of <c>https://host/prefix</c> the operator
    ///   meant is how a proxy path becomes unreachable without anyone noticing.</para>
    /// </summary>
    internal static class EndpointRule
    {
        /// <summary>Whether <paramref name="endpoint" /> can be dialled, with the operator-facing
        /// reason naming <paramref name="sectionKey" /> when it cannot.</summary>
        internal static Boolean Validate(String sectionKey, String endpoint, out String problem)
        {
            if (String.IsNullOrWhiteSpace(endpoint))
            {
                problem = sectionKey + ":Endpoint is required.";
                return false;
            }

            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                problem = sectionKey + ":Endpoint must be an absolute http:// or https:// URL.";
                return false;
            }

            if (uri.AbsolutePath != "/" || !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment))
            {
                problem = sectionKey + ":Endpoint must be a host root - scheme, host and optional"
                    + " port, nothing more - because the client adds its own route to this value and"
                    + " a path here is dropped or doubled rather than honoured.";
                return false;
            }

            problem = null;
            return true;
        }
    }
}
