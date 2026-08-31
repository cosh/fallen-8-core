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
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
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

    /// <summary>
    ///   The CALLER's request was rejected while it was being forwarded, so the fault is theirs and
    ///   not the runtime's. Its own type because the alternative was measured and is worse than a
    ///   wrong status: a body over this route's bound fails mid-copy inside
    ///   <c>HttpClient.SendAsync</c>, which made it an <c>HttpRequestException</c>, which made it
    ///   503 "the integrations runtime did not answer" - about a runtime that was serving providers
    ///   a second earlier. Distinguishing them is the whole point; the status here is the one
    ///   Kestrel already chose (413 for a body over the bound, 400 otherwise).
    /// </summary>
    public sealed class IntegrationsRequestRejectedException : Exception
    {
        public IntegrationsRequestRejectedException(Int32 status, String message, Exception inner = null)
            : base(message, inner)
        {
            Status = status;
        }

        /// <summary>The status Kestrel chose for this fault; the proxy answers with it verbatim.</summary>
        public Int32 Status
        {
            get;
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

        /// <summary>
        ///   Forwards a request whose body is COPIED STRAIGHT THROUGH from the caller's stream, never
        ///   parsed and never held whole. For the one route whose body carries a file: a job with a
        ///   100 MB extract on it would otherwise be resident about four times over in this process -
        ///   the parsed document, the UTF-16 string a re-serialisation produces, and the UTF-8 bytes it
        ///   is encoded back into - which is hundreds of megabytes of large-object heap per in-flight
        ///   request, for a hop that is not allowed to look at the body anyway.
        ///
        ///   <para>The caller's declared body length is passed through as well (null when they declared
        ///   none), so the runtime can refuse on the HEADER rather than mid-body: without it the
        ///   forwarded request has no length and becomes chunked, and the runtime's own bound could
        ///   only fire once bytes were already moving. A body that fails to read because the CALLER's
        ///   request was refused throws <see cref="IntegrationsRequestRejectedException" /> rather than
        ///   <see cref="IntegrationsUnavailableException" />, so their fault is never reported as an
        ///   unreachable runtime.</para>
        /// </summary>
        Task<SidecarResponse> ForwardStreamAsync(HttpMethod method, String path, Stream body,
            String contentType, Int64? contentLength, CancellationToken cancellationToken);

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
            _small = TimeSpan.FromSeconds(Math.Max(1, Resolve(options).TimeoutSeconds));
            _job = TimeSpan.FromSeconds(Math.Max(1, Resolve(options).JobTimeoutSeconds));

            // The two budgets differ, and HttpClient.Timeout is one number for the whole client, so
            // it is disarmed here and applied per call instead. Safe for the base's cached /health
            // probe, which already links its own 5 second token rather than relying on this.
            //
            // Null when no endpoint is configured, which is the default: the base builds no client at
            // all then, and every call fails at the Configured check before reaching one.
            if (Configured)
            {
                Http.Timeout = Timeout.InfiniteTimeSpan;
            }
        }

        private readonly TimeSpan _small;
        private readonly TimeSpan _job;

        private static Fallen8IntegrationsOptions Resolve(IOptions<Fallen8IntegrationsOptions> options)
            => options.Value ?? new Fallen8IntegrationsOptions();

        /// <summary>
        ///   Was this the CALLER's fault rather than the runtime's? Kestrel raises
        ///   <see cref="BadHttpRequestException" /> while the caller's body is being read, and because
        ///   the read happens inside the forward it arrives wrapped. Walking the chain is what keeps a
        ///   413 from being reported as an unreachable sidecar.
        /// </summary>
        private static Boolean IsCallerFault(Exception ex, out Int32 status, out String detail)
        {
            for (var walk = ex; walk != null; walk = walk.InnerException)
            {
                if (walk is BadHttpRequestException bad)
                {
                    status = bad.StatusCode;
                    detail = bad.Message;
                    return true;
                }
            }

            status = 0;
            detail = null;
            return false;
        }

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

            using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                budget.CancelAfter(_small);
                try
                {
                    using (var request = new HttpRequestMessage(method, path))
                    {
                        if (jsonBody != null)
                        {
                            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                        }

                        using (var response = await Http.SendAsync(request, budget.Token))
                        {
                            // Read as TEXT, never deserialized: the body belongs to the runtime's contract
                            // and this hop must not be able to change what it says.
                            var body = await response.Content.ReadAsStringAsync(budget.Token);
                            var contentType = response.Content.Headers.ContentType?.ToString();
                            return new SidecarResponse((Int32)response.StatusCode, body, contentType);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException)
                {
                    throw new IntegrationsUnavailableException(String.Format(
                        "The integrations runtime did not answer: {0}", ex.Message), ex);
                }
            }
        }

        /// <inheritdoc />
        public async Task<SidecarResponse> ForwardStreamAsync(HttpMethod method, String path, Stream body,
            String contentType, Int64? contentLength, CancellationToken cancellationToken)
        {
            if (!Configured)
            {
                throw new IntegrationsUnavailableException(
                    "No integrations endpoint is configured (Fallen8:Integrations:Endpoint).");
            }

            using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                budget.CancelAfter(_job);
                try
                {
                    using (var request = new HttpRequestMessage(method, path))
                    {
                        // StreamContent over the caller's own body, so the bytes are copied through in
                        // chunks and nothing here ever holds the whole job. The content type is the
                        // caller's verbatim: this hop decides nothing about the body, including how it is
                        // labelled.
                        var content = new StreamContent(body);
                        content.Headers.TryAddWithoutValidation("Content-Type",
                            String.IsNullOrEmpty(contentType) ? "application/json" : contentType);

                        // Forwarded so the runtime can refuse on the HEADER instead of mid-body, which
                        // is the same courtesy this proxy now extends to its own caller. Without it
                        // StreamContent has no length and the hop becomes chunked, so the runtime's own
                        // bound could only fire once bytes were already moving.
                        if (contentLength.HasValue)
                        {
                            content.Headers.ContentLength = contentLength.Value;
                        }

                        request.Content = content;

                        // ResponseHeadersRead so the forwarding does not wait on a body it is about to read
                        // itself, which for a long-running job run is the difference between streaming and
                        // sitting on a buffer.
                        using (var response = await Http.SendAsync(request,
                            HttpCompletionOption.ResponseHeadersRead, budget.Token))
                        {
                            // The RESPONSE is still read whole, deliberately: it is a job report, bounded by
                            // its own diagnostics list, and the proxy has to hand it back as one string.
                            var answered = await response.Content.ReadAsStringAsync(budget.Token);
                            var answeredType = response.Content.Headers.ContentType?.ToString();
                            return new SidecarResponse((Int32)response.StatusCode, answered, answeredType);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException)
                {
                    // The caller's own body failing to read is checked FIRST, or a 413 arrives as a
                    // report that a healthy runtime is unreachable.
                    if (IsCallerFault(ex, out var status, out var detail))
                    {
                        throw new IntegrationsRequestRejectedException(status, detail, ex);
                    }

                    throw new IntegrationsUnavailableException(String.Format(
                        "The integrations runtime did not answer: {0}", ex.Message), ex);
                }
            }
        }
    }
}
