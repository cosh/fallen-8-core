// MIT License
//
// AgentsStartupProbe.cs
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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.Agents.Configuration;
using NoSQL.GraphDB.Agents.Runtime;

namespace NoSQL.GraphDB.Agents.Hosting
{
    /// <summary>
    ///   Reads the two things this host depends on, once, at startup: the MCP server's tool list and
    ///   the chat gateway's reachability.
    ///
    ///   <para>
    ///     <b>Neither read gates startup.</b> An instance or an MCP server that is slow to come up is
    ///     the ordinary case in compose, and a host that refused to start would be reported by the
    ///     apiApp's proxy as a runtime that did not answer, sending an operator to look at a healthy
    ///     container. So both outcomes are recorded, said on the posture line and served by
    ///     <c>GET /agent/status</c>.
    ///   </para>
    /// </summary>
    public sealed class AgentsStartupProbe : IHostedService
    {
        private readonly McpToolset _toolset;
        private readonly ChatGatewayPosture _posture;
        private readonly IHttpClientFactory _clients;
        private readonly IOptions<AgentsOptions> _options;
        private readonly IOptions<Fallen8TargetOptions> _target;
        private readonly RoleCatalog _roles;
        private readonly ILogger<AgentsStartupProbe> _logger;

        public AgentsStartupProbe(McpToolset toolset, ChatGatewayPosture posture,
            IHttpClientFactory clients, IOptions<AgentsOptions> options,
            IOptions<Fallen8TargetOptions> target, RoleCatalog roles,
            ILogger<AgentsStartupProbe> logger)
        {
            _toolset = toolset ?? throw new ArgumentNullException(nameof(toolset));
            _posture = posture ?? throw new ArgumentNullException(nameof(posture));
            _clients = clients ?? throw new ArgumentNullException(nameof(clients));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _roles = roles ?? throw new ArgumentNullException(nameof(roles));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _toolset.ConnectAsync(cancellationToken).ConfigureAwait(false);

            var http = _clients.CreateClient(AgentsHost.ChatClientName);
            _posture.Record(await AgentsHost
                .ProbeChatAsync(http, _logger, cancellationToken).ConfigureAwait(false));

            // Said AFTER both probes, so the line reports what is true rather than what was
            // configured. It is the one place an operator can read this host's whole posture.
            AgentsHost.LogStartupPosture(_logger, _options.Value, _target.Value, _roles, _toolset);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    ///   The chat gateway's last known reachability, as one word. Separate from the adapter because
    ///   it is a fact about the PROBE, and the adapter's own provenance is a fact about the last
    ///   completion; conflating them would let a host that has never run an agent report a model.
    /// </summary>
    public sealed class ChatGatewayPosture
    {
        private String _state = "unprobed";

        /// <summary>One of <c>reachable</c>, <c>unreachable</c>, <c>refused:401</c>,
        /// <c>status:&lt;n&gt;</c> or <c>unprobed</c>.</summary>
        public String State => Volatile.Read(ref _state);

        internal void Record(String state)
        {
            Volatile.Write(ref _state, state);
        }
    }
}
