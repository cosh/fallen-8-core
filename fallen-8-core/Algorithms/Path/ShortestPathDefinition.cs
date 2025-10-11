// MIT License
//
// ShortestPathDefinition.cs
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

namespace NoSQL.GraphDB.Core.Algorithms.Path
{
    /// <summary>
    /// Defines the parameters for shortest path calculation
    /// </summary>
    public sealed class ShortestPathDefinition
    {
        /// <summary>
        /// Gets or sets the source vertex identifier
        /// </summary>
        public Int32 SourceVertexId
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the destination vertex identifier
        /// </summary>
        public Int32 DestinationVertexId
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the maximum depth
        /// </summary>
        public Int32 MaxDepth { get; set; } = 1;

        /// <summary>
        /// Gets or sets the maximum path weight
        /// </summary>
        public Double MaxPathWeight { get; set; } = Double.MaxValue;

        /// <summary>
        /// Gets or sets the maximum number of results
        /// </summary>
        public Int32 MaxResults { get; set; } = 1;

        /// <summary>
        /// Gets or sets the edge property filter
        /// </summary>
        public Delegates.EdgePropertyFilter EdgePropertyFilter
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the vertex filter
        /// </summary>
        public Delegates.VertexFilter VertexFilter
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the edge filter
        /// </summary>
        public Delegates.EdgeFilter EdgeFilter
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the edge cost delegate
        /// </summary>
        public Delegates.EdgeCost EdgeCost
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the vertex cost delegate
        /// </summary>
        public Delegates.VertexCost VertexCost
        {
            get; set;
        }
    }
}
