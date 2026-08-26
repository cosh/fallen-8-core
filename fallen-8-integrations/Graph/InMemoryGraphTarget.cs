// MIT License
//
// InMemoryGraphTarget.cs
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
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using NoSQL.GraphDB.Integrations.Identity;

namespace NoSQL.GraphDB.Integrations.Graph
{
    /// <summary>
    ///   The graph the conformance suite runs against: the same seam, in memory, so the WHOLE run - the real
    ///   runner, catalog, validator, credential path and redaction - can be exercised with no live graph and
    ///   no network. An author who needs a controller on the desk to iterate will not iterate.
    ///
    ///   <para>Its fidelity to <c>Fallen8RestTarget</c> is not asserted by reading it: ONE SHARED CONTRACT
    ///   SUITE runs the same assertions against both, so this fake cannot drift stricter or laxer than the
    ///   platform. Every place it would be tempting to be stricter (rejecting a removal of an absent
    ///   property, refusing an unknown element id) it deliberately matches what the platform actually does,
    ///   because a fake that is harsher than the platform hides a real failure and one that is laxer invents
    ///   a guarantee.</para>
    /// </summary>
    public sealed class InMemoryGraphTarget : IGraphTarget
    {
        private readonly Dictionary<Int32, Element> _elements = new Dictionary<Int32, Element>();
        private readonly Dictionary<String, Dictionary<String, SortedSet<Int32>>> _indices =
            new Dictionary<String, Dictionary<String, SortedSet<Int32>>>(StringComparer.Ordinal);

        // ZERO, like the engine, and that is the whole point: the platform hands element id 0 to the first
        // element of a fresh graph (every namespace is its own graph, so this is the first run of any
        // integration), and this fake used to start at 1. That one-off difference hid a real defect for the
        // entire life of the feature - the applier read id 0 as "this entity has no element" and dropped every
        // relation to it - because no test could ever produce the id that triggered it. A fake that cannot
        // produce the platform's first id is not the same seam.
        private Int32 _nextId = 0;
        private TargetDurability _durability = TargetDurability.Healthy;

        // A fixture graph can embed by default, so the interesting case (it cannot) has to be asked for. The
        // dimension and metric are the TARGET'S to declare, which is the point of reading them at all.
        private TargetEmbedding _embedding =
            new TargetEmbedding(true, 1024, "Cosine", "fixture-embedding-model", null);

        /// <inheritdoc />
        public Int32 IssuedMutationCount => MutationCalls.Count;

        /// <summary>
        ///   Every mutation call this target was asked to make, in order, so a test can assert on the CALL
        ///   CHANNEL rather than on stored values: an equal-value write is a true no-op in the platform, so a
        ///   runtime that wrote unconditionally would leave the graph correct and the invariant unobservable.
        /// </summary>
        public IList<String> MutationCalls { get; } = new List<String>();

        /// <summary>Which indices exist, for a test that drops one to prove the repair path.</summary>
        public IEnumerable<String> Indices => _indices.Keys;

        /// <summary>
        ///   Every element a mutation named, in order. This is what makes the claim-scope check observable: a run
        ///   may write only to what it claims, to what it withdraws its own claim from, and to an unclaimed orphan
        ///   it reclaims, so an element carrying ANOTHER instance's claim appearing here is the violation.
        /// </summary>
        public IList<Int32> TouchedElements { get; } = new List<Int32>();

        /// <summary>Replaces the durability posture, for the deletion-deferral tests.</summary>
        public void SetDurability(TargetDurability durability)
        {
            _durability = durability ?? throw new ArgumentNullException(nameof(durability));
        }

        /// <summary>
        ///   Replaces the embedding posture, which is how the degradation cells are made red: with the capability
        ///   absent, a run must still succeed and simply carry no summaries.
        /// </summary>
        public void SetEmbeddingState(TargetEmbedding state)
        {
            _embedding = state ?? throw new ArgumentNullException(nameof(state));
        }

        /// <summary>Every summary embedded, by element, so a test can assert on the text rather than a vector.</summary>
        public IDictionary<Int32, String> EmbeddedSummaries { get; } = new Dictionary<Int32, String>();

        /// <summary>
        ///   Drops an index the way three ordinary operations do (a tabula rasa, loading a save game, a
        ///   per-index serialization failure), so the ensure-repair-retry path can be exercised.
        /// </summary>
        public void DropIndex(String indexId)
        {
            _indices.Remove(indexId);
        }

