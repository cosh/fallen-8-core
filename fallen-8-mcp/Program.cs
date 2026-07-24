// MIT License
//
// Program.cs
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
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.Mcp.Configuration;
using NoSQL.GraphDB.Mcp.Hosting;

namespace NoSQL.GraphDB.Mcp
{
    /// <summary>
    ///   Entry point. Explicitly namespaced (not a top-level-statement global
    ///   <c>Program</c>) so that <c>WebApplicationFactory&lt;NoSQL.GraphDB.Mcp.Program&gt;</c>
    ///   in the test suite is unambiguous against the apiApp's
    ///   <c>NoSQL.GraphDB.App.Program</c> (spec §3.11).
    /// </summary>
    public sealed class Program
    {
        public static async Task Main(String[] args)
        {
            if (McpHost.ResolveTransport(args) == "stdio")
            {
                await RunStdioAsync(args).ConfigureAwait(false);
                return;
            }

            await RunHttpAsync(args).ConfigureAwait(false);
        }

        /// <summary>Local dev: a console generic host, NO Kestrel. Under stdio, stdout is the
        /// JSON-RPC frame stream, so ALL logging is routed to stderr (spec §3.3).</summary>
        private static async Task RunStdioAsync(String[] args)
        {
            var builder = Host.CreateApplicationBuilder(args);
            builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

            var holder = McpHost.AddFallen8Mcp(builder.Services, builder.Configuration, stdio: true);

            var host = builder.Build();
            holder.Provider = host.Services;

            McpHost.LogStartupPosture(
                host.Services.GetRequiredService<ILogger<Program>>(),
                host.Services.GetRequiredService<IOptions<McpOptions>>().Value,
                host.Services.GetRequiredService<IOptions<Fallen8TargetOptions>>().Value);

            await host.RunAsync().ConfigureAwait(false);
        }

        /// <summary>Remote: Streamable HTTP over Kestrel, loopback-bound by default, with origin
        /// validation, a fixed-window rate limiter, and (mode-dependent) a static bearer — spec
        /// §3.3/§3.8.</summary>
        private static async Task RunHttpAsync(String[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            var mcp = builder.Configuration.GetSection(McpOptions.SectionName).Get<McpOptions>() ?? new McpOptions();
            builder.WebHost.UseUrls($"http://{mcp.Security.BindAddress}:{mcp.Port}");

            if (mcp.Security.RateLimit.Enabled)
            {
                builder.Services.AddRateLimiter(options =>
                {
                    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, String>(_ =>
                        RateLimitPartition.GetFixedWindowLimiter("mcp", _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = mcp.Security.RateLimit.PermitPerWindow,
                            Window = TimeSpan.FromSeconds(mcp.Security.RateLimit.WindowSeconds),
                            QueueLimit = 0,
                        }));
                });
            }

            // Behind a TLS-terminating proxy the server sees http://; trust the forwarded scheme/host
            // so Phase C metadata (and the cleartext-auth check) reflect the external identity.
            builder.Services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
            });

            var holder = McpHost.AddFallen8Mcp(builder.Services, builder.Configuration, stdio: false);

            var app = builder.Build();
            holder.Provider = app.Services;
            var logger = app.Services.GetRequiredService<ILogger<Program>>();

            // Fail-closed: a non-loopback bind must not run anonymously (spec §3.3).
            var refusal = TransportSecurity.EvaluateStartupRefusal(mcp);
            if (refusal is not null)
            {
                logger.LogCritical("{Refusal}", refusal);
                throw new InvalidOperationException(refusal);
            }

            app.UseForwardedHeaders();
            if (mcp.Security.RateLimit.Enabled)
            {
                app.UseRateLimiter();
            }

            // Origin validation (DNS-rebinding) + static bearer (Phase B). /healthz stays anonymous.
            app.Use(async (context, next) =>
            {
                if (!context.Request.Path.StartsWithSegments("/healthz"))
                {
                    if (!TransportSecurity.IsOriginAllowed(context.Request.Headers["Origin"].ToString(), mcp.Security))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }

                    if (String.Equals(mcp.Auth.Mode, "StaticToken", StringComparison.OrdinalIgnoreCase) &&
                        !TransportSecurity.IsBearerValid(context.Request.Headers["Authorization"].ToString(), mcp.Auth.StaticToken))
                    {
                        context.Response.Headers["WWW-Authenticate"] = "Bearer";
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                }

                await next().ConfigureAwait(false);
            });

            app.MapMcp();
            app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

            McpHost.LogStartupPosture(
                logger,
                mcp,
                app.Services.GetRequiredService<IOptions<Fallen8TargetOptions>>().Value);

            if (!mcp.Auth.IsAnonymous && !TransportSecurity.IsLoopbackHost(mcp.Security.BindAddress))
            {
                logger.LogWarning(
                    "Auth is enabled on a non-loopback bind — ensure TLS terminates in front of this server so bearer tokens are not sent in cleartext (spec §3.3).");
            }

            await app.RunAsync().ConfigureAwait(false);
        }
    }
}
