// MIT License
//
// AgentsClient.cs
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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Ingestion;
using NoSQL.GraphDB.App.Integrations;

namespace NoSQL.GraphDB.App.Agents
{
    /// <summary>The agent host is not configured, unreachable, or timed out. Never a fabricated
    /// status: the proxy turns this into 503, and a status the HOST chose is always the host's
    /// own.</summary>
    public sealed class AgentsUnavailableException : Exception
    {
        public AgentsUnavailableException(String message, Exception inner = null) : base(message, inner)
        {
        }
    }

    /// <summary>The agent host behind the fallen-8-agents sidecar (feature agent-host). One
    /// implementation; the seam exists for the proxy's tests.</summary>
    public interface IAgentsClient
    {
        Boolean Configured
        {
            get;
        }

        /// <summary>Forwards one request to the host and returns what it answered. Throws
        /// <see cref="AgentsUnavailableException"/> when the host is unconfigured or does not
        /// answer; every status the host itself chose comes back on the result.</summary>
        Task<SidecarResponse> ForwardAsync(HttpMethod method, String path, String jsonBody,
            CancellationToken cancellationToken);

        Task<Boolean> IsReachableAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    ///   The HTTP client for fallen-8-agents: ONE forwarding method plus the base's cached
    ///   <c>GET /health</c> probe. There is no method per route and no typed body anywhere, because
    ///   the proxy decides nothing from a body - it hands the host's own contract through in both
    ///   directions, exactly as the integrations proxy does and for the same reason: re-declaring
    ///   spawn requests, listings and the posture here would be a second definition to keep in step
    ///   for no gain.
    ///
    ///   <para><b>The bodies here are small and stay small</b>, which is why this client has no
    ///   streamed arm at all where the integrations one does: a spawn carries a task sentence and a
    ///   listing carries counters. The event feed IS a stream, and it gets its own arm in the phase
    ///   that adds it rather than a shared one that would have to buffer.</para>
    /// </summary>
    public sealed class AgentsClient : SidecarHttpClient, IAgentsClient
    {
        // The base owns the HttpClient, endpoint normalization, the cached /health probe, Configured
        // and Dispose; this client keeps only the forwarding call. It takes an ALREADY-COMPUTED
        // timeout, so the formula stays here: floored at 1 second, since a zero timeout would fail
        // every call instantly.
        public AgentsClient(IOptions<Fallen8AgentsOptions> options, ILogger<AgentsClient> logger,
            HttpMessageHandler handler = null)
            : base(Resolve(options).Endpoint,
                   TimeSpan.FromSeconds(Math.Max(1, Resolve(options).TimeoutSeconds)),
                   logger, "Agents", handler)
        {
        }

        private static Fallen8AgentsOptions Resolve(IOptions<Fallen8AgentsOptions> options)
            => options.Value ?? new Fallen8AgentsOptions();

        /// <summary>
        ///   Sends <paramref name="jsonBody" /> to the host's <paramref name="path" /> unchanged and
        ///   returns its status, body and content type unchanged. <paramref name="path" /> is
        ///   RELATIVE with no leading slash: the base address ends in a slash, so a rooted path would
        ///   discard a reverse proxy's path prefix.
        /// </summary>
        public async Task<SidecarResponse> ForwardAsync(HttpMethod method, String path, String jsonBody,
            CancellationToken cancellationToken)
        {
            if (!Configured)
            {
                throw new AgentsUnavailableException(
                    "No agent-host endpoint is configured (Fallen8:Agents:Endpoint).");
            }

            try
            {
                using (var request = new HttpRequestMessage(method, path))
                {
                    if (jsonBody != null)
                    {
                        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                    }

                    using (var response = await Http.SendAsync(request, cancellationToken))
                    {
                        // Read as TEXT, never deserialized: the body belongs to the host's contract
                        // and this hop must not be able to change what it says.
                        var body = await response.Content.ReadAsStringAsync(cancellationToken);
                        return new SidecarResponse((Int32)response.StatusCode, body,
                            response.Content.Headers.ContentType?.ToString());
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The CALLER went away. Their own cancellation is not a sidecar failure and must not
                // be reported as one.
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException)
            {
                throw new AgentsUnavailableException(
                    String.Format("The agent host did not answer ({0}).", ex.Message), ex);
            }
        }
    }
}
