// MIT License
//
// AgentEndpoints.cs
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
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.Agents.Configuration;
using NoSQL.GraphDB.Agents.Model;
using NoSQL.GraphDB.Agents.Runtime;

namespace NoSQL.GraphDB.Agents.Hosting
{
    /// <summary>
    ///   The host's control plane, under <c>/agent/*</c> on an unpublished port. The apiApp proxies
    ///   it as <c>/agents/*</c>, and that proxy is the only way in: an agent can be talked into
    ///   calling a tool, so its control plane is not something to expose to a browser directly.
    ///
    ///   <para>
    ///     <b>Nothing here blocks on inference.</b> A spawn answers 202 with an id and the agent
    ///     runs on its own; one measured step against a remote provider took 41 seconds, so a
    ///     control-plane call that waited for an answer would be a request that looks hung. What
    ///     happened is read back from the listing and, from Phase 2, the trace and the feed.
    ///   </para>
    /// </summary>
    public static class AgentEndpoints
    {
        /// <summary>Maps the routes. Kept minimal-API rather than controllers, as the integrations
        /// runtime is: a sidecar's own surface is small and has no OpenAPI document to serve.</summary>
        public static IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
        {
            if (app == null)
            {
                throw new ArgumentNullException(nameof(app));
            }

            app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

            // Mapped before /agent/{id} for a READER's benefit only. The router does not care:
            // a literal segment outranks a parameter in ASP.NET's route precedence whatever the
            // registration order, so "status" and "feed" are never candidate agent ids. Belt and
            // braces anyway, because ids are minted by this host and neither word is a legal one.
            //
            // One home for that: the /agent/feed mapping below does not repeat it.
            app.MapGet("/agent/status", (AgentRegistry registry, IAgentToolSource toolset,
                ChatGatewayPosture posture, Fallen8ChatClient chat, RoleCatalog roles,
                AgentFeedDispatcher feed,
                IOptions<AgentsOptions> options, IOptions<Fallen8TargetOptions> target) =>
                Results.Ok(Status(registry, toolset, posture, chat, roles, feed, options.Value,
                    target.Value)));

            app.MapPost("/agent", (SpawnRequest? request, AgentRegistry registry, RoleCatalog roles,
                AgentRunner runner, IOptions<AgentsOptions> options) =>
            {
                if (request == null || String.IsNullOrWhiteSpace(request.Task))
                {
                    return Problem(StatusCodes.Status400BadRequest, "A task is required.");
                }

                if (!roles.TryGet(request.Role, out var role, out var roleProblem))
                {
                    return Problem(StatusCodes.Status400BadRequest, roleProblem);
                }

                // Checked before the role, because a caller who set parentId has made the same
                // mistake whatever role they named: it is set BY an orchestrator through its swarm
                // tool, never over the control plane, so a caller naming a parent would otherwise
                // quietly build a tree nobody orchestrates.
                if (!String.IsNullOrWhiteSpace(request.ParentId))
                {
                    return Problem(StatusCodes.Status400BadRequest,
                        "parentId is set by an orchestrator spawning a worker, not by a caller.");
                }

                if (!RoleCatalog.IsSpawnableByACaller(role.Name, out var roleRefusal))
                {
                    return Problem(StatusCodes.Status400BadRequest, roleRefusal);
                }

                var spawn = new AgentSpawn(role.Name, request.Task.Trim())
                {
                    Name = request.Name,
                    TokenBudget = request.TokenBudget,
                    SystemPromptAppendix = request.SystemPromptAppendix,
                };

                if (!registry.TryAdmit(spawn, out var agent, out var admitProblem))
                {
                    // 429 rather than 503: the host is healthy and the caller may retry. A 503 would
                    // read as "this sidecar is broken", which is what the proxy's own 503 means.
                    return Problem(StatusCodes.Status429TooManyRequests, admitProblem);
                }

                runner.Start(agent, role, request.SystemPromptAppendix);

                return Results.Accepted((String?)null, agent.Summarize(agent.CreatedUtc));
            });

            app.MapGet("/agent", (AgentRegistry registry) => Results.Ok(registry.All()));

            app.MapGet("/agent/feed", (HttpContext context, AgentFeedDispatcher feed,
                    AgentRegistry registry, IOptions<AgentsOptions> options,
                    [FromQuery] String?[]? agents, [FromQuery] String?[]? kinds) =>
                AgentFeedStream.WriteAsync(context, feed, options.Value, registry.HostInstanceId,
                    agents, kinds));

            app.MapGet("/agent/{id}", (String id, AgentRegistry registry) =>
                registry.TrySummarize(id, out var summary)
                    ? Results.Ok(Detail(summary, registry))
                    : Problem(StatusCodes.Status404NotFound, NotFound(id)));

            app.MapGet("/agent/{id}/trace", (String id, AgentRegistry registry) =>
            {
                if (!registry.TryGet(id, out var agent))
                {
                    return Problem(StatusCodes.Status404NotFound, NotFound(id));
                }

                // One snapshot under one lock. Reading the rows and the two totals separately let
                // a single response contradict itself: a step list that did not match the numbers
                // printed beside it, on the route whose job is to be the trustworthy record.
                var view = agent.Trace.View();
                return Results.Ok(new AgentTraceView
                {
                    AgentId = agent.Id,
                    HostInstanceId = agent.HostInstanceId,
                    Recorded = view.Recorded,
                    Dropped = view.Dropped,
                    Steps = view.Steps,
                });
            });

            app.MapDelete("/agent/{id}", (String id, AgentRegistry registry) =>
            {
                if (!registry.TryCancel(id, out var signalled))
                {
                    return Problem(StatusCodes.Status404NotFound, NotFound(id));
                }

                // Accepted rather than OK, and honestly so: cancellation is cooperative. The token
                // is observed between steps and passed into the call in flight, so a tool call
                // already sent to the graph is not undone.
                return Results.Accepted((String?)null, new CancelAccepted
                {
                    Agent = registry.TrySummarize(id, out var summary) ? summary : null,
                    Signalled = signalled,
                });
            });

            return app;
        }

