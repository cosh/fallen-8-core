// MIT License
//
// StatusREST.cs
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
using System.ComponentModel;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   Represents the current status of the Fallen-8 database
    /// </summary>
    /// <example>
    /// {
    ///   "usedMemory": 1073741824,
    ///   "vertexCount": 10000,
    ///   "edgeCount": 25000,
    ///   "indices": [{ "indexId": "nameIndex", "pluginType": "DictionaryIndex" }],
    ///   "availableIndexPlugins": ["DictionaryIndex", "SpatialIndex"],
    ///   "availablePathPlugins": ["Dijkstra", "AStar"],
    ///   "availableAnalyticsPlugins": ["PAGERANK", "WCC"],
    ///   "availableServicePlugins": ["ImportService", "ExportService"],
    ///   "apiKeyRequired": false,
    ///   "authenticated": false
    /// }
    /// </example>
    public sealed class StatusREST
    {
        /// <summary>
        ///   The database process's working set (physical RAM currently in use), in bytes — the
        ///   figure an operator reads as "memory used" (matches /statistics processWorkingSetBytes).
        ///   NOT the reserved virtual address space, which modern .NET makes enormous.
        /// </summary>
        /// <example>1073741824</example>
        [DefaultValue(1073741824L)] // Using long literal by adding 'L' suffix
        public Int64 UsedMemory
        {
            get; set;
        }

        /// <summary>
        ///   The total number of vertices in the database
        /// </summary>
        /// <example>10000</example>
        [DefaultValue(10000)]
        public Int32 VertexCount
        {
            get; set;
        }

        /// <summary>
        ///   The total number of edges in the database
        /// </summary>
        /// <example>25000</example>
        [DefaultValue(25000)]
        public Int32 EdgeCount
        {
            get; set;
        }

        /// <summary>
        ///   The indices currently registered on this instance (id + plugin type) — the live
        ///   inventory, available without running the budgeted statistics pass
        /// </summary>
        public List<IndexDescriptionREST> Indices
        {
            get; set;
        }

        /// <summary>
        ///   List of available index plugins that can be used with the database
        /// </summary>
        /// <example>["DictionaryIndex", "SpatialIndex", "FullTextIndex"]</example>
        public List<String> AvailableIndexPlugins
        {
            get; set;
        }

        /// <summary>
        ///   List of available path-finding algorithm plugins
        /// </summary>
        /// <example>["Dijkstra", "AStar", "BellmanFord"]</example>
        public List<String> AvailablePathPlugins
        {
            get; set;
        }

        /// <summary>
        ///   List of available subgraph algorithm plugins (reflection-discovered built-ins unioned
        ///   with the addressed namespace's runtime-registered subgraph plugins)
        /// </summary>
        /// <example>["Breadth First Search Subgraph Algorithm"]</example>
        public List<String> AvailableSubGraphPlugins
        {
            get; set;
        }

        /// <summary>
        ///   List of available graph-analytics algorithm plugins
        /// </summary>
        /// <example>["PAGERANK", "WCC", "LABELPROPAGATION", "DEGREE", "TRIANGLECOUNT"]</example>
        public List<String> AvailableAnalyticsPlugins
        {
            get; set;
        }

        /// <summary>
        ///   List of available service plugins that can be started with the database
        /// </summary>
        /// <example>["ImportService", "ExportService", "AnalyticsService"]</example>
        public List<String> AvailableServicePlugins
        {
            get; set;
        }

        /// <summary>
        ///   True when this server has an API key configured, i.e. every endpoint outside the
        ///   anonymous allowlist answers 401 without a valid credential. /status itself stays
        ///   anonymous, so it doubles as the connection probe: a caller is authorized iff
        ///   <c>!ApiKeyRequired || Authenticated</c>.
        /// </summary>
        /// <example>false</example>
        public Boolean ApiKeyRequired
        {
            get; set;
        }

        /// <summary>
        ///   True when the request that produced this status carried a valid credential
        ///   (see <see cref="ApiKeyRequired"/> for how clients combine the two).
        /// </summary>
        /// <example>false</example>
        public Boolean Authenticated
        {
            get; set;
        }

        /// <summary>
        ///   The embedding provider state (feature embedding-provider) — here on the cheap
        ///   discovery surface because it is a config read, not a graph pass; null only when
        ///   the host wired no provider. See <see cref="EmbeddingProviderStatsREST"/>.
        /// </summary>
        public EmbeddingProviderStatsREST Embedding
        {
            get; set;
        }

        /// <summary>
        ///   The chat gateway state (feature instance-config) — a config read like
        ///   <see cref="Embedding"/>, so it rides the cheap probe for capability discovery
        ///   (e.g. the MCP overview's chatEnabled). Null when the host wired no provider. The
        ///   GPU field stays null here; GET /config carries the probed value.
        /// </summary>
        public ChatProviderStatsREST Chat
        {
            get; set;
        }

        /// <summary>
        ///   The unstructured-ingestion state (feature unstructured-ingestion): a config read
        ///   plus a cached sidecar probe, so it rides the cheap discovery surface too. Null
        ///   only when the host wired no ingestion options.
        /// </summary>
        public IngestionStatsREST Ingestion
        {
            get; set;
        }

        /// <summary>
        ///   The semantic-layer NLP enrichment state (feature semantic-layer): a config read
        ///   plus a cached sidecar probe. Null only when the host wired no NLP options.
        /// </summary>
        public NlpStatsREST Nlp
        {
            get; set;
        }

        /// <summary>
        ///   Whether this namespace's writes are actually reaching disk, and whether the state being
        ///   served is the complete committed history or a prefix of it (feature
        ///   platform-integrity-audit W5). A cheap read of state the engine already publishes.
        /// </summary>
        public DurabilityStatusREST Durability
        {
            get; set;
        }
    }

    /// <summary>
    ///   The durability and recovery-integrity block on <c>GET /status</c> (feature
    ///   platform-integrity-audit W5).
    ///
    ///   <para>Every fact here was already computed by the engine and reachable nowhere: the
    ///   degraded-log state existed only as an OpenTelemetry gauge, so it existed only if the operator
    ///   had wired a collector, and a truncated recovery logged one error and became an activity tag.
    ///   A client could therefore write into a degraded log, receive success for every write, and lose
    ///   all of them on the next kill.</para>
    ///
    ///   <para><b>Who needs it and why.</b> Any writer that DELETES state because "nothing asserts it
    ///   any more" is reading that conclusion out of graph content. If the content is a
    ///   post-truncation prefix, or if an index it reasoned over was dropped from the last checkpoint,
    ///   the conclusion is wrong and the deletion is the one mutation that re-syncing cannot undo.
    ///   Such a client checks this block first and DEFERS the deletion when anything here is set:
    ///   deferring is recoverable, deleting wrongly is not.</para>
    /// </summary>
    public sealed class DurabilityStatusREST
    {
        /// <summary>
        ///   Whether a write-ahead log is configured. When false, a committed transaction is durable
        ///   only as far as the last checkpoint - the documented volatile/no-WAL posture, not a fault,
        ///   which is why <see cref="Degraded" /> stays false and this flag distinguishes the two.
        /// </summary>
        /// <example>true</example>
        public Boolean WalEnabled
        {
            get; set;
        }

        /// <summary>
        ///   Whether write durability is DEGRADED right now: the sticky failure fence tripped, or an
        ///   anchored log is awaiting its paired snapshot load. Transactions still commit in memory and
        ///   still report success, so this is the only signal that they are not reaching disk. A
        ///   successful save clears it.
        /// </summary>
        /// <example>false</example>
        public Boolean Degraded
        {
            get; set;
        }

        /// <summary>Whether a log recovery has run in this engine's lifetime. When false the two
        /// recovery fields below carry no information.</summary>
        /// <example>false</example>
        public Boolean RecoveryRan
        {
            get; set;
        }

        /// <summary>
        ///   Whether the last recovery stopped BEFORE the end of the log. Replay is fail-stop for
        ///   core-data entries, because continuing past a bad one would misapply every later entry
        ///   against a diverged id space - so the graph is internally consistent but is a PREFIX of the
        ///   committed history.
        /// </summary>
        /// <example>false</example>
        public Boolean LastRecoveryTruncated
        {
            get; set;
        }

        /// <summary>How many log entries the last recovery replayed.</summary>
        /// <example>0</example>
        public Int32 LastRecoveryReplayedEntries
        {
            get; set;
        }

        /// <summary>
        ///   How many indices the last checkpoint could not persist and dropped from its manifest.
        ///   Dropping is deliberate - one failing index must not cost the whole checkpoint - but the
        ///   next load then comes up with every element intact and those indices gone. Index content is
        ///   derived state, so the repair is <c>POST /index/backfill/{indexId}</c>; this number is what
        ///   tells a client it needs to.
        /// </summary>
        /// <example>0</example>
        public Int32 LastCheckpointDroppedIndices
        {
            get; set;
        }
    }
}
