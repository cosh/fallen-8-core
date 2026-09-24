// MIT License
//
// RemoteModelHttpClient.cs
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
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;

namespace NoSQL.GraphDB.App.Helper
{
    /// <summary>
    ///   The transport every provider-SDK client sits on (feature model-providers). It takes no
    ///   deadline, for the reason stated once on <see cref="OllamaHttpClientFactory" />: the caller's
    ///   configured budget is the single authoritative one, and the SDK's own deadline is disarmed
    ///   here too, so a client cannot be composed with only half of the rule applied.
    ///
    ///   <para>It sets no <c>BaseAddress</c> and NO credential header, which is an omission only
    ///   until you know that each provider's SDK builds its own request URL and attaches its own
    ///   credential from the options object it was constructed with. Adding either here would give
    ///   the key a second place to be and the URL a second author.</para>
    ///
    ///   <para>This is the ONE home for that composition, the way
    ///   <see cref="OllamaHttpClientFactory" /> is for Ollama-protocol clients: a provider with more
    ///   than one capability has more than one client (OpenAI has chat AND embeddings), and a second
    ///   copy of the settings is how one of them quietly keeps an SDK default.</para>
    /// </summary>
    internal static class RemoteModelHttpClient
    {
        /// <summary>
        ///   The statuses an OpenAI-protocol provider uses to mean "ask again", with a
        ///   <c>Retry-After</c> it means. Anything else reaches the caller at once; waiting out a
        ///   <c>503</c> is Nahil's warm-up contract, not this one.
        ///   <para>It lives here, beside <see cref="OpenAIOptions" />, for the reason stated on this
        ///   class: OpenAI has chat AND embeddings, so a second copy of this set is how one of the two
        ///   clients quietly keeps waiting for a status the other one already gave up on. Anthropic's
        ///   set has exactly one reader and stays on its own backend.</para>
        /// </summary>
        internal static readonly HttpStatusCode[] OpenAIRetryable = { HttpStatusCode.TooManyRequests };

        /// <param name="retry">Our retry, composed INTO this chain. Never handed to an SDK's own
        /// handler list: an SDK that re-sends the request message refuses a resent one.</param>
        /// <param name="handler">A test-supplied transport handler; used verbatim when non-null, so a
        /// test exercises the real credential and retry composition rather than a stand-in.</param>
        internal static HttpClient Create(RetryAfterHandler retry, HttpMessageHandler handler = null)
        {
            var inner = handler ?? new SocketsHttpHandler
            {
                PooledConnectionLifetime = OllamaHttpClientFactory.PooledConnectionLifetime
            };

            if (retry != null)
            {
                retry.InnerHandler = inner;
                inner = retry;
            }

            return new HttpClient(inner, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        /// <summary>
        ///   The OpenAI-protocol client options, for every client of that protocol we build (chat and
        ///   embeddings today). Three of the four settings exist to hand a default back:
        ///   <c>System.ClientModel</c>'s network deadline is an undocumented 100 seconds that once
        ///   shipped here as an unhandled <c>TaskCanceledException</c>, and its default retry policy
        ///   makes FOUR attempts against one failure without saying so, which on a metered provider
        ///   is spend the operator did not ask for. Attempts are ours instead, in
        ///   <see cref="RemoteModelRetryHandler" />, where they are logged and bounded by the caller's
        ///   budget.
        /// </summary>
        /// <param name="target">The validated target. Only its endpoint is read; the credential is the
        /// SDK client constructor's own argument, so it never passes through here.</param>
        /// <param name="http">The transport from <see cref="Create" />.</param>
        internal static OpenAI.OpenAIClientOptions OpenAIOptions(RemoteModelTarget target, HttpClient http)
        {
            return new OpenAI.OpenAIClientOptions
            {
                // The SDK appends the route suffix ("chat/completions", "embeddings") to this
                // VERBATIM, so the configured host root gains the version segment here and never in
                // configuration. Anthropic does the opposite; see EndpointRule for why configuration
                // takes a host root either way.
                Endpoint = new Uri(target.Endpoint.TrimEnd('/') + "/v1"),
                Transport = new HttpClientPipelineTransport(http),
                RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
                NetworkTimeout = Timeout.InfiniteTimeSpan
            };
        }

        /// <summary>
        ///   All that may be said about an OpenAI-protocol SDK failure, for BOTH clients of that
        ///   protocol. The SDK's own message is the status line plus the provider's response body
        ///   verbatim, and neither a body nor a URL is safe to repeat: either can carry the
        ///   credential, and these sentences reach an operator through a problem-detail that is
        ///   anonymous on a keyless instance.
        /// </summary>
        internal static String Describe(String providerName, ClientResultException ex)
        {
            // Status 0 is a socket fault that never reached the provider: the SDK drops the
            // HttpRequestException type, so the status is the only thing left saying so.
            return ex.Status == 0
                ? providerName + " could not be reached."
                : providerName + " answered " + ex.Status.ToString(CultureInfo.InvariantCulture) + ".";
        }

        /// <summary>
        ///   The same failure as the exception a caller may see. The SDK's own is kept as the INNER
        ///   exception - no surface reads that, so a debugger still has the raw refusal while nothing
        ///   composes it into a response or a log line.
        /// </summary>
        internal static HttpRequestException Failed(String providerName, ClientResultException ex)
        {
            return new HttpRequestException(Describe(providerName, ex), ex,
                ex.Status == 0 ? null : (HttpStatusCode)ex.Status);
        }
    }
}
