// MIT License
//
// McpHost.cs
//
// Copyright (c) 2026 Henning Rauch
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
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using NoSQL.GraphDB.Mcp.Bridge;
using NoSQL.GraphDB.Mcp.Configuration;
using NoSQL.GraphDB.Mcp.Tools;

namespace NoSQL.GraphDB.Mcp.Hosting
{
    /// <summary>
    ///   Composition root. The MCP server runs one of two transports (spec §3.3): stdio (a
    ///   console generic host — no Kestrel) for local dev, or Streamable HTTP (a Kestrel web
    ///   host, loopback-bound by default) for remote agents. Both share the same service graph
    ///   (<see cref="AddFallen8Mcp"/>) and the same low-level tool handlers; only the transport
    ///   and the process model differ.
    /// </summary>
    public static class McpHost
    {
        /// <summary>Late-bound provider so the pre-build MCP handler delegates can resolve the
        /// singleton <see cref="ToolCatalog"/> from the built container (spec §3.2 — the low-level
        /// handlers are registered before <c>Build()</c>).</summary>
        public sealed class ProviderHolder
        {
            public IServiceProvider? Provider { get; set; }
        }

        public static String ResolveTransport(String[] args)
        {
            if (args.Any(a => String.Equals(a, "--stdio", StringComparison.OrdinalIgnoreCase)))
            {
                return "stdio";
            }
            var env = Environment.GetEnvironmentVariable("Mcp__Transport");
            return String.Equals(env, "stdio", StringComparison.OrdinalIgnoreCase) ? "stdio" : "http";
        }

        /// <summary>Registers the bridge, tools, catalog, and the MCP server with its low-level
        /// handlers and the chosen transport. Shared by both transports and re-usable by the test
        /// harness (which passes <paramref name="stdio"/> = false for the HTTP transport).</summary>
        public static ProviderHolder AddFallen8Mcp(IServiceCollection services, IConfiguration configuration, Boolean stdio)
        {
            services.Configure<McpOptions>(configuration.GetSection(McpOptions.SectionName));
            services.Configure<Fallen8TargetOptions>(configuration.GetSection(Fallen8TargetOptions.SectionName));

            services.AddHttpClient(Fallen8RestClient.HttpClientName, (sp, client) =>
                {
                    var target = sp.GetRequiredService<IOptions<Fallen8TargetOptions>>().Value;
                    client.BaseAddress = new Uri(EnsureTrailingSlash(target.BaseUrl));
                    if (!String.IsNullOrEmpty(target.ApiKey))
                    {
                        client.DefaultRequestHeaders.TryAddWithoutValidation(target.ApiKeyHeader, target.ApiKey);
                    }
                })
                .ConfigurePrimaryHttpMessageHandler(sp =>
                {
                    var target = sp.GetRequiredService<IOptions<Fallen8TargetOptions>>().Value;
                    var handler = new HttpClientHandler();
                    if (target.TlsInsecure)
                    {
                        // Lab-only; the loud warning is emitted in LogStartupPosture.
                        handler.ServerCertificateCustomValidationCallback =
                            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                    }
                    return handler;
                });

            services.AddSingleton<Fallen8RestClient>();

            // Read tier (default on).
            services.AddSingleton<IMcpTool, OverviewTool>();
            services.AddSingleton<IMcpTool, GetTool>();
            services.AddSingleton<IMcpTool, SearchTool>();
            services.AddSingleton<IMcpTool, PathsTool>();
            services.AddSingleton<IMcpTool, AnalyticsTool>();

            // Write tier (Mcp:Tools:EnableWrite) — absent from tools/list and rejected on call when off.
            services.AddSingleton<IMcpTool, MutateTool>();
            services.AddSingleton<IMcpTool, SubgraphTool>();
            services.AddSingleton<IMcpTool, NamespaceTool>();

            // Admin tier (Mcp:Tools:EnableAdmin).
            services.AddSingleton<IMcpTool, AdminTool>();

            services.AddSingleton<ToolCatalog>();

            var holder = new ProviderHolder();
            services.AddSingleton(holder);

            var mcp = services.AddMcpServer(options =>
                {
                    options.ServerInfo = new Implementation { Name = "fallen-8-mcp", Version = "0.1.0" };
                    // Pinned protocol revision (spec §3.2): structuredContent is object-wrapped.
                    options.ProtocolVersion = "2025-06-18";
                })
                .WithListToolsHandler((context, cancellationToken) =>
                    Catalog(holder).ListToolsHandlerAsync(context, cancellationToken))
                .WithCallToolHandler((context, cancellationToken) =>
                    Catalog(holder).CallToolHandlerAsync(context, cancellationToken));

            if (stdio)
            {
                mcp.WithStdioServerTransport();
            }
            else
            {
                mcp.WithHttpTransport(_ => { });
            }

            return holder;
        }

        private static ToolCatalog Catalog(ProviderHolder holder)
        {
            var provider = holder.Provider
                ?? throw new InvalidOperationException("The service provider is not yet available.");
            return provider.GetRequiredService<ToolCatalog>();
        }

        private static String EnsureTrailingSlash(String url)
        {
            return url.EndsWith('/') ? url : url + "/";
        }

        /// <summary>Honest posture line (spec §2/§3.3), always to the logger (stderr under stdio).</summary>
        public static void LogStartupPosture(ILogger logger, McpOptions mcp, Fallen8TargetOptions target)
        {
            logger.LogInformation(
                "fallen-8-mcp starting: transport={Transport} bind={Bind}:{Port} auth={Auth} tiers=[read{Write}{Admin}{Code}] target={Target}",
                mcp.Transport,
                mcp.Security.BindAddress,
                mcp.Port,
                mcp.Auth.Mode,
                mcp.Tools.EnableWrite ? ",write" : String.Empty,
                mcp.Tools.EnableAdmin ? ",admin" : String.Empty,
                mcp.Tools.EnableCode ? ",code" : String.Empty,
                target.BaseUrl);

            if (target.TlsInsecure)
            {
                logger.LogWarning("Fallen8Target:TlsInsecure is ON — downstream TLS validation is DISABLED (lab only).");
            }
        }
    }
}
