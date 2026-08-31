// MIT License
//
// IntegrationsController.cs
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
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.App.Integrations;
using NoSQL.GraphDB.App.Namespaces;

namespace NoSQL.GraphDB.App.Controllers
{
    /// <summary>
    ///   The instance's door to the integration runtime (feature integrations): an authenticated
    ///   proxy for the seven routes of the <c>fallen-8-integrations</c> sidecar, which reads a system
    ///   on the operator's own network and writes what it saw into one namespace. The runtime's
    ///   container port is not published, because jobs hand that container third-party credentials, so
    ///   this proxy is the only way in and needs no second auth story on the runtime side.
    ///
    ///   <para>Fallen-8-level (instance-wide, no <c>/ns/{ns}</c> twin): one runtime serves the whole
    ///   instance and a job names the namespace it writes into, so twinning would offer a second way
    ///   to say the same thing and let the two disagree. Gated by the Integrations capability (403
    ///   when <c>Fallen8:Integrations:Enabled</c> is off, which is what <c>F8_INTEGRATIONS=false</c>
    ///   produces); that 403 comes from the policy, so nothing here tests the flag.</para>
    /// </summary>
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiVersion("0.1")]
    [Fallen8Level]
    [Authorize(Policy = Fallen8IntegrationsOptions.IntegrationsPolicy)]
    public class IntegrationsController : ControllerBase
    {
        /// <summary>
        ///   The transport bound on a job body, which carries a provider's files as base64 (features
        ///   integration-file-upload and integration-run-lifecycle). 768 MiB, and both of its relations
        ///   are load-bearing:
        ///
        ///   <para>ABOVE any legal job. What sets the size is no longer one file but the job TOTAL: a
        ///   file setting a provider declares <c>multiple</c> takes a whole vehicle's system extracts at
        ///   once, the runtime's default <c>Integrations:MaxJobFileBytes</c> is 512 MiB of decoded bytes,
        ///   and base64 costs a third, so a maximal legal job arrives at about 683 MiB and this bound
        ///   never fires for one the runtime would accept. Real files set both numbers: the first extract
        ///   anybody pointed at this feature was a large size, an earlier 48 MiB bound refused it with a bare
        ///   transport 413, and a vehicle arrives as several such extracts that reference each other.</para>
        ///
        ///   <para>BELOW the runtime's own transport bound (832 MiB, fixed). That ordering is the point:
        ///   an absurd body has to be refused HERE, with a 413 whose meaning is plain, because a body
        ///   this proxy accepts and the runtime refuses fails while being FORWARDED - which surfaces as
        ///   503 "the runtime did not answer", sending whoever sent a huge file to look at a sidecar that
        ///   is perfectly healthy.</para>
        ///
        ///   <para>A private const rather than a configuration key, exactly as
        ///   <c>DocumentController</c>'s upload bound is: the real ceiling lives in the OTHER
        ///   deployable's configuration, so a key here would be a second number to keep in step with it
        ///   and a caller could not tell which one refused them. The consequence, stated rather than
        ///   hidden: raising <c>Integrations:MaxJobFileBytes</c> past about 576 MiB has no effect through
        ///   this proxy, and the proxy is the only way in because the runtime publishes no port.</para>
        /// </summary>
        private const Int32 JobTransportLimit = 805_306_368;

        private readonly IIntegrationsClient _client;

        public IntegrationsController(IIntegrationsClient client)
        {
            _client = client;
        }

        /// <summary>The single fault mapping, and the ONLY status this proxy invents: an unconfigured
        /// or unreachable runtime becomes 503. Everything the runtime itself answered is passed
        /// through by <see cref="Forward" /> instead, because the runtime's own message is more use to
        /// whoever is configuring an integration than a proxy-shaped error.</summary>
        private static ObjectResult RuntimeProblem(IntegrationsUnavailableException ex)
        {
            return ProblemResults.Create(StatusCodes.Status503ServiceUnavailable,
                "Integration runtime unavailable", ex.Message);
        }

        /// <summary>
        ///   Forwards one call and hands the runtime's answer back untouched: its status, its body and
        ///   its content type. Non-2xx is deliberately NOT mapped to 502 - a 400 naming a missing
        ///   setting and a 409 conflict are answers a caller has to read. The content type is set from
        ///   what came back, because the global <c>ProblemDetailsContentTypeFilter</c> only rewrites it
        ///   for a real <c>ProblemDetails</c> instance and this body is an opaque string.
        /// </summary>
        private async Task<IActionResult> Forward(HttpMethod method, String path, String jsonBody,
            CancellationToken cancellationToken)
        {
            SidecarResponse response;
            try
            {
                response = await _client.ForwardAsync(method, path, jsonBody, cancellationToken);
            }
            catch (IntegrationsUnavailableException ex)
            {
                return RuntimeProblem(ex);
            }

            return new ContentResult
            {
                StatusCode = response.Status,
                Content = response.Body,
                ContentType = String.IsNullOrEmpty(response.ContentType)
                    ? "application/json"
                    : response.ContentType
            };
        }

        /// <summary>
        /// Lists the integrations this instance's runtime can run (feature integrations)
        /// </summary>
        /// <param name="cancellationToken">Aborts the proxied call when the request is cancelled</param>
        /// <remarks>Each entry is a provider descriptor: its id, what it reads, its settings as data
        /// (key, label, kind, required, help), the entity kinds, claim types and relation types it
        /// produces, and whether it can observe complete state. A settings form renders from that
        /// data, so adding an integration needs no client change.
        /// <para>The response body is the RUNTIME's own contract and is deliberately untyped here, so
        /// there is exactly one definition of it: see
        /// https://docs.fallen-8.com/integrations/. A provider is C# compiled into the
        /// runtime, so this list changes only when that deployable does.</para></remarks>
        /// <response code="200">The provider catalog, as the runtime describes it</response>
        /// <response code="400">The runtime refused the request, its own message saying why</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">Integrations are disabled (Fallen8:Integrations:Enabled)</response>
        /// <response code="409">The runtime reported a conflict</response>
        /// <response code="503">No runtime is configured, or it did not answer</response>
        [HttpGet("/integrations/providers")]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public Task<IActionResult> Providers(CancellationToken cancellationToken)
        {
            return Forward(HttpMethod.Get, "integration/providers", null, cancellationToken);
        }

        /// <summary>
        /// Reports what a job may carry, as the ceiling that actually binds (feature integration-file-transport)
        /// </summary>
        /// <param name="cancellationToken">Aborts the proxied call when the request is cancelled</param>
        /// <remarks>Three numbers: maxFileBytes per file, maxJobFileBytes for their total, and maxJobFiles
        /// for how many. Zero or less means that ceiling is switched off. A client reads them so it can
        /// refuse a job BEFORE uploading it, which is the only refusal that costs nothing.
        /// <para>This is the ONE integrations route whose answer this proxy composes rather than forwarding
        /// verbatim, and the reason is that the binding ceiling genuinely is the smaller of two: the runtime
        /// owns its configuration, but every request arrives through this route's own transport bound. A
        /// runtime number above what this proxy would accept is lowered to what it would, so a caller
        /// learns one number for one question instead of two it has to combine.</para></remarks>
        /// <response code="200">The ceilings that bind, already reconciled with this proxy's own</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">Integrations are disabled (Fallen8:Integrations:Enabled)</response>
        /// <response code="503">No runtime is configured, or it did not answer</response>
        [HttpGet("/integrations/limits")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(IntegrationLimitsREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> Limits(CancellationToken cancellationToken)
        {
            SidecarResponse response;
            try
            {
                response = await _client.ForwardAsync(HttpMethod.Get, "integration/limits", null,
                    cancellationToken);
            }
            catch (IntegrationsUnavailableException ex)
            {
                return RuntimeProblem(ex);
            }

            if (response.Status != StatusCodes.Status200OK)
            {
                // Anything the runtime did not answer 200 to is its own answer, handed back untouched, as
                // every other route does.
                return new ContentResult
                {
                    StatusCode = response.Status,
                    Content = response.Body,
                    ContentType = String.IsNullOrEmpty(response.ContentType)
                        ? "application/json"
                        : response.ContentType
                };
            }

            IntegrationLimitsREST runtimeLimits = null;
            if (!String.IsNullOrWhiteSpace(response.Body))
            {
                try
                {
                    runtimeLimits = JsonSerializer.Deserialize<IntegrationLimitsREST>(response.Body,
                        LimitsJson);
                }
                catch (JsonException)
                {
                    // Falls through to the refusal below.
                }
            }

            if (runtimeLimits == null)
            {
                // A runtime too old to serve this route, or one answering something else: say the proxy
                // could not read it rather than invent ceilings a caller would then trust. An empty body
                // and a literal "null" land here too, deliberately - defaulting them to an all-zero
                // record would report this proxy's transport bound as if the runtime had agreed to it.
                return ProblemResults.Create(StatusCodes.Status503ServiceUnavailable,
                    "Integration runtime unavailable",
                    "The integrations runtime did not report its limits in a shape this instance can read, " +
                    "so no ceiling can be stated. It may predate the limits route.");
            }

            return Ok(new IntegrationLimitsREST
            {
                MaxFileBytes = Binding(runtimeLimits.MaxFileBytes),
                MaxJobFileBytes = Binding(runtimeLimits.MaxJobFileBytes),
                MaxJobFiles = runtimeLimits.MaxJobFiles,
            });
        }

        /// <summary>
        ///   The allowance for the multipart framing and the job envelope around the files themselves, so a
        ///   caller told "you may send N bytes of files" is not refused by the transport for the wrapper.
        ///   1 MiB, which is orders of magnitude more than the part headers of a legal job need.
        /// </summary>
        private const Int64 JobEnvelopeAllowance = 1_048_576;

        /// <summary>
        ///   The ceiling that binds for one of the byte numbers: the runtime's, unless this proxy's own
        ///   transport bound is tighter, in which case a caller has to hear that one instead. A runtime
        ///   ceiling that is switched off (zero or less) is NOT passed through as "unlimited": every
        ///   request still arrives through here, so the transport bound is the real answer.
        /// </summary>
        private static Int64 Binding(Int64 runtimeCeiling)
        {
            var transport = JobTransportLimit - JobEnvelopeAllowance;
            return runtimeCeiling <= 0 || runtimeCeiling > transport ? transport : runtimeCeiling;
        }

        private static readonly JsonSerializerOptions LimitsJson =
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        /// <summary>
        /// Reports every integration run this runtime knows about (feature integration-run-visibility)
        /// </summary>
        /// <param name="cancellationToken">Aborts the proxied call when the request is cancelled</param>
        /// <remarks>What is happening NOW, and what happened LAST, per integration identity - and nothing
        /// else: this is deliberately not a run log. Exactly how narrow that is, and why a report has to
        /// be readable after the run at all, is stated once on the runtime's <c>RunTracker</c>.</remarks>
        /// <response code="200">Every tracked run, newest first</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">Integrations are disabled (Fallen8:Integrations:Enabled)</response>
        /// <response code="503">No runtime is configured, or it did not answer</response>
        [HttpGet("/integrations/run")]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public Task<IActionResult> Runs(CancellationToken cancellationToken)
        {
            return Forward(HttpMethod.Get, "integration/run", null, cancellationToken);
        }

        /// <summary>
        /// Reports one integration identity's current or most recent run (feature integration-run-visibility)
        /// </summary>
        /// <param name="instanceId">The integration identity the run asserts as</param>
        /// <param name="cancellationToken">Aborts the proxied call when the request is cancelled</param>
        /// <remarks>While a run is in flight this carries the phase it is in and how far through that phase
        /// it is; once it ends it carries the report itself, or the error if it produced none. The phases are
        /// observe, validate, resolve, write-elements, write-edges, embed-summaries and reconcile - and two of
        /// them matter most, because both can run for a long time while the graph shows no change at all: a
        /// large extract parses for minutes, and summary embedding is model inference for hours.
        /// <para>A 404 means this runtime has no slot for that identity: it has not run in this process, or a
        /// restart or enough other identities have displaced it.</para></remarks>
        /// <response code="200">The run, in flight or finished</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">Integrations are disabled (Fallen8:Integrations:Enabled)</response>
        /// <response code="404">No run is tracked for that identity</response>
        /// <response code="503">No runtime is configured, or it did not answer</response>
        [HttpGet("/integrations/run/{instanceId}")]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public Task<IActionResult> Run([FromRoute] String instanceId, CancellationToken cancellationToken)
        {
            return Forward(HttpMethod.Get, "integration/run/" + Uri.EscapeDataString(instanceId ?? String.Empty),
                null, cancellationToken);
        }

        /// <summary>
        /// Asks the integration run in flight under one identity to stop (feature integration-run-lifecycle)
        /// </summary>
        /// <param name="instanceId">The integration identity whose run should stop</param>
        /// <param name="cancellationToken">Aborts the proxied call when the request is cancelled</param>
        /// <remarks>A stop is a REQUEST, which is why this answers 202 rather than 200: the run honours it at
        /// its next safe point, and for summary embedding that is after the chunk already in the model. Watch
        /// GET /integrations/run/{instanceId} to see it take effect - cancelRequested turns true at once,
        /// cancelled when the run has actually stopped.
        /// <para>A cancelled run KEEPS what it had already written and deliberately does not reconcile, so it
        /// withdraws nothing and deletes nothing; the next completed run under that identity converges the
        /// graph. Why that is the safe half of the bargain is stated once on the runtime's own applier, and
        /// for readers at https://docs.fallen-8.com/integrations/.</para>
        /// <para>404 means nothing is in flight under that identity: a run that already ended is not
        /// cancellable, and its slot already says what it ended as. Cancelling twice is not an error.</para></remarks>
        /// <response code="202">The stop was delivered to a run in flight</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">Integrations are disabled (Fallen8:Integrations:Enabled)</response>
        /// <response code="404">No run is in flight under that identity</response>
        /// <response code="503">No runtime is configured, or it did not answer</response>
        [HttpPost("/integrations/run/{instanceId}/cancel")]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public Task<IActionResult> CancelRun([FromRoute] String instanceId, CancellationToken cancellationToken)
        {
            return Forward(HttpMethod.Post,
                "integration/run/" + Uri.EscapeDataString(instanceId ?? String.Empty) + "/cancel",
                null, cancellationToken);
        }

        /// <summary>
        /// Starts one integration job and returns a run id to watch it by (feature integrations)
        /// </summary>
        /// <param name="wait">Wait for the run and return its report instead of a run id. For a small
        /// source and a script only: this proxy holds a connection for a bounded time.</param>
        /// <param name="cancellationToken">Aborts the proxied call when the request is cancelled</param>
        /// <remarks>A job carries everything one run needs: which provider, the identity it asserts
        /// as, the namespace to write into, the provider's settings, its credentials as VALUES in
        /// credentialValues, and any file it reads as a name plus its bytes in files. The runtime
        /// stores none of them: it holds a credential and a file for the run that needs them and
        /// drops both when the run ends, mounts no directory, keeps no job history, and no route
        /// reads a job back. All of that travels through here in the request body, so serve this API
        /// over TLS.
        /// <para>The call is ACCEPTED, not awaited: it answers 202 with a run id, and the run is watched
        /// through GET /integrations/run/{instanceId}. Everything that can REJECT a job is still judged
        /// before the answer, so a 202 means the run really started and a 400 or 409 means it never did.
        /// Pass wait=true for the old synchronous shape, which returns the report itself - suitable for a
        /// small source and a script, and not for a large one, because this proxy holds a connection for a
        /// bounded time while a real import runs far longer.</para>
        /// A job that ran and
        /// failed still answers 200 with the failure on its report; one that could not be run at all
        /// is the runtime's 400 or its 409 (one job at a time per identity).
        /// <para>The request and response bodies are the RUNTIME's own contract and are deliberately
        /// untyped here, so there is exactly one definition of them:
        /// https://docs.fallen-8.com/integrations/. The caller owns the stability of the
        /// integration instance id, which nothing can validate: a run under an identity that
        /// integration has not always used withdraws and deletes what the real one claimed.</para>
        /// <para>Because the files travel in the body, this route carries a 768 MiB body bound rather than
        /// the 1 MiB every other endpoint has, and a body over it is refused here with a 413 read from the
        /// declared Content-Length, before any of it is uploaded. That header is therefore REQUIRED: a
        /// chunked body cannot be judged before it is read, so one is refused with 411. An oversized
        /// FILE inside a legal body, or a legal set of files whose total is too large, is the runtime's
        /// refusal instead, naming the size and the ceiling it broke. Why that number and what it means for
        /// the runtime's own Integrations:MaxFileBytes and Integrations:MaxJobFileBytes is stated once on
        /// this controller's <c>JobTransportLimit</c>.</para>
        /// <para>The upload has its own budget, Fallen8:Integrations:JobTimeoutSeconds (default 900),
        /// because the clock runs at the caller's send rate while the body is streamed through.</para>
        /// <para>A file setting the provider declares <c>multiple</c> takes an ARRAY of files rather than
        /// one, and the order is preserved because a provider composing several files may depend on it. A
        /// single object stays valid everywhere.</para></remarks>
        /// <response code="200">The report, for a run that ended before it had a phase or when wait=true</response>
        /// <response code="202">The run was accepted; watch it at /integrations/run/{instanceId}</response>
        /// <response code="400">The runtime refused the job as written, its own message saying why</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">Integrations are disabled (Fallen8:Integrations:Enabled)</response>
        /// <response code="409">A job is already running under this identity</response>
        /// <response code="411">The body was sent without a Content-Length, which this route requires</response>
        /// <response code="413">The body exceeds this route's transport bound (see the remarks)</response>
        /// <response code="503">No runtime is configured, or it did not answer</response>
        [HttpPost("/integrations/job")]
        [RequestSizeLimit(JobTransportLimit)]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status411LengthRequired)]
        [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> Job([FromQuery] Boolean? wait, CancellationToken cancellationToken)
        {
            // Refused on the HEADER, before a byte of body is read. This is what makes the 413 above a
            // real answer rather than a declaration: [RequestSizeLimit] alone fires while Kestrel is
            // READING the body, and because the read happens inside the forward the failure arrived as
            // 503 "the integrations runtime did not answer" - about a runtime that was serving
            // providers a second earlier. Measured, not theorised (see the feature's findings.md).
            //
            // A caller with no Content-Length is refused too, with 411. That is a deliberate contract
            // narrowing: a chunked body cannot be judged before it is read, and a chunked body over the
            // bound cannot be refused with a status that reliably reaches the caller at all. Browsers
            // and curl always declare a length, and a caller who declares one and then sends more is
            // caught by the backstop below.
            if (!Request.ContentLength.HasValue)
            {
                return ProblemResults.Create(StatusCodes.Status411LengthRequired,
                    "Length required",
                    String.Format(
                        "This route needs a Content-Length so an oversized job can be refused before it " +
                        "is uploaded, and its bound is {0} bytes. Send the body with a declared length " +
                        "rather than chunked.", JobTransportLimit));
            }

            if (Request.ContentLength.Value > JobTransportLimit)
            {
                return ProblemResults.Create(StatusCodes.Status413PayloadTooLarge,
                    "Job body too large",
                    String.Format(
                        "The job body is {0} bytes, over this route's {1}-byte transport bound. The bound " +
                        "belongs to this instance's proxy, not to the integrations runtime, which was not " +
                        "asked. A single file over the runtime's own per-file ceiling, or a legal set whose " +
                        "total is too large, is refused by the runtime instead and names both numbers.",
                        Request.ContentLength.Value, JobTransportLimit));
            }

            // The backstop for a caller who declares a small length and sends more: lowered to what they
            // declared, so the body is cut off at their own number instead of the route's. Same
            // mechanism BulkController uses for its import carve-out.
            var bodySize = HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (bodySize != null && !bodySize.IsReadOnly)
            {
                bodySize.MaxRequestBodySize = Request.ContentLength.Value;
            }

            // The body is STREAMED, not bound. There is deliberately no [FromBody] parameter: a job can
            // carry a 128 MiB file, and binding it would leave the whole thing resident here about four
            // times over (the parsed document, the UTF-16 string of a re-serialisation, and the UTF-8
            // bytes it is encoded back into) for a hop whose entire contract is not to look at the body.
            //
            // What that costs, stated plainly: a malformed-JSON body is no longer refused by this app's
            // input formatter, it is refused by the RUNTIME. That is the same direction the rest of this
            // controller already points - a 400 naming a missing setting is an answer a caller has to
            // read, and the runtime's own message is more use than a proxy-shaped one.
            SidecarResponse response;
            try
            {
                // The query has to be carried explicitly: this hop forwards a PATH, so a `wait` the caller
                // asked for would otherwise be silently dropped and they would get a 202 they cannot use.
                response = await _client.ForwardStreamAsync(HttpMethod.Post,
                    wait == true ? "integration/job?wait=true" : "integration/job",
                    Request.Body, Request.ContentType, Request.ContentLength, cancellationToken);
            }
            catch (IntegrationsRequestRejectedException ex)
            {
                // The caller's own body, not the runtime. Answered with the status Kestrel chose and
                // never through RuntimeProblem, which would blame a sidecar that never saw the request.
                return ProblemResults.Create(ex.Status, "Job body rejected", ex.Message);
            }
            catch (IntegrationsUnavailableException ex)
            {
                return RuntimeProblem(ex);
            }

            return new ContentResult
            {
                StatusCode = response.Status,
                Content = response.Body,
                ContentType = String.IsNullOrEmpty(response.ContentType)
                    ? "application/json"
                    : response.ContentType
            };
        }

        /// <summary>
        /// Returns the identifier vocabulary the runtime resolves claims against (feature integrations)
        /// </summary>
        /// <param name="cancellationToken">Aborts the proxied call when the request is cancelled</param>
        /// <remarks>One entry per identifier type: its strength (only a strong one may resolve), its
        /// uniqueness scope, how a value is canonicalised and what values are accepted. It is data a
        /// provider author reads before declaring a claim type, and it is embedded in the runtime
        /// rather than mounted, so a deployment cannot silently change whether a claim resolves.
        /// <para>The response body is the RUNTIME's own contract and is deliberately untyped here, so
        /// there is exactly one definition of it: see
        /// https://docs.fallen-8.com/integrations/.</para></remarks>
        /// <response code="200">The vocabulary, as the runtime describes it</response>
        /// <response code="400">The runtime refused the request, its own message saying why</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">Integrations are disabled (Fallen8:Integrations:Enabled)</response>
        /// <response code="409">The runtime reported a conflict</response>
        /// <response code="503">No runtime is configured, or it did not answer</response>
        [HttpGet("/integrations/vocabulary")]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public Task<IActionResult> Vocabulary(CancellationToken cancellationToken)
        {
            return Forward(HttpMethod.Get, "integration/vocabulary", null, cancellationToken);
        }

        /// <summary>
        /// Validates a snapshot document without running anything (feature integrations)
        /// </summary>
        /// <param name="definition">The snapshot document, forwarded to the runtime untouched</param>
        /// <param name="cancellationToken">Aborts the proxied call when the request is cancelled</param>
        /// <remarks>An authoring aid: it says which envelope errors would refuse the whole document
        /// and which entities would be skipped, with the diagnostic code for each, so somebody writing
        /// a provider gets the verdict on a document before wiring a source to it. Nothing is written
        /// and no source is read.
        /// <para>The request and response bodies are the RUNTIME's own contract and are deliberately
        /// untyped here, so there is exactly one definition of them:
        /// https://docs.fallen-8.com/integrations/.</para></remarks>
        /// <response code="200">The validation verdict and its diagnostics</response>
        /// <response code="400">The runtime refused the request, its own message saying why</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">Integrations are disabled (Fallen8:Integrations:Enabled)</response>
        /// <response code="409">The runtime reported a conflict</response>
        /// <response code="503">No runtime is configured, or it did not answer</response>
        [HttpPost("/integrations/snapshot/validate")]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public Task<IActionResult> ValidateSnapshot([FromBody] JsonElement definition,
            CancellationToken cancellationToken)
        {
            return Forward(HttpMethod.Post, "integration/snapshot/validate", definition.GetRawText(),
                cancellationToken);
        }
    }
}
