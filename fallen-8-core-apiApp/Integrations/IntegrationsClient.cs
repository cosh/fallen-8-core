// MIT License
//
// IntegrationsClient.cs
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

namespace NoSQL.GraphDB.App.Integrations
{
    /// <summary>
    ///   What the integration runtime answered, carried verbatim: its status, its body as text and
    ///   its content type. Untyped on purpose - the request and response shapes are the runtime's own
    ///   contract, and re-declaring descriptors, jobs and reports here would create a second
    ///   definition to keep in step for no gain (the proxy reads nothing in them).
    /// </summary>
    public sealed class SidecarResponse
    {
        public SidecarResponse(Int32 status, String body, String contentType)
        {
            Status = status;
            Body = body;
            ContentType = contentType;
        }

        /// <summary>The runtime's own status code, including a 400 naming a missing setting and a
        /// 409 conflict; it is passed through rather than remapped.</summary>
        public Int32 Status
        {
            get;
        }

        /// <summary>The runtime's response body, unparsed.</summary>
        public String Body
        {
            get;
        }

        /// <summary>The runtime's response content type (<c>application/problem+json</c> for its own
        /// failures); null when it sent none.</summary>
        public String ContentType
        {
            get;
        }
    }

    /// <summary>The integration runtime is not configured, unreachable, or timed out. Never a
    /// fabricated status: the proxy turns this into 503, and a status the RUNTIME chose is always
    /// the runtime's own.</summary>
    public sealed class IntegrationsUnavailableException : Exception
    {
        public IntegrationsUnavailableException(String message, Exception inner = null) : base(message, inner)
        {
        }
    }

    /// <summary>The integration runtime behind the fallen-8-integrations sidecar (feature
    /// integrations). One implementation; the seam exists for the proxy's tests.</summary>
    public interface IIntegrationsClient
    {
        Boolean Configured
        {
            get;
        }

        /// <summary>Forwards one request to the runtime and returns what it answered. Throws
        /// <see cref="IntegrationsUnavailableException"/> when the runtime is unconfigured or does
        /// not answer; every status the runtime itself chose comes back on the result.</summary>
        Task<SidecarResponse> ForwardAsync(HttpMethod method, String path, String jsonBody,
            CancellationToken cancellationToken);

        Task<Boolean> IsReachableAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    ///   The HTTP client for fallen-8-integrations: ONE forwarding method plus the base's cached
    ///   <c>GET /health</c> probe. There is no method per route and no typed body anywhere, because
    ///   the proxy decides nothing from a body - it hands the runtime's own contract through in both
    ///   directions (feature integrations, the boundary rules in spec section 1).
    /// </summary>
    public sealed class IntegrationsClient : SidecarHttpClient, IIntegrationsClient
    {
        // The base owns the HttpClient, endpoint normalization, the cached /health probe, Configured
        // and Dispose; this client keeps only the forwarding call. The base takes an ALREADY-COMPUTED
        // timeout (each sidecar client clamps its configured seconds its own way), so the formula
        // stays here: floored at 1 second, since a zero timeout would fail every call instantly.
        public IntegrationsClient(IOptions<Fallen8IntegrationsOptions> options, ILogger<IntegrationsClient> logger,
            HttpMessageHandler handler = null)
            : base(Resolve(options).Endpoint,
                   TimeSpan.FromSeconds(Math.Max(1, Resolve(options).TimeoutSeconds)),
                   logger, "Integrations", handler)
        {
        }

        private static Fallen8IntegrationsOptions Resolve(IOptions<Fallen8IntegrationsOptions> options)
            => options.Value ?? new Fallen8IntegrationsOptions();

        /// <summary>
        ///   Sends <paramref name="jsonBody" /> to the runtime's <paramref name="path" /> unchanged
        ///   and returns its status, body and content type unchanged. <paramref name="path" /> is
        ///   RELATIVE with no leading slash: the base address ends in a slash, so a rooted path would
        ///   discard a reverse proxy's path prefix.
        /// </summary>
        public async Task<SidecarResponse> ForwardAsync(HttpMethod method, String path, String jsonBody,
            CancellationToken cancellationToken)
        {
            if (!Configured)
            {
                throw new IntegrationsUnavailableException(
                    "No integrations endpoint is configured (Fallen8:Integrations:Endpoint).");
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
                        // Read as TEXT, never deserialized: the body belongs to the runtime's contract
                        // and this hop must not be able to change what it says.
                        var body = await response.Content.ReadAsStringAsync(cancellationToken);
                        var contentType = response.Content.Headers.ContentType?.ToString();
                        return new SidecarResponse((Int32)response.StatusCode, body, contentType);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                throw new IntegrationsUnavailableException(String.Format(
                    "The integrations runtime did not answer: {0}", ex.Message), ex);
            }
        }
    }
}