        /// <summary>
        ///   Removes ONE index entry, leaving the element and its properties alone: the state a run interrupted
        ///   (or partially declined) between its creates and its index write leaves behind, where an element
        ///   carries a claim as a property that the index does not name. It has to be constructible, because it is
        ///   the one state the runtime cannot heal from the outside - unfindable by the next resolve, and
        ///   invisible to a reconciliation that withdraws by set difference over that same index - so the heal
        ///   for it needs a fixture. A test-only control, exactly like <see cref="DropIndex" />.
        /// </summary>
        public void RemoveIndexEntry(String indexId, String key, Int32 elementId)
        {
            if (_indices.TryGetValue(indexId, out var index) && index.TryGetValue(key, out var ids))
            {
                ids.Remove(elementId);
                if (ids.Count == 0)
                {
                    index.Remove(key);
                }
            }
        }

        /// <summary>
        ///   Seeds an element without going through the write path, for a fixture that needs a graph with a
        ///   history: an element another instance claims, or an orphan left by a deferred deletion.
        /// </summary>
        public Int32 SeedVertex(String label, IEnumerable<GraphProperty> properties)
        {
            var element = new Element(_nextId++, label, false, 0, 0);
            foreach (var property in properties ?? Array.Empty<GraphProperty>())
            {
                element.Properties[property.Key] = property;
            }

            _elements.Add(element.Id, element);
            IndexSeededElement(element);
            return element.Id;
        }

        /// <summary>The current state of one element, for an assertion.</summary>
        public Boolean TryReadElement(Int32 id, out ElementState? state)
        {
            if (_elements.TryGetValue(id, out var element))
            {
                state = element.Snapshot();
                return true;
            }

            state = null;
            return false;
        }

        /// <summary>Every live element, for an assertion over the whole graph.</summary>
        public IEnumerable<ElementState> AllElements()
        {
            foreach (var element in _elements.Values)
            {
                yield return element.Snapshot();
            }
        }

        /// <inheritdoc />
        public Task<Boolean> EnsureIndicesAsync(CancellationToken cancellationToken)
        {
            var created = false;
            foreach (var indexId in new[] { ClaimSchema.IdentityIndexId, ClaimSchema.ClaimsIndexId })
            {
                if (!_indices.ContainsKey(indexId))
                {
                    _indices[indexId] = new Dictionary<String, SortedSet<Int32>>(StringComparer.Ordinal);
                    created = true;
                }
            }

            return Task.FromResult(created);
        }

        /// <inheritdoc />
        public Task<IndexRepairOutcome> RepairIndicesAsync(CancellationToken cancellationToken)
        {
            var identity = Backfill(ClaimSchema.IdentityIndexId, ClaimSchema.IdentityPrefix);
            var claims = Backfill(ClaimSchema.ClaimsIndexId, ClaimSchema.ClaimPrefix);
            return Task.FromResult(new IndexRepairOutcome(identity, claims));
        }

