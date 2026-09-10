// MIT License
//
// AgentsHost.cs
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
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.Agents.Configuration;
using NoSQL.GraphDB.Agents.Model;
using NoSQL.GraphDB.Agents.Runtime;

namespace NoSQL.GraphDB.Agents.Hosting
{
    /// <summary>
    ///   The host's service graph, in one place so the entry point stays a bind-and-run and a test
    ///   can build the same graph without Kestrel.
    /// </summary>
    public static class AgentsHost
    {
        /// <summary>The named transport that reaches the Fallen-8 chat gateway.</summary>
        public const String ChatClientName = "fallen8-chat";

        /// <summary>Registers everything the host needs.</summary>
        public static IServiceCollection AddFallen8Agents(IServiceCollection services,
            IConfiguration configuration)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            services.Configure<AgentsOptions>(configuration.GetSection(AgentsOptions.SectionName));
            services.Configure<Fallen8TargetOptions>(
                configuration.GetSection(Fallen8TargetOptions.SectionName));

            // Loaded once and THROWS on a missing or blank prompt, so a host whose agents would
            // fabricate tool results does not start. See RoleCatalog for the measurement behind
            // that being fatal rather than a warning.
            services.AddSingleton(provider =>
                RoleCatalog.Load(provider.GetRequiredService<IOptions<AgentsOptions>>().Value));

            services.AddHttpClient(ChatClientName, (provider, http) =>
            {
                var target = provider.GetRequiredService<IOptions<Fallen8TargetOptions>>().Value;
                http.BaseAddress = new Uri(BaseAddress(target.BaseUrl), UriKind.Absolute);

                // No HttpClient.Timeout: the adapter's own linked deadline is the budget, and a
                // second timeout at this layer would be the NEARER of two, reporting a vague local
                // failure in place of the instance's answer that names what to change.
                http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
                http.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                if (!String.IsNullOrWhiteSpace(target.ApiKey))
                {
                    http.DefaultRequestHeaders.TryAddWithoutValidation(
                        target.ApiKeyHeader, target.ApiKey.Trim());
                }
            });

            // One adapter shared by every agent, over one transport. Per-agent state lives in the
            // meter that wraps it (AgentBudgetChatClient), not here.
            services.AddSingleton(provider =>
            {
                var target = provider.GetRequiredService<IOptions<Fallen8TargetOptions>>().Value;
                var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ChatClientName);
                return new Fallen8ChatClient(http,
                    TimeSpan.FromSeconds(Math.Max(1, target.TimeoutSeconds)));
            });

            // The runner asks for the INTERFACE, so a test drives it with a scripted client and
            // never a model; the status route asks for the concrete adapter, because what last
            // served a step is the adapter's own fact.
            services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(provider =>
                provider.GetRequiredService<Fallen8ChatClient>());

            services.AddSingleton<McpToolset>();
            services.AddSingleton<IAgentToolSource>(provider => provider.GetRequiredService<McpToolset>());
            services.AddSingleton<ChatGatewayPosture>();

            // Singleton because it IS this process's memory of its agents. Nothing here is durable;
            // a restart ends everything, which is why every listing names the host instance.
            services.AddSingleton<AgentRegistry>();
            services.AddSingleton<AgentRunner>();

            services.AddHostedService<AgentsStartupProbe>();

