// MIT License
//
// VectorIndex.cs
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
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Error;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Serializer;

namespace NoSQL.GraphDB.Core.Index.Vector
{
    /// <summary>
    ///   Exact brute-force kNN over a structure-of-arrays vector slab (feature vector-index):
    ///   one flat <c>float[]</c> holding slot i at <c>[i*d, (i+1)*d)</c>, a parallel element
    ///   array, and a REFERENCE-keyed reverse slot map (survives a Trim id-renumber) that makes
    ///   <see cref="RemoveValue"/> and replace O(1). Removal swaps the last slot into the hole,
    ///   so the scan range stays dense. Scoring is SIMD via <c>TensorPrimitives</c>
    ///   (<c>CosineSimilarity</c>/<c>Dot</c>/<c>Distance</c>) - exact, no recall parameter;
    ///   at self-hosted scale the scan is memory-bandwidth bound (~4*d bytes per candidate).
    ///
    ///   <para>Memory per element is roughly <c>4*d</c> bytes for the vector plus ~64 bytes of
    ///   bookkeeping; the vectors dominate (d=768: ~3.1 kB/element, 1M elements ~3.1 GB).</para>
    /// </summary>
    public sealed class VectorIndex : AThreadSafeElement, IVectorIndex
    {
        #region Data

        /// <summary>The inclusive upper bound on the configured dimension. A constant, not
        /// config: one operator, one box; revisit when a real model beyond 4096 dims shows up.</summary>
        public const Int32 MaxDimension = 4096;

        /// <summary>The inclusive upper bound on k.</summary>
        public const Int32 MaxK = 1024;

        /// <summary>The flat vector slab; slot i occupies [i*Dimension, (i+1)*Dimension).</summary>
        private float[] _vectors;

        /// <summary>Slot -&gt; element, parallel to the slab.</summary>
        private AGraphElementModel[] _elements;

        /// <summary>Element -&gt; slot, keyed by REFERENCE identity (Trim-safe), O(1) removal/replace.</summary>
        private Dictionary<AGraphElementModel, Int32> _slotByElement;

        /// <summary>The number of occupied slots (the dense scan range).</summary>
        private Int32 _count;

        private ILogger<VectorIndex> _logger;

        #endregion

        /// <summary>The fixed vector dimension, set at creation.</summary>
        public Int32 Dimension
        {
            get; private set;
        }

        /// <summary>The metric, set at creation.</summary>
        public VectorDistanceMetric Metric
        {
            get; private set;
        }

        /// <summary>The bound embedding name, or null for an unbound (raw) index - the contract
        /// lives on <see cref="IVectorIndex.EmbeddingName" />.</summary>
        public String EmbeddingName
        {
            get; private set;
        }

        /// <summary>The declared model identity, or null (<see cref="IVectorIndex.Model" />).</summary>
        public String Model
        {
            get; private set;
        }

        #region IPlugin implementation

