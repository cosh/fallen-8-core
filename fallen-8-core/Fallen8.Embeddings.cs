// MIT License
//
// Fallen8.Embeddings.cs
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
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.Core
{
    public sealed partial class Fallen8
    {
        #region bound vector index projection (feature element-embeddings)

        /// <summary>
        ///   Projects one committed embedding state into every BOUND vector index of that name
        ///   (feature element-embeddings): a projectable vector replaces the element's slot;
        ///   <c>null</c>, a non-vector value written through the raw property surface, or a
        ///   vector THIS index cannot rank (wrong dimension, non-finite, zero-norm under its
        ///   Cosine metric) PURGES the slot - unprojectable ≡ not a member, so the live
        ///   projection always matches what a load-rebuild from element state would produce
        ///   (a skip instead of a purge would pin the element's PREVIOUS vector and change
        ///   answers across a restart). Runs on the single writer thread AFTER the mutation
        ///   committed; best-effort per index like the removal purge - a faulty index is
        ///   logged, never fails the commit. The typed REST write endpoints answer 400 up
        ///   front for conflicting writes; this path covers the engine/library API and raw
        ///   property writes, where the mutation is already committed element state.
        /// </summary>
        private void ProjectEmbeddingToBoundIndices(AGraphElementModel element, String embeddingName, Single[] vectorOrNull)
        {
            var indices = IndexFactory?.GetIndicesSnapshot();
            if (indices == null || indices.Count == 0)
            {
                return;
            }

            foreach (var index in indices)
            {
                if (!(index is Index.Vector.IVectorIndex vectorIndex) ||
                    !String.Equals(vectorIndex.EmbeddingName, embeddingName, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    var projectable = vectorOrNull != null &&
                        vectorOrNull.Length == vectorIndex.Dimension &&
                        !Index.Vector.VectorIndex.HasNonFiniteComponent(vectorOrNull) &&
                        !(vectorIndex.Metric == Index.Vector.VectorDistanceMetric.Cosine &&
                          Index.Vector.VectorIndex.IsZeroNorm(vectorOrNull));

                    if (projectable)
                    {
                        index.AddOrUpdate(vectorOrNull, element);
                    }
                    else
                    {
                        index.RemoveValue(element);

                        if (vectorOrNull != null)
                        {
                            _logger.LogWarning(
                                "Embedding '{EmbeddingName}' of element {GraphElementId} cannot rank in a bound vector index (wrong dimension, non-finite, or zero-norm under Cosine); the element was purged from that projection.",
                                embeddingName, element.Id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to project embedding '{EmbeddingName}' of element {GraphElementId} into a bound vector index.",
                        embeddingName, element?.Id);
                }
            }
        }

        /// <summary>
        ///   Property-surface hook: when <paramref name="propertyId" /> is a reserved embedding
        ///   key, projects the written value (a non-<c>float[]</c> value purges - the element no
        ///   longer carries a usable embedding of that name). Called after a committed
        ///   set/remove/restore on the writer thread; a plain property key is a two-comparison
        ///   no-op.
        /// </summary>
        private void ProjectEmbeddingPropertyWrite(AGraphElementModel element, String propertyId, Object valueOrNull)
        {
            if (!AGraphElementModel.TryGetEmbeddingName(propertyId, out var embeddingName))
            {
                return;
            }

            ProjectEmbeddingToBoundIndices(element, embeddingName, valueOrNull as Single[]);
        }

        /// <summary>
        ///   Element-creation hook: projects every embedding the new element was created with
        ///   (the bulk-import path creates elements WITH their embedding properties). The
        ///   store-key scan is the cheap guard; the index snapshot is only fetched on a hit.
        /// </summary>
        private void ProjectAllEmbeddingsOf(AGraphElementModel element)
        {
            var store = element.GetPropertyStoreForSerialization();
            if (store == null)
            {
                return;
            }

            foreach (var property in store)
            {
                if (AGraphElementModel.TryGetEmbeddingName(property.Key, out var embeddingName))
                {
                    ProjectEmbeddingToBoundIndices(element, embeddingName, property.Value as Single[]);
                }
            }
        }

        #endregion
    }
}