        private static String NotFound(String id)
        {
            return String.Format(
                "No agent '{0}'. It may have finished and been evicted: nothing here survives a "
                + "host restart, and a finished agent is retained for a bounded time "
                + "(Agents:Limits:RetainFinishedMinutes).", id);
        }

        /// <summary>How many trace steps the detail route shows. A TAIL rather than the whole
        /// trace, because detail is a summary view and the trace route is the whole one; the number
        /// is a constant rather than configuration so a client knows what it will get.</summary>
        private const Int32 DetailTraceSteps = 20;

        private static AgentDetail Detail(AgentSummary agent, AgentRegistry registry)
        {
            var detail = new AgentDetail
            {
                Agent = agent,
                Children = registry.All()
                    .Where(a => String.Equals(a.ParentId, agent.Id, StringComparison.Ordinal))
                    .Select(a => a.Id)
                    .ToList(),
            };

            if (registry.TryGet(agent.Id, out var record))
            {
                detail.Trace = record.Trace.Tail(DetailTraceSteps);
                detail.TraceRecorded = record.Trace.Recorded;

                // The citation counts come off the trace's own citationCheck step rather than being
                // recomputed, so the detail route and the feed event cannot disagree about a run
                // that has already ended.
                foreach (var step in detail.Trace)
                {
                    if (step.ValidCitations != null || step.DanglingCitations != null)
                    {
                        detail.Citations = new CitationCounts
                        {
                            Valid = step.ValidCitations ?? 0,
                            Dangling = step.DanglingCitations ?? 0,
                        };
                    }
                }
            }

            return detail;
        }

