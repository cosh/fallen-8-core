// MIT License
//
// BoundIndexContract.cs
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
using NoSQL.GraphDB.App.Embedding;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Index.Vector;

namespace NoSQL.GraphDB.App.Helper
{
    /// <summary>
    ///   The embedding-provider consistency contract against BOUND vector indices (feature
    ///   embedding-provider FR-8): provider dimension must equal a bound index's, and a
    ///   declared index model identity must match the provider's stamp. One home; the
    ///   embedding endpoints and the ingestion pipeline both check through here.
    /// </summary>
    internal static class BoundIndexContract
    {
        /// <summary>Returns the conflict message for the first violated bound index of
        /// <paramref name="embeddingName"/>, or null when consistent.</summary>
        internal static String FindConflict(IFallen8 fallen8, String embeddingName, Fallen8EmbeddingProvider provider)
        {
            foreach (var namedIndex in fallen8.IndexFactory.GetNamedIndicesSnapshot())
            {
                if (!(namedIndex.Value is IVectorIndex vectorIndex) ||
                    !String.Equals(vectorIndex.EmbeddingName, embeddingName, StringComparison.Ordinal))
                {
                    continue;
                }

                var conflict = FindConflictForIndex(vectorIndex, namedIndex.Key, provider, embeddingName);
                if (conflict != null)
                {
                    return conflict;
                }
            }

            return null;
        }

        /// <summary>The single-index FR-8 check (dimension always; model identity when the index
        /// declares one), returning the conflict message or null when consistent. The one home the
        /// by-name <see cref="FindConflict"/> scan, the <c>/embedding/search</c> endpoint and the
        /// fused document search all resolve through, so the contract cannot drift between the write
        /// and query paths. <paramref name="embeddingName"/> is non-null only for the by-name scan,
        /// which adds the "bound to embedding" clause to the dimension message.</summary>
        internal static String FindConflictForIndex(IVectorIndex index, String indexId,
            Fallen8EmbeddingProvider provider, String embeddingName = null)
        {
            if (index.Dimension != provider.Identity.Dimension)
            {
                return embeddingName == null
                    ? String.Format(
                        "The provider produces dimension {0}, but index '{1}' requires {2}.",
                        provider.Identity.Dimension, indexId, index.Dimension)
                    : String.Format(
                        "The provider produces dimension {0}, but index '{1}' bound to embedding '{2}' requires {3}.",
                        provider.Identity.Dimension, indexId, embeddingName, index.Dimension);
            }

            if (index.Model != null &&
                !String.Equals(index.Model, provider.Identity.Stamp, StringComparison.Ordinal))
            {
                return String.Format(
                    "Index '{0}' declares model identity '{1}', but the active provider is '{2}'.",
                    indexId, index.Model, provider.Identity.Stamp);
            }

            return null;
        }
    }
}
