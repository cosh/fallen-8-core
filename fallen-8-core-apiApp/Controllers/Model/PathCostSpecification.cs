﻿// MIT License
//
// PathCostSpecification.cs
//
// Copyright (c) 2022 Henning Rauch
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

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The path cost specification
    /// </summary>
    public sealed class PathCostSpecification : IEquatable<PathCostSpecification>
    {
        /// <summary>
        /// The vertex cost function
        /// </summary>
        public String Vertex
        {
            get; set;
        }

        /// <summary>
        /// The edge cost function
        /// </summary>
        public String Edge
        {
            get; set;
        }

        public override Boolean Equals(Object obj)
        {
            return Equals(obj as PathCostSpecification);
        }

        public Boolean Equals(PathCostSpecification other)
        {
            return other != null &&
                   Vertex == other.Vertex &&
                   Edge == other.Edge;
        }

        public override Int32 GetHashCode()
        {
            return HashCode.Combine(Vertex, Edge);
        }
    }
}
