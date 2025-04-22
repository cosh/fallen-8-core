// MIT License
//
// Graph.cs
//
// Copyright (c) 2021 Henning Rauch
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

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using NoSQL.GraphDB.App.Controllers.Model;

namespace NoSQL.GraphDB.Core.App.Controllers.Model
{
    /// <summary>
    /// Represents an entire graph structure with vertices and edges
    /// </summary>
    /// <remarks>
    /// The primary response model for retrieving graph data through the API
    /// </remarks>
    /// <example>
    /// {
    ///   "vertices": [
    ///     {
    ///       "id": 1,
    ///       "label": "person",
    ///       "properties": {
    ///         "name": "John Doe",
    ///         "age": 30
    ///       }
    ///     },
    ///     {
    ///       "id": 2,
    ///       "label": "person",
    ///       "properties": {
    ///         "name": "Jane Smith",
    ///         "age": 28
    ///       }
    ///     }
    ///   ],
    ///   "edges": [
    ///     {
    ///       "id": 10,
    ///       "label": "friendship",
    ///       "sourceVertex": 1,
    ///       "targetVertex": 2,
    ///       "edgePropertyId": "knows",
    ///       "properties": {
    ///         "since": "2024-01-15T00:00:00"
    ///       }
    ///     }
    ///   ]
    /// }
    /// </example>
    public class Graph
    {
        /// <summary>
        /// Collection of all edges in the graph
        /// </summary>
        /// <remarks>
        /// Each edge connects two vertices and represents a relationship between them
        /// </remarks>
        [JsonPropertyName("edges")]
        public List<Edge> Edges
        {
            get;
            internal set;
        }

        /// <summary>
        /// Collection of all vertices in the graph
        /// </summary>
        /// <remarks>
        /// Vertices (nodes) are the fundamental entities in the graph
        /// </remarks>
        [JsonPropertyName("vertices")]
        public List<Vertex> Vertices
        {
            get;
            internal set;
        }
    }
}
