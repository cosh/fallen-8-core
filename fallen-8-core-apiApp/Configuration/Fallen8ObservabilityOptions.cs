// MIT License
//
// Fallen8ObservabilityOptions.cs
//
// Copyright (c) 2025 Henning Rauch
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

namespace NoSQL.GraphDB.App.Configuration
{
    /// <summary>
    ///   Observability configuration, bound from the <c>Fallen8:Observability</c> section
    ///   (feature observability). Everything defaults to OFF: a default configuration runs
    ///   zero OpenTelemetry code paths and exposes no new endpoints - the same opt-in
    ///   philosophy as the WAL and the security gates.
    /// </summary>
    public sealed class Fallen8ObservabilityOptions
    {
        /// <summary>The configuration section this binds from.</summary>
        public const String SectionName = "Fallen8:Observability";

        private Double _tracingSamplingRatio = 1.0d;
        private Int32 _statisticsElementBudget = 1_000_000;
        private Int32 _statisticsTopN = 20;

        /// <summary>The Prometheus block (<c>Fallen8:Observability:Prometheus</c>).</summary>
        public PrometheusOptions Prometheus { get; set; } = new PrometheusOptions();

        /// <summary>The OTLP block (<c>Fallen8:Observability:Otlp</c>).</summary>
        public OtlpOptions Otlp { get; set; } = new OtlpOptions();

        /// <summary>Root sampling ratio for traces (parent-based sampler); clamped to [0, 1],
        /// default 1.0 - honest for a single-operator box with modest request rates.</summary>
        public Double TracingSamplingRatio
        {
            get { return _tracingSamplingRatio; }
            set { _tracingSamplingRatio = value < 0d ? 0d : value > 1d ? 1d : value; }
        }

        /// <summary>The GET /statistics element budget: when V+E exceeds it, the pass samples
        /// with a uniform stride and says so. Default 1,000,000; non-positive resets.</summary>
        public Int32 StatisticsElementBudget
        {
            get { return _statisticsElementBudget; }
            set { _statisticsElementBudget = value > 0 ? value : 1_000_000; }
        }

        /// <summary>Top-N size for the label / property-key cardinality lists. Default 20.</summary>
        public Int32 StatisticsTopN
        {
            get { return _statisticsTopN; }
            set { _statisticsTopN = value > 0 ? value : 20; }
        }

        /// <summary>Whether any OpenTelemetry pipeline must be registered at all.</summary>
        public Boolean AnyExporterEnabled =>
            (Prometheus?.Enabled ?? false) || !String.IsNullOrWhiteSpace(Otlp?.Endpoint);

        public sealed class PrometheusOptions
        {
            /// <summary>Maps GET /metrics (Prometheus exposition format) when true. Default false.</summary>
            public Boolean Enabled { get; set; }

            /// <summary>When true, /metrics requires the API key like any other endpoint instead
            /// of the documented anonymous default (spec §3.7 - the inventory carries zero
            /// user-supplied strings, and the server binds loopback by default).</summary>
            public Boolean RequireApiKey { get; set; }
        }

        public sealed class OtlpOptions
        {
            /// <summary>OTLP endpoint URL (e.g. http://localhost:4317). When set, an OTLP
            /// exporter for metrics AND traces is added - point-to-point, no collector required.
            /// Default null (off).</summary>
            public String Endpoint { get; set; }
        }
    }
}
