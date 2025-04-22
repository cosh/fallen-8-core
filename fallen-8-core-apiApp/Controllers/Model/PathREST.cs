// MIT License
//
// PathREST.cs
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

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using NoSQL.GraphDB.Core.Algorithms.Path;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    /// Represents a path between two vertices in the graph
    /// </summary>
    /// <remarks>
    /// A path consists of an ordered sequence of vertices and connecting edges with a total weight
    /// </remarks>
    /// <example>
    /// {
    ///   "pathElements": [
    ///     {
    ///       "vertexId": 1,
    ///       "edgeId": 10,
    ///       "direction": "Outgoing",
    ///       "weight": 1.0
    ///     },
    ///     {
    ///       "vertexId": 2,
    ///       "edgeId": 15,
    ///       "direction": "Outgoing",
    ///       "weight": 2.5
    ///     },
    ///     {
    ///       "vertexId": 5,
    ///       "edgeId": null,
    ///       "direction": null,
    ///       "weight": 0.0
    ///     }
    ///   ],
    ///   "totalWeight": 3.5
    /// }
    /// </example>
    public sealed class PathREST
    {
        #region data

        /// <summary>
        /// The ordered sequence of path elements (vertices and edges) that form the path
        /// </summary>
        /// <remarks>
        /// The first element represents the starting vertex, the last element represents the destination vertex
        /// </remarks>
        [Required]
        [JsonPropertyName("pathElements")]
        public List<PathElementREST> PathElements
        {
            get; set;
        }

        /// <summary>
        /// The aggregate weight/cost of the entire path
        /// </summary>
        /// <remarks>
        /// Calculated based on the weights of individual vertices and edges in the path
        /// </remarks>
        /// <example>3.5</example>
        [Required]
        [JsonPropertyName("totalWeight")]
        public double TotalWeight
        {
            get; set;
        }

        #endregion

        #region constructor

        /// <summary>
        /// Creates a new PathREST instance from an internal Path object
        /// </summary>
        /// <param name="toBeTransferredResult">The internal path object to convert</param>
        public PathREST(Path toBeTransferredResult)
        {
            var toBeTransferredPathElements = toBeTransferredResult.GetPathElements();

            PathElements = new List<PathElementREST>(toBeTransferredPathElements.Count);

            for (var i = 0; i < toBeTransferredPathElements.Count; i++)
            {
                PathElements.Add(new PathElementREST(toBeTransferredPathElements[i]));
            }

            TotalWeight = toBeTransferredResult.Weight;
        }

        #endregion
    }
}
