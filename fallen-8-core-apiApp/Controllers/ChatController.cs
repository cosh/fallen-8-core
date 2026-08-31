// MIT License
//
// ChatController.cs
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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Chat;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.App.Namespaces;

namespace NoSQL.GraphDB.App.Controllers
{
    /// <summary>
    ///   The instance's chat gateway (feature instance-config): proxies a chat completion to the
    ///   configured model backend (the Ollama sidecar by default), so Studio's NL-assist and other
    ///   clients reach a model THROUGH the instance instead of talking to it directly. Fallen-8-level
    ///   (instance-wide, no <c>/ns/{ns}</c> twin) and gated by the Chat capability (403 when
    ///   <c>Fallen8:Chat:Enabled</c> is off). The model is server-owned; the request carries no model
    ///   field. Message content is never written to spans or logs.
    ///   <para>
    ///     It also publishes what that backend CATALOGUES (feature chat-model-catalog), so the
    ///     Configuration surface can offer real model names for the server-owned model instead of a
    ///     blank field. That is a read: choosing a model is still a configuration write, never a
    ///     per-request field. The read itself lives in <see cref="ChatModelCatalog" />.
    ///   </para>
    /// </summary>
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiVersion("0.1")]
    [Fallen8Level]
    [Authorize(Policy = Fallen8ChatOptions.ChatPolicy)]
    public class ChatController : ControllerBase
    {
        private readonly Fallen8ChatProvider _provider;

        /// <summary>The bound options, read for the CATALOG only: it resolves its target through the
        /// backend factory the way the residency probe does, and the provider exposes no
        /// Ollama-protocol-independent target to resolve it from.</summary>
        private readonly Fallen8ChatOptions _options;

        public ChatController(Fallen8ChatProvider provider, IOptions<Fallen8ChatOptions> options)
        {
            _provider = provider;
            _options = options.Value;
        }

        /// <summary>Maps chat provider faults to problem+json: timeout → 504, backend down → 503,
        /// garbled/empty output → 502 (the single home for that mapping).</summary>
        private static ObjectResult ProviderProblem(Exception ex)
        {
            return ex switch
            {
                ChatProviderTimeoutException => ProblemResults.Create(StatusCodes.Status504GatewayTimeout,
                    "Chat backend timed out", ex.Message),
                ChatProviderUnavailableException => ProblemResults.Create(StatusCodes.Status503ServiceUnavailable,
                    "Chat provider unavailable", ex.Message),
                _ => ProblemResults.Create(StatusCodes.Status502BadGateway,
                    "Chat backend produced no output", ex.Message)
            };
        }

        /// <summary>
        /// Runs a chat completion against the instance's configured model backend
        /// </summary>
        /// <param name="definition">The conversation turns and optional generation knobs</param>
        /// <param name="cancellationToken">Aborts the backend call when the request is cancelled</param>
        /// <remarks>The instance proxies to whichever backend Fallen8:Chat:Backend selects (feature
        /// instance-config); the response names it. The model is server-owned (that backend's own
        /// Model setting); clients cannot choose it on this path. A custom endpoint is a
        /// browser-direct concern, never proxied here.</remarks>
        /// <response code="200">The completion, with the backend's generation stats</response>
        /// <response code="400">Empty message list or a message missing content</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">The chat provider is disabled (Fallen8:Chat:Enabled)</response>
        /// <response code="429">The sensitive-endpoint rate limit was exceeded</response>
        /// <response code="502">The backend returned no usable content</response>
        /// <response code="503">The backend is unavailable (failed to init, or the sidecar is down)</response>
        /// <response code="504">The backend did not respond within Fallen8:Chat:TimeoutSeconds</response>
        [HttpPost("/chat")]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [RequestSizeLimit(1_048_576)]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(ChatResultREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
        public async Task<IActionResult> Chat([FromBody] ChatSpecification definition,
            CancellationToken cancellationToken)
        {
            if (definition?.Messages == null || definition.Messages.Count == 0)
            {
                return ProblemResults.BadRequest("A non-empty messages list is required.");
            }

            var turns = new List<ChatTurn>(definition.Messages.Count);
            foreach (var message in definition.Messages)
            {
                if (String.IsNullOrEmpty(message?.Content))
                {
                    return ProblemResults.BadRequest("Every message requires non-empty content.");
                }

                turns.Add(new ChatTurn(message.Role, message.Content));
            }

            // Both knobs travel together or not at all: a request naming stop sequences must not lose
            // its temperature on the way, which is why this is one object rather than two ternaries.
            var stop = definition.Options?.Stop?.Where(s => !String.IsNullOrEmpty(s)).ToList();
            var options = definition.Options?.Temperature is Double temperature || stop is { Count: > 0 }
                ? new ChatBackendOptions
                {
                    Temperature = definition.Options?.Temperature,
                    Stop = stop is { Count: > 0 } ? stop : null
                }
                : null;

            ChatBackendResult result;
            try
            {
                result = await _provider.ChatAsync(turns, options, cancellationToken);
            }
            catch (Exception ex) when (ex is ChatProviderUnavailableException
                || ex is ChatProviderTimeoutException || ex is ChatProviderOutputException)
            {
                return ProviderProblem(ex);
            }

            return Ok(new ChatResultREST
            {
                Content = result.Content,
                Model = result.Model,
                // The selector belongs to the provider: a backend that could name itself here could
                // name one it is not.
                Backend = _provider.Backend,
                Stats = new ChatStatsREST
                {
                    PromptTokens = result.PromptTokens,
                    CompletionTokens = result.CompletionTokens,
                    DurationMs = result.DurationMs,
                    TokensPerSecond = result.TokensPerSecond
                }
            });
        }

        /// <summary>
        /// Lists the models the instance's configured chat backend catalogues
        /// </summary>
        /// <param name="cancellationToken">Aborts the outbound catalog read when the caller goes away</param>
        /// <remarks>For the RUNNING backend (feature chat-model-catalog), so a client can offer real
        /// names for the server-owned model instead of a blank field; choosing one is still a
        /// configuration write (Fallen8:Chat:&lt;Backend&gt;:Model), not a per-request field. A
        /// pending-restart backend switch is not previewed. The list is not necessarily the whole
        /// RESOLVABLE set - a backend can resolve a name it does not catalogue - so free-text entry
        /// stays valid. Capability, availability and class are null wherever the backend does not
        /// report them; filtering is the client's job.</remarks>
        /// <response code="200">The running backend and its catalogued models, sorted by name</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">The chat provider is disabled (Fallen8:Chat:Enabled)</response>
        /// <response code="429">The sensitive-endpoint rate limit was exceeded</response>
        /// <response code="503">The backend is misconfigured or did not answer its catalog in time</response>
        [HttpGet("/chat/models")]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [Produces("application/json")]
        [ProducesResponseType(typeof(ChatModelsREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> ChatModels(CancellationToken cancellationToken)
        {
            // The same reason the boot warning and the chat 503 give, from the same one home.
            if (ChatBackendFactory.Validate(_options) is { } problem)
            {
                return ProblemResults.Create(StatusCodes.Status503ServiceUnavailable,
                    "Chat provider unavailable", problem);
            }

            var models = await ChatModelCatalog.ReadAsync(_options, cancellationToken);
            if (models == null)
            {
                // It names the possibilities rather than the actual fault, because the actual fault
                // arrives as a transport message that can carry the endpoint value or the credential,
                // and neither may be echoed (the nahil-backend rule).
                return ProblemResults.Create(StatusCodes.Status503ServiceUnavailable,
                    "Chat provider unavailable", String.Format(
                        "The chat backend '{0}' returned no usable model catalog: it did not answer"
                        + " within {1}s, refused the read, or answered something this instance could"
                        + " not parse.", _provider.Backend, (Int32)ChatModelCatalog.Budget.TotalSeconds));
            }

            // The selector belongs to the provider, for the reason stated on the completion above.
            return Ok(ChatModelsREST.From(_provider.Backend, models));
        }
    }
}