        /// <inheritdoc />
        public async Task<ClaimLookup> ResolveClaimKeysAsync(IReadOnlyCollection<String> claimKeys,
            String instanceId, CancellationToken cancellationToken)
        {
            var index = Index(ClaimSchema.IdentityIndexId);
            var byKey = new Dictionary<String, IReadOnlyList<Int32>>(StringComparer.Ordinal);
            var named = new HashSet<Int32>();

            foreach (var key in claimKeys ?? Array.Empty<String>())
            {
                if (index.TryGetValue(key, out var ids) && ids.Count > 0)
                {
                    byKey[key] = new List<Int32>(ids);
                    foreach (var id in ids)
                    {
                        named.Add(id);
                    }
                }
            }

            var elements = await ReadElementsAsync(named, cancellationToken).ConfigureAwait(false);
            return ClaimLookup.Build(byKey, elements, instanceId);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<Int32>> ElementsClaimedByAsync(String instanceId,
            CancellationToken cancellationToken)
        {
            var index = Index(ClaimSchema.ClaimsIndexId);
            var ids = index.TryGetValue(instanceId, out var found)
                ? new List<Int32>(found)
                : new List<Int32>();
            return Task.FromResult<IReadOnlyList<Int32>>(ids);
        }

        /// <inheritdoc />
        public Task<IReadOnlyDictionary<Int32, ElementState>> ReadElementsAsync(IReadOnlyCollection<Int32> ids,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<Int32, ElementState>();
            foreach (var id in ids ?? Array.Empty<Int32>())
            {
                // An id that resolves to no live element is simply absent, exactly as the platform's batch
                // read reports it: "gone" and "has no properties" are different conclusions.
                if (_elements.TryGetValue(id, out var element))
                {
                    result[id] = element.Snapshot();
                }
            }

            return Task.FromResult<IReadOnlyDictionary<Int32, ElementState>>(result);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<Int32>> CreateVerticesAsync(IReadOnlyList<VertexWrite> vertices,
            CancellationToken cancellationToken)
        {
            var ids = new List<Int32>(vertices?.Count ?? 0);
            if (vertices == null || vertices.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<Int32>>(ids);
            }

            RecordMutation("createVertices", vertices.Count);

            foreach (var vertex in vertices)
            {
                var element = new Element(_nextId++, vertex.Label, false, 0, 0);
                foreach (var property in vertex.Properties)
                {
                    element.Properties[property.Key] = property;
                }

                _elements.Add(element.Id, element);
                ids.Add(element.Id);
                TouchedElements.Add(element.Id);
            }

            return Task.FromResult<IReadOnlyList<Int32>>(ids);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<Int32>> CreateEdgesAsync(IReadOnlyList<EdgeWrite> edges,
            CancellationToken cancellationToken)
        {
            var ids = new List<Int32>(edges?.Count ?? 0);
            if (edges == null || edges.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<Int32>>(ids);
            }

            RecordMutation("createEdges", edges.Count);

            foreach (var edge in edges)
            {
                // The platform rolls the whole batch back when an endpoint does not exist, so this refuses
                // the same way rather than silently creating a dangling edge.
                if (!_elements.ContainsKey(edge.SourceId) || !_elements.ContainsKey(edge.TargetId))
                {
                    throw new GraphTargetException(String.Format(
                        "An edge references element(s) that do not exist ({0} -> {1}); the whole batch is " +
                        "rolled back.", edge.SourceId, edge.TargetId));
                }

                var element = new Element(_nextId++, edge.EdgeType, true, edge.SourceId, edge.TargetId);
                foreach (var property in edge.Properties)
                {
                    element.Properties[property.Key] = property;
                }

                _elements.Add(element.Id, element);
                ids.Add(element.Id);
                TouchedElements.Add(element.Id);
            }

            return Task.FromResult<IReadOnlyList<Int32>>(ids);
        }

        /// <inheritdoc />
        public Task ApplyPropertyWritesAsync(IReadOnlyList<PropertyWrite> writes,
            CancellationToken cancellationToken)
        {
            if (writes == null || writes.Count == 0)
            {
                return Task.CompletedTask;
            }

            RecordMutation("setProperties", writes.Count);

            foreach (var write in writes)
            {
                TouchedElements.Add(write.ElementId);

                if (!_elements.TryGetValue(write.ElementId, out var element))
                {
                    // An in-range but absent id is a committed no-op in the platform, matching the single
                    // element routes. Being stricter here would hide the platform's own behaviour.
                    continue;
                }

                if (write.Remove)
                {
                    element.Properties.Remove(write.Key);
                    continue;
                }

                element.Properties[write.Key] = new GraphProperty(write.Key, write.TypeName!, write.Text!);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task RemoveElementsAsync(IReadOnlyCollection<Int32> ids, CancellationToken cancellationToken)
        {
            if (ids == null || ids.Count == 0)
            {
                return Task.CompletedTask;
            }

            RecordMutation("removeElements", ids.Count);

            foreach (var id in ids)
            {
                TouchedElements.Add(id);

                if (!_elements.TryGetValue(id, out var element))
                {
                    continue;
                }

                _elements.Remove(id);

                if (element.IsEdge)
                {
                    continue;
                }

                // Removing a vertex cascades to its edges, exactly as the platform does.
                var cascade = new List<Int32>();
                foreach (var candidate in _elements.Values)
                {
                    if (candidate.IsEdge && (candidate.SourceId == id || candidate.TargetId == id))
                    {
                        cascade.Add(candidate.Id);
                    }
                }

                foreach (var edgeId in cascade)
                {
                    _elements.Remove(edgeId);
                }
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<IndexWriteOutcome> IndexClaimsAsync(IReadOnlyList<IndexEntry> entries,
            CancellationToken cancellationToken)
        {
            if (entries == null || entries.Count == 0)
            {
                return Task.FromResult(IndexWriteOutcome.Empty);
            }

            RecordMutation("indexClaims", entries.Count);

            var accepted = 0;
            var declined = ImmutableArray.CreateBuilder<IndexEntry>();

            foreach (var entry in entries)
            {
                // The platform declines with a plain false when the index or the element does not exist.
                // Mirroring that exactly is what makes the declined path testable at all.
                if (!_indices.TryGetValue(entry.IndexId, out var index) || !_elements.ContainsKey(entry.ElementId))
                {
                    declined.Add(entry);
                    continue;
                }

                if (!index.TryGetValue(entry.Key, out var ids))
                {
                    ids = new SortedSet<Int32>();
                    index[entry.Key] = ids;
                }

                ids.Add(entry.ElementId);
                accepted++;
            }

            return Task.FromResult(new IndexWriteOutcome(accepted, declined.ToImmutable()));
        }

        /// <inheritdoc />
        public Task<TargetDurability> ReadDurabilityAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_durability);
        }

        /// <inheritdoc />
        public Task<TargetEmbedding> ReadEmbeddingStateAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_embedding);
        }

        /// <inheritdoc />
        public Task<EmbeddingWriteOutcome> EmbedSummariesAsync(String embeddingName,
            IReadOnlyList<SummaryWrite> summaries, CancellationToken cancellationToken,
            NoSQL.GraphDB.Integrations.Run.IRunProgress? progress = null,
            NoSQL.GraphDB.Integrations.Run.RunAbort abort = default)
        {
            // One safe point, because there is one chunk: this target does no inference, so its whole write
            // is the first chunk. Honoured rather than ignored so the fake cannot be laxer than the platform
            // about the one thing a cancelled embed must guarantee - nothing written after the stop.
            abort.ThrowIfRequested();

            if (!_embedding.Available)
            {
                return Task.FromResult(new EmbeddingWriteOutcome(0,
                    _embedding.Reason ?? "the target cannot embed text"));
            }

            if (summaries == null || summaries.Count == 0)
            {
                return Task.FromResult(EmbeddingWriteOutcome.None);
            }

            RecordMutation("embedSummaries", summaries.Count);

            foreach (var summary in summaries)
            {
                EmbeddedSummaries[summary.ElementId] = summary.Text;
            }

            // This target does no inference, so there is one tick: it lands all at once. Reported anyway,
            // so a fixture can assert the applier threads the sink this far rather than dropping it.
            progress?.Advance(summaries.Count, summaries.Count);

            return Task.FromResult(new EmbeddingWriteOutcome(summaries.Count, null));
        }

        public void Dispose()
        {
            // Nothing to release: the graph is this object.
        }

        private Dictionary<String, SortedSet<Int32>> Index(String indexId)
        {
            if (_indices.TryGetValue(indexId, out var index))
            {
                return index;
            }

            throw new GraphIndexMissingException(indexId);
        }

        private Int32 Backfill(String indexId, String propertyPrefix)
        {
            if (!_indices.TryGetValue(indexId, out var index))
            {
                index = new Dictionary<String, SortedSet<Int32>>(StringComparer.Ordinal);
                _indices[indexId] = index;
            }

            var indexed = 0;
            foreach (var element in _elements.Values)
            {
                foreach (var property in element.Properties)
                {
                    if (!property.Key.StartsWith(propertyPrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!index.TryGetValue(property.Value.Text, out var ids))
                    {
                        ids = new SortedSet<Int32>();
                        index[property.Value.Text] = ids;
                    }

                    // Add-only and idempotent per (key, element), like the platform's repair.
                    if (ids.Add(element.Id))
                    {
                        indexed++;
                    }
                }
            }

            return indexed;
        }

        /// <summary>
        ///   Indexes a seeded element the way a previous run would have, so a fixture's history is findable.
        ///   Only touches indices that exist, matching the platform's decline.
        /// </summary>
        private void IndexSeededElement(Element element)
        {
            foreach (var property in element.Properties)
            {
                String? indexId = null;
                if (ClaimSchema.IsIdentityProperty(property.Key))
                {
                    indexId = ClaimSchema.IdentityIndexId;
                }
                else if (ClaimSchema.IsClaimProperty(property.Key))
                {
                    indexId = ClaimSchema.ClaimsIndexId;
                }

                if (indexId == null || !_indices.TryGetValue(indexId, out var index))
                {
                    continue;
                }

                if (!index.TryGetValue(property.Value.Text, out var ids))
                {
                    ids = new SortedSet<Int32>();
                    index[property.Value.Text] = ids;
                }

                ids.Add(element.Id);
            }
        }

        private void RecordMutation(String call, Int32 count)
        {
            MutationCalls.Add(call + "(" + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
        }

        private sealed class Element
        {
            public Element(Int32 id, String? label, Boolean isEdge, Int32 sourceId, Int32 targetId)
            {
                Id = id;
                Label = label;
                IsEdge = isEdge;
                SourceId = sourceId;
                TargetId = targetId;
            }

            public Int32 Id { get; }

            public String? Label { get; }

            public Boolean IsEdge { get; }

            public Int32 SourceId { get; }

            public Int32 TargetId { get; }

            public Dictionary<String, GraphProperty> Properties { get; } =
                new Dictionary<String, GraphProperty>(StringComparer.Ordinal);

            public ElementState Snapshot()
            {
                return new ElementState(Id, Label,
                    Properties.ToImmutableDictionary(StringComparer.Ordinal));
            }
        }
    }
}
