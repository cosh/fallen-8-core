// MIT License
//
// IntegrationsObservability.cs
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
using NoSQL.GraphDB.Integrations.Configuration;
using NoSQL.GraphDB.Integrations.Diagnostics;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace NoSQL.GraphDB.Integrations.Hosting
{
    /// <summary>
    ///   Metrics, traces and logs over OTLP to the same collector as the Fallen-8 this runtime feeds, declaring
    ///   the SAME tenant and instance identity, so the fleet dashboards resolve its panels under that instance
    ///   rather than as an unrelated service.
    ///
    ///   <para>Off by default: with no endpoint configured, zero OpenTelemetry code paths are registered.</para>
    ///
    ///   <para>
    ///     <b>The service name is <c>fallen8-integrations</c>, and the spelling is load-bearing.</b> It was
    ///     <c>fallen-8-integrations</c>, and the shipped per-tenant dashboard selects its log panel with
    ///     <c>{service_name=~"fallen8.*"}</c> - a fully anchored Loki regex that matches <c>fallen8</c>,
    ///     <c>fallen8-mcp</c> and <c>fallen8-agents</c> and did not match this runtime. A panel described as
    ///     "logs scoped to the selected instance" was therefore showing every service on that instance
    ///     except this one. The convention is written down in <c>observability/loki/loki-config.yaml</c>;
    ///     <c>CodeQualityTest</c> now reads the selector out of the dashboard and holds all four names
    ///     against it, so a fifth deployable that misses the panel fails the suite instead.
    ///   </para>
    /// </summary>
    public static class IntegrationsObservability
    {
        /// <summary>Registers the meter, and the OTLP pipeline when an endpoint is configured.</summary>
        public static IServiceCollection Add(IServiceCollection services, IConfiguration configuration)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            services.AddSingleton<IntegrationsMetrics>();

            var identity = configuration.GetSection(IntegrationsIdentityOptions.SectionName)
                               .Get<IntegrationsIdentityOptions>() ?? new IntegrationsIdentityOptions();
            var observability = configuration.GetSection(IntegrationsObservabilityOptions.SectionName)
                                    .Get<IntegrationsObservabilityOptions>()
                                ?? new IntegrationsObservabilityOptions();

            if (!observability.OtlpEnabled)
            {
                return services;
            }

            var endpoint = new Uri(observability.Otlp.Endpoint!);

            // Resolved ONCE: an unset instance id yields a fresh value per call, so a second call would
            // declare a second identity for one process.
            var resolved = identity.Resolve();

            var otel = services.AddOpenTelemetry();

            // ONE resource for all three signals, which logs previously reached by a SECOND call path of
            // their own (ResourceBuilder.CreateDefault() inside AddLogging). Measured, the two resources
            // came out attribute-for-attribute identical, so this is not a fix for an observed difference:
            // it removes a second place the same four attributes had to be assembled correctly, which is a
            // divergence waiting rather than a divergence. WithLogging shares what ConfigureResource sets,
            // as it does in the MCP server and the agent host.
            //
            // service.instance.id is the RESOLVED id rather than the SDK's random per-process GUID, which
            // the other two sidecars always passed and this one did not. What that cost is narrower than it
            // sounds and is worth stating precisely: no dashboard in this repository keys on
            // service.instance.id, so no shipped panel was wrong. What churned was a promoted resource
            // label, which an operator's own queries and any grouping by it would see move on every
            // restart.
            otel.ConfigureResource(resource => resource
                .AddService("fallen8-integrations", serviceInstanceId: resolved.InstanceId)
                .AddAttributes(resolved.Attributes()));

            otel.WithMetrics(metrics => metrics
                .AddMeter(IntegrationsMetrics.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter(exporter => exporter.Endpoint = endpoint));

            otel.WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(exporter => exporter.Endpoint = endpoint));

            // Log export runs BEHIND the credential redaction wrap, which is why that wrap is installed last
            // in DI. IncludeFormattedMessage is kept: it decides what a RECORD carries, which is a separate
            // question from which service it is attributed to.
            //
            // IncludeScopes is NOT set, and its absence is deliberate rather than an omission. It was
            // carried over from the previous registration, where it had no effect either:
            // RedactingLoggerProvider.WrapRegisteredProviders REPLACES each ILoggerProvider descriptor with
            // the wrap, and the wrap implements ILoggerProvider only - not ISupportExternalScope - so the
            // logging factory never hands the OTel provider a scope provider and there are no scopes for it
            // to include. Making the wrap forward them would be the wrong fix: a scope value reaches the
            // exporter WITHOUT passing through redaction, and a credential is exactly the kind of thing a
            // scope carries. Setting an inert flag told the next reader that scopes are exported.
            otel.WithLogging(
                logging => logging.AddOtlpExporter(exporter => exporter.Endpoint = endpoint),
                options => options.IncludeFormattedMessage = true);

            return services;
        }
    }
}
