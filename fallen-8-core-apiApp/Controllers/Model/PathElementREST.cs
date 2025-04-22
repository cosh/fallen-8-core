// MIT License
//
// PathElementREST.cs
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
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using NoSQL.GraphDB.Core.Algorithms;
using NoSQL.GraphDB.Core.Algorithms.Path;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    /// Represents a single segment of a path between vertices in the graph
    /// </summary>
    /// <remarks>
    /// Each path element consists of a source vertex, target vertex, and the edge connecting them
    /// </remarks>
    /// <example>
    /// {
    ///   "sourceVertexId": 1,
    ///   "targetVertexId": 2,
    ///   "edgeId": 10,
    ///   "edgePropertyId": "knows",
    ///   "direction": "OutgoingEdge",
    ///   "weight": 1.5
    /// }
    /// </example>
    public sealed class PathElementREST
    {
        #region data

        /// <summary>
        /// The identifier of the vertex where this path segment begins
        /// </summary>
        /// <example>1</example>
        [Required]
        [DefaultValue(1)]
        [JsonPropertyName("sourceVertexId")]
        public Int32 SourceVertexId
        {
            get; set;
        }

        /// <summary>
        /// The identifier of the vertex where this path segment ends
        /// </summary>
        /// <example>2</example>
        [Required]
        [DefaultValue(2)]
        [JsonPropertyName("targetVertexId")]
        public Int32 TargetVertexId
        {
            get; set;
        }

        /// <summary>
        /// The identifier of the edge connecting the source and target vertices
        /// </summary>
        /// <example>10</example>
        [Required]
        [DefaultValue(10)]
        [JsonPropertyName("edgeId")]
        public Int32 EdgeId
        {
            get; set;
        }

        /// <summary>
        /// The property identifier/type of the edge in this path segment
        /// </summary>
        /// <example>knows</example>
        [Required]
        [DefaultValue("knows")]
        [JsonPropertyName("edgePropertyId")]
        public String EdgePropertyId
        {
            get; set;
        }

        /// <summary>
        /// The direction in which the edge is traversed (OutgoingEdge, IncomingEdge, or UndirectedEdge)
        /// </summary>
        /// <example>OutgoingEdge</example>
        [Required]
        [DefaultValue(Direction.OutgoingEdge)]
        [JsonPropertyName("direction")]
        public Direction Direction
        {
            get; set;
        }

        /// <summary>
        /// The cost/weight associated with traversing this path segment
        /// </summary>
        /// <remarks>
        /// Used for calculating the total cost of the path
        /// </remarks>
        /// <example>1.5</example>
        [Required]
        [DefaultValue(1.5)]
        [JsonPropertyName("weight")]
        public double Weight
        {
            get; set;
        }

        #endregion

        #region constructor

        /// <summary>
        /// Creates a new PathElementREST instance from an internal PathElement
        /// </summary>
        /// <param name="toBeTransferredResult">The internal path element to convert</param>
        public PathElementREST(PathElement toBeTransferredResult)
        {
            SourceVertexId = toBeTransferredResult.SourceVertex.Id;
            TargetVertexId = toBeTransferredResult.TargetVertex.Id;
            EdgeId = (int)toBeTransferredResult.Edge.Id;
            EdgePropertyId = toBeTransferredResult.EdgePropertyId;
            Direction = toBeTransferredResult.Direction;
            Weight = toBeTransferredResult.Weight;
        }

        #endregion
    }
}
