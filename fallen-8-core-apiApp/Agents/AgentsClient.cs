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

        /// <summary>
        ///   Forwards a request whose answer is a STREAM, and copies it through to
        ///   <paramref name="destination" /> as it arrives.
        ///
        ///   <para>
        ///     Its own method rather than a flag on the one above, because everything about it
        ///     differs: response-headers-read semantics so the answer is not buffered, a deadline on
        ///     the HEADERS phase only, and a flush per event so a subscriber is not served in
        ///     bursts. Sharing one method would mean the small routes inherited the body's absent
        ///     deadline, which is the more dangerous direction.
        ///   </para>
        ///   <para>
        ///     <b>The deadline split, because the implementation is <c>inheritdoc</c> and this is
        ///     the only contract a caller reads.</b> It said there is no per-call deadline at all,
        ///     "because a feed is open for as long as the client wants it". The body has none, which
        ///     is that sentence's true half. The headers phase gets the small arm's budget, because
        ///     a host that accepts the connection and then never answers is unreachable, and with no
        ///     deadline there that held the caller's request open forever and reported no 503. So a
        ///     caller CAN see an unavailable failure from this method, and should expect one.
        ///   </para>
        ///   <para>
        ///     Returns the host's status and content type through <paramref name="onHeaders" /> BEFORE
        ///     the body flows, because a refusal has to reach the caller as a refusal: a 400 naming
        ///     a bad filter must not arrive as a 200 with an error in the stream.
        ///   </para>
        /// </summary>
        /// <param name="path">The host route, relative, with no leading slash.</param>
        /// <param name="onHeaders">Called once with the host's status and content type, BEFORE any
        /// body flows.</param>
        /// <param name="destination">Where the body is copied, flushed per read.</param>
        /// <param name="cancellationToken">The caller's own; its cancellation propagates as
        /// itself.</param>
        Task StreamAsync(String path, Func<Int32, String, Task> onHeaders,
            System.IO.Stream destination, CancellationToken cancellationToken);

        Task<Boolean> IsReachableAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    ///   The HTTP client for fallen-8-agents: TWO forwarding methods, one buffered and one
    ///   streamed, plus the base's cached <c>GET /health</c> probe. There is no method per route and
    ///   no typed body anywhere, because the proxy decides nothing from a body - it hands the host's
    ///   own contract through in both directions, exactly as the integrations proxy does and for the
    ///   same reason: re-declaring spawn requests, listings and the posture here would be a second
    ///   definition to keep in step for no gain.
    ///
    ///   <para><b>Two arms, because one of the routes is a stream and the rest are not.</b> Every
    ///   body but the feed's is small and stays small: a spawn carries a task sentence, a listing
    ///   carries counters, so <see cref="ForwardAsync" /> buffers and applies the whole per-call
    ///   budget. The feed never ends, so <see cref="StreamAsync" /> reads headers-first, copies
    ///   through, flushes per event, and holds the budget on the headers phase alone.</para>
    ///   <para>
    ///     This said the client had "ONE forwarding method" and "no streamed arm at all", and
    ///     described the feed's arm in the future tense, in the file that had contained it since
    ///     the phase before. A maintainer adding a second streaming route would have read that as a
    ///     deliberate absence and either built a parallel client or routed a stream through
    ///     <see cref="ForwardAsync" />, whose buffering read would hold an endless body until the
    ///     per-call budget killed it.
    ///   </para>
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
            _small = TimeSpan.FromSeconds(Math.Max(1, Resolve(options).TimeoutSeconds));

            // HttpClient.Timeout is ONE number for the whole client and the two arms need
            // different ones: a listing should not take 30 seconds, and a feed is open for as long
            // as its subscriber wants it. So it is disarmed here and the small arm applies its own
            // per call. Null when no endpoint is configured, which is the default: the base builds
            // no client then and every call fails at the Configured check before reaching one.
            if (Configured)
            {
                Http.Timeout = Timeout.InfiniteTimeSpan;
            }
        }

        private readonly TimeSpan _small;

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
                            // Read as TEXT, never deserialized: the body belongs to the host's
                            // contract and this hop must not be able to change what it says.
                            var body = await response.Content.ReadAsStringAsync(budget.Token);
                            return new SidecarResponse((Int32)response.StatusCode, body,
                                response.Content.Headers.ContentType?.ToString());
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // The CALLER went away. Their own cancellation is not a sidecar failure and must
                    // not be reported as one.
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException)
                {
                    throw new AgentsUnavailableException(
                        String.Format("The agent host did not answer ({0}).", ex.Message), ex);
                }
            }
        }

        /// <inheritdoc />
        public async Task StreamAsync(String path, Func<Int32, String, Task> onHeaders,
            System.IO.Stream destination, CancellationToken cancellationToken)
        {
            if (!Configured)
            {
                throw new AgentsUnavailableException(
                    "No agent-host endpoint is configured (Fallen8:Agents:Endpoint).");
            }

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, path))
                {
                    // ResponseHeadersRead, which is the whole point: the default buffers the entire
                    // response before returning, and an event feed never ends, so the default would
                    // hold this call open forever and deliver nothing.
                    //
                    // The HEADERS phase gets the small arm's budget and the BODY gets none, which is
                    // the only split that works: a stream legitimately stays open for hours, but a
                    // host that accepted the connection and never answered at all is unreachable,
                    // and without a deadline here that held the caller's request open forever with
                    // no 503 ever reported.
                    HttpResponseMessage response;
                    using (var headers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        headers.CancelAfter(_small);
                        response = await Http.SendAsync(request,
                            HttpCompletionOption.ResponseHeadersRead, headers.Token);
                    }

                    using (response)
                    {
                        await onHeaders((Int32)response.StatusCode,
                            response.Content.Headers.ContentType?.ToString());

                        using (var body = await response.Content.ReadAsStreamAsync(cancellationToken))
                        {
                            // A small buffer and a flush per read, so an event reaches the browser
                            // when the host wrote it. A large buffer would reintroduce exactly the
                            // burstiness DisableBuffering exists to remove.
                            var chunk = new Byte[4096];
                            while (true)
                            {
                                var read = await body.ReadAsync(chunk, cancellationToken);
                                if (read <= 0)
                                {
                                    break;
                                }

                                await destination.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
                                await destination.FlushAsync(cancellationToken);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The CALLER closed the stream. That is how a feed subscription normally ends, and
                // it is not a sidecar failure.
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException
                || ex is System.IO.IOException)
            {
                // IOException is named explicitly because it is what a host DYING MID-STREAM throws
                // out of the body copy, and it is the likeliest failure of a connection that stays
                // open for hours. Unnamed, it escaped this method as itself and reached the
                // controller after the response had already started, where there is no status left
                // to send.
                throw new AgentsUnavailableException(
                    String.Format("The agent host did not answer ({0}).", ex.Message), ex);
            }
        }
    }
}