        private static HostStatus Status(AgentRegistry registry, IAgentToolSource toolset,
            ChatGatewayPosture posture, Fallen8ChatClient chat, RoleCatalog roles,
            AgentFeedDispatcher feed, AgentsOptions options, Fallen8TargetOptions target)
        {
            var seen = chat.LastSeen;

            return new HostStatus
            {
                HostInstanceId = registry.HostInstanceId,
                Chat = new ChatStatus
                {
                    BaseUrl = target.BaseUrl,
                    Reachability = posture.State,
                    ProbedAt = posture.ProbedAt,
                    TimeoutSeconds = target.TimeoutSeconds,
                    // Absent until a step has actually run, and that is the point: this host holds
                    // no model configuration, so there is nothing to report before the instance has
                    // answered once. Reporting a configured name here would be reporting a value
                    // that does not exist.
                    // Absent rather than an object of nulls when no step has been served, so
                    // "nothing yet" is one fact a client can test rather than two fields to
                    // correlate.
                    LastSeen = seen == null ? null : new LastSeenStatus
                    {
                        Backend = seen.Backend,
                        Model = seen.Model,
                    },
                },
                Mcp = new McpStatus
                {
                    Endpoint = options.Mcp.Endpoint,
                    Connected = toolset.Connected,
                    ToolCount = toolset.Tools.Count,
                    Tools = toolset.Tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList(),
                    Failure = toolset.Failure,
                },
                Roles = roles.Names
                    .Select(name =>
                    {
                        roles.TryGet(name, out var role, out _);
                        return new RoleStatus
                        {
                            Name = name,
                            // What the role ACTUALLY got, not what was configured. An allowlist
                            // naming a tool the server does not advertise is silently narrower than
                            // it looks, and this is where that shows.
                            ToolCount = role.Filter(toolset.Tools).Count,
                            AllowedTools = role.AllowedTools.Count == 0 ? null : role.AllowedTools.ToList(),
                        };
                    })
                    .ToList(),
                Limits = new LimitsStatus
                {
                    MaxStepsPerRun = options.Limits.MaxStepsPerRun,
                    MaxToolCallsPerRun = options.Limits.MaxToolCallsPerRun,
                    MaxRunSeconds = options.Limits.MaxRunSeconds,
                    MaxTokenBudget = options.Limits.MaxTokenBudget,
                    DefaultTokenBudget = options.Limits.DefaultTokenBudget,
                    MaxConcurrentAgents = options.Limits.MaxConcurrentAgents,
                    RetainFinishedMinutes = options.Limits.RetainFinishedMinutes,
                    MaxRetainedAgents = options.Limits.MaxRetainedAgents,
                },
                ActiveAgents = registry.ActiveCount,
                RetainedAgents = registry.RetainedCount,
                Feed = new FeedStatus
                {
                    Subscribers = feed.SubscriberCount,
                    Published = feed.Published,
                    KeepAliveSeconds = options.Feed.KeepAliveSeconds,
                    MaxSubscribers = options.Feed.MaxSubscribers,
                    AcceptedKinds = AgentEventKinds.Names.ToList(),
                    EmittedKinds = AgentEventKinds.Emitted.ToList(),
                },
            };
        }

        /// <summary>
        ///   The host's failure shape: problem+json, so the apiApp's proxy passes the body through
        ///   untouched and the message a caller reads is this host's own rather than a proxy-shaped
        ///   one. The same shape the integrations runtime uses, for the same reason.
        /// </summary>
        private static IResult Problem(Int32 status, String detail)
        {
            return Results.Problem(detail: detail, statusCode: status,
                title: status switch
                {
                    StatusCodes.Status400BadRequest => "Bad Request",
                    StatusCodes.Status404NotFound => "Not Found",
                    StatusCodes.Status429TooManyRequests => "Too Many Requests",
                    _ => null,
                });
        }
    }

    /// <summary>What a caller asks for. Notably NOT a model: the instance owns that.</summary>
    public sealed class SpawnRequest
    {
        /// <summary>What the agent should do. Required.</summary>
        [JsonPropertyName("task")]
        public String? Task
        {
            get; set;
        }

        /// <summary>One of the catalogue's roles; omitted means <c>assistant</c>.</summary>
        [JsonPropertyName("role")]
        public String? Role
        {
            get; set;
        }

        /// <summary>A label for a reviewer. Omitted takes the agent's id.</summary>
        [JsonPropertyName("name")]
        public String? Name
        {
            get; set;
        }

        /// <summary>Tokens this agent may spend; omitted takes
        /// <c>Agents:Limits:DefaultTokenBudget</c>.</summary>
        [JsonPropertyName("tokenBudget")]
        public Int32? TokenBudget
        {
            get; set;
        }

        /// <summary>Appended to the role prompt, never replacing it.</summary>
        [JsonPropertyName("systemPromptAppendix")]
        public String? SystemPromptAppendix
        {
            get; set;
        }

        /// <summary>Present only so a caller setting it can be REFUSED with a reason. A worker is
        /// spawned by its orchestrator's tool, not over the control plane.</summary>
        [JsonPropertyName("parentId")]
        public String? ParentId
        {
            get; set;
        }
    }

    /// <summary>One agent, with the ids of the workers it spawned and the tail of its trace.</summary>
    public sealed class AgentDetail
    {
        [JsonPropertyName("agent")]
        public AgentSummary? Agent
        {
            get; set;
        }

        [JsonPropertyName("children")]
        public IReadOnlyList<String> Children { get; set; } = Array.Empty<String>();

        /// <summary>The last steps of this agent's trace. A tail; <c>GET /agent/{id}/trace</c> is
        /// the whole of what is retained.</summary>
        [JsonPropertyName("trace")]
        public IReadOnlyList<TraceStep> Trace { get; set; } = Array.Empty<TraceStep>();

