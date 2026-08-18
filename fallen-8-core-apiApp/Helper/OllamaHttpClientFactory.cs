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

namespace NoSQL.GraphDB.App.Helper
{
    /// <summary>
    ///   Builds the bounded <see cref="HttpClient" /> every Ollama call goes through (chat backend,
    ///   embedding backend, residency probe). It exists because OllamaSharp's
    ///   <c>OllamaApiClient(Uri, model)</c> convenience ctor creates its OWN client and leaves
    ///   <see cref="HttpClient.Timeout" /> at the .NET default of 100s: an undocumented,
    ///   non-configurable deadline that silently pre-empted the providers' configured budgets and
    ///   surfaced as an unhandled <see cref="System.Threading.Tasks.TaskCanceledException" />
    ///   (HTTP 500) instead of the documented gateway-timeout mapping.
    ///   <para>
    ///     THE DEADLINE RULE, stated once for all Ollama callers: a caller either owns the deadline
    ///     with a linked <see cref="System.Threading.CancellationTokenSource" /> and passes
    ///     <see cref="System.Threading.Timeout.InfiniteTimeSpan" /> here (the two providers, whose
    ///     budget is operator-configured and must be authoritative), or it has no outer budget and
    ///     passes a finite one here (the probe). Never both: two deadlines is the bug above.
    ///   </para>
    ///   Handler shape matches the house sidecar client
    ///   (<see cref="NoSQL.GraphDB.App.Ingestion.SidecarHttpClient" />): a
    ///   <see cref="SocketsHttpHandler" /> that recycles pooled connections so a restarted sidecar
    ///   is not pinned to a stale DNS answer.
    /// </summary>
    internal static class OllamaHttpClientFactory
    {
        /// <summary>The pooled-connection lifetime, shared with the sidecar clients so a restarted
        /// Ollama container is picked up without an app restart.</summary>
        private static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(2);

        /// <param name="endpoint">The Ollama base URL. Must be non-blank; callers guard first.</param>
        /// <param name="requestTimeout">The transport deadline. Pass
        /// <see cref="System.Threading.Timeout.InfiniteTimeSpan" /> when an outer linked token owns
        /// the budget, otherwise a finite bound this caller is willing to wait.</param>
        internal static HttpClient Create(String endpoint, TimeSpan requestTimeout)
        {
            if (String.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentException("An Ollama endpoint is required.", nameof(endpoint));
            }

            return new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = PooledConnectionLifetime
            }, disposeHandler: true)
            {
                BaseAddress = new Uri(endpoint),
                Timeout = requestTimeout
            };
        }
    }
}
