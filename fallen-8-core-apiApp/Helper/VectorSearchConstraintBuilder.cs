// MIT License
//
// VectorSearchConstraintBuilder.cs
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
using NoSQL.GraphDB.Core.Index.Vector;

namespace NoSQL.GraphDB.App.Helper
{
    /// <summary>
    ///   The single home for turning a vector-scan request's <c>kind</c>/<c>label</c> pair into a
    ///   <see cref="VectorSearchConstraint"/>, shared by the vector-index scan and the embedding
    ///   semantic-search endpoints. When neither is set there is no constraint (null); an unknown
    ///   <c>kind</c> is rejected with the same message both endpoints used before.
    /// </summary>
    public static class VectorSearchConstraintBuilder
    {
        public static Boolean TryBuild(String kind, String label, out VectorSearchConstraint constraint, out String error)
        {
            constraint = null;
            error = null;

            if (String.IsNullOrEmpty(kind) && label == null)
            {
                return true;
            }

            var built = new VectorSearchConstraint { Label = label };
            switch (kind)
            {
                case null:
                case "":
                case "any":
                    built.Kind = VectorSearchElementKind.Any;
                    break;
                case "vertex":
                    built.Kind = VectorSearchElementKind.Vertex;
                    break;
                case "edge":
                    built.Kind = VectorSearchElementKind.Edge;
                    break;
                default:
                    error = String.Format("'{0}' is not a valid kind. Expected vertex, edge or any.", kind);
                    return false;
            }

            constraint = built;
            return true;
        }
    }
}
