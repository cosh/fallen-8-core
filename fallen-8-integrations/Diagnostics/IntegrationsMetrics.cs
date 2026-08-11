// MIT License
//
// IntegrationsMetrics.cs
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
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using NoSQL.GraphDB.Integrations.Run;

namespace NoSQL.GraphDB.Integrations.Diagnostics
{
    /// <summary>
    ///   One instrument per count the report carries, on one meter, pushed to the same collector as the Fallen-8
    ///   this runtime feeds.
    ///
    ///   <para>The job's CLAIM IDENTITY is deliberately not a metric tag: the identity arrives from a caller on
    ///   every request, so tagging by it lets a caller mint unbounded time series in somebody else's monitoring
    ///   backend. The provider id is a closed set and is safe, and the run outcome is <c>ok</c> or
    ///   <c>failed</c>.</para>
    /// </summary>
    public sealed class IntegrationsMetrics : IDisposable
    {
        /// <summary>The meter name the collector's view is keyed on.</summary>
        public const String MeterName = "NoSQL.GraphDB.Integrations";

        private readonly Meter _meter;
        private readonly Counter<Int64> _runs;
        private readonly Histogram<Double> _duration;
        private readonly Counter<Int64> _elements;
        private readonly Counter<Int64> _claimsWithdrawn;
        private readonly Counter<Int64> _elementsDeleted;
        private readonly Counter<Int64> _deletionsDeferred;

        public IntegrationsMetrics()
        {
            _meter = new Meter(MeterName);
            _runs = _meter.CreateCounter<Int64>("f8i.job.runs", "run", "Integration job runs.");
            _duration = _meter.CreateHistogram<Double>("f8i.job.duration", "ms", "Integration job duration.");
            _elements = _meter.CreateCounter<Int64>("f8i.job.elements", "element",
                "Elements a run created or matched.");
            _claimsWithdrawn = _meter.CreateCounter<Int64>("f8i.job.claims_withdrawn", "claim",
                "Claims a run withdrew.");
            _elementsDeleted = _meter.CreateCounter<Int64>("f8i.job.elements_deleted", "element",
                "Elements a run deleted on their last claim.");
            _deletionsDeferred = _meter.CreateCounter<Int64>("f8i.job.deletions_deferred", "element",
                "Deletions a run deferred because deleting was unsafe.");
        }

        /// <summary>Records one finished run.</summary>
        public void Record(JobReport report, String providerId)
        {
            if (report == null)
            {
                throw new ArgumentNullException(nameof(report));
            }

            var tags = new KeyValuePair<String, Object?>[]
            {
                new KeyValuePair<String, Object?>("provider", providerId),
                new KeyValuePair<String, Object?>("outcome", report.Failed ? "failed" : "ok"),
            };

            _runs.Add(1, tags);
            _duration.Record(report.DurationMilliseconds, tags);
            _elements.Add(report.ElementsCreated + report.ElementsMatched, tags);
            _claimsWithdrawn.Add(report.ClaimsWithdrawn, tags);
            _elementsDeleted.Add(report.ElementsDeleted, tags);
            _deletionsDeferred.Add(report.DeletionsDeferred, tags);
        }

        public void Dispose()
        {
            _meter.Dispose();
        }
    }
}
