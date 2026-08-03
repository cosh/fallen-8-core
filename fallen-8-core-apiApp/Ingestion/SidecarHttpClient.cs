// MIT License
//
// SidecarHttpClient.cs
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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace NoSQL.GraphDB.App.Ingestion
{
    /// <summary>
    ///   Shared HTTP plumbing for the ingestion sidecars (docling, NLP) - the single home for
    ///   what <c>DoclingClient</c> and <c>NlpClient</c> used to copy verbatim (consolidation-audit
    ///   CA-10). Owns the DNS-recycling <see cref="HttpClient"/> (a <see cref="SocketsHttpHandler"/>
    ///   with a bounded <c>PooledConnectionLifetime</c> so a restarted sidecar container's new IP is
    ///   picked up rather than pinned for the process lifetime), the trailing-slash endpoint
    ///   normalization, and the cached <c>GET /health</c> probe (30s TTL, 5s per-probe timeout,
    ///   caller-cancellation propagated and never cached as "down").
    ///
    ///   <para>Each concrete client supplies its endpoint, its ALREADY-COMPUTED request timeout (the
    ///   two clients clamp their configured seconds differently, so the base never computes it), a
    ///   log label, and its own protocol methods over <see cref="Http"/>. A null/blank endpoint
    ///   leaves the client <see cref="Configured"/> = false with no <see cref="HttpClient"/> - the
    ///   state <c>/status</c> reports.</para>
    /// </summary>
    public abstract class SidecarHttpClient : IDisposable
    {
        private static readonly TimeSpan HealthCacheTtl = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(5);

        private readonly HttpClient _client;
        private readonly ILogger _logger;
        private readonly String _logLabel;

        /// <summary>Monotonic-clock cache for the health probe (0 ticks = never probed).</summary>
        private Int64 _healthProbedAtTicks;
        private volatile Boolean _healthy;

        /// <summary>The configured HTTP client for a subclass's protocol calls; null when the sidecar
        /// endpoint is unconfigured, so guard with <see cref="Configured"/> first.</summary>
        protected HttpClient Http => _client;

        /// <param name="endpoint">The sidecar base URL; null/blank leaves the client unconfigured.</param>
        /// <param name="requestTimeout">The per-request <see cref="HttpClient.Timeout"/>, computed by
        /// the subclass (the two clients clamp differently).</param>
        /// <param name="logger">The subclass logger, used for the health-probe debug line.</param>
        /// <param name="logLabel">The name used in the health-probe debug log ("Docling"/"NLP").</param>
        /// <param name="handler">A test-supplied handler; used verbatim when non-null.</param>
        protected SidecarHttpClient(String endpoint, TimeSpan requestTimeout, ILogger logger, String logLabel,
            HttpMessageHandler handler)
        {
            _logger = logger;
            _logLabel = logLabel;
            if (!String.IsNullOrWhiteSpace(endpoint))
            {
                var normalized = endpoint.EndsWith("/", StringComparison.Ordinal) ? endpoint : endpoint + "/";
                _client = new HttpClient(handler ?? new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2)
                }, disposeHandler: true);
                _client.BaseAddress = new Uri(normalized);
                _client.Timeout = requestTimeout;
            }
        }

        /// <summary>Whether a sidecar endpoint is configured (an HTTP client was built).</summary>
        public Boolean Configured => _client != null;

        /// <summary>
        ///   Cached <c>GET /health</c> probe for the <c>/status</c> block: returns the cached verdict
        ///   within the 30s TTL, otherwise probes with a 5s budget. A caller cancellation propagates
        ///   (never cached as "down" - a cancelled request says nothing about the sidecar); any other
        ///   failure caches "down" for the TTL.
        /// </summary>
        public async Task<Boolean> IsReachableAsync(CancellationToken cancellationToken)
        {
            if (!Configured)
            {
                return false;
            }

            var now = Environment.TickCount64;
            var probedAt = Interlocked.Read(ref _healthProbedAtTicks);
            if (probedAt != 0 && now - probedAt < (Int64)HealthCacheTtl.TotalMilliseconds)
            {
                return _healthy;
            }

            Boolean healthy;
            try
            {
                using (var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    probeCts.CancelAfter(HealthProbeTimeout);
                    using (var response = await _client.GetAsync("health", probeCts.Token))
                    {
                        healthy = response.IsSuccessStatusCode;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                _logger.LogDebug("{Label} health probe failed: {Reason}", _logLabel, ex.Message);
                healthy = false;
            }

            _healthy = healthy;
            Interlocked.Exchange(ref _healthProbedAtTicks, now);
            return healthy;
        }

        public void Dispose()
        {
            _client?.Dispose();
        }
    }
}
