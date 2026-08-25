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
using Microsoft.AspNetCore.Mvc;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.App.Integrations;
using NoSQL.GraphDB.App.Namespaces;

namespace NoSQL.GraphDB.App.Controllers
{
    /// <summary>
    ///   The instance's door to the integration runtime (feature integrations): an authenticated
    ///   proxy for the four routes of the <c>fallen-8-integrations</c> sidecar, which reads a system
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
        ///   The transport bound on a job body, which carries a provider's file as base64 (feature
        ///   integration-file-upload). 192 MiB, and both of its relations are load-bearing:
        ///
        ///   <para>ABOVE any legal job. The runtime's default <c>Integrations:MaxFileBytes</c> is 128 MiB
        ///   of decoded bytes, and base64 costs a third, so a maximal legal job arrives at about 171 MiB
        ///   and this bound never fires for one the runtime would accept. A real AUTOSAR system extract
        ///   is what set that size: the first one anybody pointed at this feature was a large size, and an
        ///   earlier 48 MiB bound refused it with a bare transport 413.</para>
        ///
        ///   <para>BELOW the runtime's own transport bound (256 MiB, fixed). That ordering is the point:
        ///   an absurd body has to be refused HERE, with a 413 whose meaning is plain, because a body
        ///   this proxy accepts and the runtime refuses fails while being FORWARDED - which surfaces as
        ///   503 "the runtime did not answer", sending whoever sent a huge file to look at a sidecar that
        ///   is perfectly healthy.</para>
        ///
        ///   <para>A private const rather than a configuration key, exactly as
        ///   <c>DocumentController</c>'s upload bound is: the real ceiling lives in the OTHER
        ///   deployable's configuration, so a key here would be a second number to keep in step with it
        ///   and a caller could not tell which one refused them. The consequence, stated rather than
        ///   hidden: raising <c>Integrations:MaxFileBytes</c> past about 144 MiB has no effect through
        ///   this proxy, and the proxy is the only way in because the runtime publishes no port.</para>
        /// </summary>
        private const Int32 JobTransportLimit = 201_326_592;

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
        /// Reports every integration run this runtime knows about (feature integration-run-visibility)
        /// </summary>
        /// <param name="cancellationToken">Aborts the proxied call when the request is cancelled</param>
        /// <remarks>What is happening NOW, and what happened LAST, per integration identity - and nothing
        /// else. This is deliberately not a run log: the runtime keeps one slot per identity, superseded by
        /// that identity's next run, in memory, dropped on restart, and capped so a caller inventing an
        /// identity per run cannot grow it without bound.
        /// <para>It exists because a report used to be unreachable. The job route's answer was the only copy
        /// the runtime made, and any real source outlives the connection that would have carried it.</para></remarks>
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
        /// <para>Because a file travels in the body, this is the one route besides document upload
        /// whose body bound is larger than the 1 MiB every other endpoint carries. That bound (192 MiB)
        /// sits above any legal job and below the runtime's own, so an oversized FILE is refused by the
        /// runtime with a message naming both its size and the ceiling, while an absurd BODY is refused
        /// here with a 413 - and neither is ever reported as a runtime that did not answer. It is fixed
        /// rather than configurable, so raising the runtime's Integrations:MaxFileBytes past about 144 MiB
        /// has no effect through this proxy, which is the only way in (base64 costs a third, so a maximal
        /// 128 MiB file arrives at about 171 MiB).</para></remarks>
        /// <response code="200">The report, for a run that ended before it had a phase or when wait=true</response>
        /// <response code="202">The run was accepted; watch it at /integrations/run/{instanceId}</response>
        /// <response code="400">The runtime refused the job as written, its own message saying why</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">Integrations are disabled (Fallen8:Integrations:Enabled)</response>
        /// <response code="409">A job is already running under this identity</response>
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
        [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> Job([FromQuery] Boolean? wait, CancellationToken cancellationToken)
        {
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
                    Request.Body, Request.ContentType, cancellationToken);
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
