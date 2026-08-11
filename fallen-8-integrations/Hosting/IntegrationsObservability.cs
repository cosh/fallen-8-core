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
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Integrations.Configuration;
using NoSQL.GraphDB.Integrations.Diagnostics;
using OpenTelemetry;
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

            // Called ONCE: an unset instance id yields a fresh value per call, so a second call would declare a
            // second identity for one process.
            var attributes = identity.ResourceAttributes();

            services.AddOpenTelemetry()
                .ConfigureResource(resource => resource
                    .AddService("fallen-8-integrations")
                    .AddAttributes(attributes))
                .WithMetrics(metrics => metrics
                    .AddMeter(IntegrationsMetrics.MeterName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddOtlpExporter(exporter => exporter.Endpoint = endpoint))
                .WithTracing(tracing => tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter(exporter => exporter.Endpoint = endpoint));

            services.AddLogging(logging => logging.AddOpenTelemetry(options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
                options.SetResourceBuilder(ResourceBuilder.CreateDefault()
                    .AddService("fallen-8-integrations")
                    .AddAttributes(attributes));
                options.AddOtlpExporter(exporter => exporter.Endpoint = endpoint);
            }));

            return services;
        }
    }
}
