// MIT License
//
// McpToolset.cs
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
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using NoSQL.GraphDB.Agents.Configuration;

namespace NoSQL.GraphDB.Agents.Runtime
{
    /// <summary>
    ///   This host's ONLY route to a graph: the tools the MCP server advertises, fetched once and
    ///   handed to every agent through its role's allowlist.
    ///
    ///   <para>
    ///     <b>Why the graph arrives this way rather than as REST calls of its own.</b> The MCP
    ///     server already holds the consolidated, token-frugal tool set, its read/write/admin tiers
    ///     and its own downstream credential. An agent's reach is therefore whatever those tiers
    ///     allow and nothing more, enforced server-side where an agent cannot argue with it. A
    ///     second graph client here would be a second thing to keep in step with the engine and a
    ///     second place to get a tier wrong.
    ///   </para>
    ///   <para>
    ///     <b>An unreachable MCP server does not stop this host from starting.</b> It starts, says
    ///     so on the posture line, and answers <c>GET /agent/status</c> with the failure. The
    ///     alternative is a crash loop behind a proxy that reports it as a runtime that did not
    ///     answer, which sends an operator to look at the wrong container.
    ///   </para>
    ///   <para>
    ///     An agent spawned in that state has no tools, so it cannot reach a graph. Its role prompt
    ///     forbids answering from nothing, but a prompt is not a guarantee and this feature's own
    ///     measurements say so plainly, so what such an agent produces is whatever the model does
    ///     with no tools. That is why the failure is on the posture line and on the status route
    ///     rather than left for a reader to infer from an empty tool list.
    ///   </para>
    /// </summary>
    public sealed class McpToolset : IAgentToolSource, IAsyncDisposable
    {
        private readonly AgentsOptions.McpOptions _options;
        private readonly ILoggerFactory _loggers;
        private readonly ILogger<McpToolset> _logger;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly HttpMessageHandler? _handler;
        private McpClient? _client;
        private IReadOnlyList<AITool> _tools = Array.Empty<AITool>();
        private Int32 _disposed;

        /// <param name="options">This host's configuration.</param>
        /// <param name="loggers">The logger factory, handed to the transport as well.</param>
        /// <param name="handler">The transport to reach the server through, for a test that hosts
        /// one in process. Null builds the ordinary one. The same seam the apiApp's sidecar clients
        /// take, and for the same reason: an MCP server that only exists over a real socket is a
        /// server the success path cannot be tested against.</param>
        public McpToolset(IOptions<AgentsOptions> options, ILoggerFactory loggers,
            HttpMessageHandler? handler = null)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _options = options.Value.Mcp;
            _loggers = loggers ?? throw new ArgumentNullException(nameof(loggers));
            _logger = loggers.CreateLogger<McpToolset>();
            _handler = handler;
        }

        /// <summary>The tools last fetched. Empty until <see cref="ConnectAsync" /> succeeds, and
        /// empty again is never assumed to mean "the server has none": <see cref="Failure" /> is
        /// what distinguishes a server with no tools from a server that did not answer.</summary>
        public IReadOnlyList<AITool> Tools => Volatile.Read(ref _tools);

        /// <summary>Why the last connect attempt failed, or null. Reported by the status route.</summary>
        public String? Failure
        {
            get; private set;
        }

        /// <summary>True once a connect attempt has succeeded and has not since failed.</summary>
        public Boolean Connected => Volatile.Read(ref _client) != null;

        /// <summary>
        ///   Connects and reads the tool list, bounded by
        ///   <c>Agents:Mcp:ConnectTimeoutSeconds</c> and never throwing: the outcome is the return
        ///   value and the state above.
        ///   <para>
        ///     Called ONCE, at startup. It is written to be callable again, and safely so, but
        ///     nothing calls it again: an MCP server that comes up late needs this host restarted,
        ///     which is why the compose service waits for that server to be healthy. The reconnect
        ///     trigger spec section 3.2 describes is a recorded deferral waiting on a route to ask
        ///     for it, so no message here promises one.
        ///   </para>
        /// </summary>
        public async Task<Boolean> ConnectAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.ConnectTimeoutSeconds)));

                await CloseAsync().ConfigureAwait(false);

                var transportOptions = new HttpClientTransportOptions
                {
                    Endpoint = new Uri(_options.Endpoint, UriKind.Absolute),
                    Name = "fallen-8-agents",
                    ConnectionTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.ConnectTimeoutSeconds)),
                };

                if (!String.IsNullOrWhiteSpace(_options.BearerToken))
                {
                    // Set here rather than through the transport's OAuth options, because this is a
                    // static token an operator configured, not a flow to run. The MCP server accepts
                    // it in its Static auth mode; a server in its None mode ignores it.
                    transportOptions.AdditionalHeaders = new Dictionary<String, String>(StringComparer.Ordinal)
                    {
                        ["Authorization"] = "Bearer " + _options.BearerToken.Trim(),
                    };
                }

                var transport = _handler == null
                    ? new HttpClientTransport(transportOptions, _loggers)
                    : new HttpClientTransport(transportOptions, new HttpClient(_handler, disposeHandler: false),
                        _loggers, ownsHttpClient: true);

                var client = await McpClient.CreateAsync(transport, loggerFactory: _loggers,
                    cancellationToken: budget.Token).ConfigureAwait(false);

                var tools = await client.ListToolsAsync(cancellationToken: budget.Token).ConfigureAwait(false);

                if (Volatile.Read(ref _disposed) == 1)
                {
                    // The host stopped while this was connecting. Closing what was just opened is
                    // the whole point of noticing.
                    await client.DisposeAsync().ConfigureAwait(false);
                    return false;
                }

                Volatile.Write(ref _client, client);
                Volatile.Write(ref _tools, tools.Cast<AITool>().ToList());
                Failure = null;

                _logger.LogInformation(
                    "MCP server at {Endpoint} advertises {ToolCount} tools: {Tools}.",
                    _options.Endpoint, tools.Count, String.Join(", ", tools.Select(t => t.Name)));
                return true;
            }
            catch (Exception failure) when (failure is not OperationCanceledException
                || !cancellationToken.IsCancellationRequested)
            {
                // A timeout arrives here as OperationCanceledException too, and it is a failure of
                // the probe rather than of the host; only the CALLER's cancellation propagates.
                Volatile.Write(ref _tools, Array.Empty<AITool>());
                Failure = failure.Message;

                _logger.LogWarning(failure,
                    "The MCP server at {Endpoint} did not answer, so agents on this host start with NO "
                    + "tools and cannot reach a graph at all. Fix Agents:Mcp:Endpoint or the server "
                    + "and restart this host: nothing re-probes it while it is running. "
                    + "GET /agent/status reports this state.",
                    _options.Endpoint);
                return false;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        ///   Closes the session, once. Idempotent because a container disposes a singleton it also
        ///   handed out, so this is called more than once in practice and the second call must be a
        ///   no-op rather than an <see cref="ObjectDisposedException" /> out of a shutdown path.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            // Under the gate, so a connect in flight cannot publish a live client after this ran and
            // leak its session. Waiting without a token: a dispose that gave up would leave exactly
            // the session it exists to close.
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await CloseAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
            }
        }

        private async Task CloseAsync()
        {
            var client = Interlocked.Exchange(ref _client, null);
            if (client == null)
            {
                return;
            }

            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                // Closing a session that is already gone is the ordinary case on a reconnect, and
                // there is nothing to do about it: the replacement client is what matters.
                _logger.LogDebug(failure, "Closing the previous MCP session failed.");
            }
        }
    }
}
