// MIT License
//
// OllamaHttpClientFactory.cs
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
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace NoSQL.GraphDB.App.Helper
{
    /// <summary>
    ///   Builds the bounded <see cref="HttpClient" /> every Ollama-protocol call goes through (chat
    ///   backend, embedding backend, residency probe). It exists because OllamaSharp's
    ///   <c>OllamaApiClient(Uri, model)</c> convenience ctor creates its OWN client and leaves
    ///   <see cref="HttpClient.Timeout" /> at the .NET default of 100s: an undocumented,
    ///   non-configurable deadline that silently pre-empted the providers' configured budgets and
    ///   surfaced as an unhandled <see cref="System.Threading.Tasks.TaskCanceledException" />
    ///   (HTTP 500) instead of the documented gateway-timeout mapping.
    ///   <para>
    ///     THE DEADLINE RULE, stated once for all Ollama-protocol callers: there is exactly ONE
    ///     deadline on a call, never two, because two deadlines is the bug above. A caller either owns
    ///     it with a linked <see cref="CancellationTokenSource" /> and takes no transport one
    ///     (<see cref="CreateForProvider" />, used by the two providers, whose budget is
    ///     operator-configured and must be authoritative), or it has no outer budget and takes a
    ///     finite transport one (<see cref="CreateForProbe" /> with a real timeout, the residency
    ///     probe). A caller that owns a budget covering SEVERAL calls is the third shape: it passes
    ///     <see cref="Timeout.InfiniteTimeSpan" /> to <see cref="CreateForProbe" />, which keeps its
    ///     one budget authoritative while still declining the warm-up retry, because retrying a
    ///     metadata read would spend a shared budget on one of its calls
    ///     (<see cref="Chat.ChatModelCatalog" />). What the two entry points really choose between is
    ///     the RETRY, and the transport deadline is the argument, which is why only the retry is
    ///     baked into the name.
    ///   </para>
    ///   <para>
    ///     A Nahil connection additionally carries the bearer credential it requires on every route
    ///     and, on the provider path only, the warm-up retry - see <see cref="OllamaConnection" /> for
    ///     the protocol delta and <see cref="RetryAfterHandler" />, which owns the wait arithmetic
    ///     for every provider, for the waiting. The
    ///     credential is set ONCE here, on the client, so no call site ever formats an
    ///     <c>Authorization</c> header (and no log line can reach one).
    ///   </para>
    ///   Handler shape matches the house sidecar client
    ///   (<see cref="NoSQL.GraphDB.App.Ingestion.SidecarHttpClient" />): a
    ///   <see cref="SocketsHttpHandler" /> that recycles pooled connections so a restarted sidecar
    ///   is not pinned to a stale DNS answer.
    /// </summary>
    public static class OllamaHttpClientFactory
    {
        /// <summary>The pooled-connection lifetime, shared with the sidecar clients so a restarted
        /// Ollama container is picked up without an app restart.</summary>
        internal static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(2);

        /// <summary>
        ///   The transport for a provider call: no deadline of its own, and on Nahil it waits out
        ///   a warm-up <c>503</c>/<c>429</c> INSIDE the caller's budget.
        /// </summary>
        /// <param name="connection">The target. Must be <see cref="OllamaConnection.IsValid" />;
        /// callers check first so the reason reaches the operator.</param>
        /// <param name="logger">Where the per-retry lines go. Only used for Nahil.</param>
        /// <param name="handler">A test-supplied transport handler; used verbatim when non-null, so a
        /// test sees the real credential and retry composition rather than a stand-in for it.</param>
        public static HttpClient CreateForProvider(OllamaConnection connection, ILogger logger,
            HttpMessageHandler handler = null)
        {
            return Create(connection, Timeout.InfiniteTimeSpan,
                connection.IsNahil ? new NahilWarmupRetryHandler(connection.Model, logger) : null, handler);
        }

        /// <summary>
        ///   The transport for the residency probe: a finite bound of its own and NEVER a retry.
        ///   Waiting out a warm-up here would defeat the very bound the probe exists to keep - a
        ///   config read answers "unknown" rather than blocking on a model pull.
        /// </summary>
        public static HttpClient CreateForProbe(OllamaConnection connection, TimeSpan probeTimeout,
            HttpMessageHandler handler = null)
        {
            return Create(connection, probeTimeout, retry: null, handler);
        }

        private static HttpClient Create(OllamaConnection connection, TimeSpan requestTimeout,
            RetryAfterHandler retry, HttpMessageHandler transport)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            if (String.IsNullOrWhiteSpace(connection.Endpoint))
            {
                throw new ArgumentException("An Ollama endpoint is required.", nameof(connection));
            }

            var handler = transport ?? new SocketsHttpHandler
            {
                PooledConnectionLifetime = PooledConnectionLifetime
            };

            if (retry != null)
            {
                retry.InnerHandler = handler;
                handler = retry;
            }

            var client = new HttpClient(handler, disposeHandler: true)
            {
                BaseAddress = new Uri(connection.Endpoint),
                Timeout = requestTimeout
            };

            if (!String.IsNullOrWhiteSpace(connection.ApiKey))
            {
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", connection.ApiKey);
            }

            return client;
        }
    }
}
