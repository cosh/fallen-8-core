// MIT License
//
// CapacityReport.cs
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
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.Bench
{
    /// <summary>
    ///   The result file, shaped exactly as <c>capacity-report.schema.json</c> declares it. That
    ///   schema is the contract: these types exist to emit it, so a field added here must be added
    ///   there in the same change, and the schema is what a consumer validates against.
    ///
    ///   <para>Every number is paired with the <see cref="Environment" /> that produced it. That
    ///   pairing is the point of the file: a capacity figure with no machine attached is not a fact
    ///   about Fallen-8, it is a fact about someone's laptop.</para>
    /// </summary>
    public sealed class CapacityReport
    {
        /// <summary>The schema major this file conforms to.</summary>
        [JsonPropertyName("schemaVersion")]
        public String SchemaVersion => "1";

        [JsonPropertyName("generatedAtUtc")]
        public DateTimeOffset GeneratedAtUtc { get; set; }

        [JsonPropertyName("tool")]
        public ToolInfo Tool { get; set; } = new ToolInfo();

        [JsonPropertyName("source")]
        public SourceInfo Source { get; set; } = new SourceInfo();

        [JsonPropertyName("environment")]
        public EnvironmentInfo Environment { get; set; } = new EnvironmentInfo();

        /// <summary><c>quick</c> (CI-sized) or <c>full</c>.</summary>
        [JsonPropertyName("profile")]
        public String Profile { get; set; } = "quick";

        [JsonPropertyName("metrics")]
        public MetricSet Metrics { get; set; } = new MetricSet();
    }

    /// <summary>Which build of the measuring tool ran.</summary>
    public sealed class ToolInfo
    {
        [JsonPropertyName("name")]
        public String Name => "fallen-8-bench";

        [JsonPropertyName("version")]
        public String Version { get; set; } = "0";
    }

    /// <summary>Which code was measured, so a number can be tied back to a commit.</summary>
    public sealed class SourceInfo
    {
        [JsonPropertyName("engineVersion")]
        public String EngineVersion { get; set; } = "0";

        [JsonPropertyName("commit")]
        public String? Commit { get; set; }

        [JsonPropertyName("dirtyWorkingTree")]
        public Boolean DirtyWorkingTree { get; set; }
    }

    /// <summary>The machine the numbers describe.</summary>
    public sealed class EnvironmentInfo
    {
        [JsonPropertyName("operatingSystem")]
        public String OperatingSystem { get; set; } = String.Empty;

        [JsonPropertyName("architecture")]
        public String Architecture { get; set; } = String.Empty;

        [JsonPropertyName("runtime")]
        public String Runtime { get; set; } = String.Empty;

        [JsonPropertyName("processorCount")]
        public Int32 ProcessorCount { get; set; }

        [JsonPropertyName("processorName")]
        public String? ProcessorName { get; set; }

        [JsonPropertyName("totalPhysicalMemoryMb")]
        public Double? TotalPhysicalMemoryMb { get; set; }

        [JsonPropertyName("serverGarbageCollection")]
        public Boolean ServerGarbageCollection { get; set; }

        [JsonPropertyName("concurrentGarbageCollection")]
        public Boolean ConcurrentGarbageCollection { get; set; }

        [JsonPropertyName("runnerLabel")]
        public String? RunnerLabel { get; set; }
    }

    /// <summary>The four measurement families, each a list so a profile can size its own scenarios.</summary>
    public sealed class MetricSet
    {
        [JsonPropertyName("memory")]
        public List<MemoryMetric> Memory { get; set; } = new List<MemoryMetric>();

        [JsonPropertyName("writeThroughput")]
        public List<WriteThroughputMetric> WriteThroughput { get; set; } = new List<WriteThroughputMetric>();

        [JsonPropertyName("saveStall")]
        public List<SaveStallMetric> SaveStall { get; set; } = new List<SaveStallMetric>();

        [JsonPropertyName("traversal")]
        public List<TraversalMetric> Traversal { get; set; } = new List<TraversalMetric>();
    }

    /// <summary>Retained bytes attributable to a graph of a given shape.</summary>
    public sealed class MemoryMetric
    {
        [JsonPropertyName("label")]
        public String Label { get; set; } = String.Empty;

        [JsonPropertyName("vertices")]
        public Int32 Vertices { get; set; }

        [JsonPropertyName("edges")]
        public Int64 Edges { get; set; }

        [JsonPropertyName("averageDegree")]
        public Double AverageDegree { get; set; }

        [JsonPropertyName("bytesPerVertex")]
        public Double BytesPerVertex { get; set; }

        [JsonPropertyName("bytesPerEdge")]
        public Double BytesPerEdge { get; set; }

        [JsonPropertyName("retainedMb")]
        public Double RetainedMb { get; set; }
    }

    /// <summary>Committed writes per second at a given producer count.</summary>
    public sealed class WriteThroughputMetric
    {
        [JsonPropertyName("label")]
        public String Label { get; set; } = String.Empty;

        [JsonPropertyName("producers")]
        public Int32 Producers { get; set; }

        [JsonPropertyName("writes")]
        public Int64 Writes { get; set; }

        [JsonPropertyName("writesPerSecond")]
        public Double WritesPerSecond { get; set; }

        [JsonPropertyName("walEnabled")]
        public Boolean WalEnabled { get; set; }
    }

    /// <summary>How long a checkpoint holds the single writer thread.</summary>
    public sealed class SaveStallMetric
    {
        [JsonPropertyName("elements")]
        public Int64 Elements { get; set; }

        [JsonPropertyName("vertices")]
        public Int32 Vertices { get; set; }

        [JsonPropertyName("edges")]
        public Int64 Edges { get; set; }

        [JsonPropertyName("writerHoldMs")]
        public Double WriterHoldMs { get; set; }
    }

    /// <summary>Raw out-edge traversal throughput.</summary>
    public sealed class TraversalMetric
    {
        [JsonPropertyName("label")]
        public String Label { get; set; } = String.Empty;

        [JsonPropertyName("vertices")]
        public Int32 Vertices { get; set; }

        [JsonPropertyName("edges")]
        public Int64 Edges { get; set; }

        [JsonPropertyName("iterations")]
        public Int32 Iterations { get; set; }

        [JsonPropertyName("edgesPerSecond")]
        public Double EdgesPerSecond { get; set; }
    }
}
