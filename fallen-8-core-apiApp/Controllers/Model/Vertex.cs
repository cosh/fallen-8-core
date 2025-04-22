// MIT License
//
// VertexSpecification.cs
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
    /// Represents a vertex (node) in the graph with its properties and connected edges
    /// </summary>
    /// <remarks>
    /// Vertices are the fundamental entities in the graph structure
    /// </remarks>
    /// <example>
    /// {
    ///   "id": 1,
    ///   "creationDate": 1713862800,
    ///   "modificationDate": 1713862800,
    ///   "label": "person",
    ///   "properties": {
    ///     "name": "John Doe",
    ///     "age": 30
    ///   },
    ///   "outEdges": {
    ///     "knows": [10, 15]
    ///   },
    ///   "inEdges": {
    ///     "knows": [5]
    ///   }
    /// }
    /// </example>
    public class Vertex : AGraphElement
    {
        /// <summary>
        /// Dictionary of outgoing edges grouped by edge property type
        /// </summary>
        /// <remarks>
        /// Keys are edge property types, values are lists of edge IDs
        /// </remarks>
        [JsonPropertyName("outEdges")]
        public Dictionary<String, List<int>> OutEdges
        {
            get; set;
        }

        /// <summary>
        /// Dictionary of incoming edges grouped by edge property type
        /// </summary>
        /// <remarks>
        /// Keys are edge property types, values are lists of edge IDs
        /// </remarks>
        [JsonPropertyName("inEdges")]
        public Dictionary<String, List<int>> InEdges
        {
            get; set;
        }

        /// <summary>
        /// Creates a new Vertex instance from a VertexModel
        /// </summary>
        /// <param name="vertex">The internal vertex model to convert</param>
        public Vertex(VertexModel vertex) : base(vertex.Id, vertex.CreationDate, vertex.ModificationDate, vertex.Label, vertex.GetAllProperties())
        {
            if (vertex.InEdges != null)
            {
                InEdges = vertex.InEdges.ToDictionary(_ => _.Key, _ => _.Value.Select(__ => __.Id).ToList());
            }

            if (vertex.OutEdges != null)
            {
                OutEdges = vertex.OutEdges.ToDictionary(_ => _.Key, _ => _.Value.Select(__ => __.Id).ToList());
            }
        }
    }
}
