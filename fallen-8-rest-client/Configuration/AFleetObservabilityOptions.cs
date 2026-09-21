// MIT License
//
// AFleetObservabilityOptions.cs
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

namespace NoSQL.GraphDB.Rest.Configuration
{
    /// <summary>
    ///   The OTLP push endpoint block. One field, and it is the on switch: the deployables treat an
    ///   unset endpoint as "off" rather than registering a pipeline with no exporter.
    /// </summary>
    public sealed class OtlpOptions
    {
        /// <summary>OTLP endpoint URL (for example <c>http://otel-collector:4317</c>, gRPC). When
        /// set, an exporter for metrics, traces AND logs is added. Default null, which is
        /// off.</summary>
        public String? Endpoint { get; set; }
    }

    /// <summary>
    ///   Observability configuration for a deployable that sits beside a Fallen-8 (feature
    ///   fleet-observability 3.6), and the mirror of the apiApp's <c>Fallen8:Observability</c>.
    ///
    ///   <para>
    ///     <b>Off by default, and off means ZERO OpenTelemetry code paths</b> rather than a pipeline
    ///     with no exporter: with no endpoint configured each consumer's wiring returns before
    ///     touching <c>AddOpenTelemetry</c>. What a consumer may still register unconditionally is
    ///     its own meter, because an instrument nobody listens to costs a few objects, and a
    ///     deployable that had to be configured before it could count would have nothing to say
    ///     about the run that made an operator look.
    ///   </para>
    ///   <para>
    ///     The WIRING is deliberately not shared, only this shape: it needs the OpenTelemetry
    ///     packages, and this library holds no package reference so that it cannot hand any consumer
    ///     anything (see the project file). The three wiring blocks are also not identical on
    ///     purpose - which meter, which activity source, whether a framework's own spans are
    ///     registered - and those are per-deployable choices rather than drift.
    ///   </para>
    /// </summary>
    public abstract class AFleetObservabilityOptions
    {
        /// <summary>The OTLP push block.</summary>
        public OtlpOptions Otlp { get; set; } = new OtlpOptions();

        /// <summary>Whether an OTLP pipeline must be registered at all.</summary>
        public Boolean OtlpEnabled => !String.IsNullOrWhiteSpace(Otlp?.Endpoint);
    }
}
