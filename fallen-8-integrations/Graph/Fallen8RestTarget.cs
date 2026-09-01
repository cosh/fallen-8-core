// MIT License
//
// Fallen8RestTarget.cs
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
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NoSQL.GraphDB.Integrations.Identity;
using NoSQL.GraphDB.Rest;

namespace NoSQL.GraphDB.Integrations.Graph
{
    /// <summary>
    ///   The seam against a live Fallen-8, over its PUBLIC REST API only. This deployable holds no reference
    ///   to the engine or the API app: a container that can read somebody's network-admin credential must not
    ///   load the engine in process, and the two must version independently against a contract a REST boundary
    ///   makes explicit and a project reference would widen to the whole engine surface.
    ///
    ///   <para>Two per-item loops remain and are VISIBLE here rather than hidden: the index scan, whose route
    ///   takes a single literal, and the identity-index write, because the engine maintains no property-keyed
    ///   index automatically. Everything else is batched, because without a batched element read and an atomic
    ///   property replace a run over a few hundred devices is over a thousand round-trips, most producing no
    ///   change.</para>
    ///
    ///   <para>Sending, the absent-body convention and the timeout-versus-cancellation classification come from
    ///   <see cref="RestSeam"/>, shared with the other deployable held to the same REST-only rule; this file
    ///   names every outcome in the run report's own vocabulary.</para>
    /// </summary>
    public sealed class Fallen8RestTarget : IGraphTarget
    {
        /// <summary>Ids per batch read. Well under the platform's page cap, and small enough that one
        /// failure does not lose a whole run's worth of work.</summary>
        private const Int32 ReadBatchSize = 2000;

        /// <summary>
        ///   Elements per batch WRITE, and the reason it exists is measured rather than assumed: a run
        ///   over a 99 MiB device list produced a single <c>PUT vertices</c> body of about 110 MB, which
        ///   the graph's own route refuses at Kestrel's 30 MB default - and the runtime saw it only as
        ///   "Error while copying content to a stream", an errorKind of <c>graph</c> for a graph that was
        ///   perfectly healthy.
        ///
        ///   <para>Batching here rather than raising a limit over there is deliberate: the write body is
        ///   THIS deployable's doing, it is unbounded in exactly the way an uploaded file is, and a
        ///   bigger cap on a shared route would invite a single transaction nobody sized. 500 keeps a
        ///   batch comfortably inside that default even for elements carrying kilobytes of properties.</para>
        ///
        ///   <para>It is NOT transactional across batches, and that is not a regression: the graph applied
        ///   one transaction per call before this too, so a failure part-way has always been able to leave
        ///   a run's writes half-applied. What protects the graph is that a failed run withdraws nothing,
        ///   and that the next run over the same source reconciles by claim rather than by memory of what
        ///   it managed last time.</para>
        /// </summary>
        private const Int32 WriteBatchSize = 500;

        /// <summary>
        ///   Summaries per batch EMBED. A different number from <see cref="WriteBatchSize" /> because a
        ///   different limit bounds it: the embedding route counts ITEMS and refuses any batch larger than
        ///   the target's <c>Fallen8:Embedding:MaxBatchSize</c>.
        ///
        ///   <para>TWO limits bound this number and the tighter one wins. The first is that item cap, whose
        ///   smallest shipped value is 32 (the apiApp defaults to 64; the Nahil compose sets 32), and
        ///   exceeding it is not survivable: the route answers 400, correctly outside the degrade set, so it
        ///   fails a run whose graph writes have already landed.</para>
        ///
        ///   <para>The second is the client TIMEOUT, and it is why this is 16 rather than 32. A chunk's
        ///   duration is model inference, not graph work: measured against a CPU-backed bge-m3, one element
        ///   costs ~3.5 s, so 32 elements is ~113 s - which was six percent of headroom against the fixed
        ///   120 s this runtime used to hold every call to. That was not a theoretical margin. A real
        ///   many-entity extract embedded exactly many chunks and then died on the 86th, losing two
        ///   hours of inference and leaving the graph a fifth embedded. The deadline is now the operator's
        ///   (<c>Fallen8Target:TimeoutSeconds</c>, default 330), so the margin is no longer thin - but 16
        ///   stays, because it also halves the interval between progress ticks on the one phase that runs
        ///   for hours, and it costs only more round-trips: those stay cheap because chunks are sequential,
        ///   so the request RATE never approaches the route's rate limit whatever the backend.</para>
        ///
        ///   <para>Revisit by making the size ADAPTIVE (halve on timeout, retry, floor at 4) when a
        ///   deployment appears whose per-element cost is far outside the ~50 ms GPU to ~3.5 s CPU range this
        ///   number was chosen across.</para>
        ///
        ///   <para>A constant rather than a read of that setting, because the target does not publish it.
        ///   The runtime already asks the target for the numbers it owns - dimension, metric, model - and a
        ///   batch cap belongs in that set; it is simply absent from the status contract, so reading it
        ///   would mean adding a field to a snapshot-pinned surface. If it is ever published, this constant
        ///   becomes the fallback for a target that does not answer.</para>
        ///
        ///   <para>It also keeps each body far inside the route's <c>[RequestSizeLimit(1_048_576)]</c>,
        ///   which is a compile-time attribute no configuration can raise. ONE unchunked body for the
        ///   recorded many-entity system extract was both hundreds of times over the item cap and past
        ///   that megabyte, which is why no real extract could be embedded before this.</para>
        /// </summary>
        private const Int32 EmbedBatchSize = 16;