        /// <summary>How many steps this agent has recorded in total, including any dropped to stay
        /// inside the trace bound. The denominator for "am I seeing the whole run".</summary>
        [JsonPropertyName("traceRecorded")]
        public Int64 TraceRecorded
        {
            get; set;
        }

        /// <summary>Absent until the run ended with a citation check. Absent is NOT zero: it means
        /// no check was made, where zero means an answer cited nothing.</summary>
        [JsonPropertyName("citations")]
        public CitationCounts? Citations
        {
            get; set;
        }
    }

    /// <summary>One agent's whole retained trace.</summary>
    public sealed class AgentTraceView
    {
        [JsonPropertyName("agentId")]
        public String AgentId { get; set; } = String.Empty;

        /// <summary>Which run of the host produced these steps. Nothing here survives a restart.</summary>
        [JsonPropertyName("hostInstanceId")]
        public String HostInstanceId { get; set; } = String.Empty;

        /// <summary>Steps ever recorded, including dropped ones.</summary>
        [JsonPropertyName("recorded")]
        public Int64 Recorded
        {
            get; set;
        }

        /// <summary>Steps dropped to stay inside <c>Agents:Trace:MaxSteps</c>. Non-zero means this
        /// is not the whole run, and a <c>dropped</c> marker step says the same thing in place.</summary>
        [JsonPropertyName("dropped")]
        public Int64 Dropped
        {
            get; set;
        }

        [JsonPropertyName("steps")]
        public IReadOnlyList<TraceStep> Steps { get; set; } = Array.Empty<TraceStep>();
    }

    /// <summary>The feed's own posture, on the status route.</summary>
    public sealed class FeedStatus
    {
        [JsonPropertyName("subscribers")]
        public Int32 Subscribers
        {
            get; set;
        }

        [JsonPropertyName("published")]
        public Int64 Published
        {
            get; set;
        }

        [JsonPropertyName("keepAliveSeconds")]
        public Int32 KeepAliveSeconds
        {
            get; set;
        }

        [JsonPropertyName("maxSubscribers")]
        public Int32 MaxSubscribers
        {
            get; set;
        }

        /// <summary>Every kind the <c>kinds</c> filter accepts.</summary>
        [JsonPropertyName("acceptedKinds")]
        public IReadOnlyList<String> AcceptedKinds { get; set; } = Array.Empty<String>();

        /// <summary>
        ///   The kinds this host can actually emit, which is a SUBSET of the accepted ones.
        ///   <para>
        ///     Reported because the difference is otherwise invisible and costly: a subscriber
        ///     filtering on an accepted-but-never-emitted kind waits forever for an event that
        ///     cannot arrive. Today <c>agentMessage</c> is the difference, because the only things
        ///     that would publish one are a conversation and a swarm, and both are later phases.
        ///   </para>
        /// </summary>
        [JsonPropertyName("emittedKinds")]
        public IReadOnlyList<String> EmittedKinds { get; set; } = Array.Empty<String>();
    }

    /// <summary>What a cancel did. <c>signalled</c> counts the agent and its live descendants.</summary>
    public sealed class CancelAccepted
    {
        [JsonPropertyName("agent")]
        public AgentSummary? Agent
        {
            get; set;
        }

        [JsonPropertyName("signalled")]
        public Int32 Signalled
        {
            get; set;
        }
    }

    /// <summary>This host's posture, which is the whole answer to "why did my agent fail".</summary>
    public sealed class HostStatus
    {
        [JsonPropertyName("hostInstanceId")]
        public String HostInstanceId { get; set; } = String.Empty;

        [JsonPropertyName("chat")]
        public ChatStatus? Chat
        {
            get; set;
        }

        [JsonPropertyName("mcp")]
        public McpStatus? Mcp
        {
            get; set;
        }

        [JsonPropertyName("roles")]
        public IReadOnlyList<RoleStatus> Roles { get; set; } = Array.Empty<RoleStatus>();

        [JsonPropertyName("limits")]
        public LimitsStatus? Limits
        {
            get; set;
        }

        [JsonPropertyName("activeAgents")]
        public Int32 ActiveAgents
        {
            get; set;
        }

        [JsonPropertyName("retainedAgents")]
        public Int32 RetainedAgents
        {
            get; set;
        }

        [JsonPropertyName("feed")]
        public FeedStatus? Feed
        {
            get; set;
        }
    }

    public sealed class ChatStatus
    {
        [JsonPropertyName("baseUrl")]
        public String BaseUrl { get; set; } = String.Empty;

