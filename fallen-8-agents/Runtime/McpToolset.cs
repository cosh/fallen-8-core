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

        /// <summary>Monotonic ticks at the last connect attempt, or <see cref="Int64.MinValue" />
        /// before the first. Read and claimed by <see cref="EnsureConnectedAsync" />.</summary>
        private Int64 _lastAttempt = Int64.MinValue;
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
        ///     Called at startup, and again by <see cref="EnsureConnectedAsync" /> when a run finds
        ///     no session. It has always been written to be callable again; what changed is that
        ///     something calls it. The reconnect was deferred on the grounds that the compose
        ///     service waits for the MCP server to be healthy, and that is true of <c>up</c> only:
        ///     a reboot or a <c>docker start</c> restarts the two containers in an unspecified
        ///     order, which left this host with no tools for the life of the process. A route that
        ///     asks for a reconnect is still a deferral; a run asking for one is not the same thing.
        ///   </para>
        /// </summary>
        public async Task<Boolean> ConnectAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(_options.Connect);

                await CloseAsync().ConfigureAwait(false);

                var transportOptions = new HttpClientTransportOptions
                {
                    Endpoint = new Uri(_options.Endpoint, UriKind.Absolute),
                    Name = "fallen-8-agents",
                    ConnectionTimeout = _options.Connect,
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
                    + "tools and cannot reach a graph at all. Fix Agents:Mcp:Endpoint or the server: "
                    + "the next run tries again, at most once every {ConnectSeconds} seconds, so a "
                    + "server that comes up late needs no restart here. GET /agent/status reports "
                    + "this state.",
                    _options.Endpoint, (Int64)_options.Connect.TotalSeconds);
                return false;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        ///   Whether a tool result is the MCP protocol's own way of saying the call FAILED, and the
        ///   message if it is. This lives here because the shape belongs to the protocol this class
        ///   is the one home for.
        ///
        ///   <para>
        ///     An MCP tool does not throw to report a failure: it answers with a result whose
        ///     <c>isError</c> is true and whose content carries the reason, so a 401 from the graph,
        ///     a refused tier and a provider outage all arrive as ordinary returns. The invoker
        ///     therefore cannot tell work from failure by catching, and every graph call an agent
        ///     makes comes this way. Without this read, a run whose every tool call failed is
        ///     recorded, published and counted as a run that worked.
        ///   </para>
        ///   <para>
        ///     Read off the serialized result rather than a typed response, because that is what
        ///     the function invocation hands back: the tool is an <c>AIFunction</c> over the client,
        ///     and its return value arrives as a <c>JsonElement</c>. Both spellings of each property
        ///     are accepted so a serializer-casing change cannot silently turn this off.
        ///   </para>
        /// </summary>
        public async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
        {
            if (Connected || Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            // At most one attempt per connect timeout, claimed with a compare-exchange so a host
            // running four agents at once makes one attempt rather than four. The cooldown IS the
            // timeout because that is what an attempt costs: a server that is genuinely down then
            // costs a run one handshake, not one per step.
            //
            // Monotonic ticks rather than a wall clock: this is an interval, and the repository
            // reserves DateTime.Now for the documented helper.
            var now = Environment.TickCount64;
            var cooldown = (Int64)_options.Connect.TotalMilliseconds;
            var last = Volatile.Read(ref _lastAttempt);
            if (last != Int64.MinValue && now - last < cooldown)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastAttempt, now, last) != last)
            {
                // Another run claimed this window. Its attempt is the one this run waits for
                // nothing on: an agent with no tools still runs and still says so.
                return;
            }

            if (await ConnectAsync(cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "The MCP server at {Endpoint} answered on a later attempt, so agents on this "
                    + "host have tools again without a restart.",
                    _options.Endpoint);
            }
        }

        public static Boolean TryReadError(Object? result, out String message)
        {
            message = String.Empty;
            if (result is not System.Text.Json.JsonElement element
                || element.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return false;
            }

            if (!(Property(element, "isError") is { } flag)
                || flag.ValueKind != System.Text.Json.JsonValueKind.True)
            {
                return false;
            }

            var said = new List<String>();
            if (Property(element, "content") is { } content
                && content.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind == System.Text.Json.JsonValueKind.Object
                        && Property(block, "text") is { } text
                        && text.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var spoken = text.GetString();
                        if (!String.IsNullOrWhiteSpace(spoken))
                        {
                            said.Add(spoken.Trim());
                        }
                    }
                }
            }

            // A tool may report an error with no text at all; the flag is the finding, so say that
            // rather than recording an empty reason.
            message = said.Count > 0
                ? String.Join(" ", said)
                : "The tool reported an error and said nothing about it.";
            return true;
        }

        /// <summary>
        ///   One property under either casing. <c>JsonElement.TryGetProperty</c> is ordinal, and
        ///   which casing arrives depends on the serializer the client was built with.
        /// </summary>
        private static System.Text.Json.JsonElement? Property(
            System.Text.Json.JsonElement element, String name)
        {
            if (element.TryGetProperty(name, out var exact))
            {
                return exact;
            }

            var other = Char.IsUpper(name[0])
                ? Char.ToLowerInvariant(name[0]) + name.Substring(1)
                : Char.ToUpperInvariant(name[0]) + name.Substring(1);
            return element.TryGetProperty(other, out var swapped) ? swapped : null;
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
