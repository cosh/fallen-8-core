// MIT License
//
// GraphStatisticsREST.cs
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
    /// <summary>One label (or property key) with its element count. When the response is
    /// sampled, counts are AS COUNTED IN THE SAMPLE (multiply by sampleStride to extrapolate).</summary>
    public sealed class NamedCountREST
    {
        /// <summary>The label / property key.</summary>
        /// <example>person</example>
        [JsonPropertyName("name")]
        public String Name
        {
            get; set;
        }

        /// <summary>How many sampled elements carry it.</summary>
        /// <example>1200</example>
        [JsonPropertyName("count")]
        public Int64 Count
        {
            get; set;
        }
    }

    /// <summary>Top-N names by count plus the distinct total. Sampling honestly UNDERCOUNTS
    /// distinct values: when sampled=true, distinctTotal is distinct-within-the-sample - the
    /// DTO documents that rather than pretending to estimate.</summary>
    public sealed class CardinalityStatsREST
    {
        /// <summary>The top-N entries, count-descending.</summary>
        [JsonPropertyName("top")]
        public List<NamedCountREST> Top
        {
            get; set;
        }

        /// <summary>Distinct names seen (within the sample when sampled=true).</summary>
        /// <example>17</example>
        [JsonPropertyName("distinctTotal")]
        public Int32 DistinctTotal
        {
            get; set;
        }
    }

    /// <summary>Degree distribution over the sampled vertices - strided-sample percentiles are
    /// statistically sound.</summary>
    public sealed class DegreeStatsREST
    {
        /// <example>0</example>
        [JsonPropertyName("min")]
        public Int64 Min
        {
            get; set;
        }

        /// <example>420</example>
        [JsonPropertyName("max")]
        public Int64 Max
        {
            get; set;
        }

        /// <example>3.7</example>
        [JsonPropertyName("mean")]
        public Double Mean
        {
            get; set;
        }

        /// <example>2</example>
        [JsonPropertyName("p50")]
        public Int64 P50
        {
            get; set;
        }

        /// <example>9</example>
        [JsonPropertyName("p90")]
        public Int64 P90
        {
            get; set;
        }

        /// <example>40</example>
        [JsonPropertyName("p99")]
        public Int64 P99
        {
            get; set;
        }
    }

    /// <summary>One registered index.</summary>
    public sealed class IndexStatsREST
    {
        /// <summary>The index name.</summary>
        /// <example>myIndex</example>
        [JsonPropertyName("name")]
        public String Name
        {
            get; set;
        }

        /// <summary>The plugin type name.</summary>
        /// <example>DictionaryIndex</example>
        [JsonPropertyName("type")]
        public String Type
        {
            get; set;
        }

        /// <summary>CountOfKeys().</summary>
        /// <example>1000</example>
        [JsonPropertyName("keys")]
        public Int32 Keys
        {
            get; set;
        }

        /// <summary>CountOfValues().</summary>
        /// <example>1200</example>
        [JsonPropertyName("values")]
        public Int32 Values
        {
            get; set;
        }
    }

    /// <summary>Process/GC memory numbers that are FREE to read - this endpoint never forces a
    /// GC (deliberate contrast with the benchmark-only GC.GetTotalMemory(true)).</summary>
    public sealed class MemoryStatsREST
    {
        /// <summary>Process working set.</summary>
        /// <example>1073741824</example>
        [JsonPropertyName("processWorkingSetBytes")]
        public Int64 ProcessWorkingSetBytes
        {
            get; set;
        }

        /// <summary>GC.GetTotalMemory(false) - allocated managed memory, no forced collection.</summary>
        /// <example>805306368</example>
        [JsonPropertyName("gcHeapBytes")]
        public Int64 GcHeapBytes
        {
            get; set;
        }

        /// <summary>GCMemoryInfo.HeapSizeBytes from the last GC.</summary>
        /// <example>805306368</example>
        [JsonPropertyName("gcLastHeapSizeBytes")]
        public Int64 GcLastHeapSizeBytes
        {
            get; set;
        }

        /// <summary>GCMemoryInfo.FragmentedBytes from the last GC.</summary>
        /// <example>52428800</example>
        [JsonPropertyName("gcFragmentedBytes")]
        public Int64 GcFragmentedBytes
        {
            get; set;
        }
    }

    /// <summary>
    ///   The graph-shape snapshot behind GET /statistics (feature observability). An ADVISORY,
    ///   lock-free snapshot - not transactionally consistent - computed on demand under an
    ///   element budget: exact when V+E fits the budget, uniformly strided (and flagged) above it.
    /// </summary>
    public sealed class GraphStatisticsREST
    {
        /// <example>2500000</example>
        [JsonPropertyName("vertexCount")]
        public Int32 VertexCount
        {
            get; set;
        }

        /// <example>10000000</example>
        [JsonPropertyName("edgeCount")]
        public Int32 EdgeCount
        {
            get; set;
        }

        /// <summary>Vertex label cardinalities (top-N + distinct).</summary>
        [JsonPropertyName("vertexLabels")]
        public CardinalityStatsREST VertexLabels
        {
            get; set;
        }

        /// <summary>Edge label cardinalities (top-N + distinct).</summary>
        [JsonPropertyName("edgeLabels")]
        public CardinalityStatsREST EdgeLabels
        {
            get; set;
        }

        /// <summary>In-degree distribution over the sampled vertices.</summary>
        [JsonPropertyName("inDegree")]
        public DegreeStatsREST InDegree
        {
            get; set;
        }

        /// <summary>Out-degree distribution over the sampled vertices.</summary>
        [JsonPropertyName("outDegree")]
        public DegreeStatsREST OutDegree
        {
            get; set;
        }

        /// <summary>Total (in+out) degree distribution over the sampled vertices.</summary>
        [JsonPropertyName("totalDegree")]
        public DegreeStatsREST TotalDegree
        {
            get; set;
        }

        /// <summary>Property-key cardinalities by element count (top-N + distinct) - the
        /// heaviest stat (O(total properties) over the sampled elements).</summary>
        [JsonPropertyName("propertyKeys")]
        public CardinalityStatsREST PropertyKeys
        {
            get; set;
        }

        /// <summary>The registered indices.</summary>
        [JsonPropertyName("indices")]
        public List<IndexStatsREST> Indices
        {
            get; set;
        }

        /// <summary>Free process/GC memory reads (never a forced GC).</summary>
        [JsonPropertyName("memory")]
        public MemoryStatsREST Memory
        {
            get; set;
        }

        /// <summary>Wall-clock cost of computing this snapshot.</summary>
        /// <example>184.2</example>
        [JsonPropertyName("computedInMs")]
        public Double ComputedInMs
        {
            get; set;
        }

        /// <summary>True when V+E exceeded the element budget and the pass sampled with a
        /// uniform stride; per-name counts are then within-sample.</summary>
        /// <example>false</example>
        [JsonPropertyName("sampled")]
        public Boolean Sampled
        {
            get; set;
        }

        /// <summary>The uniform stride used (1 when exact).</summary>
        /// <example>1</example>
        [JsonPropertyName("sampleStride")]
        public Int32 SampleStride
        {
            get; set;
        }

        /// <summary>The embedding provider state (feature embedding-provider). Reading this
        /// NEVER triggers the lazy model load.</summary>
        [JsonPropertyName("embedding")]
        public EmbeddingProviderStatsREST Embedding
        {
            get; set;
        }
    }

    /// <summary>The active embedding provider and its declared model identity (feature
    /// embedding-provider). Cheap config/state reads only - statistics never loads a model.</summary>
    public sealed class EmbeddingProviderStatsREST
    {
        /// <summary>Whether the capability flag (Fallen8:Embedding:Enabled) is on.</summary>
        /// <example>false</example>
        [JsonPropertyName("enabled")]
        public Boolean Enabled
        {
            get; set;
        }

        /// <summary>The configured backend: Onnx, LLamaSharp or Ollama.</summary>
        /// <example>Onnx</example>
        [JsonPropertyName("backend")]
        public String Backend
        {
            get; set;
        }

        /// <summary>The declared model name.</summary>
        /// <example>bge-micro-v2</example>
        [JsonPropertyName("modelName")]
        public String ModelName
        {
            get; set;
        }

        /// <summary>The declared model version (empty when unspecified).</summary>
        [JsonPropertyName("modelVersion")]
        public String ModelVersion
        {
            get; set;
        }

        /// <summary>The declared output dimension.</summary>
        /// <example>384</example>
        [JsonPropertyName("dimension")]
        public Int32 Dimension
        {
            get; set;
        }

        /// <summary>The metric the embeddings are intended for.</summary>
        /// <example>Cosine</example>
        [JsonPropertyName("intendedMetric")]
        public String IntendedMetric
        {
            get; set;
        }

        /// <summary>Whether the backend has actually been created (lazy load happened).</summary>
        /// <example>false</example>
        [JsonPropertyName("loaded")]
        public Boolean Loaded
        {
            get; set;
        }
    }
}