        /// <summary>
        ///   What the STARTUP probe saw. Paired with <see cref="ProbedAt" />, because nothing
        ///   refreshes it: see <see cref="ChatGatewayPosture" /> for why the age matters and what to
        ///   read instead for a live answer.
        /// </summary>
        [JsonPropertyName("reachability")]
        public String Reachability { get; set; } = String.Empty;

        /// <summary>When the probe behind <see cref="Reachability" /> ran. Absent until it has.</summary>
        [JsonPropertyName("probedAt")]
        public DateTimeOffset? ProbedAt
        {
            get; set;
        }

        [JsonPropertyName("timeoutSeconds")]
        public Int32 TimeoutSeconds
        {
            get; set;
        }

        /// <summary>
        ///   The backend and model that served the most recent step, or absent when none has.
        ///   <para>
        ///     ONE object rather than two sibling scalars, which is what spec 3.2 and 3.3 both
        ///     specify and what this shipped differently: a consumer written from either read
        ///     <c>chat.lastSeen.backend</c> and got nothing on a host that had served many steps.
        ///     Nested is the truer shape too, because the two are one fact about one step, and its
        ///     absence says "nothing has served a step yet" once instead of twice.
        ///   </para>
        /// </summary>
        [JsonPropertyName("lastSeen")]
        public LastSeenStatus? LastSeen
        {
            get; set;
        }
    }

    /// <summary>The backend and the model that served one step, as the instance reported them.
    /// Per STEP: see <see cref="Runtime.TraceStep.Backend" />, which owns that rule.</summary>
    public sealed class LastSeenStatus
    {
        [JsonPropertyName("backend")]
        public String? Backend
        {
            get; set;
        }

        [JsonPropertyName("model")]
        public String? Model
        {
            get; set;
        }
    }

    public sealed class McpStatus
    {
        [JsonPropertyName("endpoint")]
        public String Endpoint { get; set; } = String.Empty;

        [JsonPropertyName("connected")]
        public Boolean Connected
        {
            get; set;
        }

        [JsonPropertyName("toolCount")]
        public Int32 ToolCount
        {
            get; set;
        }

        [JsonPropertyName("tools")]
        public IReadOnlyList<String> Tools { get; set; } = Array.Empty<String>();

        /// <summary>Why the last connect failed. Present is what distinguishes a server with no
        /// tools from a server that did not answer.</summary>
        [JsonPropertyName("failure")]
        public String? Failure
        {
            get; set;
        }
    }

    public sealed class RoleStatus
    {
        [JsonPropertyName("name")]
        public String Name { get; set; } = String.Empty;

        [JsonPropertyName("toolCount")]
        public Int32 ToolCount
        {
            get; set;
        }

        /// <summary>The configured or shipped allowlist, absent when the role sees everything.</summary>
        [JsonPropertyName("allowedTools")]
        public IReadOnlyList<String>? AllowedTools
        {
            get; set;
        }
    }

    public sealed class LimitsStatus
    {
        [JsonPropertyName("maxStepsPerRun")]
        public Int32 MaxStepsPerRun
        {
            get; set;
        }

        [JsonPropertyName("maxToolCallsPerRun")]
        public Int32 MaxToolCallsPerRun
        {
            get; set;
        }

        [JsonPropertyName("maxRunSeconds")]
        public Int32 MaxRunSeconds
        {
            get; set;
        }

        /// <summary>
        ///   The operator ceiling on a caller's own <c>tokenBudget</c>. Reported because it CLAMPS
        ///   rather than refuses: a caller asking for more is honoured up to this and told nothing,
        ///   so the one cap that silently rewrites a request was the one cap this route omitted
        ///   while documenting itself as the caps a run is held to. There is otherwise no
        ///   pre-flight way to learn it.
        /// </summary>
        [JsonPropertyName("maxTokenBudget")]
        public Int32 MaxTokenBudget
        {
            get; set;
        }

        [JsonPropertyName("defaultTokenBudget")]
        public Int32 DefaultTokenBudget
        {
            get; set;
        }

        [JsonPropertyName("maxConcurrentAgents")]
        public Int32 MaxConcurrentAgents
        {
            get; set;
        }

        [JsonPropertyName("retainFinishedMinutes")]
        public Int32 RetainFinishedMinutes
        {
            get; set;
        }

        [JsonPropertyName("maxRetainedAgents")]
        public Int32 MaxRetainedAgents
        {
            get; set;
        }
    }
}
