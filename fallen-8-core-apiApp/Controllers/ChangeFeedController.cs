// MIT License
//
// ChangeFeedController.cs
//
// Copyright (c) 2025 Henning Rauch
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
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.ChangeFeed;

namespace NoSQL.GraphDB.App.Controllers
{
    /// <summary>
    ///   Streams committed graph mutations as Server-Sent Events (feature change-feed).
    ///   The delivery contract is documented once, on the streaming action below (it feeds
    ///   the OpenAPI operation); usage lives in features/done/change-feed/README.md.
    /// </summary>
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiVersion("0.1")]
    public class ChangeFeedController : ControllerBase
    {
        #region Data

        private readonly IFallen8 _fallen8;
        private readonly ILogger<ChangeFeedController> _logger;
        private readonly Fallen8ChangeFeedOptions _options;

        private static readonly byte[] _keepAliveBytes = Encoding.UTF8.GetBytes(": keepalive\n\n");

        #endregion

        public ChangeFeedController(ILogger<ChangeFeedController> logger, IFallen8 fallen8,
            IOptions<Fallen8ChangeFeedOptions> options = null)
        {
            _logger = logger;
            _fallen8 = fallen8;
            _options = options?.Value ?? new Fallen8ChangeFeedOptions();
        }

        /// <summary>
        /// Streams committed graph mutations as Server-Sent Events, with declarative server-side filtering and catch-up.
        /// </summary>
        /// <param name="kinds">Event kinds to include (repeatable or comma-separated): vertexCreated, vertexRemoved, edgeCreated, edgeRemoved, propertySet, propertyRemoved. Unset = all kinds. resync events always pass.</param>
        /// <param name="elements">Element types to include: vertex, edge. Unset = both.</param>
        /// <param name="labels">Element labels to include (exact, case-sensitive). Unset = any label. An unlabeled element never matches a labels filter.</param>
        /// <param name="keys">Property keys to include (exact, case-sensitive). Only property events carry a key, so setting this excludes create/remove events.</param>
        /// <param name="since">Catch-up position: the last seen SSE id ("&lt;epoch&gt;:&lt;seq&gt;") or a bare sequence number. Buffered missed events replay first; a position outside the buffered window (or from another process epoch) starts the stream with resync(seekOutOfRange). The Last-Event-ID header (native EventSource reconnect) is honoured when this parameter is unset.</param>
        /// <remarks>
        /// The stream format per event:
        ///
        ///     id: &lt;epoch-guid&gt;:&lt;seq&gt;
        ///     event: &lt;kind&gt;
        ///     data: {"seq":4712,"ts":"2026-07-15T12:34:56.789Z","kind":"propertySet","element":"vertex","id":42,"label":"person","key":"name"}
        ///
        /// A comment line (": keepalive") is written every KeepAliveSeconds so proxies do not idle
        /// the stream out. Filters combine with AND across dimensions and OR within one dimension;
        /// resync events bypass every filter (continuity loss must always reach the client). On
        /// any resync, re-fetch the state you display; for reason trim/tabulaRasa/load, treat all
        /// held element ids as invalid.
        ///
        /// The feed is fully functional with dynamic code execution disabled - filters are
        /// declarative parameters, never compiled code. Payloads never contain property values;
        /// re-fetch the element when the value is needed.
        /// </remarks>
        /// <response code="200">The SSE stream (text/event-stream); it stays open until the client disconnects</response>
        /// <response code="400">An unknown kind/element value or a malformed since position</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="503">The change feed is disabled (Fallen8:ChangeFeed:Enabled=false) or the concurrent subscriber limit (Fallen8:ChangeFeed:MaxSubscribers) is reached</response>
        [HttpGet("/changefeed")]
        [Produces("text/event-stream")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> StreamChanges(
            [FromQuery] String[] kinds = null,
            [FromQuery] String[] elements = null,
            [FromQuery] String[] labels = null,
            [FromQuery] String[] keys = null,
            [FromQuery] String since = null)
        {
            var feed = _fallen8.ChangeFeed;
            if (feed == null)
            {
                return Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Change feed disabled",
                    detail: "The change feed is disabled on this server (Fallen8:ChangeFeed:Enabled=false).");
            }

            // ---- filter parsing (declarative; a bad value is a 400, never a silently-empty stream)
            var parseError = ChangeFeedQueryParser.TryParseFilter(kinds, elements, labels, keys, out var filter);
            if (parseError != null)
            {
                return Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid change feed filter", detail: parseError);
            }

            // ---- catch-up position: ?since= wins; the EventSource reconnect header fills in.
            var sinceValue = since;
            if (String.IsNullOrEmpty(sinceValue) &&
                Request.Headers.TryGetValue("Last-Event-ID", out var lastEventId) && lastEventId.Count > 0)
            {
                sinceValue = lastEventId[0];
            }

            var sinceError = ChangeFeedQueryParser.TryParseSince(sinceValue, out var sinceEpoch, out var sinceSeq);
            if (sinceError != null)
            {
                return Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid since position", detail: sinceError);
            }

            if (!feed.TrySubscribe(filter, sinceEpoch, sinceSeq, out var subscription))
            {
                return Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Subscriber limit reached",
                    detail: String.Format("The maximum number of concurrent change feed subscribers ({0}) is reached.",
                        feed.Options.MaxSubscribers));
            }

            try
            {
                await StreamSubscription(feed, subscription, HttpContext.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // Client disconnect: normal end of stream.
            }
            finally
            {
                subscription.Dispose();
            }

            return new EmptyResult();
        }

        /// <summary>
        ///   The SSE writer loop: events as they arrive, keep-alive comments while idle, flush per
        ///   write (buffering disabled), until the client disconnects or the feed completes.
        /// </summary>
        private async Task StreamSubscription(ChangeFeedDispatcher feed, ChangeFeedSubscription subscription,
            CancellationToken cancellation)
        {
            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            await Response.Body.FlushAsync(cancellation);

            var keepAlive = TimeSpan.FromSeconds(Math.Max(1, _options.KeepAliveSeconds));
            var epoch = feed.Epoch.ToString("D");

            // ONE periodic timer per connection (a Task.WhenAny against a fresh Task.Delay per
            // event would abandon a live timer for every delivered event - pure churn on a hot
            // stream). The pending read survives heartbeat rounds.
            using var heartbeat = new System.Threading.PeriodicTimer(keepAlive);

            Task<ChangeEvent> pendingRead = null;
            Task<bool> pendingTick = null;
            while (!cancellation.IsCancellationRequested)
            {
                pendingRead ??= ReadNextAsync(subscription, cancellation);
                pendingTick ??= NextTickAsync(heartbeat, cancellation);

                var completed = await Task.WhenAny(pendingRead, pendingTick);
                if (completed == pendingTick)
                {
                    var ticked = await pendingTick;
                    pendingTick = null;
                    if (!ticked)
                    {
                        break; // cancelled while waiting for the heartbeat
                    }

                    // Idle: heartbeat comment (bounds dead-connection detection, defeats proxy
                    // idle timeouts).
                    await Response.Body.WriteAsync(_keepAliveBytes, cancellation);
                    await Response.Body.FlushAsync(cancellation);
                    continue;
                }

                var changeEvent = await pendingRead;
                pendingRead = null;

                if (changeEvent == null)
                {
                    // The feed completed (engine dispose) or the client cancelled.
                    break;
                }

                var payload = JsonSerializer.Serialize(ChangeEventREST.FromEvent(changeEvent),
                    AppJsonContext.Default.ChangeEventREST);
                var frame = "id: " + epoch + ":" + changeEvent.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                            "\nevent: " + ChangeEventREST.KindName(changeEvent.Kind) +
                            "\ndata: " + payload + "\n\n";

                await Response.Body.WriteAsync(Encoding.UTF8.GetBytes(frame), cancellation);
                await Response.Body.FlushAsync(cancellation);
            }
        }

        /// <summary>Waits for the next heartbeat tick; false when cancelled or the timer is disposed.</summary>
        private static async Task<bool> NextTickAsync(System.Threading.PeriodicTimer heartbeat,
            CancellationToken cancellation)
        {
            try
            {
                return await heartbeat.WaitForNextTickAsync(cancellation);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        /// <summary>Reads the next event; null when the stream is complete or cancelled.</summary>
        private static async Task<ChangeEvent> ReadNextAsync(ChangeFeedSubscription subscription,
            CancellationToken cancellation)
        {
            try
            {
                return await subscription.Reader.ReadAsync(cancellation);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
    }

    /// <summary>
    ///   Parses the change feed's declarative query grammar (feature change-feed): repeatable
    ///   and/or comma-separated <c>kinds</c>/<c>elements</c>/<c>labels</c>/<c>keys</c> values
    ///   (union within a dimension), and the <c>since</c> position (<c>&lt;epoch&gt;:&lt;seq&gt;</c>
    ///   or a bare sequence number). Unknown kind/element values and malformed positions are
    ///   errors, never silently-empty streams.
    /// </summary>
    internal static class ChangeFeedQueryParser
    {
        internal static String TryParseFilter(String[] kinds, String[] elements, String[] labels, String[] keys,
            out ChangeFeedFilter filter)
        {
            filter = null;

            List<ChangeEventKind> kindList = null;
            foreach (var value in Flatten(kinds))
            {
                switch (value)
                {
                    case "vertexCreated": (kindList ??= new List<ChangeEventKind>()).Add(ChangeEventKind.VertexCreated); break;
                    case "vertexRemoved": (kindList ??= new List<ChangeEventKind>()).Add(ChangeEventKind.VertexRemoved); break;
                    case "edgeCreated": (kindList ??= new List<ChangeEventKind>()).Add(ChangeEventKind.EdgeCreated); break;
                    case "edgeRemoved": (kindList ??= new List<ChangeEventKind>()).Add(ChangeEventKind.EdgeRemoved); break;
                    case "propertySet": (kindList ??= new List<ChangeEventKind>()).Add(ChangeEventKind.PropertySet); break;
                    case "propertyRemoved": (kindList ??= new List<ChangeEventKind>()).Add(ChangeEventKind.PropertyRemoved); break;
                    case "resync":
                        // Accepted for symmetry; resync bypasses filters anyway.
                        (kindList ??= new List<ChangeEventKind>()).Add(ChangeEventKind.Resync);
                        break;
                    default:
                        return String.Format("'{0}' is not a valid event kind. Expected vertexCreated, vertexRemoved, edgeCreated, edgeRemoved, propertySet, propertyRemoved or resync.", value);
                }
            }

            List<ChangeElementType> elementList = null;
            foreach (var value in Flatten(elements))
            {
                switch (value)
                {
                    case "vertex": (elementList ??= new List<ChangeElementType>()).Add(ChangeElementType.Vertex); break;
                    case "edge": (elementList ??= new List<ChangeElementType>()).Add(ChangeElementType.Edge); break;
                    default:
                        return String.Format("'{0}' is not a valid element type. Expected vertex or edge.", value);
                }
            }

            filter = ChangeFeedFilter.Create(kindList, elementList, Flatten(labels), Flatten(keys));
            return null;
        }

        internal static String TryParseSince(String since, out Guid? epoch, out Int64? seq)
        {
            epoch = null;
            seq = null;

            if (String.IsNullOrWhiteSpace(since))
            {
                return null;
            }

            var separator = since.LastIndexOf(':');
            String seqPart;
            if (separator >= 0)
            {
                var epochPart = since.Substring(0, separator);
                if (!Guid.TryParse(epochPart, out var parsedEpoch))
                {
                    return String.Format("'{0}' is not a valid since position: the epoch part is not a GUID.", since);
                }
                epoch = parsedEpoch;
                seqPart = since.Substring(separator + 1);
            }
            else
            {
                seqPart = since;
            }

            if (!Int64.TryParse(seqPart, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedSeq))
            {
                return String.Format("'{0}' is not a valid since position: expected '<epoch>:<seq>' or a bare sequence number.", since);
            }

            seq = parsedSeq;
            return null;
        }

        /// <summary>Splits repeatable and/or comma-separated values into one trimmed union.</summary>
        private static IEnumerable<String> Flatten(String[] values)
        {
            if (values == null)
            {
                yield break;
            }

            foreach (var value in values)
            {
                if (String.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    yield return part;
                }
            }
        }
    }
}
