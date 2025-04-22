// MIT License
//
// EdgeSpecification.cs
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

using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Model;
using System;
using System.Linq;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    /// Represents an edge (relationship) between two vertices in the graph
    /// </summary>
    /// <remarks>
    /// Edges define the connections and relationships between vertices in the graph
    /// </remarks>
    /// <example>
    /// {
    ///   "id": 10,
    ///   "creationDate": 1713862800,
    ///   "modificationDate": 1713862800,
    ///   "label": "friendship",
    ///   "properties": {
    ///     "since": "2024-01-15T00:00:00",
    ///     "strength": 0.85
    ///   },
    ///   "sourceVertex": 1,
    ///   "targetVertex": 2
    /// }
    /// </example>
    public class Edge : AGraphElement
    {
        /// <summary>
        /// The identifier of the vertex where this edge ends
        /// </summary>
        /// <remarks>
        /// Represents the destination of the relationship
        /// </remarks>
        /// <example>2</example>
        [Required]
        [JsonPropertyName("targetVertex")]
        public int TargetVertex
        {
            get; set;
        }

        /// <summary>
        /// The identifier of the vertex where this edge starts
        /// </summary>
        /// <remarks>
        /// Represents the origin of the relationship
        /// </remarks>
        /// <example>1</example>
        [Required]
        [JsonPropertyName("sourceVertex")]
        public int SourceVertex
        {
            get; set;
        }

        /// <summary>
        /// Creates a new Edge instance from an internal EdgeModel
        /// </summary>
        /// <param name="edge">The internal edge model to convert</param>
        public Edge(EdgeModel edge) : base(edge.Id, edge.CreationDate, edge.ModificationDate, edge.Label, edge.GetAllProperties())
        {
            TargetVertex = edge.TargetVertex.Id;
            SourceVertex = edge.SourceVertex.Id;
        }
    }
}
