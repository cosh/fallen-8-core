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
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NoSQL.GraphDB.Integrations.Identity;

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
    /// </summary>
    public sealed class Fallen8RestTarget : IGraphTarget
    {
        /// <summary>Ids per batch read. Well under the platform's page cap, and small enough that one
        /// failure does not lose a whole run's worth of work.</summary>
        private const Int32 ReadBatchSize = 2000;

        /// <summary>The equality operator's wire code, from the engine's own operator enum.</summary>
        private const Int32 EqualsOperator = 0;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

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
            var ids = await SendAsync<List<Int32>>(HttpMethod.Put, "vertices?waitForCompletion=true", body,
                cancellationToken).ConfigureAwait(false);
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

            var ids = await SendAsync<List<Int32>>(HttpMethod.Put, "edges?waitForCompletion=true", body,
                cancellationToken).ConfigureAwait(false);
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

            await SendVoidAsync(HttpMethod.Put, "graphelements/properties?waitForCompletion=true", body,
                cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task RemoveElementsAsync(IReadOnlyCollection<Int32> ids, CancellationToken cancellationToken)
        {
            if (ids == null || ids.Count == 0)
            {
                return;
            }

            _mutations++;

            await SendVoidAsync(HttpMethod.Delete, "graphelements?waitForCompletion=true",
                new List<Int32>(ids), cancellationToken).ConfigureAwait(false);
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

            foreach (var entry in entries)
            {
                var body = new
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
                };

                var ok = await SendAsync<Boolean>(HttpMethod.Put, "index/" + Uri.EscapeDataString(entry.IndexId),
                    body, cancellationToken).ConfigureAwait(false);
                if (ok)
                {
                    accepted++;
                }
                else
                {
                    declined.Add(entry);
                }
            }

            return new IndexWriteOutcome(accepted, declined.ToImmutable());
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
            IReadOnlyList<SummaryWrite> summaries, CancellationToken cancellationToken)
        {
            if (summaries == null || summaries.Count == 0)
            {
                return EmbeddingWriteOutcome.None;
            }

            var items = new List<Object>(summaries.Count);
            foreach (var summary in summaries)
            {
                items.Add(new { graphElementId = summary.ElementId, text = summary.Text });
            }

            _mutations++;

            using var request = new HttpRequestMessage(HttpMethod.Post, _prefix + "embedding/elements")
            {
                Content = JsonContent.Create(new { name = embeddingName, items }, mediaType: null, JsonOptions),
            };

            var response = await SendCoreAsync(request, "the embedding write", cancellationToken)
                .ConfigureAwait(false);

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    return new EmbeddingWriteOutcome(summaries.Count, null);
                }

                var status = (Int32)response.StatusCode;

                // 403 is the capability switched off, 502 and 503 are the backend not answering. All three
                // DEGRADE TO ABSENT rather than failing a run whose whole purpose is the graph write: an
                // embedding is an addition to what landed, never a precondition for it. Anything else is a real
                // graph failure and surfaces as one.
                if (status == 403 || status == 502 || status == 503)
                {
                    var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    return new EmbeddingWriteOutcome(0, String.Format(CultureInfo.InvariantCulture,
                        "the target answered {0} to the embedding write ({1})", status,
                        String.IsNullOrWhiteSpace(detail) ? response.ReasonPhrase : detail.Trim()));
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new GraphTargetException(String.Format(CultureInfo.InvariantCulture,
                    "The graph refused the embedding write with {0}: {1}", status,
                    String.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body.Trim()));
            }
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
        /// </summary>
        private static String BuildPrefix(String? namespaceName)
        {
            if (String.IsNullOrWhiteSpace(namespaceName) ||
                String.Equals(namespaceName, "default", StringComparison.OrdinalIgnoreCase))
            {
                return String.Empty;
            }

            return "ns/" + Uri.EscapeDataString(namespaceName!) + "/";
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
                return JsonSerializer.Deserialize<T>(text, JsonOptions);
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

        private async Task<String?> SendTextAsync(HttpMethod method, String suffix, Object? body,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(method, _prefix + suffix);
            if (body != null)
            {
                request.Content = JsonContent.Create(body, mediaType: null, JsonOptions);
            }

            var response = await SendCoreAsync(request, String.Format("{0} {1}", method, suffix), cancellationToken)
                .ConfigureAwait(false);

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    throw new GraphTargetException(String.Format(
                        "The graph refused {0} {1} with {2}: {3}", method, suffix, (Int32)response.StatusCode,
                        String.IsNullOrWhiteSpace(detail) ? response.ReasonPhrase : detail.Trim()));
                }

                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    return null;
                }

                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return String.IsNullOrWhiteSpace(text) || text.Trim() == "null" ? null : text;
            }
        }

        /// <summary>
        ///   The ONE place a transport failure becomes this seam's failure, for every request this target sends,
        ///   including the embedding write, which reads the status code itself and so cannot go through
        ///   <see cref="SendTextAsync"/>.
        ///
        ///   <para>A client-side timeout arrives as a <c>TaskCanceledException</c>, which IS an
        ///   <see cref="OperationCanceledException"/>. Letting one escape would present "the target was too slow"
        ///   to every layer above as "the caller walked away", and those two license opposite statements about
        ///   what a run wrote. The token is consulted rather than the type, because a cancellation the caller DID
        ///   request must stay a cancellation.</para>
        /// </summary>
        /// <param name="request">The prepared request; the caller owns and disposes it.</param>
        /// <param name="what">How the failure names this call, e.g. "PUT vertices" or "the embedding write".</param>
        /// <param name="cancellationToken">The caller's token, and the only thing that distinguishes its
        /// cancellation from a timeout.</param>
        private async Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, String what,
            CancellationToken cancellationToken)
        {
            try
            {
                return await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new GraphTargetException(String.Format(
                    "The graph did not answer {0}: {1}", what, ex.Message), ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new GraphTargetException(String.Format(
                    "The graph did not answer {0} within the request timeout.", what), ex);
            }
        }
    }
}
