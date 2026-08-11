// MIT License
//
// IntegrationsObservabilityOptions.cs
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

namespace NoSQL.GraphDB.Integrations.Configuration
{
    /// <summary>
    ///   OTLP push configuration, bound from <c>Integrations:Observability</c> - this runtime's mirror
    ///   of <c>Fallen8:Observability</c> and <c>Mcp:Observability</c>. Off by default: with no endpoint
    ///   the runtime registers zero OpenTelemetry code paths. Metrics, traces and logs go to the same
    ///   collector as the Fallen-8 this runtime feeds, and log export runs BEHIND the credential
    ///   redaction wrap, which is why that wrap is installed last in DI.
    /// </summary>
    public sealed class IntegrationsObservabilityOptions
    {
        /// <summary>The configuration section this binds from.</summary>
        public const String SectionName = "Integrations:Observability";

        /// <summary>The OTLP push block.</summary>
        public OtlpOptions Otlp { get; set; } = new OtlpOptions();

        /// <summary>Whether an OTLP pipeline must be registered at all.</summary>
        public Boolean OtlpEnabled => !String.IsNullOrWhiteSpace(Otlp?.Endpoint);

        /// <summary>The OTLP endpoint block.</summary>
        public sealed class OtlpOptions
        {
            /// <summary>OTLP endpoint URL (e.g. http://otel-collector:4317, gRPC). When set, an
            /// exporter for metrics, traces AND logs is added. Default null (off).</summary>
            public String? Endpoint { get; set; }
        }
    }
}
