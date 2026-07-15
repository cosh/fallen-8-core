// MIT License
//
// BenchmarkResultREST.cs
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
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   Structured result of the edge-traversal benchmark (GET /benchmark)
    /// </summary>
    /// <remarks>
    ///   TPS = traversed edges per second. Each iteration traverses every out-edge of every
    ///   vertex once; the statistics are computed over the per-iteration TPS samples.
    /// </remarks>
    public sealed class BenchmarkResultREST
    {
        /// <summary>Number of timed iterations the statistics are computed over</summary>
        [JsonPropertyName("iterations")]
        public Int32 Iterations
        {
            get; set;
        }

        /// <summary>Edges traversed in a single iteration</summary>
        [JsonPropertyName("edgesTraversed")]
        public Int64 EdgesTraversed
        {
            get; set;
        }

        /// <summary>Mean traversals per second across iterations</summary>
        [JsonPropertyName("averageTps")]
        public Double AverageTps
        {
            get; set;
        }

        /// <summary>Median traversals per second across iterations</summary>
        [JsonPropertyName("medianTps")]
        public Double MedianTps
        {
            get; set;
        }

        /// <summary>Standard deviation of the per-iteration TPS samples</summary>
        [JsonPropertyName("standardDeviationTps")]
        public Double StandardDeviationTps
        {
            get; set;
        }
    }
}
