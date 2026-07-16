// MIT License
//
// AnalyticsREST.cs
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
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   One analytics run request (feature graph-analytics): data-only scoping, budgets,
    ///   algorithm parameters, the bounded-result knob and the opt-in property write-back.
    ///   No dynamic code anywhere - these endpoints work with the dynamic-code switch off.
    /// </summary>
    /// <example>
    /// { "vertexLabel": "person", "maxResults": 10, "parameters": { "DampingFactor": 0.85 } }
    /// </example>
    public sealed class AnalyticsSpecification
    {
        /// <summary>Only vertices with exactly this label participate (induced-subgraph
        /// scoping); null = whole graph.</summary>
        /// <example>person</example>
        [JsonPropertyName("vertexLabel")]
        public String VertexLabel
        {
            get; set;
        }

        /// <summary>Only edges in this adjacency group (edge-property-id) are traversed;
        /// null = all edges.</summary>
        /// <example>knows</example>
        [JsonPropertyName("edgePropertyId")]
        public String EdgePropertyId
        {
            get; set;
        }

        /// <summary>Edge-direction interpretation: "in", "out" or "both". Null selects the
        /// algorithm's default (PAGERANK: out; DEGREE and LABELPROPAGATION: both; WCC and
        /// TRIANGLECOUNT ignore direction).</summary>
        /// <example>out</example>
        [JsonPropertyName("direction")]
        public String Direction
        {
            get; set;
        }

        /// <summary>Iteration cap for iterative algorithms (PAGERANK default 100,
        /// LABELPROPAGATION default 20; ceiling 10000). Reaching the cap is a normal outcome:
        /// converged=false, values usable.</summary>
        /// <example>100</example>
        [JsonPropertyName("maxIterations")]
        public Int32 MaxIterations
        {
            get; set;
        }

        /// <summary>PageRank convergence threshold (L1 delta); 0 selects the default 1e-6.</summary>
        /// <example>1e-6</example>
        [JsonPropertyName("epsilon")]
        public Double Epsilon
        {
            get; set;
        }

        /// <summary>Wall-clock budget in seconds; null selects the configured default (30 s).
        /// Values above the configured ceiling are a 400.</summary>
        /// <example>30</example>
        [JsonPropertyName("timeBudgetSeconds")]
        public Int32? TimeBudgetSeconds
        {
            get; set;
        }

        /// <summary>Algorithm-specific numeric knobs, e.g. {"DampingFactor": 0.85}.</summary>
        [JsonPropertyName("parameters")]
        public Dictionary<String, Double> Parameters
        {
            get; set;
        }

        /// <summary>How many rows the response carries (top-K scores or partition summaries).
        /// Default 100, ceiling 10000 - the full result set's delivery vehicle is write-back,
        /// not pagination through millions.</summary>
        /// <example>100</example>
        [JsonPropertyName("maxResults")]
        public Int32? MaxResults
        {
            get; set;
        }

        /// <summary>Offset into a partition's membership page (partition endpoint only).</summary>
        /// <example>0</example>
        [JsonPropertyName("offset")]
        public Int32 Offset
        {
            get; set;
        }

        /// <summary>Opt-in: write each in-scope vertex's value as a property through chunked
        /// plugin write transactions. Snapshot-durable only (a WAL-only replay loses the
        /// properties; re-run to restore) - the mode-(a) contract of DelegateTransaction.</summary>
        /// <example>false</example>
        [JsonPropertyName("writeBack")]
        public Boolean WriteBack
        {
            get; set;
        }

        /// <summary>Overrides the convention property key (e.g. "analytics.pagerank");
        /// non-empty, at most 256 chars.</summary>
        /// <example>analytics.pagerank</example>
        [JsonPropertyName("writeBackPropertyKey")]
        public String WriteBackPropertyKey
        {
            get; set;
        }
    }

    /// <summary>One scored vertex (score algorithms), rows ordered score-descending with
    /// ascending-id tie-break.</summary>
    public sealed class ScoredVertexREST
    {
        /// <summary>The vertex id.</summary>
        /// <example>7</example>
        [JsonPropertyName("graphElementId")]
        public Int32 GraphElementId
        {
            get; set;
        }

        /// <summary>The raw score (PageRank mass, degree, triangle count).</summary>
        /// <example>0.25</example>
        [JsonPropertyName("score")]
        public Double Score
        {
            get; set;
        }
    }

    /// <summary>One partition summary (partition algorithms), rows ordered size-descending
    /// with ascending-partition-id tie-break.</summary>
    public sealed class PartitionSummaryREST
    {
        /// <summary>The partition id (WCC: smallest member vertex id; LABELPROPAGATION: the
        /// community's label, itself a vertex id).</summary>
        /// <example>0</example>
        [JsonPropertyName("partitionId")]
        public Int32 PartitionId
        {
            get; set;
        }

        /// <summary>The number of vertices in the partition.</summary>
        /// <example>42</example>
        [JsonPropertyName("size")]
        public Int32 Size
        {
            get; set;
        }
    }

    /// <summary>The write-back report when the request opted in.</summary>
    public sealed class WriteBackResultREST
    {
        /// <summary>The property key the values were written under.</summary>
        /// <example>analytics.pagerank</example>
        [JsonPropertyName("propertyKey")]
        public String PropertyKey
        {
            get; set;
        }

        /// <summary>How many vertex writes the committed chunks carried. A vertex removed
        /// concurrently between the run and its chunk is a silent no-op on the writer yet
        /// still counted - the fuzzy-consistency story applies to write-back too.</summary>
        /// <example>2500000</example>
        [JsonPropertyName("verticesWritten")]
        public Int32 VerticesWritten
        {
            get; set;
        }

        /// <summary>How many DelegateTransaction chunks carried the write-back (each chunk is
        /// atomic; the whole write-back is not - re-run to remedy a mid-way failure).</summary>
        /// <example>50</example>
        [JsonPropertyName("chunks")]
        public Int32 Chunks
        {
            get; set;
        }
    }

    /// <summary>
    ///   One analytics run's response: run metadata, statistics, and the BOUNDED projection of
    ///   the per-vertex result (top-K scores or partition summaries - the full result set's
    ///   delivery vehicle is write-back).
    /// </summary>
    public sealed class AnalyticsResultREST
    {
        /// <summary>The plugin that ran.</summary>
        /// <example>PAGERANK</example>
        [JsonPropertyName("algorithm")]
        public String Algorithm
        {
            get; set;
        }

        /// <summary>Whether the iterative algorithm converged (single-pass algorithms: true).
        /// False with usable values when the iteration cap stopped the run.</summary>
        /// <example>true</example>
        [JsonPropertyName("converged")]
        public Boolean Converged
        {
            get; set;
        }

        /// <summary>Completed iterations.</summary>
        /// <example>23</example>
        [JsonPropertyName("iterationsRun")]
        public Int32 IterationsRun
        {
            get; set;
        }

        /// <summary>Wall-clock duration in milliseconds.</summary>
        /// <example>184.2</example>
        [JsonPropertyName("elapsedMs")]
        public Double ElapsedMs
        {
            get; set;
        }

        /// <summary>True when the time budget or cancellation stopped the run and the values
        /// are the last completed iteration's (partial relative to the requested work).</summary>
        /// <example>false</example>
        [JsonPropertyName("budgetExhausted")]
        public Boolean BudgetExhausted
        {
            get; set;
        }

        /// <summary>How many vertices were in scope for the run.</summary>
        /// <example>2500000</example>
        [JsonPropertyName("vertexCount")]
        public Int32 VertexCount
        {
            get; set;
        }

        /// <summary>Run-level aggregates: TriangleCount, ComponentCount, CommunityCount,
        /// degree Min/Max/Mean.</summary>
        [JsonPropertyName("statistics")]
        public Dictionary<String, Double> Statistics
        {
            get; set;
        }

        /// <summary>Top-K scored vertices (score algorithms only), best first.</summary>
        [JsonPropertyName("results")]
        public List<ScoredVertexREST> Results
        {
            get; set;
        }

        /// <summary>Partition summaries (partition algorithms only), largest first.</summary>
        [JsonPropertyName("partitions")]
        public List<PartitionSummaryREST> Partitions
        {
            get; set;
        }

        /// <summary>The write-back report; null when the request did not opt in.</summary>
        [JsonPropertyName("writeBack")]
        public WriteBackResultREST WriteBack
        {
            get; set;
        }
    }

    /// <summary>One partition's membership page (partition algorithms).</summary>
    public sealed class PartitionMembersREST
    {
        /// <summary>The partition id.</summary>
        /// <example>0</example>
        [JsonPropertyName("partitionId")]
        public Int32 PartitionId
        {
            get; set;
        }

        /// <summary>The partition's total size (across all pages).</summary>
        /// <example>42</example>
        [JsonPropertyName("size")]
        public Int32 Size
        {
            get; set;
        }

        /// <summary>This page's offset into the ascending-id member list.</summary>
        /// <example>0</example>
        [JsonPropertyName("offset")]
        public Int32 Offset
        {
            get; set;
        }

        /// <summary>The member vertex ids on this page, ascending.</summary>
        [JsonPropertyName("members")]
        public List<Int32> Members
        {
            get; set;
        }
    }
}
