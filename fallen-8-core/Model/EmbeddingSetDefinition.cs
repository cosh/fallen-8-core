// MIT License
//
// EmbeddingSetDefinition.cs
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

namespace NoSQL.GraphDB.Core.Model
{
    /// <summary>
    ///   One embedding write in a <see cref="Transaction.SetEmbeddingsTransaction" /> batch
    ///   (feature element-embeddings): sets (replace semantics) or removes (<c>null</c>
    ///   <see cref="Vector" />) the named embedding of one element.
    /// </summary>
    public class EmbeddingSetDefinition
    {
        /// <summary>
        ///   The target graph element id.
        /// </summary>
        public Int32 GraphElementId
        {
            get; set;
        }

        /// <summary>
        ///   The embedding name (<see cref="AGraphElementModel.IsValidEmbeddingName" />).
        /// </summary>
        public String Name
        {
            get; set;
        } = AGraphElementModel.DefaultEmbeddingName;

        /// <summary>
        ///   The embedding vector to store, or <c>null</c> to remove the named embedding.
        /// </summary>
        public Single[] Vector
        {
            get; set;
        }

        /// <summary>
        ///   The model-identity stamp to store NEXT TO the vector (feature embedding-provider),
        ///   or <c>null</c>. Every embedding write replaces the stamp state - a provider write
        ///   carries its stamp, a bring-your-own-vector write clears any stale one - so the
        ///   stamp always reflects the LAST write and can never lie about provenance.
        /// </summary>
        public String ModelStamp
        {
            get; set;
        }
    }
}
