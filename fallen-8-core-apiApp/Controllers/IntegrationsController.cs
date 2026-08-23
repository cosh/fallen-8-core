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
        ///   integration-file-upload). 48 MiB, and every digit of that is load-bearing:
        ///
        ///   <para>ABOVE any legal job. The runtime's default <c>Integrations:MaxFileBytes</c> is 32 MiB
        ///   of decoded bytes, which is 42.7 MiB once base64 costs its third, so a maximal legal job
        ///   arrives with room to spare and this bound never fires for one the runtime would accept.</para>
        ///
        ///   <para>BELOW the runtime's own transport bound (56 MiB at that default). That ordering is the
        ///   point: an absurd body has to be refused HERE, with a 413 whose meaning is plain, because a
        ///   body this proxy accepts and the runtime refuses fails while being forwarded - which surfaces
        ///   as 503 "the runtime did not answer", sending whoever sent a 60 MiB file to look at a sidecar
        ///   that is perfectly healthy.</para>
        ///
        ///   <para>A private const rather than a configuration key, exactly as
        ///   <c>DocumentController</c>'s upload bound is: the real ceiling lives in the OTHER
        ///   deployable's configuration, so a key here would be a second number to keep in step with it
        ///   and a caller could not tell which one refused them. The consequence, stated rather than
        ///   hidden: raising <c>Integrations:MaxFileBytes</c> past about 34 MiB has no effect through this
        ///   proxy, and the proxy is the only way in because the runtime publishes no port.</para>
        /// </summary>
        private const Int32 JobTransportLimit = 50_331_648;

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
        /// Runs one integration job and returns its report (feature integrations)
        /// </summary>
        /// <param name="definition">The job definition, forwarded to the runtime untouched</param>
        /// <param name="cancellationToken">Aborts the proxied call when the request is cancelled</param>
        /// <remarks>A job carries everything one run needs: which provider, the identity it asserts
        /// as, the namespace to write into, the provider's settings, its credentials as VALUES in
        /// credentialValues, and any file it reads as a name plus its bytes in files. The runtime
        /// stores none of them: it holds a credential and a file for the run that needs them and
        /// drops both when the run ends, mounts no directory, keeps no job history, and no route
        /// reads a job back. All of that travels through here in the request body, so serve this API
        /// over TLS. The call is synchronous: the source is read, what it
        /// said is written, the report comes back, and the runtime keeps nothing. A job that ran and
        /// failed still answers 200 with the failure on its report; one that could not be run at all
        /// is the runtime's 400 or its 409 (one job at a time per identity).
        /// <para>The request and response bodies are the RUNTIME's own contract and are deliberately
        /// untyped here, so there is exactly one definition of them:
        /// https://docs.fallen-8.com/integrations/. The caller owns the stability of the
        /// integration instance id, which nothing can validate: a run under an identity that
        /// integration has not always used withdraws and deletes what the real one claimed.</para>
        /// <para>Because a file travels in the body, this is the one route besides document upload
        /// whose body bound is larger than the 1 MiB every other endpoint carries. That bound (48 MiB)
        /// sits above any legal job and below the runtime's own, so an oversized FILE is refused by the
        /// runtime with a message naming both its size and the ceiling, while an absurd BODY is refused
        /// here with a 413 - and neither is ever reported as a runtime that did not answer. It is fixed
        /// rather than configurable, so raising the runtime's Integrations:MaxFileBytes past about 34 MiB
        /// has no effect through this proxy, which is the only way in.</para></remarks>
        /// <response code="200">The job report, including a run that failed</response>
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
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public Task<IActionResult> Job([FromBody] JsonElement definition, CancellationToken cancellationToken)
        {
            return Forward(HttpMethod.Post, "integration/job", definition.GetRawText(), cancellationToken);
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
