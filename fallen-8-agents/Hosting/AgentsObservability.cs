// MIT License
//
// AgentsObservability.cs
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NoSQL.GraphDB.Agents.Configuration;
using NoSQL.GraphDB.Agents.Diagnostics;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace NoSQL.GraphDB.Agents.Hosting
{
    /// <summary>
    ///   OpenTelemetry wiring for the agent host (feature fleet-observability 3.6, agent-host 3.6),
    ///   the third of these after the MCP server's and the apiApp's and deliberately the same shape.
    ///
    ///   <para>
    ///     <b>Off unless an OTLP endpoint is set.</b> With none configured this returns before
    ///     touching <c>AddOpenTelemetry</c>, so a bare <c>dotnet run</c> registers zero OTel code
    ///     paths. What it does NOT skip is the meter itself, which
    ///     <see cref="AgentsHost.AddFallen8Agents" /> always registers: an instrument nobody
    ///     listens to costs a few objects, and a host that had to be configured before it could
    ///     count would have nothing to say about the run that made an operator look.
    ///   </para>
    ///   <para>
    ///     <b>The Agent Framework's own GenAI telemetry is registered, not reimplemented.</b> The
    ///     runner wraps an agent to produce it and hands the library this host's own source name,
    ///     so one <c>AddSource</c> and one <c>AddMeter</c> of that name cover it; what it consists
    ///     of is on <see cref="AgentsMetrics.SourceName" />. The runner also leaves the library's
    ///     <c>EnableSensitiveData</c> off, which is what keeps message content, tool arguments and
    ///     tool results out of telemetry: the tag-hygiene rule this feature is held to would
    ///     otherwise be broken by a library rather than by us.
    ///   </para>
    /// </summary>
    public static class AgentsObservability
    {
        /// <summary>
        ///   Registers the OTLP pipeline (metrics, traces and logs) plus the identity resource, or
        ///   no-ops when <c>Agents:Observability:Otlp:Endpoint</c> is unset.
        /// </summary>
        public static void AddAgentsObservability(IServiceCollection services,
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

            services.Configure<AgentsObservabilityOptions>(
                configuration.GetSection(AgentsObservabilityOptions.SectionName));
            services.Configure<AgentsIdentityOptions>(
                configuration.GetSection(AgentsIdentityOptions.SectionName));

            var observability = configuration.GetSection(AgentsObservabilityOptions.SectionName)
                .Get<AgentsObservabilityOptions>() ?? new AgentsObservabilityOptions();
            if (!observability.OtlpEnabled)
            {
                return;
            }

            var endpoint = new Uri(observability.Otlp.Endpoint!);

            // Resolved ONCE: an unset instance id mints a fresh GUID per call, so a second call
            // would describe a second instance that does not exist.
            var identity = (configuration.GetSection(AgentsIdentityOptions.SectionName)
                .Get<AgentsIdentityOptions>() ?? new AgentsIdentityOptions()).Resolve();

            var otel = services.AddOpenTelemetry();

            // One resource for all three signals: service.name plus the four identity attributes.
            // service.instance.id is the resolved id rather than the SDK's random per-process GUID,
            // so the promoted label does not churn across restarts.
            otel.ConfigureResource(r => r
                .AddService("fallen8-agents", serviceInstanceId: identity.InstanceId)
                .AddAttributes(identity.Attributes()));

            otel.WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                // ONE registration, not two: Microsoft Agent Framework names its own Meter after
                // the source name the runner gives it, which is this one, so the library's GenAI
                // token and duration instruments arrive here too (AgentsMetrics.SourceName).
                .AddMeter(AgentsMetrics.MeterName)
                .AddOtlpExporter(o => o.Endpoint = endpoint));

            otel.WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                // The client span and W3C traceparent injection to the chat gateway and the MCP
                // server, which is what makes a slow step attributable to the hop that was slow.
                .AddHttpClientInstrumentation()
                // OUR source name, because the runner hands it to the framework's OTel wrapper:
                // the invoke_agent span is the framework's, the source it lands on is ours.
                .AddSource(AgentsMetrics.SourceName)
                .AddOtlpExporter(o => o.Endpoint = endpoint));

            // ILogger to OTLP; the console logging a container captures is untouched. WithLogging
            // shares the resource configured above, so logs carry the same identity attributes.
            otel.WithLogging(logging => logging
                .AddOtlpExporter(o => o.Endpoint = endpoint));
        }
    }
}
