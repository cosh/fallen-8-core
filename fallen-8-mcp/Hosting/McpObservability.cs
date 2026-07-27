// MIT License
//
// McpObservability.cs
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using NoSQL.GraphDB.Mcp.Configuration;
using NoSQL.GraphDB.Mcp.Diagnostics;

namespace NoSQL.GraphDB.Mcp.Hosting
{
    /// <summary>
    ///   OpenTelemetry wiring for the MCP server (feature fleet-observability §3.6). Registered from
    ///   the shared <see cref="McpHost.AddFallen8Mcp"/> seam so BOTH transports (stdio generic host
    ///   + Streamable-HTTP web host) export identically. Off unless an OTLP endpoint is set: with
    ///   none configured this returns before touching AddOpenTelemetry, so a bare run keeps zero
    ///   OTel code paths (spec §9).
    /// </summary>
    public static class McpObservability
    {
        /// <summary>Registers the MCP OTLP pipeline (metrics + traces + logs) plus the identity
        /// resource, or no-ops when <c>Mcp:Observability:Otlp:Endpoint</c> is unset.</summary>
        public static void AddMcpObservability(IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<McpObservabilityOptions>(configuration.GetSection(McpObservabilityOptions.SectionName));
            services.Configure<McpIdentityOptions>(configuration.GetSection(McpIdentityOptions.SectionName));

            var observability = configuration.GetSection(McpObservabilityOptions.SectionName).Get<McpObservabilityOptions>()
                ?? new McpObservabilityOptions();
            if (!observability.OtlpEnabled)
            {
                return;
            }

            var endpoint = new Uri(observability.Otlp.Endpoint!);
            // Resolve identity ONCE (a fresh GUID would be minted on each call otherwise).
            var identityAttributes = (configuration.GetSection(McpIdentityOptions.SectionName).Get<McpIdentityOptions>()
                ?? new McpIdentityOptions()).ResourceAttributes();

            // Reuse the single resolved instance id for service.instance.id (not the SDK's random
            // per-process GUID), so the promoted label does not churn across restarts.
            var instanceId = identityAttributes.First(kv => kv.Key == "fallen8.instance.id").Value.ToString();

            var otel = services.AddOpenTelemetry();

            // One resource for all three signals: service.name + the four identity attributes (§3.1).
            otel.ConfigureResource(r => r
                .AddService("fallen8-mcp", serviceInstanceId: instanceId)
                .AddAttributes(identityAttributes));

            otel.WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(McpDiagnostics.MeterName)
                .AddOtlpExporter(o => o.Endpoint = endpoint));

            otel.WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()   // client span + W3C traceparent injection to the REST target
                .AddSource(McpDiagnostics.SourceName)
                .AddOtlpExporter(o => o.Endpoint = endpoint));

            // ILogger -> OTLP; console logging (stderr under stdio) is untouched. WithLogging shares
            // the ConfigureResource resource above, so logs carry the same identity attributes.
            otel.WithLogging(logging => logging
                .AddOtlpExporter(o => o.Endpoint = endpoint));
        }
    }
}