            return services;
        }

        /// <summary>
        ///   Says out loud, once, what this process will and will not do: where it asks for
        ///   completions, that it chooses no model, what reaches the graph, and what bounds a run.
        /// </summary>
        public static void LogStartupPosture(ILogger logger, AgentsOptions options,
            Fallen8TargetOptions target, RoleCatalog roles, IAgentToolSource toolset)
        {
            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            if (roles == null)
            {
                throw new ArgumentNullException(nameof(roles));
            }

            if (toolset == null)
            {
                throw new ArgumentNullException(nameof(toolset));
            }

            logger.LogInformation(
                "Agent host listening on {BindAddress}:{Port} with roles {Roles}. This port is not "
                + "published: the Fallen-8 proxy at /agents/* is the way in, because an agent can be "
                + "talked into calling a tool.",
                options.BindAddress, options.Port, String.Join(", ", roles.Names));

            logger.LogInformation(
                "Completions come from {BaseUrl} as purpose 'agent' (api key {KeyState}, deadline "
                + "{TimeoutSeconds}s). This host names no provider, holds no provider credential and "
                + "chooses no model: the instance owns all three, so switching a deployment to "
                + "another provider changes nothing here.",
                target.BaseUrl, String.IsNullOrEmpty(target.ApiKey) ? "not set" : "set",
                target.TimeoutSeconds);

            if (toolset.Connected)
            {
                logger.LogInformation(
                    "The graph is reachable only through the MCP server at {Endpoint}, which advertises "
                    + "{ToolCount} tools; its own tiers are the outer bound and a role allowlist can "
                    + "only narrow them.",
                    options.Mcp.Endpoint, toolset.Tools.Count);
            }
            else
            {
                logger.LogWarning(
                    "The MCP server at {Endpoint} did not answer ({Failure}), so agents start with NO "
                    + "tools. They will report that they cannot answer rather than inventing one, and "
                    + "GET /agent/status carries this.",
                    options.Mcp.Endpoint, toolset.Failure ?? "no reason reported");
            }

            logger.LogInformation(
                "A run is bounded at {MaxSteps} model calls, {MaxToolCalls} tool calls, {MaxRunSeconds}s "
                + "wall clock and {TokenBudget} tokens, with at most {MaxConcurrent} agents at once. "
                + "Every one of those is enforced in this process, because a model that is looping is "
                + "the model that will not honour an instruction to stop.",
                options.Limits.MaxStepsPerRun, options.Limits.MaxToolCallsPerRun,
                options.Limits.MaxRunSeconds, options.Limits.DefaultTokenBudget,
                options.Limits.MaxConcurrentAgents);

            logger.LogInformation(
                "Nothing here is durable: a restart ends every agent and forgets every finished one. "
                + "A finished agent stays readable for {RetainMinutes} minutes, at most {MaxRetained} "
                + "of them.",
                options.Limits.RetainFinishedMinutes, options.Limits.MaxRetainedAgents);
        }

        /// <summary>
        ///   The chat gateway's reachability, as one bounded best-effort read at startup. It never
        ///   gates: an instance that is slow to come up is the ordinary case in compose, and a host
        ///   that refused to start would be reported by the proxy as a runtime that did not answer,
        ///   sending an operator to the wrong container.
        ///   <para>
        ///     It deliberately does not learn the agent model's NAME this way. The model is
        ///     server-owned and revealed per step by the instance's own answer, which the status
        ///     route then reports as what was last seen.
        ///   </para>
        /// </summary>
        public static async System.Threading.Tasks.Task<String> ProbeChatAsync(HttpClient http,
            ILogger logger, System.Threading.CancellationToken cancellationToken)
        {
            try
            {
                using var budget = System.Threading.CancellationTokenSource
                    .CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(TimeSpan.FromSeconds(10));

                using var response = await http.GetAsync("chat/models", budget.Token)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation("The Fallen-8 chat gateway answered its model catalogue.");
                    return "reachable";
                }

                // 401 and 403 are the two worth naming, because they are the two an operator fixes
                // rather than waits out: the capability is off, or this host's key is wrong.
                var status = (Int32)response.StatusCode;
                if (status is 401 or 403)
                {
                    logger.LogWarning(
                        "The Fallen-8 chat gateway answered {Status}: either Fallen8:Chat is off on "
                        + "that instance, or this host's Fallen8Target:ApiKey is not accepted. Agents "
                        + "will fail on their first model call with that same answer.", status);
                    return "refused:" + status.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                logger.LogWarning("The Fallen-8 chat gateway answered {Status}.", status);
                return "status:" + status.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception failure) when (failure is not OperationCanceledException
                || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "The Fallen-8 chat gateway at {BaseUrl} did not answer ({Reason}). This host "
                    + "started anyway; agents spawned now fail on their first model call.",
                    http.BaseAddress, failure.Message);
                return "unreachable";
            }
        }

        /// <summary>
        ///   A base address the framework will resolve relative paths against. A missing trailing
        ///   slash silently drops the last path segment, which turns an instance served under a path
        ///   prefix into 404s that look like a missing route.
        /// </summary>
        private static String BaseAddress(String baseUrl)
        {
            var trimmed = (baseUrl ?? String.Empty).Trim();
            if (trimmed.Length == 0)
            {
                trimmed = "http://localhost:8080";
            }

            return trimmed.EndsWith('/') ? trimmed : trimmed + "/";
        }
    }
}
