// MIT License
//
// AgentFeedStream.cs
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
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using NoSQL.GraphDB.Agents.Configuration;
using NoSQL.GraphDB.Agents.Runtime;
using NoSQL.GraphDB.Rest;

namespace NoSQL.GraphDB.Agents.Hosting
{
    /// <summary>
    ///   Writes the agent event feed as Server-Sent Events.
    ///
    ///   <para>
    ///     <b>The same frame conventions as this instance's change feed</b>, deliberately: a client
    ///     that can read one can read the other, and there is one SSE dialect in the product rather
    ///     than two. <c>id:</c> is the host instance and the sequence, so a reader can tell a
    ///     reconnect to a restarted host from a gap in one host's stream; <c>event:</c> is the kind,
    ///     so a browser's <c>EventSource</c> can subscribe per kind; keep-alive comments while idle
    ///     bound dead-connection detection and defeat a proxy's idle timeout.
    ///   </para>
    ///   <para>
    ///     <b>There is no catch-up.</b> A subscriber that connects late, or is dropped for falling
    ///     behind, has missed what it missed; <c>GET /agent/{id}/trace</c> is the documented way to
    ///     find out what. That is why <c>Last-Event-ID</c> is deliberately not honoured: pretending
    ///     to resume from a position this host cannot replay would be worse than plainly not
    ///     resuming.
    ///   </para>
    /// </summary>
    public static class AgentFeedStream
    {
        private static readonly Byte[] KeepAlive = Encoding.UTF8.GetBytes(": keep-alive\n\n");

        public static async Task WriteAsync(HttpContext context, AgentFeedDispatcher feed,
            AgentsOptions options, String?[]? agents, String?[]? kinds)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (feed == null)
            {
                throw new ArgumentNullException(nameof(feed));
            }

            // A parser and not a compiler: an unknown kind is refused with the accepted set named,
            // because a subscriber whose typo produced silence cannot tell it from an idle host and
            // would wait for events that were being discarded.
            if (!AgentEventKinds.TryParse(agents, kinds, out var filter, out var problem))
            {
                await Refuse(context, StatusCodes.Status400BadRequest, "Invalid feed filter", problem)
                    .ConfigureAwait(false);
                return;
            }

            if (!feed.TrySubscribe(filter, out var subscription, out var refusal))
            {
                // 503 rather than 429: a full subscriber table means this host cannot serve another
                // stream right now, which is the same shape as any other capacity refusal on this
                // instance's feeds, and the message says which limit it was.
                await Refuse(context, StatusCodes.Status503ServiceUnavailable,
                    "Feed unavailable", refusal).ConfigureAwait(false);
                return;
            }

            using (subscription)
            {
                try
                {
                    await Stream(context, subscription, options, context.RequestAborted)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The client disconnected. That is how a stream normally ends.
                }
            }
        }

        private static async Task Stream(HttpContext context, AgentFeedSubscription subscription,
            AgentsOptions options, CancellationToken cancellation)
        {
            var response = context.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";

            // Buffering off, so an event reaches the client when it is written rather than when a
            // buffer happens to fill. Without this the feed appears to work and delivers in bursts,
            // which is indistinguishable from a slow host.
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            // Flushed before the first event, so a client knows the stream is open even on a host
            // where nothing is happening yet.
            await response.Body.FlushAsync(cancellation).ConfigureAwait(false);

            var keepAlive = TimeSpan.FromSeconds(Math.Max(1, options.Feed.KeepAliveSeconds));

            // ONE periodic timer per connection. A fresh Task.Delay per event would abandon a live
            // timer on every delivery, which is pure churn on a busy feed; the pending read
            // survives heartbeat rounds.
            using var heartbeat = new PeriodicTimer(keepAlive);

            Task<AgentEvent?>? pendingRead = null;
            Task<Boolean>? pendingTick = null;

            while (!cancellation.IsCancellationRequested)
            {
                pendingRead ??= subscription.ReadAsync(cancellation);
                pendingTick ??= NextTick(heartbeat, cancellation);

                var completed = await Task.WhenAny(pendingRead, pendingTick).ConfigureAwait(false);
                if (completed == pendingTick)
                {
                    var ticked = await pendingTick.ConfigureAwait(false);
                    pendingTick = null;
                    if (!ticked)
                    {
                        break;
                    }

                    await response.Body.WriteAsync(KeepAlive, cancellation).ConfigureAwait(false);
                    await response.Body.FlushAsync(cancellation).ConfigureAwait(false);
                    continue;
                }

                var next = await pendingRead.ConfigureAwait(false);
                pendingRead = null;

                if (next == null)
                {
                    // Dropped for falling behind, or the host is stopping. Either way this stream is
                    // over and the client reconnects; the trace is how it finds out what it missed.
                    break;
                }

                var payload = JsonSerializer.Serialize(next, RestSeam.JsonOptions);
                var frame = "id: " + next.Seq.ToString(CultureInfo.InvariantCulture)
                    + "\nevent: " + next.Kind
                    + "\ndata: " + payload + "\n\n";

                await response.Body.WriteAsync(Encoding.UTF8.GetBytes(frame), cancellation)
                    .ConfigureAwait(false);
                await response.Body.FlushAsync(cancellation).ConfigureAwait(false);
            }
        }

        /// <summary>The next heartbeat, or false when cancelled or the timer is gone.</summary>
        private static async Task<Boolean> NextTick(PeriodicTimer heartbeat, CancellationToken cancellation)
        {
            try
            {
                return await heartbeat.WaitForNextTickAsync(cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        /// <summary>
        ///   A refusal before the stream opens, in the house problem+json shape so the apiApp's
        ///   proxy passes it through as the host's own answer.
        /// </summary>
        private static async Task Refuse(HttpContext context, Int32 status, String title, String detail)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/problem+json";

            var body = JsonSerializer.Serialize(new
            {
                title,
                status,
                detail,
            }, RestSeam.JsonOptions);

            await context.Response.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
        }
    }
}