        /// <summary>The equality operator's wire code, from the engine's own operator enum.</summary>
        private const Int32 EqualsOperator = 0;

        private readonly HttpClient _client;
        private readonly String _prefix;
        private readonly Boolean _ownsClient;

        /// <param name="client">The configured client: base address and the api key header this runtime
        /// presents as ITSELF. A caller's credential is never forwarded.</param>
        /// <param name="namespaceName">The namespace to write into; the reserved default uses the bare routes.</param>
        /// <param name="ownsClient">Whether disposing this target disposes the client.</param>
        public Fallen8RestTarget(HttpClient client, String? namespaceName, Boolean ownsClient = true)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _ownsClient = ownsClient;
            _prefix = BuildPrefix(namespaceName);
        }

        private Int32 _mutations;

        /// <inheritdoc />
        public Int32 IssuedMutationCount => _mutations;

        /// <inheritdoc />
        public async Task<Boolean> EnsureIndicesAsync(CancellationToken cancellationToken)
        {
            var existing = await ReadIndexInventoryAsync(cancellationToken).ConfigureAwait(false);
            var created = false;

            foreach (var indexId in new[] { ClaimSchema.IdentityIndexId, ClaimSchema.ClaimsIndexId })
            {
                if (existing.TryGetValue(indexId, out var capabilities))
                {
                    // An index that cannot answer exact point equality cannot answer a claim lookup, and
                    // silently scanning it would report "nothing carries this claim" for every claim.
                    if (!capabilities.Contains("equality", StringComparer.OrdinalIgnoreCase))
                    {
                        throw new GraphTargetException(String.Format(
                            "The index '{0}' exists but does not answer exact point-equality lookups, so it " +
                            "cannot resolve a claim key. Delete or rename it.", indexId));
                    }

                    continue;
                }

                var accepted = await SendAsync<Boolean>(HttpMethod.Post, "index", new
                {
                    uniqueId = indexId,
                    pluginType = "DictionaryIndex",
                    pluginOptions = new Dictionary<String, Object>(StringComparer.Ordinal),
                }, cancellationToken).ConfigureAwait(false);

                if (!accepted)
                {
                    // The create route collapses three causes into one false: unknown plugin, the name
                    // already exists, or initialization threw. A re-read disambiguates rather than guessing,
                    // because "it already exists" is the benign one and the other two are faults.
                    var recheck = await ReadIndexInventoryAsync(cancellationToken).ConfigureAwait(false);
                    if (!recheck.ContainsKey(indexId))
                    {
                        throw new GraphTargetException(String.Format(
                            "The target refused to create the index '{0}' and does not report it as existing.",
                            indexId));
                    }

                    continue;
                }

                created = true;
            }

            return created;
        }

        /// <inheritdoc />
        public async Task<IndexRepairOutcome> RepairIndicesAsync(CancellationToken cancellationToken)
        {
            var identity = await BackfillAsync(ClaimSchema.IdentityIndexId, ClaimSchema.IdentityPrefix,
                cancellationToken).ConfigureAwait(false);
            var claims = await BackfillAsync(ClaimSchema.ClaimsIndexId, ClaimSchema.ClaimPrefix,
                cancellationToken).ConfigureAwait(false);
            return new IndexRepairOutcome(identity, claims);
        }

