// MIT License
//
// IndexCapabilities.cs
//
// Copyright (c) 2011-2026 Henning Rauch
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
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Fulltext;
using NoSQL.GraphDB.Core.Index.Range;
using NoSQL.GraphDB.Core.Index.Spatial;
using NoSQL.GraphDB.Core.Index.Vector;

namespace NoSQL.GraphDB.App.Helper
{
    /// <summary>
    ///   Derives the query families an index answers for the <c>/status</c> inventory. The
    ///   <c>equality</c> capability is read straight from <see cref="IIndex.SupportsPointEqualityLookup"/>
    ///   (the single engine-owned home for "can this index be looked up by an exact key", so vector
    ///   and spatial omit it); the remaining tags are the index's own family interface. The contract
    ///   is documented on
    ///   <see cref="NoSQL.GraphDB.App.Controllers.Model.IndexDescriptionREST.Capabilities"/>.
    /// </summary>
    public static class IndexCapabilities
    {
        public const String Equality = "equality";
        public const String Range = "range";
        public const String Fulltext = "fulltext";
        public const String Spatial = "spatial";
        public const String Vector = "vector";

        public static List<String> Describe(IIndex index)
        {
            if (index == null)
            {
                return new List<String>();
            }

            var capabilities = new List<String>();
            if (index.SupportsPointEqualityLookup)
            {
                capabilities.Add(Equality);
            }
            if (index is IRangeIndex)
            {
                capabilities.Add(Range);
            }
            if (index is IFulltextIndex)
            {
                capabilities.Add(Fulltext);
            }
            if (index is IVectorIndex)
            {
                capabilities.Add(Vector);
            }
            if (index is ISpatialIndex)
            {
                capabilities.Add(Spatial);
            }

            return capabilities;
        }
    }
}
