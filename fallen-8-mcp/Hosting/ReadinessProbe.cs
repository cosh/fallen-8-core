// MIT License
//
// ReadinessProbe.cs
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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using NoSQL.GraphDB.Mcp.Bridge;
using NoSQL.GraphDB.Mcp.Configuration;
using NoSQL.GraphDB.Rest.Configuration;

namespace NoSQL.GraphDB.Mcp.Hosting
{
    /// <summary>
    ///   <c>GET /readyz</c> (feature mcp-plugin-gaps, spec section 9). <c>/healthz</c> says the
    ///   process is up; this says the server can do its one job, which is to bridge to a Fallen-8.
    ///   One <c>GET /status</c> on the default namespace, under the readiness deadline: that route
    ///   answers keyless and for a not-loaded namespace, so it measures reachability and nothing
    ///   else, and its body says whether the bridge's credential was accepted. Unready when the call
    ///   fails, times out, or the target requires a key and rejected ours, because every bridged
    ///   call will then fail and "ready" would be the same lie in a second form.
    /// </summary>
    public static class ReadinessProbe
    {
        public static async Task<IResult> ProbeAsync(
            Fallen8RestClient bridge,
            Fallen8TargetOptions target,
            McpReadinessOptions readiness,
            CancellationToken requestAborted)
        {
            var deadline = OptionBounds.Seconds(readiness.TimeoutSeconds);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
            budget.CancelAfter(deadline);

            try
            {
                var status = await bridge.GetStatusAsync(null, budget.Token).ConfigureAwait(false);
                if (status is null)
                {
                    return Unready(target, "the target answered /status with no body");
                }
                if (status.ApiKeyRequired && !status.Authenticated)
                {
                    return Unready(target, "the target requires an API key and rejected the configured one (Fallen8Target:ApiKey)");
                }
                return Results.Json(new { status = "ready", target = target.BaseUrl, authenticated = status.Authenticated });
            }
            catch (OperationCanceledException) when (!requestAborted.IsCancellationRequested)
            {
                // The seam hands a timeout back as the caller's cancellation when the caller's token
                // fired, which is what the linked budget did here; the probe's own token is the
                // only one that can have fired without the request being aborted.
                return Unready(target, $"no answer from the target within {deadline.TotalSeconds:0}s (Mcp:Readiness:TimeoutSeconds)");
            }
            catch (BridgeError error)
            {
                return Unready(target, $"{error.Status} {error.Title}: {error.Detail}");
            }
        }

        private static IResult Unready(Fallen8TargetOptions target, String reason)
        {
            return Results.Json(new { status = "unready", target = target.BaseUrl, reason }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
