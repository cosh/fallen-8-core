// MIT License
//
// AgentsController.cs
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
using NoSQL.GraphDB.App.Agents;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.App.Namespaces;

namespace NoSQL.GraphDB.App.Controllers
{
    /// <summary>
    ///   The instance's door to the agent host (feature agent-host): an authenticated proxy for the
    ///   control-plane routes of the <c>fallen-8-agents</c> sidecar, which runs agents that read this
    ///   graph through the MCP server. The host's container port is not published, because an agent
    ///   decides for itself which tools to call, so this proxy is the only way in and the host needs
    ///   no second auth story.
    ///
    ///   <para><b>An agent asks THIS instance for its completions</b>, with
    ///   <c>purpose: agent</c> on <c>POST /chat</c>. So the provider, the credential and the model
    ///   are <c>Fallen8:Chat</c>'s and live in one place, and the agent host names none of them. Two
    ///   capabilities are therefore in play for an agent to run: this one to reach the host, and Chat
    ///   for the host to reach a model. When Chat is off, spawning still succeeds and the agent fails
    ///   on its first model call with the gateway's own message; <c>GET /agents/status</c> reports
    ///   that reachability, which is why it is the first thing to read.</para>
    ///
    ///   <para>Fallen-8-level (instance-wide, no <c>/ns/{ns}</c> twin): one host serves the whole
    ///   instance and an agent reaches a namespace through an MCP tool that names it, so twinning
    ///   would offer a second way to say the same thing and let the two disagree.</para>
    ///
    ///   <para>Gated by the Agents capability, and the two statuses it produces are BOTH worth
    ///   knowing: the shared policy pairs the capability with <c>RequireAuthenticatedUser</c>, so an
    ///   anonymous caller is challenged before the capability is read. "Agents are off" is therefore
    ///   403 on an instance with an API key and 401 on one without, which is what a bare
    ///   <c>dotnet run</c> is. A client that reads only 403 as "this feature is absent" shows a
    ///   broken screen on exactly that instance. Both come from the policy, so nothing here tests
    ///   the flag.</para>
    /// </summary>
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiVersion("0.1")]
    [Fallen8Level]
    [Authorize(Policy = Fallen8AgentsOptions.AgentsPolicy)]
    public class AgentsController : ControllerBase
    {
        private readonly IAgentsClient _client;

        public AgentsController(IAgentsClient client)
        {
            _client = client;
        }

        /// <summary>
        /// Spawns one agent on a task (feature agent-host)
        /// </summary>
        /// <param name="request">The task, and optionally a role, a name, a token budget and an appendix to the role prompt</param>
        /// <param name="cancellationToken">Aborts the proxied call when the request is cancelled</param>
        /// <remarks>Answers 202 with the agent's id and initial state; the run happens on the host's own
        /// time and is read back from the listing. Nothing on this control plane blocks on inference,
        /// because one model call against a remote provider was measured at up to 41 seconds.
        /// <para>There is deliberately no <c>model</c> field. The agent asks this instance's chat gateway
        /// with <c>purpose: agent</c>, so the model is <c>Fallen8:Chat:&lt;Backend&gt;:Models:Agent</c> and
        /// a caller cannot choose it. <c>role</c> selects a prompt and a tool allowlist;
        /// <c>systemPromptAppendix</c> is APPENDED to that prompt and can never replace it, because the
        /// role prompt is what makes an agent call a tool instead of fabricating a result.</para>
        /// <para>The request and response bodies are the HOST's own contract and are deliberately untyped
        /// here, so there is exactly one definition of them: see https://docs.fallen-8.com/agents/.</para></remarks>
        /// <response code="202">Accepted; the body carries the agent's id and state</response>
        /// <response code="400">The host refused the request, its own message saying why (no task, an unknown role)</response>
        /// <response code="401">No valid credential was supplied, or agents are off on an instance with no API key</response>
        /// <response code="403">Agents are disabled (Fallen8:Agents:Enabled) on a credentialed instance</response>
        /// <response code="429">The host is already running as many agents as it may (Agents:Limits:MaxConcurrentAgents)</response>
        /// <response code="503">No host is configured, or it did not answer</response>
        [HttpPost("/agents")]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public Task<IActionResult> Spawn([FromBody] JsonElement request, CancellationToken cancellationToken)
        {
            return Forward(HttpMethod.Post, "agent", request.GetRawText(), cancellationToken);
        }

        /// <summary>
        /// Lists every agent this host knows about (feature agent-host)
        /// </summary>
        /// <param name="cancellationToken">Aborts the proxied call when the request is cancelled</param>
        /// <remarks>Newest first, live and retained together: id, name, role, state, task, parentId, the
        /// token counters, steps, toolCalls, durationMs, budget and the timestamps.
        /// <para>An empty list never means "nothing ever ran": the host keeps nothing durable, a restart
        /// ends and forgets everything, and a finished agent is retained for a bounded time. Every entry
        /// carries the <c>hostInstanceId</c> that produced it so two listings can be told apart.</para></remarks>
        /// <response code="200">The agents, as the host describes them</response>
        /// <response code="401">No valid credential was supplied, or agents are off on an instance with no API key</response>
        /// <response code="403">Agents are disabled (Fallen8:Agents:Enabled) on a credentialed instance</response>
        /// <response code="503">No host is configured, or it did not answer</response>
        [HttpGet("/agents")]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public Task<IActionResult> All(CancellationToken cancellationToken)
        {
            return Forward(HttpMethod.Get, "agent", null, cancellationToken);
        }

        /// <summary>
        /// Reports the agent host's posture (feature agent-host)
        /// </summary>
        /// <param name="cancellationToken">Aborts the proxied call when the request is cancelled</param>
        /// <remarks>The first thing to read when an agent fails: whether the host can reach this instance's
        /// chat gateway, what backend and model last served a step, whether the MCP server answered and
        /// how many tools it advertises, what each role actually ended up with, the caps a run is held to,
        /// and how many agents are active and retained.
        /// <para>The model is reported as LAST SEEN rather than as configuration, and that is not a
        /// limitation: the host holds no model configuration at all, so before a step has run there is no
        /// name to report. Which model served a step is the instance's answer, per step.</para></remarks>
        /// <response code="200">The host's posture, as it reports it</response>
        /// <response code="401">No valid credential was supplied, or agents are off on an instance with no API key</response>
        /// <response code="403">Agents are disabled (Fallen8:Agents:Enabled) on a credentialed instance</response>
        /// <response code="503">No host is configured, or it did not answer</response>
        [HttpGet("/agents/status")]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public Task<IActionResult> Status(CancellationToken cancellationToken)
        {
            return Forward(HttpMethod.Get, "agent/status", null, cancellationToken);
        }

        /// <summary>
        /// Streams what every agent is doing, as Server-Sent Events (feature agent-host)
        /// </summary>
        /// <param name="agents">Only these agent ids, comma-separated or repeated. An id also matches the workers it spawned, so subscribing to an orchestrator shows its swarm. Omitted means every agent</param>
        /// <param name="kinds">Only these event kinds: agentSpawned, agentStateChanged, agentMessage, toolCalled, agentCompleted, agentFailed. An unknown kind is a 400 naming the set, never a silently empty stream</param>
        /// <param name="cancellationToken">Ends the stream when the subscriber disconnects, which is how a feed normally ends</param>
        /// <remarks>The same frame conventions as this instance's change feed, so a client that reads one reads
        /// the other: <c>id:</c>, <c>event:</c> and <c>data:</c> per event, with keep-alive comments while idle.
        /// Every event carries the four counters, so a subscriber renders live cost without polling.
        /// <para>There is NO catch-up. A subscriber that connects late, or that is dropped for falling behind
        /// the host's queue bound, has missed what it missed, and <c>GET /agents/{id}/trace</c> is how it finds
        /// out what. <c>Last-Event-ID</c> is deliberately not honoured, because pretending to resume from a
        /// position the host cannot replay would be worse than plainly not resuming.</para>
        /// <para>A tool call's arguments and result travel as capped SUMMARIES, never full payloads. The trace
        /// holds more, and past its own caps the graph is where the data is.</para></remarks>
        /// <response code="200">The SSE stream (text/event-stream); it stays open until the client disconnects</response>
        /// <response code="400">An unknown filter value, the host's own message naming the accepted set</response>
        /// <response code="401">No valid credential was supplied, or agents are off on an instance with no API key</response>
        /// <response code="403">Agents are disabled (Fallen8:Agents:Enabled) on a credentialed instance</response>
        /// <response code="503">No host is configured, it did not answer, or it has as many subscribers as it may</response>
        [HttpGet("/agents/feed")]
        [Produces("text/event-stream")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> Feed([FromQuery] String[] agents, [FromQuery] String[] kinds,
            CancellationToken cancellationToken)
        {
            var query = Query(("agents", agents), ("kinds", kinds));

            try
            {
                await _client.StreamAsync("agent/feed" + query, async (status, contentType) =>
                {
                    Response.StatusCode = status;
                    Response.ContentType = String.IsNullOrEmpty(contentType)
                        ? "text/event-stream"
                        : contentType;

                    // Buffering off on THIS hop too. Disabling it on the host alone is not enough:
                    // a buffer here would re-batch events the host flushed one at a time, and the
                    // feed would arrive in bursts for a reason no one could see.
                    HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()
                        ?.DisableBuffering();
                    await Response.Body.FlushAsync(cancellationToken);
                }, Response.Body, cancellationToken);
            }
            catch (AgentsUnavailableException ex)
            {
                // Only reachable BEFORE the host answered. Once the stream is open the status is
                // already sent, so a later failure can only end the stream, which is what a
                // subscriber's reconnect is for.
                if (!Response.HasStarted)
                {
                    return ProblemResults.Create(StatusCodes.Status503ServiceUnavailable,
                        "Agent host unavailable", ex.Message);
                }
            }
            catch (OperationCanceledException)
            {
                // The subscriber disconnected. That is how a feed normally ends.
            }

            return new EmptyResult();
        }

        /// <summary>
        /// Reads one agent's whole retained trace (feature agent-host)
        /// </summary>
        /// <param name="id">The agent id a spawn returned</param>
        /// <param name="cancellationToken">Aborts the proxied call when the request is cancelled</param>
        /// <remarks>Every step the host still holds: each model call with the backend and model that served it
        /// and the duration the HOST measured, each tool call with capped captures of what went in and came
        /// back, the state changes, and the citation check. This is the catch-up mechanism for the event feed.
        /// <para>The trace is BOUNDED (<c>Agents:Trace:MaxSteps</c>) and drops the oldest steps, so
        /// <c>dropped</c> being non-zero means this is not the whole run. It is never silent about that: a
        /// <c>dropped</c> marker step sits where the missing steps were, and the sequence numbers do not
        /// restart, so a gap is itself evidence.</para>
        /// <para>Provenance is per STEP rather than per host, which is the point: a deployment that switches
        /// backend mid-day shows it here.</para></remarks>
        /// <response code="200">The trace, as the host holds it</response>
        /// <response code="401">No valid credential was supplied, or agents are off on an instance with no API key</response>
        /// <response code="403">Agents are disabled (Fallen8:Agents:Enabled) on a credentialed instance</response>
        /// <response code="404">No such agent on this host</response>
        /// <response code="503">No host is configured, or it did not answer</response>
        [HttpGet("/agents/{id}/trace")]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public Task<IActionResult> Trace(String id, CancellationToken cancellationToken)
        {
            return Forward(HttpMethod.Get,
                "agent/" + Uri.EscapeDataString(id ?? String.Empty) + "/trace", null, cancellationToken);
        }

        /// <summary>
        /// Reads one agent (feature agent-host)
        /// </summary>
        /// <param name="id">The agent id a spawn returned</param>
        /// <param name="cancellationToken">Aborts the proxied call when the request is cancelled</param>
        /// <remarks>The same fields the listing carries, plus the ids of the workers this agent spawned.
        /// <para>A 404 does not distinguish "never existed" from "finished and evicted", and the host's own
        /// message says so: retention is bounded, so an agent worth reviewing is worth reading soon.</para></remarks>
        /// <response code="200">The agent, as the host describes it</response>
        /// <response code="401">No valid credential was supplied, or agents are off on an instance with no API key</response>
        /// <response code="403">Agents are disabled (Fallen8:Agents:Enabled) on a credentialed instance</response>
        /// <response code="404">No such agent on this host</response>
        /// <response code="503">No host is configured, or it did not answer</response>
        [HttpGet("/agents/{id}")]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public Task<IActionResult> One(String id, CancellationToken cancellationToken)
        {
            return Forward(HttpMethod.Get, "agent/" + Uri.EscapeDataString(id ?? String.Empty),
                null, cancellationToken);
        }

        /// <summary>
        /// Cancels one agent and its live workers (feature agent-host)
        /// </summary>
        /// <param name="id">The agent id a spawn returned</param>
        /// <param name="cancellationToken">Aborts the proxied call when the request is cancelled</param>
        /// <remarks>Answers 202, and the honesty is deliberate: cancellation is cooperative. The token is
        /// observed between steps and passed into the model call and tool call in flight, so a tool call
        /// already sent to the graph is not undone and nothing is rolled back. Cancelling an orchestrator
        /// cascades to its live workers, and the body says how many agents were signalled.
        /// <para>Cancelling an agent that has already finished is not an error: it asked for a state the
        /// agent already has.</para></remarks>
        /// <response code="202">Accepted; the body carries the agent's final state and how many were signalled</response>
        /// <response code="401">No valid credential was supplied, or agents are off on an instance with no API key</response>
        /// <response code="403">Agents are disabled (Fallen8:Agents:Enabled) on a credentialed instance</response>
        /// <response code="404">No such agent on this host</response>
        /// <response code="503">No host is configured, or it did not answer</response>
        [HttpDelete("/agents/{id}")]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public Task<IActionResult> Cancel(String id, CancellationToken cancellationToken)
        {
            return Forward(HttpMethod.Delete, "agent/" + Uri.EscapeDataString(id ?? String.Empty),
                null, cancellationToken);
        }

        /// <summary>
        ///   Forwards one call and hands the host's answer back untouched: its status, its body and
        ///   its content type. Non-2xx is deliberately NOT mapped to 502 - a 400 naming an unknown
        ///   role, a 404 explaining that retention is bounded and a 429 naming the concurrency cap
        ///   are answers a caller has to read. The content type is set from what came back, because
        ///   the global <c>ProblemDetailsContentTypeFilter</c> only rewrites it for a real
        ///   <c>ProblemDetails</c> instance and this body is an opaque string.
        /// </summary>
        private async Task<IActionResult> Forward(HttpMethod method, String path, String jsonBody,
            CancellationToken cancellationToken)
        {
            NoSQL.GraphDB.App.Integrations.SidecarResponse response;
            try
            {
                response = await _client.ForwardAsync(method, path, jsonBody, cancellationToken);
            }
            catch (AgentsUnavailableException ex)
            {
                // The ONLY status this proxy invents. Everything else is the host's own.
                return ProblemResults.Create(StatusCodes.Status503ServiceUnavailable,
                    "Agent host unavailable", ex.Message);
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
        ///   The caller's repeated query values, re-encoded for the host. Rebuilt rather than
        ///   forwarded verbatim: this action's own bound parameters are the only values that reach
        ///   the host, so a caller cannot append anything the proxy did not declare.
        /// </summary>
        private static String Query(params (String Name, String[] Values)[] parameters)
        {
            var parts = new System.Collections.Generic.List<String>();
            foreach (var (name, values) in parameters)
            {
                if (values == null)
                {
                    continue;
                }

                foreach (var value in values)
                {
                    if (!String.IsNullOrEmpty(value))
                    {
                        parts.Add(name + "=" + Uri.EscapeDataString(value));
                    }
                }
            }

            return parts.Count == 0 ? String.Empty : "?" + String.Join("&", parts);
        }
    }
}
