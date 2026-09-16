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
using System.Linq;
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
    ///     <b>The Agent Framework's own GenAI spans are in the trace pipeline, not reimplemented.</b>
    ///     The framework emits the OpenTelemetry Semantic Conventions for Generative AI
    ///     (<c>invoke_agent</c>, the chat span, and <c>execute_tool</c> per tool) from its own
    ///     source, so this adds that source to the exporter rather than writing spans by hand. The
    ///     runner is what wraps an agent to produce them, and it leaves the framework's
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
                .Get<AgentsIdentityOptions>() ?? new AgentsIdentityOptions()).ResourceAttributes();

            // The resolved id rather than the SDK's random per-process GUID, so the promoted label
            // does not churn across restarts.
            var instanceId = identity.First(kv => kv.Key == "fallen8.instance.id").Value.ToString();

            var otel = services.AddOpenTelemetry();

            // One resource for all three signals: service.name plus the four identity attributes.
            otel.ConfigureResource(r => r
                .AddService("fallen8-agents", serviceInstanceId: instanceId)
                .AddAttributes(identity));

            otel.WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(AgentsMetrics.MeterName)
                // The framework's own GenAI metrics, on ITS meter rather than ours. The name is
                // the one its assembly carries, measured rather than assumed, and a test pins it:
                // registering our meter alone would export the host's view of a run and drop the
                // library's token accounting for the same run.
                .AddMeter(AgentsMetrics.FrameworkTelemetryName)
                .AddOtlpExporter(o => o.Endpoint = endpoint));

            otel.WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                // The client span and W3C traceparent injection to the chat gateway and the MCP
                // server, which is what makes a slow step attributable to the hop that was slow.
                .AddHttpClientInstrumentation()
                // OUR source name, because the runner hands it to the framework's OTel wrapper:
                // the GenAI spans are the framework's, the source they land on is ours, so one
                // registration covers invoke_agent, the chat span and execute_tool.
                .AddSource(AgentsMetrics.SourceName)
                .AddOtlpExporter(o => o.Endpoint = endpoint));

            // ILogger to OTLP; the console logging a container captures is untouched. WithLogging
            // shares the resource configured above, so logs carry the same identity attributes.
            otel.WithLogging(logging => logging
                .AddOtlpExporter(o => o.Endpoint = endpoint));
        }
    }
}