        public void Initialize(IFallen8 fallen8, IDictionary<String, Object> parameter)
        {
            _logger = fallen8.LoggerFactory.CreateLogger<VectorIndex>();

            if (parameter == null || !parameter.TryGetValue("dimension", out var dimensionValue))
            {
                throw new ArgumentException("A vector index requires the 'dimension' plugin option.");
            }

            Int32 dimension;
            try
            {
                dimension = Convert.ToInt32(dimensionValue, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
            {
                throw new ArgumentException(String.Format("'{0}' is not a valid vector dimension.", dimensionValue));
            }

            if (dimension < 1 || dimension > MaxDimension)
            {
                throw new ArgumentException(String.Format(
                    "The vector dimension must be within [1, {0}] (got {1}).", MaxDimension, dimension));
            }

            var metric = VectorDistanceMetric.Cosine;
            if (parameter.TryGetValue("metric", out var metricValue) && metricValue != null)
            {
                switch (metricValue.ToString())
                {
                    case "Cosine": metric = VectorDistanceMetric.Cosine; break;
                    case "DotProduct": metric = VectorDistanceMetric.DotProduct; break;
                    case "L2": metric = VectorDistanceMetric.L2; break;
                    default:
                        throw new ArgumentException(String.Format(
                            "'{0}' is not a valid vector metric. Expected Cosine, DotProduct or L2.", metricValue));
                }
            }

            String embeddingName = null;
            if (parameter.TryGetValue("embeddingName", out var embeddingNameValue) && embeddingNameValue != null)
            {
                embeddingName = embeddingNameValue.ToString();
                if (!AGraphElementModel.IsValidEmbeddingName(embeddingName))
                {
                    throw new ArgumentException(String.Format(
                        "'{0}' is not a valid embedding name to bind a vector index to.", embeddingName));
                }
            }

            String model = null;
            if (parameter.TryGetValue("model", out var modelValue) && modelValue != null)
            {
                model = modelValue.ToString();
            }

            Dimension = dimension;
            Metric = metric;
            EmbeddingName = embeddingName;
            Model = model;
            _vectors = new float[dimension * 16];
            _elements = new AGraphElementModel[16];
            _slotByElement = new Dictionary<AGraphElementModel, Int32>();
            _count = 0;

            // A BOUND index created over existing data materializes its projection immediately
            // (membership = every live element carrying the named embedding). An embedding
            // committed between this scan and the factory's registration is picked up on its
            // next write or the next load-rebuild - the same create-then-populate window every
            // index family has today.
            if (EmbeddingName != null && fallen8 != null)
            {
                RebuildProjection(fallen8);
            }
        }

        /// <summary>
        ///   Rebuilds the bound projection from element state: one pass over the live elements,
        ///   inserting every valid embedding of the bound name. Invalid embeddings (wrong
        ///   dimension, non-finite, zero-norm under Cosine - reachable via raw property writes)
        ///   are skipped and summarized in one log line, the family's silent-skip contract.
        ///   Lock-free by design: callers are Initialize (unpublished index) and Load (write
        ///   lock held).
        /// </summary>
        private void RebuildProjection(IFallen8 fallen8)
        {
            var embeddingPropertyId = AGraphElementModel.GetEmbeddingPropertyId(EmbeddingName);
            var skipped = 0;

            foreach (var element in fallen8.GetAllGraphElements())
            {
                if (!element.TryGetEmbeddingByPropertyId(out var vector, embeddingPropertyId))
                {
                    continue;
                }

                if (vector.Length != Dimension || HasNonFiniteComponent(vector) ||
                    (Metric == VectorDistanceMetric.Cosine && IsZeroNorm(vector)))
                {
                    skipped++;
                    continue;
                }

                AddOrUpdateCore(vector.ToArray(), element);
            }

            if (skipped > 0)
            {
                _logger?.LogWarning(
                    "Vector index projection rebuild for embedding '{EmbeddingName}' skipped {Skipped} element(s) with an invalid vector (wrong dimension, non-finite, or zero-norm under Cosine).",
                    EmbeddingName, skipped);
            }
        }

        public String PluginName => "VectorIndex";

        public Type PluginCategory => typeof(IIndex);

        public String Description => "Exact SIMD brute-force k-nearest-neighbour index over float[] embedding vectors";

        public String Manufacturer => "Henning Rauch";

        #endregion

        #region IDisposable implementation

        public void Dispose()
        {
            _vectors = null;
            _elements = null;
            _slotByElement?.Clear();
            _slotByElement = null;
            _count = 0;
        }

        #endregion

        #region IIndex implementation

        public Boolean CanPersist => true;

        public Int32 CountOfKeys()
        {
            if (ReadResource())
            {
                try
                {
                    return _count;
                }
                finally
                {
                    FinishReadResource();
                }
            }

            throw new CollisionException();
        }

        public Int32 CountOfValues()
        {
            return CountOfKeys();
        }

        public void AddOrUpdate(Object key, AGraphElementModel graphElement)
        {
            if (graphElement == null)
            {
                return;
            }

            // The family's silent-skip contract for the generic engine API; the typed REST
            // endpoint validates first and answers 400, so over REST this is never silent.
            if (!(key is float[] vector) || vector.Length != Dimension)
            {
                _logger?.LogWarning(
                    "VectorIndex.AddOrUpdate for element {ElementId} skipped: the key must be a float[{Dimension}].",
                    graphElement.Id, Dimension);
                return;
            }

            if (HasNonFiniteComponent(vector))
            {
                _logger?.LogWarning(
                    "VectorIndex.AddOrUpdate for element {ElementId} skipped: the vector contains NaN or Infinity.",
                    graphElement.Id);
                return;
            }

            if (Metric == VectorDistanceMetric.Cosine && IsZeroNorm(vector))
            {
                _logger?.LogWarning(
                    "VectorIndex.AddOrUpdate for element {ElementId} skipped: a zero-norm vector cannot rank under Cosine.",
                    graphElement.Id);
                return;
            }

            if (WriteResource())
            {
                try
                {
                    AddOrUpdateCore(vector, graphElement);
                }
                finally
                {
                    FinishWriteResource();
                }

                return;
            }

            throw new CollisionException();
        }

        /// <summary>Insert/replace without locking - the caller holds the write lock or has
        /// exclusive access (Initialize/Load rebuild of an unpublished or lock-held index).</summary>
        private void AddOrUpdateCore(float[] vector, AGraphElementModel graphElement)
        {
            // Close the purge/re-add race: an element tombstoned by a committed removal
            // must never (re-)enter a slot, or it would be pinned forever (the engine's
            // write-end purge runs once per removal and will not come back for it).
            if (graphElement._removed)
            {
                _logger?.LogWarning(
                    "VectorIndex.AddOrUpdate for element {ElementId} skipped: the element is removed.",
                    graphElement.Id);
                return;
            }

            if (_slotByElement.TryGetValue(graphElement, out var existingSlot))
            {
                // One vector per element: add-again replaces in place.
                Array.Copy(vector, 0, _vectors, existingSlot * Dimension, Dimension);
                return;
            }

            if (_count == _elements.Length)
            {
                // Grow the SLAB first: it is the large, failure-prone allocation, and a
                // failed resize must leave the slots/slab invariant intact (retryable).
                var newCapacity = _elements.Length * 2;
                var newSlabLength = (Int64)newCapacity * Dimension;
                if (newSlabLength > Int32.MaxValue)
                {
                    throw new InvalidOperationException(String.Format(
                        "The vector slab cannot grow beyond {0} slots at dimension {1}.",
                        _elements.Length, Dimension));
                }
                Array.Resize(ref _vectors, (Int32)newSlabLength);
                Array.Resize(ref _elements, newCapacity);
            }

            Array.Copy(vector, 0, _vectors, _count * Dimension, Dimension);
            _elements[_count] = graphElement;
            _slotByElement[graphElement] = _count;
            _count++;
        }

        public void RemoveValue(AGraphElementModel graphElement)
        {
            if (graphElement == null)
            {
                return;
            }

            if (WriteResource())
            {
                try
                {
                    RemoveSlotOf(graphElement);
                }
                finally
                {
                    FinishWriteResource();
                }

                return;
            }

            throw new CollisionException();
        }

        /// <summary>O(1) swap-last removal via the slot map. Caller holds the write lock.</summary>
        private void RemoveSlotOf(AGraphElementModel graphElement)
        {
            if (!_slotByElement.TryGetValue(graphElement, out var slot))
            {
                return;
            }

            var last = _count - 1;
            if (slot != last)
            {
                Array.Copy(_vectors, last * Dimension, _vectors, slot * Dimension, Dimension);
                _elements[slot] = _elements[last];
                _slotByElement[_elements[slot]] = slot;
            }

            _elements[last] = null;
            _slotByElement.Remove(graphElement);
            _count = last;
        }

        public Boolean TryRemoveKey(Object key)
        {
            if (!(key is float[] vector) || vector.Length != Dimension)
            {
                return false;
            }

            if (WriteResource())
            {
                try
                {
                    // Diagnostic-grade linear scan (like the endpoint that calls it): remove
                    // every element whose stored vector is bitwise-equal to the key.
                    var victims = new List<AGraphElementModel>();
                    for (var slot = 0; slot < _count; slot++)
                    {
                        if (SlotEquals(slot, vector))
                        {
                            victims.Add(_elements[slot]);
                        }
                    }

                    foreach (var victim in victims)
                    {
                        RemoveSlotOf(victim);
                    }

                    return victims.Count > 0;
                }
                finally
                {
                    FinishWriteResource();
                }
            }

            throw new CollisionException();
        }

        public void Wipe()
        {
            if (WriteResource())
            {
                try
                {
                    Array.Clear(_vectors, 0, _count * Dimension);
                    Array.Clear(_elements, 0, _count);
                    _slotByElement.Clear();
                    _count = 0;
                }
                finally
                {
                    FinishWriteResource();
                }

                return;
            }

            throw new CollisionException();
        }

        /// <summary>Diagnostic-only per-slot key copies - O(n*d), documented as such.</summary>
        public IEnumerable<Object> GetKeys()
        {
            if (ReadResource())
            {
                try
                {
                    var keys = new List<Object>(_count);
                    for (var slot = 0; slot < _count; slot++)
                    {
                        keys.Add(CopySlot(slot));
                    }
                    return keys;
                }
                finally
                {
                    FinishReadResource();
                }
            }

            throw new CollisionException();
        }

        /// <summary>Diagnostic-only per-slot (vector copy, single-element bucket) pairs.</summary>
        public IEnumerable<KeyValuePair<Object, ImmutableList<AGraphElementModel>>> GetKeyValues()
        {
            if (ReadResource())
            {
                try
                {
                    var pairs = new List<KeyValuePair<Object, ImmutableList<AGraphElementModel>>>(_count);
                    for (var slot = 0; slot < _count; slot++)
                    {
                        pairs.Add(new KeyValuePair<Object, ImmutableList<AGraphElementModel>>(
                            CopySlot(slot), ImmutableList.Create(_elements[slot])));
                    }
                    return pairs;
                }
                finally
                {
                    FinishReadResource();
                }
            }

            throw new CollisionException();
        }

        /// <summary>Exact-match (bitwise-equal vector) linear scan.</summary>
        public Boolean TryGetValue(out ImmutableList<AGraphElementModel> result, Object key)
        {
            result = null;

            if (!(key is float[] vector) || vector.Length != Dimension)
            {
                return false;
            }

            if (ReadResource())
            {
                try
                {
                    var matches = ImmutableList.CreateBuilder<AGraphElementModel>();
                    for (var slot = 0; slot < _count; slot++)
                    {
                        if (SlotEquals(slot, vector))
                        {
                            matches.Add(_elements[slot]);
                        }
                    }

                    if (matches.Count == 0)
                    {
                        return false;
                    }

                    result = matches.ToImmutable();
                    return true;
                }
                finally
                {
                    FinishReadResource();
                }
            }

            throw new CollisionException();
        }

        #endregion

        #region IVectorIndex implementation

        public Boolean TryNearestNeighbors(out VectorSearchResult result, ReadOnlySpan<Single> query,
            Int32 k, VectorSearchConstraint constraint = null)
        {
            result = null;

            if (query.Length != Dimension || k < 1 || k > MaxK)
            {
                return false;
            }

            if (HasNonFiniteComponent(query))
            {
                return false;
            }

            if (Metric == VectorDistanceMetric.Cosine && IsZeroNorm(query))
            {
                return false;
            }

            if (ReadResource())
            {
                try
                {
                    var higherIsBetter = Metric != VectorDistanceMetric.L2;

                    // Bounded top-k selection: a k-sized binary heap whose ROOT is the worst kept
                    // candidate under the total order (better score first; equal scores -> lower
                    // id first), so replacement is O(log k) and the result is deterministic.
                    var heapScores = new float[k];
                    var heapIds = new int[k];
                    var heapElements = new AGraphElementModel[k];
                    var heapCount = 0;

                    for (var slot = 0; slot < _count; slot++)
                    {
                        var element = _elements[slot];

                        // Defense-in-depth liveness: the engine's write-end purge should already
                        // have removed a deleted element; skipping tombstones mirrors the
                        // read-end FilterLive floor so the two ends can never disagree.
                        if (element._removed)
                        {
                            continue;
                        }

                        if (constraint != null && !MatchesConstraint(element, constraint))
                        {
                            continue;
                        }

                        var candidate = _vectors.AsSpan(slot * Dimension, Dimension);
                        // The shared primitive (feature element-embeddings): index kNN and
                        // in-traversal similarity are bit-identical because both are this call.
                        float score = VectorMath.Score(query, candidate, Metric);

                        // A non-finite score must never enter the heap: NaN is not totally
                        // ordered (it would freeze the root and degrade "top-k" to scan order),
                        // and neither NaN nor Infinity survives JSON serialization. Finite
                        // inputs can still get here - cosine squared-norm underflow yields 0/0,
                        // dot products can overflow to Infinity.
                        if (!Single.IsFinite(score))
                        {
                            continue;
                        }

                        if (heapCount < k)
                        {
                            HeapPush(heapScores, heapIds, heapElements, ref heapCount, score, element, higherIsBetter);
                        }
                        else if (IsBetter(score, element.Id, heapScores[0], heapIds[0], higherIsBetter))
                        {
                            HeapReplaceRoot(heapScores, heapIds, heapElements, heapCount, score, element, higherIsBetter);
                        }
                    }

                    // Drain the heap worst-first, filling the result best-first.
                    var entries = new VectorSearchEntry[heapCount];
                    for (var i = heapCount - 1; i >= 0; i--)
                    {
                        entries[i] = new VectorSearchEntry(heapElements[0], heapScores[0]);
                        heapCount--;
                        if (heapCount > 0)
                        {
                            heapScores[0] = heapScores[heapCount];
                            heapIds[0] = heapIds[heapCount];
                            heapElements[0] = heapElements[heapCount];
                            HeapSiftDown(heapScores, heapIds, heapElements, heapCount, higherIsBetter);
                        }
                    }

                    result = new VectorSearchResult(Metric, entries);
                    return true;
                }
                finally
                {
                    FinishReadResource();
                }
            }

            throw new CollisionException();
        }

        private static bool MatchesConstraint(AGraphElementModel element, VectorSearchConstraint constraint)
        {
            switch (constraint.Kind)
            {
                case VectorSearchElementKind.Vertex when !(element is VertexModel):
                    return false;
                case VectorSearchElementKind.Edge when !(element is EdgeModel):
                    return false;
            }

            if (constraint.Label != null &&
                !String.Equals(element.Label, constraint.Label, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        /// <summary>The selection total order: is candidate (scoreA, idA) a BETTER result than
        /// (scoreB, idB)? Better score wins; equal scores prefer the lower id (determinism).</summary>
        private static bool IsBetter(float scoreA, int idA, float scoreB, int idB, bool higherIsBetter)
        {
            if (scoreA != scoreB)
            {
                return higherIsBetter ? scoreA > scoreB : scoreA < scoreB;
            }

            return idA < idB;
        }

        private static void HeapPush(float[] scores, int[] ids, AGraphElementModel[] elements,
            ref int count, float score, AGraphElementModel element, bool higherIsBetter)
        {
            var i = count;
            scores[i] = score;
            ids[i] = element.Id;
            elements[i] = element;
            count++;

            // Sift up: the root holds the WORST kept candidate.
            while (i > 0)
            {
                var parent = (i - 1) >> 1;
                if (IsBetter(scores[parent], ids[parent], scores[i], ids[i], higherIsBetter))
                {
                    (scores[parent], scores[i]) = (scores[i], scores[parent]);
                    (ids[parent], ids[i]) = (ids[i], ids[parent]);
                    (elements[parent], elements[i]) = (elements[i], elements[parent]);
                    i = parent;
                }
                else
                {
                    break;
                }
            }
        }

        private static void HeapReplaceRoot(float[] scores, int[] ids, AGraphElementModel[] elements,
            int count, float score, AGraphElementModel element, bool higherIsBetter)
        {
            scores[0] = score;
            ids[0] = element.Id;
            elements[0] = element;
            HeapSiftDown(scores, ids, elements, count, higherIsBetter);
        }

        private static void HeapSiftDown(float[] scores, int[] ids, AGraphElementModel[] elements,
            int count, bool higherIsBetter)
        {
            var i = 0;
            while (true)
            {
                var worst = i;
                var left = 2 * i + 1;
                var right = left + 1;

                // The root must be the WORST: a child that is worse than the current worst bubbles up.
                if (left < count && IsBetter(scores[worst], ids[worst], scores[left], ids[left], higherIsBetter))
                {
                    worst = left;
                }
                if (right < count && IsBetter(scores[worst], ids[worst], scores[right], ids[right], higherIsBetter))
                {
                    worst = right;
                }

                if (worst == i)
                {
                    return;
                }

                (scores[worst], scores[i]) = (scores[i], scores[worst]);
                (ids[worst], ids[i]) = (ids[i], ids[worst]);
                (elements[worst], elements[i]) = (elements[i], elements[worst]);
                i = worst;
            }
        }

        #endregion

        #region IFallen8Serializable implementation

        /// <summary>The sentinel replacing the legacy slot count: an EXTENDED header
        /// (format version, binding, model) follows (feature element-embeddings).</summary>
        private const Int32 ExtendedHeaderSentinel = -1;

        /// <summary>The current extended-header format version.</summary>
        private const Byte ExtendedHeaderVersion = 1;

        public void Save(SerializationWriter writer)
        {
            if (ReadResource())
            {
                try
                {
                    writer.Write(Dimension);
                    writer.Write((Byte)Metric);

                    // Extended header (feature element-embeddings). Legacy sidecars wrote the
                    // slot count here; a count can never be negative, so the sentinel keys the
                    // new format while every pre-feature checkpoint still loads.
                    writer.Write(ExtendedHeaderSentinel);
                    writer.Write(ExtendedHeaderVersion);
                    writer.WriteOptimized(EmbeddingName ?? String.Empty);
                    writer.WriteOptimized(Model ?? String.Empty);

                    if (EmbeddingName != null)
                    {
                        // A bound index is a pure derived cache: the vectors live on the
                        // elements (WAL-covered), so the sidecar carries the header only and
                        // Load rebuilds the slab by scanning element state.
                        return;
                    }

                    writer.Write(_count);
                    for (var slot = 0; slot < _count; slot++)
                    {
                        writer.Write(_elements[slot].Id);
                        writer.Write(CopySlot(slot));
                    }
                }
                finally
                {
                    FinishReadResource();
                }

                return;
            }

            throw new CollisionException();
        }

        public void Load(SerializationReader reader, IFallen8 fallen8)
        {
            // The real load path (IndexFactory.OpenIndex) activates the plugin WITHOUT calling
            // Initialize, so the logger must be wired here or every skip below would be silent.
            _logger ??= fallen8?.LoggerFactory?.CreateLogger<VectorIndex>();

            if (WriteResource())
            {
                try
                {
                    // A corrupt header must THROW, not return: OpenIndex would otherwise register
                    // an index whose internals are still null. LoadIndices catches per index, so
                    // throwing skips exactly this sidecar - the family's corrupt-sidecar posture.
                    var dimension = reader.ReadInt32();
                    if (dimension < 1 || dimension > MaxDimension)
                    {
                        _logger?.LogError("A persisted vector index carries an invalid dimension ({Dimension}); the index is skipped.", dimension);
                        throw new System.IO.InvalidDataException(String.Format(
                            "Invalid persisted vector index dimension: {0}.", dimension));
                    }

                    var metricByte = reader.ReadByte();
                    if (metricByte > (Byte)VectorDistanceMetric.L2)
                    {
                        _logger?.LogError("A persisted vector index carries an invalid metric ({Metric}); the index is skipped.", metricByte);
                        throw new System.IO.InvalidDataException(String.Format(
                            "Invalid persisted vector index metric: {0}.", metricByte));
                    }

                    Dimension = dimension;
                    Metric = (VectorDistanceMetric)metricByte;

                    var persistedCount = reader.ReadInt32();

                    // Extended header (feature element-embeddings): binding + model identity;
                    // a legacy sidecar goes straight to its slots (persistedCount >= 0).
                    if (persistedCount == ExtendedHeaderSentinel)
                    {
                        var version = reader.ReadByte();
                        if (version != ExtendedHeaderVersion)
                        {
                            _logger?.LogError("A persisted vector index carries an unknown format version ({Version}); the index is skipped.", version);
                            throw new System.IO.InvalidDataException(String.Format(
                                "Unknown persisted vector index format version: {0}.", version));
                        }

                        var embeddingName = reader.ReadOptimizedString();
                        var model = reader.ReadOptimizedString();
                        EmbeddingName = String.IsNullOrEmpty(embeddingName) ? null : embeddingName;
                        Model = String.IsNullOrEmpty(model) ? null : model;

                        if (EmbeddingName != null)
                        {
                            // A bound index persists no vectors: rebuild the projection from
                            // element state (already loaded at this point of the checkpoint).
                            _vectors = new float[dimension * 16];
                            _elements = new AGraphElementModel[16];
                            _slotByElement = new Dictionary<AGraphElementModel, Int32>();
                            _count = 0;
                            RebuildProjection(fallen8);
                            return;
                        }

                        persistedCount = reader.ReadInt32();
                    }

                    if (persistedCount < 0)
                    {
                        _logger?.LogError("A persisted vector index carries an invalid entry count ({Count}); the index is skipped.", persistedCount);
                        throw new System.IO.InvalidDataException(String.Format(
                            "Invalid persisted vector index entry count: {0}.", persistedCount));
                    }

                    _vectors = new float[Math.Max(16, persistedCount) * dimension];
                    _elements = new AGraphElementModel[Math.Max(16, persistedCount)];
                    _slotByElement = new Dictionary<AGraphElementModel, Int32>(persistedCount);
                    _count = 0;

                    for (var i = 0; i < persistedCount; i++)
                    {
                        var elementId = reader.ReadInt32();
                        var vector = reader.ReadSingleArray();

                        // The family's posture: an element missing after load is logged, skipped.
                        if (!fallen8.TryGetGraphElement(out var element, elementId))
                        {
                            _logger?.LogWarning("Vector index entry for element {ElementId} skipped on load: the element does not exist.", elementId);
                            continue;
                        }

                        if (vector == null || vector.Length != dimension)
                        {
                            _logger?.LogWarning("Vector index entry for element {ElementId} skipped on load: bad vector payload.", elementId);
                            continue;
                        }

                        Array.Copy(vector, 0, _vectors, _count * dimension, dimension);
                        _elements[_count] = element;
                        _slotByElement[element] = _count;
                        _count++;
                    }
                }
                finally
                {
                    FinishWriteResource();
                }

                return;
            }

            throw new CollisionException();
        }

        #endregion

        #region private helpers

        private float[] CopySlot(int slot)
        {
            var copy = new float[Dimension];
            Array.Copy(_vectors, slot * Dimension, copy, 0, Dimension);
            return copy;
        }

        private bool SlotEquals(int slot, float[] key)
        {
            return _vectors.AsSpan(slot * Dimension, Dimension).SequenceEqual(key);
        }

        /// <summary>Whether the vector's norm is zero (it could not rank under Cosine: NaN).</summary>
        public static bool IsZeroNorm(ReadOnlySpan<Single> vector)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                if (vector[i] != 0f)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Whether any component is NaN or Infinity - such a vector is rejected on add
        /// and query (a NaN score is not totally ordered and would poison the top-k heap;
        /// neither NaN nor Infinity survives JSON serialization).</summary>
        public static bool HasNonFiniteComponent(ReadOnlySpan<Single> vector)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                if (!Single.IsFinite(vector[i]))
                {
                    return true;
                }
            }
            return false;
        }

        #endregion
    }
}