        /// <inheritdoc />
        public async Task<ClaimLookup> ResolveClaimKeysAsync(IReadOnlyCollection<String> claimKeys,
            String instanceId, CancellationToken cancellationToken)
        {
            if (claimKeys == null || claimKeys.Count == 0)
            {
                return ClaimLookup.Empty;
            }

            // One scan per key, because the scan route takes a single literal: this is one of the two per-item
            // loops the seam deliberately keeps visible rather than hiding.
            var byKey = new Dictionary<String, IReadOnlyList<Int32>>(StringComparer.Ordinal);
            var named = new HashSet<Int32>();

            foreach (var key in claimKeys)
            {
                var ids = await ScanIndexAsync(ClaimSchema.IdentityIndexId, key, cancellationToken)
                    .ConfigureAwait(false);
                if (ids.Count > 0)
                {
                    byKey[key] = ids;
                    foreach (var id in ids)
                    {
                        named.Add(id);
                    }
                }
            }

            // Then ONE batched read of every element the index named, which is what makes the narrowing a
            // question about state rather than a per-element round trip.
            var elements = await ReadElementsAsync(named, cancellationToken).ConfigureAwait(false);
            return ClaimLookup.Build(byKey, elements, instanceId);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<Int32>> ElementsClaimedByAsync(String instanceId,
            CancellationToken cancellationToken)
        {
            return ScanIndexAsync(ClaimSchema.ClaimsIndexId, instanceId, cancellationToken);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyDictionary<Int32, ElementState>> ReadElementsAsync(
            IReadOnlyCollection<Int32> ids, CancellationToken cancellationToken)
        {
            var result = new Dictionary<Int32, ElementState>();
            if (ids == null || ids.Count == 0)
            {
                return result;
            }

            var batch = new List<Int32>(Math.Min(ReadBatchSize, ids.Count));
            foreach (var id in ids)
            {
                batch.Add(id);
                if (batch.Count == ReadBatchSize)
                {
                    await ReadBatchAsync(batch, result, cancellationToken).ConfigureAwait(false);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await ReadBatchAsync(batch, result, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<Int32>> CreateVerticesAsync(IReadOnlyList<VertexWrite> vertices,
            CancellationToken cancellationToken)
        {
            if (vertices == null || vertices.Count == 0)
            {
                return Array.Empty<Int32>();
            }

            var body = new List<Object>(vertices.Count);
            foreach (var vertex in vertices)
            {
                body.Add(new
                {
                    creationDate = NowEpoch(),
                    label = vertex.Label,
                    properties = RenderProperties(vertex.Properties),
                });
            }

            _mutations++;

            // waitForCompletion is what makes the ids come back: without it the route answers 202 with no
            // body, and a run that cannot learn the ids it just created cannot index or claim them.
            var ids = await SendBatchedAsync("vertices?waitForCompletion=true", body, cancellationToken)
                .ConfigureAwait(false);
            return Expect(ids, vertices.Count, "vertices");
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<Int32>> CreateEdgesAsync(IReadOnlyList<EdgeWrite> edges,
            CancellationToken cancellationToken)
        {
            if (edges == null || edges.Count == 0)
            {
                return Array.Empty<Int32>();
            }

            var body = new List<Object>(edges.Count);
            foreach (var edge in edges)
            {
                body.Add(new
                {
                    creationDate = NowEpoch(),
                    sourceVertex = edge.SourceId,
                    targetVertex = edge.TargetId,
                    edgePropertyId = edge.EdgeType,
                    properties = RenderProperties(edge.Properties),
                });
            }

            _mutations++;

            var ids = await SendBatchedAsync("edges?waitForCompletion=true", body, cancellationToken)
                .ConfigureAwait(false);
            return Expect(ids, edges.Count, "edges");
        }

        /// <inheritdoc />
        public async Task ApplyPropertyWritesAsync(IReadOnlyList<PropertyWrite> writes,
            CancellationToken cancellationToken)
        {
            if (writes == null || writes.Count == 0)
            {
                return;
            }

            var body = new List<Object>(writes.Count);
            foreach (var write in writes)
            {
                if (write.Remove)
                {
                    body.Add(new
                    {
                        graphElementId = write.ElementId,
                        propertyId = write.Key,
                        remove = true,
                    });
                    continue;
                }

                body.Add(new
                {
                    graphElementId = write.ElementId,
                    propertyId = write.Key,
                    fullQualifiedTypeName = write.TypeName,
                    propertyValue = write.Text,
                });
            }

            _mutations++;

            for (var offset = 0; offset < body.Count; offset += WriteBatchSize)
            {
                var batch = body.GetRange(offset, Math.Min(WriteBatchSize, body.Count - offset));
                await SendVoidAsync(HttpMethod.Put, "graphelements/properties?waitForCompletion=true", batch,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public async Task RemoveElementsAsync(IReadOnlyCollection<Int32> ids, CancellationToken cancellationToken)
        {
            if (ids == null || ids.Count == 0)
            {
                return;
            }

            _mutations++;

            var all = new List<Int32>(ids);
            for (var offset = 0; offset < all.Count; offset += WriteBatchSize)
            {
                var batch = all.GetRange(offset, Math.Min(WriteBatchSize, all.Count - offset));
                await SendVoidAsync(HttpMethod.Delete, "graphelements?waitForCompletion=true", batch,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public async Task<IndexWriteOutcome> IndexClaimsAsync(IReadOnlyList<IndexEntry> entries,
            CancellationToken cancellationToken)
        {
            if (entries == null || entries.Count == 0)
            {
                return IndexWriteOutcome.Empty;
            }

            _mutations++;

            var accepted = 0;
            var declined = ImmutableArray.CreateBuilder<IndexEntry>();

            // BATCHED (feature cheap-withdrawal). The per-entry form of this cost one request per
            // index entry: about half a million sequential requests for a many-element import,
            // which is the create-path counterpart of the removal cost fixed in the engine.
            //
            // Grouped by index because the route is per index and one call may carry entries for
            // several, and each group keeps SUBMISSION ORDER, which is what makes the positions the
            // route returns map back to entries.
            foreach (var group in GroupByIndex(entries))
            {
                var suffix = "index/" + Uri.EscapeDataString(group.Key) + "/batch";
                var forIndex = group.Value;

                for (var offset = 0; offset < forIndex.Count; offset += WriteBatchSize)
                {
                    var take = Math.Min(WriteBatchSize, forIndex.Count - offset);
                    var batch = forIndex.GetRange(offset, take);
                    var body = new List<Object>(take);
                    foreach (var entry in batch)
                    {
                        body.Add(new
                        {
                            graphElementId = entry.ElementId,
                            key = new
                            {
                                // Only the type and the value are the key; propertyId names the property this index
                                // projects, so the request says what it is doing rather than repeating the index name.
                                propertyId = ProjectedPrefix(entry.IndexId),
                                fullQualifiedTypeName = WireValues.StringTypeName,
                                propertyValue = entry.Key,
                            },
                        });
                    }

                    var result = await SendAsync<IndexBatchResult>(HttpMethod.Put, suffix, body, cancellationToken)
                        .ConfigureAwait(false);

                    if (result == null)
                    {
                        // An empty answer is treated as "none of them landed", never as success: an
                        // element findable by none of its claims is DUPLICATED on the next resolve, so
                        // the safe reading of silence is the one that reports a diagnostic.
                        declined.AddRange(batch);
                        continue;
                    }

                    // The POSITIONS are authoritative and the accepted count is derived from them, so a
                    // count that disagrees with them cannot inflate the total quietly. The route
                    // documents accepted + declined == submitted; this does not depend on it.
                    var declinedHere = 0;
                    if (result.Declined != null)
                    {
                        foreach (var position in result.Declined)
                        {
                            if (position >= 0 && position < batch.Count)
                            {
                                declined.Add(batch[position]);
                                declinedHere++;
                            }
                        }
                    }

                    accepted += take - declinedHere;
                }
            }

            return new IndexWriteOutcome(accepted, declined.ToImmutable());
        }

        /// <summary>
        ///   Groups entries by their index, preserving both the order the indices were first seen and
        ///   the order of the entries within each one. Almost every call carries a single index (the
        ///   claim index), so the common case allocates one group.
        /// </summary>
        private static List<KeyValuePair<String, List<IndexEntry>>> GroupByIndex(IReadOnlyList<IndexEntry> entries)
        {
            var order = new List<String>();
            var byIndex = new Dictionary<String, List<IndexEntry>>(StringComparer.Ordinal);

            foreach (var entry in entries)
            {
                if (!byIndex.TryGetValue(entry.IndexId, out var forIndex))
                {
                    forIndex = new List<IndexEntry>();
                    byIndex.Add(entry.IndexId, forIndex);
                    order.Add(entry.IndexId);
                }

                forIndex.Add(entry);
            }

            var groups = new List<KeyValuePair<String, List<IndexEntry>>>(order.Count);
            foreach (var indexId in order)
            {
                groups.Add(new KeyValuePair<String, List<IndexEntry>>(indexId, byIndex[indexId]));
            }

            return groups;
        }

        /// <summary>The batch route's answer: how many entries were taken, and the zero-based
        /// positions of those refused.</summary>
        private sealed class IndexBatchResult
        {
            public Int32 Accepted
            {
                get; set;
            }

            public List<Int32>? Declined
            {
                get; set;
            }
        }

        /// <inheritdoc />
        public async Task<TargetDurability> ReadDurabilityAsync(CancellationToken cancellationToken)
        {
            using var status = await GetJsonAsync("status", cancellationToken).ConfigureAwait(false);
            var root = status.RootElement;

            if (!root.TryGetProperty("durability", out var durability) ||
                durability.ValueKind != JsonValueKind.Object)
            {
                // Absent means unknown, and unknown must not license the one mutation re-running cannot undo.
                return new TargetDurability(false, false, 0);
            }

            var degraded = ReadBoolean(durability, "degraded");
            var recoveryRan = ReadBoolean(durability, "recoveryRan");
            var truncated = recoveryRan && ReadBoolean(durability, "lastRecoveryTruncated");
            var dropped = ReadInt32(durability, "lastCheckpointDroppedIndices");

            // A target with no write-ahead log is the documented volatile posture rather than a fault, which
            // is why walEnabled is deliberately NOT read as "unsafe": degraded is the fault signal.
            return new TargetDurability(!degraded, truncated, dropped);
        }

        /// <inheritdoc />
        public async Task<TargetEmbedding> ReadEmbeddingStateAsync(CancellationToken cancellationToken)
        {
            using var status = await GetJsonAsync("status", cancellationToken).ConfigureAwait(false);

            if (!status.RootElement.TryGetProperty("embedding", out var embedding) ||
                embedding.ValueKind != JsonValueKind.Object)
            {
                return TargetEmbedding.Absent("the target publishes no embedding provider");
            }

            if (!ReadBoolean(embedding, "enabled"))
            {
                return TargetEmbedding.Absent("the target's embedding capability is switched off");
            }

            var dimension = ReadInt32(embedding, "dimension");
            if (dimension <= 0)
            {
                return TargetEmbedding.Absent("the target declares no embedding dimension");
            }

            var metric = embedding.TryGetProperty("intendedMetric", out var metricElement) &&
                         metricElement.ValueKind == JsonValueKind.String
                ? metricElement.GetString()
                : null;
            var model = embedding.TryGetProperty("modelName", out var modelElement) &&
                        modelElement.ValueKind == JsonValueKind.String
                ? modelElement.GetString()
                : null;

            return new TargetEmbedding(true, dimension, metric, model, null);
        }

        /// <inheritdoc />
        public async Task<EmbeddingWriteOutcome> EmbedSummariesAsync(String embeddingName,
            IReadOnlyList<SummaryWrite> summaries, CancellationToken cancellationToken,
            NoSQL.GraphDB.Integrations.Run.IRunProgress? progress = null,
            NoSQL.GraphDB.Integrations.Run.RunAbort abort = default)
        {
            if (summaries == null || summaries.Count == 0)
            {
                return EmbeddingWriteOutcome.None;
            }

            // ONE mutation for the whole logical write, before the loop, matching every other batched write
            // on this target: the count answers "did this run issue writes", not "how many round-trips".
            _mutations++;

            // Chunked because the route caps ITEMS, not bytes (see EmbedBatchSize). The chunks are
            // independent transactions on the target, so a failure part-way leaves the earlier chunks
            // written - which is correct rather than merely tolerable: an embedding is element state, and
            // the vectors that landed are as valid as if the rest had never been asked for. What is NOT
            // acceptable is reporting zero for work that happened, so the written count is accumulated
            // OUTSIDE the loop and leaves this method on every path there is: the success, the degrade, and
            // the failure.
            var written = 0;

            try
            {
                for (var offset = 0; offset < summaries.Count; offset += EmbedBatchSize)
                {
                    // THE SAFE POINT THIS FEATURE EXISTS FOR. This loop is the hours: a many-entity extract
                    // at sixteen per chunk against CPU inference runs overnight, so a stop honoured only after
                    // it would not be a stop. Checked BETWEEN chunks and never during one - a chunk is one
                    // atomic write on the target, and abandoning it in flight would leave a vector that landed
                    // uncounted, which is the one thing the written count must never be wrong about.
                    //
                    // The count leaves on the exception, exactly as a failure's does: the vectors already
                    // written are element state, as valid as if the rest had never been asked for.
                    abort.ThrowIfRequested(written);

                    var take = Math.Min(EmbedBatchSize, summaries.Count - offset);
                    var items = new List<Object>(take);
                    for (var i = offset; i < offset + take; i++)
                    {
                        items.Add(new { graphElementId = summaries[i].ElementId, text = summaries[i].Text });
                    }

                    var response = await SendForResponseAsync(HttpMethod.Post, "embedding/elements",
                        new { name = embeddingName, items }, "the embedding write", cancellationToken)
                        .ConfigureAwait(false);

                    using (response)
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            written += take;
                            // Per chunk, because that is the only tick this loop has. At ~3 s an element it is a
                            // visible move roughly every 45 s, which is the difference between "working" and
                            // "hung" for a phase that runs for hours.
                            progress?.Advance(written, summaries.Count);
                            continue;
                        }

                        var status = (Int32)response.StatusCode;

                        // 403 is the capability switched off, 502 and 503 are the backend not answering, and 429 is
                        // the target throttling this runtime. All four DEGRADE TO ABSENT rather than failing a run
                        // whose whole purpose is the graph write: an embedding is an addition to what landed, never a
                        // precondition for it. Anything else is a real graph failure and surfaces as one.
                        //
                        // 429 is in that set BECAUSE of chunking, and was not reachable before it: the embedding route
                        // carries the sensitive-endpoint rate limit (one process-wide fixed window), so a large extract
                        // sent as hundreds of chunks can trip a throttle that one request never could. Failing the run
                        // for the target's own pacing would make chunking a regression for exactly the large extracts
                        // it exists to support.
                        //
                        // Degrading STOPS the loop instead of trying the remaining chunks: every one of these statuses
                        // describes the provider or the window rather than this batch, so the next chunk would answer
                        // the same way and a run over a large extract would spend hundreds of round-trips proving it.
                        if (status == 403 || status == 429 || status == 502 || status == 503)
                        {
                            var detail = await response.Content.ReadAsStringAsync(cancellationToken)
                                .ConfigureAwait(false);
                            return new EmbeddingWriteOutcome(written, String.Format(CultureInfo.InvariantCulture,
                                "the target answered {0} to the embedding write ({1})", status,
                                String.IsNullOrWhiteSpace(detail) ? response.ReasonPhrase : detail.Trim()));
                        }

                        var body = await response.Content.ReadAsStringAsync(cancellationToken)
                            .ConfigureAwait(false);

                        throw new GraphTargetException(String.Format(CultureInfo.InvariantCulture,
                            "The graph refused the embedding write with {0}: {1}", status,
                            String.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body.Trim()));
                    }
                }
            }
            catch (GraphTargetTimeoutException)
            {
                // A client-side TIMEOUT joins the degrade set above, and only for this write: the target's own
                // embedding budget is longer than any other route's, this runtime cannot make a model answer
                // faster, and an embedding is an addition to what landed rather than a precondition for it -
                // so pre-empting it must not fail a run whose graph writes are already in. Every other call
                // this target makes keeps timeout-as-failure. It stops the loop for the reason 503 does: the
                // next chunk faces the same model. A cancellation the CALLER requested never arrives here,
                // because the token decides that and not the exception type (see RestSeam).
                return new EmbeddingWriteOutcome(written,
                    "the target did not answer the embedding write within this runtime's own timeout " +
                    "(Fallen8Target:TimeoutSeconds)");
            }
            catch (GraphTargetException failure)
            {
                // THE one place a failure is given the count, rather than each throw site being trusted to
                // remember: the chunks that landed are element state, and a connection that died mid extract
                // used to report zero for them - which is false about vectors a bound index answers searches
                // over, and sends the operator to a tabula rasa they do not need.
                failure.SummariesWritten = written;
                throw;
            }

            return new EmbeddingWriteOutcome(written, null);
        }

        public void Dispose()
        {
            if (_ownsClient)
            {
                _client.Dispose();
            }
        }

        /// <summary>
        ///   The route prefix: the bare routes for the reserved default namespace, <c>ns/{name}/</c>
        ///   otherwise. The bare URLs alias the default namespace, so both forms reach the same graph.
        ///   The name is validated and encoded by <see cref="UrlSafety"/>, which applies Fallen-8's own name
        ///   rule: a name the platform would refuse fails HERE, before a run writes anything, rather than
        ///   part way through as a 404 on some route.
        /// </summary>
        private static String BuildPrefix(String? namespaceName)
        {
            if (UrlSafety.IsDefault(namespaceName))
            {
                return String.Empty;
            }

            if (!UrlSafety.TryEncodeNamespace(namespaceName, out var encoded, out var error))
            {
                throw new GraphTargetException(String.Format(
                    "The namespace '{0}' cannot address a graph: {1}", namespaceName, error));
            }

            return "ns/" + encoded + "/";
        }

        /// <summary>Which reserved property prefix an index projects, for the add request's key.</summary>
        private static String ProjectedPrefix(String indexId)
        {
            return String.Equals(indexId, ClaimSchema.ClaimsIndexId, StringComparison.Ordinal)
                ? ClaimSchema.ClaimPrefix
                : ClaimSchema.IdentityPrefix;
        }

        private static UInt32 NowEpoch()
        {
            return (UInt32)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private static List<Object> RenderProperties(IReadOnlyList<GraphProperty> properties)
        {
            var rendered = new List<Object>(properties.Count);
            foreach (var property in properties)
            {
                rendered.Add(new
                {
                    propertyId = property.Key,
                    fullQualifiedTypeName = property.TypeName,
                    propertyValue = property.Text,
                });
            }

            return rendered;
        }

        private static IReadOnlyList<Int32> Expect(List<Int32>? ids, Int32 expected, String what)
        {
            if (ids == null || ids.Count != expected)
            {
                throw new GraphTargetException(String.Format(CultureInfo.InvariantCulture,
                    "The target created {0} {1} but returned {2} id(s); this run cannot claim what it cannot name.",
                    expected, what, ids == null ? 0 : ids.Count));
            }

            return ids;
        }

        private static Boolean ReadBoolean(JsonElement parent, String name)
        {
            return parent.TryGetProperty(name, out var value) &&
                   (value.ValueKind == JsonValueKind.True ||
                    (value.ValueKind == JsonValueKind.String && Boolean.TryParse(value.GetString(), out var parsed) && parsed));
        }

        private static Int32 ReadInt32(JsonElement parent, String name)
        {
            if (!parent.TryGetProperty(name, out var value))
            {
                return 0;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String &&
                Int32.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            return 0;
        }

        private async Task<Dictionary<String, IReadOnlyList<String>>> ReadIndexInventoryAsync(
            CancellationToken cancellationToken)
        {
            using var status = await GetJsonAsync("status", cancellationToken).ConfigureAwait(false);
            var inventory = new Dictionary<String, IReadOnlyList<String>>(StringComparer.Ordinal);

            if (!status.RootElement.TryGetProperty("indices", out var indices) ||
                indices.ValueKind != JsonValueKind.Array)
            {
                return inventory;
            }

            foreach (var index in indices.EnumerateArray())
            {
                if (!index.TryGetProperty("indexId", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var capabilities = new List<String>();
                if (index.TryGetProperty("capabilities", out var declared) &&
                    declared.ValueKind == JsonValueKind.Array)
                {
                    foreach (var capability in declared.EnumerateArray())
                    {
                        if (capability.ValueKind == JsonValueKind.String)
                        {
                            capabilities.Add(capability.GetString()!);
                        }
                    }
                }

                inventory[idElement.GetString()!] = capabilities;
            }

            return inventory;
        }

        private async Task<Int32> BackfillAsync(String indexId, String propertyPrefix,
            CancellationToken cancellationToken)
        {
            _mutations++;

            using var response = await SendRawAsync(HttpMethod.Post,
                "index/backfill/" + Uri.EscapeDataString(indexId),
                new { propertyId = propertyPrefix, prefix = true, replace = false },
                cancellationToken).ConfigureAwait(false);

            if (!response.RootElement.TryGetProperty("indexedElements", out var indexed) ||
                !indexed.TryGetInt32(out var count))
            {
                return 0;
            }

            return count;
        }

        private async Task<IReadOnlyList<Int32>> ScanIndexAsync(String indexId, String literal,
            CancellationToken cancellationToken)
        {
            var body = new
            {
                indexId,
                @operator = EqualsOperator,
                literal = new
                {
                    value = literal,
                    fullQualifiedTypeName = WireValues.StringTypeName,
                },
                resultType = "Both",
            };

            try
            {
                var ids = await SendAsync<List<Int32>>(HttpMethod.Post, "scan/index/all", body, cancellationToken)
                    .ConfigureAwait(false);
                return ids ?? (IReadOnlyList<Int32>)Array.Empty<Int32>();
            }
            catch (GraphTargetException)
            {
                // The scan route answers 400 both for a malformed request and for a vanished index, so the
                // index inventory is ASKED rather than the message parsed: a text match would silently stop
                // working the day the wording changes, and the consequence of mistaking the two is the worst
                // in the feature. The check cannot sit in an exception filter, which may not await.
                if (await IsIndexMissingAsync(indexId, cancellationToken).ConfigureAwait(false))
                {
                    throw new GraphIndexMissingException(indexId);
                }

                throw;
            }
        }

        private async Task<Boolean> IsIndexMissingAsync(String indexId, CancellationToken cancellationToken)
        {
            try
            {
                var inventory = await ReadIndexInventoryAsync(cancellationToken).ConfigureAwait(false);
                return !inventory.ContainsKey(indexId);
            }
            catch (GraphTargetException)
            {
                // The status read failed too, so nothing can be concluded about the index; let the original
                // failure stand as a graph failure.
                return false;
            }
        }

        private async Task ReadBatchAsync(List<Int32> ids, Dictionary<Int32, ElementState> result,
            CancellationToken cancellationToken)
        {
            using var document = await SendRawAsync(HttpMethod.Post, "graphelements/get", ids, cancellationToken)
                .ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("elements", out var elements) ||
                elements.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var element in elements.EnumerateArray())
            {
                if (!element.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id))
                {
                    continue;
                }

                var label = element.TryGetProperty("label", out var labelElement) &&
                            labelElement.ValueKind == JsonValueKind.String
                    ? labelElement.GetString()
                    : null;

                var properties = ImmutableDictionary.CreateBuilder<String, GraphProperty>(StringComparer.Ordinal);
                if (element.TryGetProperty("properties", out var propertyList) &&
                    propertyList.ValueKind == JsonValueKind.Array)
                {
                    foreach (var property in propertyList.EnumerateArray())
                    {
                        var key = property.TryGetProperty("propertyId", out var keyElement)
                            ? keyElement.GetString()
                            : null;
                        var typeName = property.TryGetProperty("fullQualifiedTypeName", out var typeElement)
                            ? typeElement.GetString()
                            : null;
                        var text = property.TryGetProperty("propertyValue", out var valueElement)
                            ? valueElement.GetString()
                            : null;

                        if (key == null || typeName == null || text == null)
                        {
                            continue;
                        }

                        properties[key] = new GraphProperty(key, typeName, text);
                    }
                }

                result[id] = new ElementState(id, label, properties.ToImmutable());
            }
        }

        private async Task<JsonDocument> GetJsonAsync(String suffix, CancellationToken cancellationToken)
        {
            return await SendRawAsync(HttpMethod.Get, suffix, null, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        ///   Sends one id-returning write in batches of <see cref="WriteBatchSize" /> and concatenates the
        ///   ids IN BATCH ORDER, which is what makes the result still "the assigned ids in input order" -
        ///   the contract every caller relies on to turn a vertex it just created into the endpoint of an
        ///   edge. A batch that comes back the wrong length fails the run rather than shifting every id
        ///   after it by one, because a run that attached its edges to the wrong vertices has written
        ///   plausible nonsense nothing downstream can detect.
        /// </summary>
        private async Task<List<Int32>> SendBatchedAsync(String suffix, List<Object> body,
            CancellationToken cancellationToken)
        {
            var ids = new List<Int32>(body.Count);

            for (var offset = 0; offset < body.Count; offset += WriteBatchSize)
            {
                var batch = body.GetRange(offset, Math.Min(WriteBatchSize, body.Count - offset));
                var batchIds = await SendAsync<List<Int32>>(HttpMethod.Put, suffix, batch, cancellationToken)
                    .ConfigureAwait(false);

                if (batchIds == null || batchIds.Count != batch.Count)
                {
                    throw new GraphTargetException(String.Format(
                        "The graph answered {0} with {1} id(s) for {2} element(s) in one batch, so the ids " +
                        "no longer line up with what was sent and every element after this batch would be " +
                        "attached to the wrong one.",
                        suffix, batchIds == null ? 0 : batchIds.Count, batch.Count));
                }

                ids.AddRange(batchIds);
            }

            return ids;
        }

        private async Task<T?> SendAsync<T>(HttpMethod method, String suffix, Object? body,
            CancellationToken cancellationToken)
        {
            var text = await SendTextAsync(method, suffix, body, cancellationToken).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(text))
            {
                return default;
            }

            try
            {
                return JsonSerializer.Deserialize<T>(text, RestSeam.JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new GraphTargetException(String.Format(
                    "The target's answer to {0} {1} could not be read: {2}", method, suffix, ex.Message), ex);
            }
        }

        private async Task<JsonDocument> SendRawAsync(HttpMethod method, String suffix, Object? body,
            CancellationToken cancellationToken)
        {
            var text = await SendTextAsync(method, suffix, body, cancellationToken).ConfigureAwait(false);
            try
            {
                return JsonDocument.Parse(String.IsNullOrWhiteSpace(text) ? "{}" : text!);
            }
            catch (JsonException ex)
            {
                throw new GraphTargetException(String.Format(
                    "The target's answer to {0} {1} is not JSON: {2}", method, suffix, ex.Message), ex);
            }
        }

        private async Task SendVoidAsync(HttpMethod method, String suffix, Object? body,
            CancellationToken cancellationToken)
        {
            await SendTextAsync(method, suffix, body, cancellationToken).ConfigureAwait(false);
        }

        private Task<String?> SendTextAsync(HttpMethod method, String suffix, Object? body,
            CancellationToken cancellationToken)
        {
            return RestSeam.SendForBodyAsync(_client, method, _prefix + suffix, body,
                Unanswered(String.Format("{0} {1}", method, suffix)),
                (response, token) => RefusedAsync(method, suffix, response, token),
                cancellationToken);
        }

        /// <summary>
        ///   For the embedding write alone, which reads the status code itself in order to decide between a
        ///   degradation and a failure and so cannot go through <see cref="SendTextAsync"/>.
        /// </summary>
        /// <param name="method">The HTTP method.</param>
        /// <param name="suffix">The route below this target's namespace prefix.</param>
        /// <param name="body">The JSON request body, or null.</param>
        /// <param name="what">How a failure names this call, e.g. "the embedding write".</param>
        /// <param name="cancellationToken">The caller's token.</param>
        /// <returns>The response, which the caller owns and disposes.</returns>
        private Task<HttpResponseMessage> SendForResponseAsync(HttpMethod method, String suffix, Object? body,
            String what, CancellationToken cancellationToken)
        {
            return RestSeam.SendAsync(_client, method, _prefix + suffix, body, Unanswered(what), cancellationToken);
        }

        /// <summary>
        ///   How this target names an answer that never came (<see cref="RestSeam"/> decides WHICH of the two it
        ///   was, and a cancellation the caller asked for never arrives here at all).
        ///
        ///   <para>A timeout gets its own type so a call site can act on "too slow" without reading a message:
        ///   the request may well have been applied and nothing here can tell, which is a third fact beside
        ///   "it refused" and "the caller walked away". Every caller that does not care sees the
        ///   <see cref="GraphTargetException"/> it always saw.</para>
        /// </summary>
        private static RestSendFailureNaming Unanswered(String what)
        {
            return (failure, cause) => failure == RestSendFailure.TimedOut
                ? new GraphTargetTimeoutException(String.Format(
                    "The graph did not answer {0} within the request timeout " +
                    "(Fallen8Target:TimeoutSeconds).", what), cause)
                : new GraphTargetException(String.Format(
                    "The graph did not answer {0}: {1}", what, cause.Message), cause);
        }

        /// <summary>How this target names a status the graph refused with.</summary>
        private static async Task<Exception> RefusedAsync(HttpMethod method, String suffix,
            HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new GraphTargetException(String.Format(
                "The graph refused {0} {1} with {2}: {3}", method, suffix, (Int32)response.StatusCode,
                String.IsNullOrWhiteSpace(detail) ? response.ReasonPhrase : detail.Trim()));
        }
    }
}
