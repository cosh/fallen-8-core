// MIT License
//
// AppDiagnostics.cs
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
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace NoSQL.GraphDB.App.Diagnostics
{
    /// <summary>
    ///   The API app's trace source and metric instruments (feature observability): the
    ///   codegen cache/compile signals around the Roslyn paths and the algorithm-run spans.
    ///   STATIC (unlike the engine's per-instance <c>Fallen8Metrics</c>) because everything
    ///   here reads process-wide state - the codegen caches are themselves static.
    ///
    ///   <para>TAG HYGIENE (the §3.7 invariant): tag values are fixed artifact names
    ///   (<c>path_traverser</c>/<c>subgraph</c>) and booleans - never user input. Filter
    ///   fragments, labels and index names must never appear here.</para>
    /// </summary>
    public static class AppDiagnostics
    {
        /// <summary>The app source/meter name.</summary>
        public const String SourceName = "NoSQL.GraphDB.App";

        /// <summary>The artifact tag value for path-traverser compiles.</summary>
        public const String PathTraverserArtifact = "path_traverser";

        /// <summary>The artifact tag value for subgraph delegate-provider compiles.</summary>
        public const String SubGraphArtifact = "subgraph";

        /// <summary>The app's activity source (codegen compile + algorithm-run spans).</summary>
        public static readonly ActivitySource Source = new ActivitySource(SourceName);

        private static readonly Meter _meter = new Meter(SourceName);

        private static readonly Counter<Int64> _cacheHits = _meter.CreateCounter<Int64>(
            "fallen8.codegen.cache.hits", "{lookup}", "Compiled-artifact cache hits.");

        private static readonly Counter<Int64> _cacheMisses = _meter.CreateCounter<Int64>(
            "fallen8.codegen.cache.misses", "{lookup}", "Compiled-artifact cache misses (a miss triggers a Roslyn compile).");

        private static readonly Histogram<Double> _compileDuration = _meter.CreateHistogram<Double>(
            "fallen8.codegen.compile.duration", "s", "Roslyn compile duration per artifact.");

        private static readonly Counter<Int64> _compileFailures = _meter.CreateCounter<Int64>(
            "fallen8.codegen.compile.failures", "{failure}", "Failed Roslyn compiles (diagnostics in the 400 response, never here).");

        internal static void RecordCacheHit(String artifact)
        {
            _cacheHits.Add(1, new KeyValuePair<String, Object>("artifact", artifact));
        }

        internal static void RecordCacheMiss(String artifact)
        {
            _cacheMisses.Add(1, new KeyValuePair<String, Object>("artifact", artifact));
        }

        internal static void RecordCompile(String artifact, Boolean success, Double seconds)
        {
            _compileDuration.Record(seconds,
                new KeyValuePair<String, Object>("artifact", artifact),
                new KeyValuePair<String, Object>("success", success));
            if (!success)
            {
                _compileFailures.Add(1, new KeyValuePair<String, Object>("artifact", artifact));
            }
        }
    }
}
