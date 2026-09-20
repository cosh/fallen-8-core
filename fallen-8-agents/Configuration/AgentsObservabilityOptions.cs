// MIT License
//
// AgentsObservabilityOptions.cs
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

namespace NoSQL.GraphDB.Agents.Configuration
{
    /// <summary>
    ///   Agent-host observability (feature fleet-observability 3.6), bound from
    ///   <c>Agents:Observability</c> and the third mirror of the apiApp's
    ///   <c>Fallen8:Observability</c>.
    ///
    ///   <para>
    ///     Off by default, and off means ZERO OpenTelemetry code paths rather than a pipeline with
    ///     no exporter: with no endpoint configured the wiring returns before touching
    ///     <c>AddOpenTelemetry</c>. The meter itself is always created, because an unobserved
    ///     instrument costs a few objects and nothing else, and a host that had to be configured
    ///     before it could count would report nothing about the run that made an operator look.
    ///   </para>
    /// </summary>
    public sealed class AgentsObservabilityOptions
    {
        /// <summary>The configuration section this binds from.</summary>
        public const String SectionName = "Agents:Observability";

        /// <summary>The OTLP push block (<c>Agents:Observability:Otlp</c>).</summary>
        public OtlpOptions Otlp { get; set; } = new OtlpOptions();

        /// <summary>Whether an OTLP pipeline must be registered at all.</summary>
        public Boolean OtlpEnabled => !String.IsNullOrWhiteSpace(Otlp?.Endpoint);

        /// <summary>The OTLP endpoint block.</summary>
        public sealed class OtlpOptions
        {
            /// <summary>OTLP endpoint URL (for example <c>http://otel-collector:4317</c>, gRPC).
            /// When set, an exporter for metrics, traces AND logs is added. Default null, which is
            /// off.</summary>
            public String? Endpoint
            {
                get; set;
            }
        }
    }
}
