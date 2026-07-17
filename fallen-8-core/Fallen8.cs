// MIT License
//
// Fallen8.cs
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Cache;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Fulltext;
using NoSQL.GraphDB.Core.Index.Range;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Persistency;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.ChangeFeed;
using NoSQL.GraphDB.Core.Service;
using NoSQL.GraphDB.Core.StoredQueries;
using NoSQL.GraphDB.Core.SubGraph;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Core
{
    public sealed class Fallen8 : AFallen8
    {
        #region Data

        /// <summary>
        ///   An immutable, point-in-time view of the master store: a segmented array where the
        ///   global index equals the element id (<c>id == index</c>), split into fixed-size
        ///   segments so that appends never copy the whole store and large segments never hit the
        ///   Large Object Heap. <see cref="Count" /> is the number of live slots; a reader must
        ///   only read ids in <c>[0, Count)</c>. A published snapshot is treated as immutable: the
        ///   writer only ever writes slots at index &gt;= the currently published <see cref="Count" />
        ///   (never read by a current reader) and then republishes with a larger Count, or builds a
        ///   fresh snapshot (Trim/Load). See <see cref="AppendGraphElement" /> for the publication
        ///   ordering rationale.
        /// </summary>
        private sealed class Snapshot
        {
            /// <summary>The segments. Segment <c>s</c> holds ids <c>[s*SegmentSize, (s+1)*SegmentSize)</c>.</summary>
            internal readonly AGraphElementModel[][] Segments;

            /// <summary>The number of live slots (ids <c>0 .. Count-1</c>). Published LAST.</summary>
            internal readonly int Count;

            internal Snapshot(AGraphElementModel[][] segments, int count)
            {
                Segments = segments;
                Count = count;
            }
        }

        /// <summary>
        ///   log2(SegmentSize). 4096 elements/segment => 32 KB per segment array (8-byte
        ///   references), comfortably below the ~85 KB Large Object Heap threshold, so segment
        ///   allocations stay on the (compacting) small-object heap.
        /// </summary>
        private const int SegmentShift = 12;
        private const int SegmentSize = 1 << SegmentShift;
        private const int SegmentMask = SegmentSize - 1;

        /// <summary>Shared empty snapshot. Never mutated (appends always allocate a new segment).</summary>
        private static readonly Snapshot EmptySnapshot = new Snapshot(Array.Empty<AGraphElementModel[]>(), 0);

        /// <summary>
        ///   The graph elements storage, held behind a single <c>volatile</c> reference so the
        ///   publish (release) / capture (acquire) pair is correct on weak-memory (for example
        ///   ARM) hardware, not only x86. Thread safety is provided by the TransactionManager:
        ///   every mutation runs on its single worker thread and publishes a new <see cref="Snapshot" />
        ///   by an atomic reference swap of this field, while lock-free readers capture the current
        ///   reference exactly once and get a consistent O(1)-indexable snapshot.
        /// </summary>
        private volatile Snapshot _snapshot;

        /// <summary>
        /// The delegate to find elements in the big list
        /// </summary>
        /// <param name="objectOfT">The to be analyzed object of T</param>
        /// <returns>True or false</returns>
        public delegate Boolean ElementSeeker(AGraphElementModel objectOfT);

        /// <summary>
        ///   The index factory.
        /// </summary>
        public override IndexFactory IndexFactory
        {
            get; internal set;
        }

        /// <summary>
        ///   The service factory.
        /// </summary>
        public override ServiceFactory ServiceFactory
        {
            get; internal set;
        }

        /// <summary>
        ///   The subgraph factory.
        /// </summary>
        public override SubGraphFactory SubGraphFactory
        {
            get; internal set;
        }

        /// <summary>
        ///   The compiler used to rebuild persisted subgraphs on load. Null unless set by the
        ///   hosting layer (for example the REST API).
        /// </summary>
        public override ISubGraphRecipeCompiler SubGraphRecipeCompiler
        {
            get; set;
        }

        /// <summary>
        ///   The stored query library (feature stored-query-library).
        /// </summary>
        public override StoredQueryLibrary StoredQueries
        {
            get; internal set;
        }

        /// <summary>
        ///   The change feed (feature change-feed), or null when the engine was constructed
        ///   without <see cref="ChangeFeedOptions"/> - the write path then pays only a null check.
        /// </summary>
        public override ChangeFeedDispatcher ChangeFeed
        {
            get; internal set;
        }

        /// <summary>
        ///   The compiler used to (re)build stored query artifacts from their persisted source.
        ///   Null unless set by the hosting layer (for example the REST API).
        /// </summary>
        public override IStoredQueryCompiler StoredQueryCompiler
        {
            get; set;
        }

        /// <summary>
        /// The count of edges
        /// </summary>
        public override Int32 EdgeCount
        {
            get; protected set;
        }

        /// <summary>
        /// The count of vertices
        /// </summary>
        public override Int32 VertexCount
        {
            get; protected set;
        }

        /// <summary>
        ///   The current identifier.
        /// </summary>
        private Int32 _currentId = 0;

        /// <summary>
        ///   Runtime intern table for the low-cardinality, schema-like strings that repeat across
        ///   many elements: labels, property keys and edge-property-ids (finding M2). It mirrors
        ///   what the load path's string token table already does, but for the runtime create /
        ///   <c>SetProperty</c> paths: without it, every element deserialized from a distinct REST
        ///   request holds its OWN copy of the same <c>"person"</c> label or <c>"name"</c> key, so
        ///   N duplicate strings are retained instead of one shared instance. It is populated only
        ///   from the single-writer transaction thread, so a plain dictionary would suffice; a
        ///   <see cref="ConcurrentDictionary{TKey,TValue}"/> is used for defensiveness. Bounded by
        ///   the schema cardinality (distinct labels/keys/edge-property-ids), never by element
        ///   count. Interning is purely a footprint optimisation: it never changes an observable
        ///   value, because it only ever substitutes a value-equal string instance.
        /// </summary>
        private readonly ConcurrentDictionary<String, String> _internTable = new ConcurrentDictionary<String, String>(StringComparer.Ordinal);

        /// <summary>
        ///   Upper bound on the number of distinct strings the <see cref="_internTable" /> retains.
        ///   The table is bounded by schema cardinality (distinct labels / property keys /
        ///   edge-property-ids), so this cap is far above any real schema; it exists only so a
        ///   pathological high-cardinality-key/label workload cannot pin an unbounded number of
        ///   distinct strings for the process lifetime (which would undercut M4's store bounding).
        ///   Past the cap <see cref="Intern" /> becomes a no-op and returns its argument as-is -
        ///   correctness is unaffected because interning only ever substitutes a value-equal
        ///   instance. A field (not a const) so a test can lower it.
        /// </summary>
        internal int _internTableCap = 1_000_000;

        /// <summary>
        ///   Binary operator delegate.
        /// </summary>
        private delegate Boolean BinaryOperatorDelegate(IComparable property, IComparable literal);

        /// <summary>
        /// Cache for all kinds of plugins
        /// </summary>
        private readonly PluginCache _pluginCache = new PluginCache();

        /// <summary>
        /// Transaction manager
        /// </summary>
        private readonly TransactionManager _txManager;

        /// <summary>
        ///   The persistency factory.
        /// </summary>
        private readonly PersistencyFactory _persistencyFactory;

        /// <summary>
        ///   The write-ahead log, or <c>null</c> when the WAL is disabled (the default). When set,
        ///   every committed data-mutating transaction is appended (and fsync'd) here on the single
        ///   writer thread, providing durability between full snapshots (spec P4 / plan Phase 5).
        ///   See <see cref="WriteAheadLogOptions" />.
        /// </summary>
        private WriteAheadLog _wal;

        /// <summary>
        ///   Set while the engine is re-executing logged transactions during recovery. It suppresses
        ///   WAL logging (a replayed operation must not be re-appended to the log it came from) and is
        ///   only ever read/written on the single writer thread (or, for replay-on-open, the
        ///   constructing thread before the instance is published).
        /// </summary>
        private bool _walSuspended;

        /// <summary>
        ///   Set when an ANCHORED write-ahead log is adopted at construction (a log paired with a
        ///   snapshot that has not been loaded yet). While set, logging is suspended: a mutation made
        ///   before the paired <c>Load</c> would otherwise be recorded against the empty initial graph
        ///   in a file whose header claims the snapshot baseline, producing a state that never existed
        ///   on replay (feature crash-durability-hardening D3). Cleared by a paired <c>Load</c> (either
        ///   pairing branch) and by <c>Save</c>. Read/written only on the single writer thread (or the
        ///   constructing thread before the instance is published).
        /// </summary>
        private bool _walAwaitingPairedLoad;

        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger<Fallen8> _logger;

        /// <summary>
        ///   The engine's per-instance metric instruments (feature observability). Created by
        ///   the constructor after the transaction manager, disposed FIRST on teardown so no
        ///   gauge callback can observe torn-down state. Null only after Dispose.
        /// </summary>
        internal Diagnostics.Fallen8Metrics Metrics;

        #endregion

        #region metric gauge accessors (feature observability - atomically published values only)

        /// <summary>Transactions waiting for the single writer (0 during teardown races).</summary>
        internal Int32 TransactionQueueDepthForMetrics => _txManager?.QueueDepth ?? 0;

        /// <summary>The metric face of DurabilityDegraded: the D1 sticky fence has tripped or an
        /// anchored log awaits its paired load (D3).</summary>
        internal Boolean WalDegradedForMetrics
        {
            get
            {
                var wal = _wal;
                return (wal != null && wal.HasFailed) || _walAwaitingPairedLoad;
            }
        }

        /// <summary>The current write-ahead log file length (0 when the WAL is off).</summary>
        internal Int64 WalSizeForMetrics => _wal?.CurrentLength ?? 0L;

        /// <summary>Registered index count.</summary>
        internal Int32 IndexCountForMetrics => IndexFactory?.Indices?.Count ?? 0;

        /// <summary>Total keys across all registered indices (aggregate only - per-index detail
        /// is GET /statistics' job, behind auth; no index NAME ever becomes a metric tag).
        /// Enumerates a read-locked SNAPSHOT of the index map - the exporter's collection
        /// thread must never race a concurrent create/delete on the plain dictionary.</summary>
        internal Int64 IndexEntriesForMetrics
        {
            get
            {
                var factory = IndexFactory;
                if (factory == null)
                {
                    return 0L;
                }

                var total = 0L;
                foreach (var index in factory.GetIndicesSnapshot())
                {
                    total += index.CountOfKeys();
                }
                return total;
            }
        }

        #endregion

        #region Internal Helper Methods

        /// <summary>
        /// Creates a logger for the specified type. Used internally by persistence factory.
        /// </summary>
        internal ILogger<T> CreateLogger<T>()
        {
            return LoggerFactory.CreateLogger<T>();
        }

        #endregion

        #region Constructor

        /// <summary>
        ///   Initializes a new instance of the Fallen-8 class.
        /// </summary>
        public Fallen8(ILoggerFactory loggerfactory)
        {
            LoggerFactory = loggerfactory;
            _logger = loggerfactory.CreateLogger<Fallen8>();

            // Create loggers for factories
            var indexLogger = loggerfactory.CreateLogger<IndexFactory>();
            var serviceLogger = loggerfactory.CreateLogger<ServiceFactory>();
            var subGraphLogger = loggerfactory.CreateLogger<SubGraphFactory>();
            var persistencyLogger = loggerfactory.CreateLogger<PersistencyFactory>();

            IndexFactory = new IndexFactory(this, indexLogger);
            _snapshot = EmptySnapshot;
            ServiceFactory = new ServiceFactory(this, serviceLogger);
            SubGraphFactory = new SubGraphFactory(this, subGraphLogger, _pluginCache);
            StoredQueries = new StoredQueryLibrary(loggerfactory.CreateLogger<StoredQueryLibrary>());
            IndexFactory.Indices.Clear();
            _txManager = new TransactionManager(this);
            _persistencyFactory = new PersistencyFactory(persistencyLogger);

            // Per-engine metric instruments (feature observability): created AFTER the
            // transaction manager (the queue-depth gauge reads it), disposed BEFORE it.
            Metrics = new Diagnostics.Fallen8Metrics(this);
        }

        /// <summary>
        ///   Initializes a new instance of the Fallen-8 class and loads the vertices from a save point.
        /// </summary>
        /// <param name='path'> Path to the save point. </param>
        public Fallen8(String path, ILoggerFactory loggerfactory)
            : this(loggerfactory)
        {
            Load_internal(path, true);
        }

        /// <summary>
        ///   Initializes a new in-memory instance with an OPT-IN change feed (feature change-feed):
        ///   committed mutations become an in-order event stream with catch-up and per-subscriber
        ///   backpressure. Without options the engine carries no feed.
        /// </summary>
        public Fallen8(ILoggerFactory loggerfactory, ChangeFeedOptions changeFeedOptions)
            : this(loggerfactory)
        {
            if (changeFeedOptions != null)
            {
                ChangeFeed = new ChangeFeedDispatcher(changeFeedOptions, loggerfactory.CreateLogger<ChangeFeedDispatcher>());
            }
        }

        /// <summary>
        ///   Initializes a new instance of the Fallen-8 class with an OPT-IN write-ahead log for
        ///   durability between snapshots (spec P4 / plan Phase 5). When
        ///   <paramref name="writeAheadLogOptions" /> is null or carries no path, the WAL is disabled
        ///   and the instance behaves exactly like the default constructor. When a log path is given,
        ///   an existing log at that path is adopted: a log that predates any snapshot is replayed
        ///   immediately onto the (empty) graph, while a log anchored to a snapshot is replayed later
        ///   by <c>Load</c> of that snapshot.
        ///
        ///   <para>An optional <paramref name="subGraphRecipeCompiler" /> is registered BEFORE the log
        ///   is opened, so that an UNANCHORED log's subgraph entries - which replay during construction,
        ///   before any property could be set - can be recompiled and recovered. For the
        ///   snapshot-paired path the compiler may instead be assigned to
        ///   <see cref="SubGraphRecipeCompiler" /> before <c>Load</c> (replay happens during Load); a
        ///   subgraph entry encountered with no compiler registered is skipped with a warning. The
        ///   optional <paramref name="storedQueryCompiler" /> follows the same rule for stored-query
        ///   entries, and an optional <paramref name="changeFeedOptions" /> activates the change feed
        ///   (feature change-feed).</para>
        /// </summary>
        public Fallen8(ILoggerFactory loggerfactory, WriteAheadLogOptions writeAheadLogOptions,
            ISubGraphRecipeCompiler subGraphRecipeCompiler = null,
            IStoredQueryCompiler storedQueryCompiler = null,
            ChangeFeedOptions changeFeedOptions = null)
            : this(loggerfactory)
        {
            if (changeFeedOptions != null)
            {
                ChangeFeed = new ChangeFeedDispatcher(changeFeedOptions, loggerfactory.CreateLogger<ChangeFeedDispatcher>());
            }

            if (subGraphRecipeCompiler != null)
            {
                SubGraphRecipeCompiler = subGraphRecipeCompiler;
            }

            // Registered BEFORE the log is opened for the same reason as the recipe compiler: an
            // unanchored log's RegisterStoredQuery entries replay during construction, and only a
            // compiler present then can recompile them (feature stored-query-library).
            if (storedQueryCompiler != null)
            {
                StoredQueryCompiler = storedQueryCompiler;
            }

            if (writeAheadLogOptions != null && !String.IsNullOrWhiteSpace(writeAheadLogOptions.Path))
            {
                EnableWriteAheadLog(writeAheadLogOptions.Path);
            }
        }

        /// <summary>
        ///   Opens (or creates) the write-ahead log and, if it holds committed transactions that were
        ///   never captured in a snapshot (an "unanchored" log), replays them onto the empty initial
        ///   graph so a crash before the first Save still recovers those transactions. Runs during
        ///   construction, before the instance is handed out, so the replay is single-threaded.
        /// </summary>
        private void EnableWriteAheadLog(string walPath)
        {
            _wal = new WriteAheadLog(walPath, CreateLogger<WriteAheadLog>());

            if (_wal.IsUnanchored)
            {
                var baseline = (int)_wal.BaselineCurrentId;
                SetSnapshotCountForReplay(baseline);
                _currentId = baseline;
                ReplayWriteAheadLog();
                RecalculateGraphElementCounter();
            }
            else if (_wal.IsAnchored)
            {
                // The adopted log pairs with a snapshot that has NOT been loaded yet. Suspend logging
                // until that snapshot is Loaded (or a Save re-baselines), so a mutation made before the
                // paired Load is not recorded against the empty initial graph / wrong baseline
                // (feature crash-durability-hardening D3). Recommended usage is still: Load, then mutate.
                _walAwaitingPairedLoad = true;
            }
        }

        #endregion

        #region master store mutation (single-writer, append-only segmented array)

        /// <summary>
        ///   Appends one element to the master store. Runs ONLY on the single TransactionManager
        ///   writer thread. The new element's id equals its index (<c>= current Count</c>).
        ///
        ///   Publication ordering (the crux of the lock-free contract):
        ///   1. The spare slot being written is at index &gt;= the currently published Count, so
        ///      no reader (which only reads ids in <c>[0, Count)</c>) can observe it yet.
        ///   2. We write that slot FIRST, then publish a NEW holder whose Count is one larger.
        ///      Count is therefore published LAST, and the volatile store of <c>_snapshot</c> is a
        ///      release: a reader that acquires the new holder (and thus the new Count) is
        ///      guaranteed to also see the fully written slot and the fully constructed element.
        ///   3. A reader that still holds the old holder sees the old Count and never touches the
        ///      new slot. So a reader observes either "element absent" or "element fully present",
        ///      never a torn or null slot.
        ///   Growing only allocates a new 32 KB segment (no whole-store copy, no LOH churn); the
        ///   top-level segment array is copied only when a new segment is added (rare).
        /// </summary>
        private void AppendGraphElement(AGraphElementModel element)
        {
            var snap = _snapshot;
            int index = snap.Count;                 // new id == index
            int seg = index >> SegmentShift;
            int slot = index & SegmentMask;

            AGraphElementModel[][] segments = snap.Segments;
            if (seg >= segments.Length)
            {
                // Last segment is full: grow the top-level array (copy-on-write) and add a fresh
                // segment. The old segments array is never mutated, so old-holder readers are safe.
                var grown = new AGraphElementModel[seg + 1][];
                Array.Copy(segments, grown, segments.Length);
                grown[seg] = new AGraphElementModel[SegmentSize];
                segments = grown;
            }

            segments[seg][slot] = element;          // (1)+(2): write the spare slot FIRST ...
            _snapshot = new Snapshot(segments, index + 1); // ... then publish Count LAST (release).
        }

        /// <summary>
        ///   Appends a batch of elements in ONE publication (single Count bump). Same ordering
        ///   guarantee as <see cref="AppendGraphElement" />: every slot written is at index
        ///   &gt;= the old Count, all slots are written before the new holder (with the larger
        ///   Count) is published. <paramref name="elements"/> is a covariant read-only list so a
        ///   <c>List&lt;VertexModel&gt;</c>/<c>List&lt;EdgeModel&gt;</c> can be passed directly.
        /// </summary>
        private void AppendGraphElements(IReadOnlyList<AGraphElementModel> elements)
        {
            int n = elements?.Count ?? 0;
            if (n == 0)
            {
                return;
            }

            var snap = _snapshot;
            int startCount = snap.Count;
            int newCount = startCount + n;

            // Ensure enough segments for newCount (copy-on-write the top-level array if it grows).
            int neededSegments = (newCount + SegmentMask) >> SegmentShift;
            AGraphElementModel[][] segments = snap.Segments;
            if (neededSegments > segments.Length)
            {
                var grown = new AGraphElementModel[neededSegments][];
                Array.Copy(segments, grown, segments.Length);
                for (int s = segments.Length; s < neededSegments; s++)
                {
                    grown[s] = new AGraphElementModel[SegmentSize];
                }
                segments = grown;
            }

            // Write every element into its (index >= startCount) slot BEFORE publishing the Count.
            for (int i = 0; i < n; i++)
            {
                int index = startCount + i;
                segments[index >> SegmentShift][index & SegmentMask] = elements[i];
            }

            _snapshot = new Snapshot(segments, newCount); // publish Count LAST (release).
        }

        /// <summary>
        ///   Resolves an element by id for a single-writer mutation. Preserves the historical
        ///   contract that an out-of-range id throws <see cref="ArgumentOutOfRangeException"/>:
        ///   the original <c>ImmutableList</c> indexer threw that type, whereas a raw array
        ///   indexer would throw <see cref="IndexOutOfRangeException"/>. The returned element may
        ///   itself be null (a slot left empty by a load).
        /// </summary>
        private AGraphElementModel GetGraphElementForMutation(Int32 graphElementId)
        {
            var snap = _snapshot;
            if (graphElementId < 0 || graphElementId >= snap.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(graphElementId));
            }
            return snap.Segments[graphElementId >> SegmentShift][graphElementId & SegmentMask];
        }

        /// <summary>
        ///   Resolves an element's category and label for the change feed (feature change-feed),
        ///   INCLUDING tombstoned (soft-removed) elements - a removal descriptor is captured after
        ///   the element was marked removed, and removal is a soft-delete that keeps the model in
        ///   its slot. WRITER THREAD ONLY (descriptor capture).
        /// </summary>
        internal bool TryDescribeElement(Int32 graphElementId, out ChangeElementType elementType, out String label)
        {
            elementType = ChangeElementType.None;
            label = null;

            var snap = _snapshot;
            if (graphElementId < 0 || graphElementId >= snap.Count)
            {
                return false;
            }

            var element = snap.Segments[graphElementId >> SegmentShift][graphElementId & SegmentMask];
            if (element == null)
            {
                return false;
            }

            elementType = element is VertexModel ? ChangeElementType.Vertex : ChangeElementType.Edge;
            label = element.Label;
            return true;
        }

        /// <summary>
        ///   Describes one element THIS transaction removed - plus, for a vertex, its
        ///   cascade-removed edges - into a change descriptor (feature change-feed). WRITER THREAD
        ///   ONLY, called from a removal transaction's <c>DescribeChanges</c> after a successful
        ///   execute. Cascades enumerate the removed vertex's own raw adjacency: an edge removed
        ///   EARLIER (directly, or by the other endpoint's removal) was detached from this vertex's
        ///   containers at that time, so exactly the edges this removal cascaded remain - a
        ///   self-loop (present in both directions) is deduplicated by id.
        /// </summary>
        internal void DescribeRemovedElement(Int32 graphElementId, ChangeDescriptor.Builder builder)
        {
            var snap = _snapshot;
            if (graphElementId < 0 || graphElementId >= snap.Count)
            {
                return;
            }

            var element = snap.Segments[graphElementId >> SegmentShift][graphElementId & SegmentMask];

            if (element is VertexModel vertex)
            {
                builder.VertexRemoved(vertex.Id, vertex.Label);

                var seenEdges = new HashSet<Int32>();

                var outgoing = vertex.GetRawOutEdges();
                if (outgoing != null)
                {
                    foreach (var edgesPerProperty in outgoing)
                    {
                        foreach (var edge in edgesPerProperty.Value)
                        {
                            if (seenEdges.Add(edge.Id))
                            {
                                builder.EdgeRemoved(edge.Id, edge.Label);
                            }
                        }
                    }
                }

                var incoming = vertex.GetRawInEdges();
                if (incoming != null)
                {
                    foreach (var edgesPerProperty in incoming)
                    {
                        foreach (var edge in edgesPerProperty.Value)
                        {
                            if (seenEdges.Add(edge.Id))
                            {
                                builder.EdgeRemoved(edge.Id, edge.Label);
                            }
                        }
                    }
                }
            }
            else if (element is EdgeModel removedEdge)
            {
                builder.EdgeRemoved(removedEdge.Id, removedEdge.Label);
            }
        }

        /// <summary>
        ///   Resolves a vertex by id for WIRING AN EDGE, returning <c>null</c> - rather than
        ///   throwing - when the id is out of range, the slot is empty (left by a load), the element
        ///   is not a vertex, or the vertex has been removed. Unlike
        ///   <see cref="GetGraphElementForMutation" /> (whose historical out-of-range throw is relied
        ///   on by the removal/property paths), this lets <c>CreateEdge(s)</c> fail a client-caused
        ///   missing/removed endpoint CLEANLY (NotFound) instead of letting the master-store bounds
        ///   check throw - which used to surface as a misleading 500. Single-writer, so a vertex
        ///   resolved here cannot be concurrently removed before the edge is wired.
        /// </summary>
        private VertexModel TryResolveLiveVertexForEdge(Int32 vertexId)
        {
            var snap = _snapshot;
            if (vertexId < 0 || vertexId >= snap.Count)
            {
                return null;
            }

            var vertex = snap.Segments[vertexId >> SegmentShift][vertexId & SegmentMask] as VertexModel;
            if (vertex == null || vertex._removed)
            {
                return null;
            }

            return vertex;
        }

        /// <summary>
        ///   Builds a segmented <see cref="Snapshot" /> from a dense, id-ordered source array
        ///   (index == id). Used by Load (from the on-disk flat array) and Trim (from the freshly
        ///   compacted array). The final segment keeps its full <see cref="SegmentSize" /> spare
        ///   capacity so subsequent appends reuse it.
        /// </summary>
        private static Snapshot BuildSnapshotFromDenseArray(AGraphElementModel[] source, int count)
        {
            if (count == 0)
            {
                return EmptySnapshot;
            }

            int segCount = (count + SegmentMask) >> SegmentShift;
            var segments = new AGraphElementModel[segCount][];
            for (int s = 0; s < segCount; s++)
            {
                var segment = new AGraphElementModel[SegmentSize];
                int baseIndex = s << SegmentShift;
                int len = Math.Min(SegmentSize, count - baseIndex);
                Array.Copy(source, baseIndex, segment, 0, len);
                segments[s] = segment;
            }
            return new Snapshot(segments, count);
        }

        /// <summary>
        ///   A PARALLEL query over the live elements (ids <c>[0, Count)</c>) of the given snapshot.
        ///   Reserved for the genuinely heavy full-graph scan with a user predicate
        ///   (<see cref="FindElements(ElementSeeker, String)" />); the light-predicate enumerations
        ///   (counts, GetAll*) use the sequential <see cref="LiveElementsSequential" /> instead, where
        ///   PLINQ's partition/merge overhead would exceed the per-element work (finding P7). Uses a
        ///   range partitioner (no source-array allocation), bounded by
        ///   <see cref="ParallelHelper.GetOptimalNumberOfTasks" /> (clamped to at least 1, since it can
        ///   compute 0 on a single-core host). Elements may be null (load-left gaps); callers filter.
        /// </summary>
        private static ParallelQuery<AGraphElementModel> LiveElements(Snapshot snap)
        {
            var segments = snap.Segments;
            return ParallelEnumerable.Range(0, snap.Count)
                .WithDegreeOfParallelism(Math.Max(1, ParallelHelper.GetOptimalNumberOfTasks()))
                .Select(i => segments[i >> SegmentShift][i & SegmentMask]);
        }

        /// <summary>
        ///   A SEQUENTIAL enumeration over the live elements (ids <c>[0, Count)</c>) of the given
        ///   snapshot, in id order. Used for the light-predicate scans (counts, GetAll*) where the
        ///   per-element work is a cheap null/removed/type/label check and PLINQ overhead is not worth
        ///   paying (finding P7). Consecutive ids map to consecutive slots within a segment, so the
        ///   walk is cache-friendly. Elements may be null (load-left gaps); callers filter.
        /// </summary>
        private static IEnumerable<AGraphElementModel> LiveElementsSequential(Snapshot snap)
        {
            var segments = snap.Segments;
            int count = snap.Count;
            for (int i = 0; i < count; i++)
            {
                yield return segments[i >> SegmentShift][i & SegmentMask];
            }
        }

        #endregion

        /// <summary>
        ///   Interns a schema-like string (label / property key / edge-property-id) so all
        ///   elements that use the same value share one instance (finding M2). Returns the
        ///   argument unchanged for <c>null</c>. The returned string is always value-equal to the
        ///   argument, so callers observe no change.
        /// </summary>
        internal String Intern(String value)
        {
            if (value == null)
            {
                return null;
            }

            // Bound the table (see _internTableCap): once it holds a cap's worth of distinct
            // strings, stop adding new ones and return the argument unchanged, so interning becomes
            // a no-op past the cap. The Count read races with a concurrent writer, but the table is
            // populated only from the single transaction-writer thread and the goal is bounding,
            // not an exact size, so a slight overshoot is harmless. Correctness is unaffected
            // either way: the result is always value-equal to the argument.
            if (_internTable.Count >= _internTableCap)
            {
                return value;
            }

            return _internTable.GetOrAdd(value, value);
        }

        /// <summary>
        ///   Returns a property dictionary whose KEYS are interned (finding M2). The values are
        ///   never touched (they are user data, not schema). Returns the input unchanged when it
        ///   is null or empty (an empty/absent property map allocates no container, per M1).
        /// </summary>
        private Dictionary<String, Object> InternPropertyKeys(Dictionary<String, Object> properties)
        {
            if (properties == null || properties.Count == 0)
            {
                return properties;
            }

            var interned = new Dictionary<String, Object>(properties.Count, StringComparer.Ordinal);
            foreach (var kv in properties)
            {
                interned[Intern(kv.Key)] = kv.Value;
            }
            return interned;
        }

        internal VertexModel CreateVertex_internal(UInt32 creationDate, String label, Dictionary<String, Object> properties = null)
        {
            //create the new vertex (interning the label and property keys, finding M2)
            var newVertex = new VertexModel(_currentId, creationDate, Intern(label), InternPropertyKeys(properties));

            //insert it
            AppendGraphElement(newVertex);

            //increment the id (single-writer field: a plain increment, consistent with the
            //VertexCount/EdgeCount counters beside it - finding P10)
            _currentId++;

            //Increase the vertex count
            VertexCount++;

            // Elements created WITH embedding properties feed bound vector indices
            // (feature element-embeddings; the bulk-import path). A residual rollback
            // compensates via the standard removal, whose index purge undoes this.
            ProjectAllEmbeddingsOf(newVertex);

            return newVertex;
        }

        internal List<VertexModel> CreateVertices_internal(List<VertexDefinition> definitions, out Boolean inputValid)
        {
            // Construct-then-commit (feature transaction-atomicity): nothing is mutated - not the id
            // counter, not VertexCount, not the store - until every model in the batch has been built
            // successfully. So a throw (or a structurally invalid definition) before the atomic append
            // leaves the engine byte-for-byte as it was, and the id == index invariant is preserved
            // under every failure path (the old code advanced _currentId/VertexCount per definition
            // BEFORE the append, so a mid-loop throw left _currentId > Count permanently).
            inputValid = true;
            var newVertices = new List<VertexModel>();

            if (definitions == null || definitions.Count == 0)
            {
                return newVertices;
            }

            // 1. Validate structure up front WITHOUT mutating: a null definition (a JSON array element
            //    can be null) rolls the whole batch back cleanly as InvalidInput rather than NRE-ing
            //    mid-loop after some ids were already consumed.
            foreach (var aVertexDef in definitions)
            {
                if (aVertexDef == null)
                {
                    inputValid = false;
                    return newVertices; // nothing built, nothing mutated
                }
            }

            // 2. Build every model against a LOCAL id counter seeded from _currentId. A throw here
            //    (e.g. OOM) still leaves the engine untouched - no compensation needed.
            var nextId = _currentId;
            foreach (var aVertexDef in definitions)
            {
                //create the new vertex (interning the label and property keys, finding M2)
                newVertices.Add(new VertexModel(nextId, aVertexDef.CreationDate, Intern(aVertexDef.Label), InternPropertyKeys(aVertexDef.Properties)));
                nextId++;
            }

            // 3. Commit atomically: one append (one Count bump), THEN advance the counters. The append
            //    publishes Count last, so a reader sees either none or all of the batch.
            AppendGraphElements(newVertices);
            _currentId = nextId;
            VertexCount += newVertices.Count;

            // Bound-index projection of creation-time embeddings (feature element-embeddings);
            // a residual rollback compensates via the standard removal purge.
            foreach (var newVertex in newVertices)
            {
                ProjectAllEmbeddingsOf(newVertex);
            }

            return newVertices;
        }

        /// <summary>
        ///   THE live-element resolve (feature code-quality: one implementation instead of one
        ///   per element type). Captures the published snapshot once (volatile acquire) so the
        ///   bound check and the indexer operate on the same holder - a concurrent single-writer
        ///   append or Trim can never make this read observe a Count that disagrees with the
        ///   segments it indexes (no out-of-range, no torn/null slot within [0, Count)). The
        ///   <c>as</c> cast makes a type mismatch (asking for a vertex at an edge id) a clean
        ///   false, like a missing or tombstoned element.
        /// </summary>
        private bool TryGetLiveElement<T>(out T result, int id) where T : AGraphElementModel
        {
            var snap = _snapshot;

            if (id < 0 || id >= snap.Count)
            {
                result = null;
                return false;
            }

            result = snap.Segments[id >> SegmentShift][id & SegmentMask] as T;
            return result != null && !result._removed;
        }

        public override bool TryGetGraphElement(out AGraphElementModel result, int id)
        {
            return TryGetLiveElement(out result, id);
        }

        public override bool TryGetEdge(out EdgeModel result, int id)
        {
            return TryGetLiveElement(out result, id);
        }

        public override bool TryGetVertex(out VertexModel result, int id)
        {
            return TryGetLiveElement(out result, id);
        }



        public override bool GraphScan(out List<AGraphElementModel> result, String propertyId, IComparable literal,
            BinaryOperator binOp = BinaryOperator.Equals, String interestingLabel = null)
        {
            if (string.IsNullOrWhiteSpace(propertyId))
            {
                throw new ArgumentException("Property ID cannot be null or whitespace.", nameof(propertyId));
            }

            if (literal == null)
            {
                throw new ArgumentNullException(nameof(literal));
            }

            #region binary operation

            switch (binOp)
            {
                case BinaryOperator.Equals:
                    result = FindElements(BinaryEqualsMethod, literal, propertyId, interestingLabel);
                    break;

                case BinaryOperator.Greater:
                    result = FindElements(BinaryGreaterMethod, literal, propertyId, interestingLabel);
                    break;

                case BinaryOperator.GreaterOrEquals:
                    result = FindElements(BinaryGreaterOrEqualMethod, literal, propertyId, interestingLabel);
                    break;

                case BinaryOperator.LowerOrEquals:
                    result = FindElements(BinaryLowerOrEqualMethod, literal, propertyId, interestingLabel);
                    break;

                case BinaryOperator.Lower:
                    result = FindElements(BinaryLowerMethod, literal, propertyId, interestingLabel);
                    break;

                case BinaryOperator.NotEquals:
                    result = FindElements(BinaryNotEqualsMethod, literal, propertyId, interestingLabel);
                    break;

                default:
                    result = new List<AGraphElementModel>();
                    break;
            }

            #endregion

            return result.Count > 0;
        }

        public override bool IndexScan(out IReadOnlyList<AGraphElementModel> result, string indexId, IComparable literal, BinaryOperator binOp = BinaryOperator.Equals)
        {
            if (string.IsNullOrWhiteSpace(indexId))
            {
                throw new ArgumentException("Index ID cannot be null or whitespace.", nameof(indexId));
            }

            if (literal == null)
            {
                throw new ArgumentNullException(nameof(literal));
            }

            IIndex index;
            if (!IndexFactory.TryGetIndex(out index, indexId))
            {
                result = null;
                return false;
            }

            // P4 (engine-performance-followups): when the resolved index is a RangeIndex AND the
            // operator is an ordered one, route through the RangeIndex's O(log n + k) sorted methods
            // instead of the generic O(n) FindElementsIndex scan. The rerouted set is deduped exactly
            // like FindElementsIndex's cross-bucket .Distinct(), so the result is identical. Equals /
            // NotEquals and every non-range index keep the generic path below.
            var orderedRangeIndex = index as IRangeIndex;
            if (orderedRangeIndex != null && TryOrderedRangeIndexScan(out result, orderedRangeIndex, literal, binOp))
            {
                return result.Count > 0;
            }

            #region binary operation

            switch (binOp)
            {
                case BinaryOperator.Equals:
                    // The Equals fast path returns the index's OWN posting-list bucket, which the index
                    // retains and shares (its copy-on-write is load-bearing) - so keep it as-is, just
                    // widened to IReadOnlyList (feature scan-result-representation), then filtered to the
                    // LIVE elements (feature index-lifecycle 3.2) so a removed-but-still-indexed element
                    // never surfaces. FilterLive returns the same shared bucket when nothing is dead.
                    if (!index.TryGetValue(out var equalsBucket, literal))
                    {
                        result = null;
                        return false;
                    }
                    result = FilterLive(equalsBucket);
                    break;

                case BinaryOperator.Greater:
                    result = FindElementsIndex(BinaryGreaterMethod, literal, index);
                    break;

                case BinaryOperator.GreaterOrEquals:
                    result = FindElementsIndex(BinaryGreaterOrEqualMethod, literal, index);
                    break;

                case BinaryOperator.LowerOrEquals:
                    result = FindElementsIndex(BinaryLowerOrEqualMethod, literal, index);
                    break;

                case BinaryOperator.Lower:
                    result = FindElementsIndex(BinaryLowerMethod, literal, index);
                    break;

                case BinaryOperator.NotEquals:
                    result = FindElementsIndex(BinaryNotEqualsMethod, literal, index);
                    break;

                default:
                    result = null;
                    return false;
            }

            #endregion

            return result.Count > 0;
        }

        public override bool RangeIndexScan(out IReadOnlyList<AGraphElementModel> result, string indexId, IComparable leftLimit, IComparable rightLimit, bool includeLeft = true, bool includeRight = true)
        {
            if (string.IsNullOrWhiteSpace(indexId))
            {
                throw new ArgumentException("Index ID cannot be null or whitespace.", nameof(indexId));
            }

            if (leftLimit == null)
            {
                throw new ArgumentNullException(nameof(leftLimit));
            }

            if (rightLimit == null)
            {
                throw new ArgumentNullException(nameof(rightLimit));
            }

            IIndex index;
            if (!IndexFactory.TryGetIndex(out index, indexId))
            {
                result = null;
                return false;
            }

            var rangeIndex = index as IRangeIndex;
            if (rangeIndex != null)
            {
                // IRangeIndex.Between still returns the index's own ImmutableList bucket (IIndex return
                // types are unchanged - its copy-on-write is load-bearing); widen it to IReadOnlyList and
                // filter to the LIVE elements (feature index-lifecycle 3.2) so a removed element does not
                // surface through the range path either.
                var found = rangeIndex.Between(out var between, leftLimit, rightLimit, includeLeft, includeRight);
                result = FilterLive(between);
                return found;
            }

            result = null;
            return false;
        }

        public override bool FulltextIndexScan(out FulltextSearchResult result, string indexId, string searchQuery)
        {
            if (string.IsNullOrWhiteSpace(indexId))
            {
                throw new ArgumentException("Index ID cannot be null or whitespace.", nameof(indexId));
            }

            if (string.IsNullOrWhiteSpace(searchQuery))
            {
                throw new ArgumentException("Search query cannot be null or whitespace.", nameof(searchQuery));
            }

            IIndex index;
            if (!IndexFactory.TryGetIndex(out index, indexId))
            {
                result = null;
                return false;
            }

            var fulltextIndex = index as IFulltextIndex;
            if (fulltextIndex != null)
            {
                return fulltextIndex.TryQuery(out result, searchQuery);
            }

            result = null;
            return false;
        }

        /// <summary>
        ///   k-nearest-neighbour scan over a vector index (feature vector-index) - the vector
        ///   analogue of <see cref="FulltextIndexScan"/>: resolve the index, type-check
        ///   <see cref="Index.Vector.IVectorIndex"/>, delegate. False for an unknown/non-vector
        ///   index or invalid input.
        /// </summary>
        public override bool VectorIndexScan(out Index.Vector.VectorSearchResult result, string indexId,
            float[] query, int k, Index.Vector.VectorSearchConstraint constraint = null)
        {
            result = null;

            if (string.IsNullOrWhiteSpace(indexId) || query == null)
            {
                return false;
            }

            if (!IndexFactory.TryGetIndex(out var index, indexId))
            {
                return false;
            }

            if (index is Index.Vector.IVectorIndex vectorIndex)
            {
                return vectorIndex.TryNearestNeighbors(out result, query, k, constraint);
            }

            return false;
        }

        internal string Save(string path, int savePartitions = 5)
        {
            // Cold-path instrumentation (feature observability): a save is seconds of I/O, so
            // the unconditional timestamp is noise; the span is null when nothing samples.
            using var span = Diagnostics.Fallen8Diagnostics.Source.StartActivity("fallen8.checkpoint.save");
            span?.SetTag("checkpoint.partitions", savePartitions);
            var start = System.Diagnostics.Stopwatch.GetTimestamp();

            string actualPath;
            try
            {
                actualPath = _persistencyFactory.Save(this, path, savePartitions);
            }
            catch
            {
                Metrics?.RecordCheckpointFailure("save");
                span?.SetStatus(System.Diagnostics.ActivityStatusCode.Error);
                throw;
            }

            // WAL compose (spec P4/§5): the snapshot is now DURABLY committed (the factory writes it
            // via temp + fsync + atomic rename). Only now reset the log to build upon this snapshot -
            // recording the current id-space high-water mark as the new baseline and pairing it with
            // the snapshot's actual path, discarding the pre-snapshot entries it superseded. Doing the
            // reset strictly AFTER the snapshot is durable is what makes "snapshot-then-truncate"
            // crash-safe: a crash in between leaves the log still paired with the PREVIOUS snapshot, so
            // loading the NEW snapshot will not replay it (no double-apply) while the new snapshot
            // already contains everything committed up to this save.
            _wal?.ResetToSnapshot(actualPath, _currentId);

            // A successful Save re-baselines the log against this snapshot: it is no longer awaiting a
            // paired load, and its failure fence (if any) was cleared by ResetToSnapshot
            // (feature crash-durability-hardening D1/D3).
            _walAwaitingPairedLoad = false;

            var bytes = MeasureCheckpointBytes(actualPath);
            Metrics?.RecordCheckpointSave(
                System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds, bytes);
            span?.SetTag("checkpoint.bytes", bytes);

            return actualPath;
        }

        /// <summary>
        ///   Total on-disk size of a checkpoint: the snapshot file plus its partitions and index
        ///   sidecars, which all share the snapshot path as their name prefix. Files whose name
        ///   extends the prefix with a '#' are EXCLUDED - that is the version stamp a later save
        ///   to the same base path gets (PersistencyFactory), i.e. a DIFFERENT checkpoint whose
        ///   size must not be summed into this one's. Best-effort (-1 when unmeasurable) - a
        ///   metrics detail must never fault a save/load.
        /// </summary>
        private static long MeasureCheckpointBytes(string checkpointPath)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(checkpointPath));
                var prefix = System.IO.Path.GetFileName(checkpointPath);
                if (directory == null || string.IsNullOrEmpty(prefix))
                {
                    return -1L;
                }

                var total = 0L;
                foreach (var file in System.IO.Directory.EnumerateFiles(directory, prefix + "*"))
                {
                    var name = System.IO.Path.GetFileName(file);
                    if (name.Length > prefix.Length && name[prefix.Length] == '#')
                    {
                        continue;
                    }
                    total += new System.IO.FileInfo(file).Length;
                }
                return total;
            }
            catch
            {
                return -1L;
            }
        }

        public override bool TryCalculateShortestPath(
            out List<Path> result,
            string plugin,
            ShortestPathDefinition definition)
        {
            if (string.IsNullOrWhiteSpace(plugin))
            {
                throw new ArgumentException("Plugin name cannot be null or whitespace.", nameof(plugin));
            }

            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            var algo = ResolveCachedPlugin<IShortestPathAlgorithm>(
                _pluginCache.ShortestPath, plugin, _pluginCache.AddShortestPath);
            if (algo != null)
            {
                return algo.TryCalculateShortestPath(out result, definition);
            }

            result = new List<Path>();
            return false;
        }

        /// <summary>
        ///   THE resolve-initialize-cache flow for string-named algorithm plugins (feature
        ///   code-quality: one implementation instead of one copy per plugin family). Cache
        ///   hit returns the cached instance; otherwise the plugin is discovered via
        ///   <see cref="PluginFactory"/>, initialized with this engine, registered, and
        ///   returned. Null when no plugin carries the name.
        /// </summary>
        private T ResolveCachedPlugin<T>(Microsoft.Extensions.Caching.Memory.IMemoryCache cache,
            string pluginName, Action<T> register) where T : class, Plugin.IPlugin
        {
            if (cache.TryGetValue(pluginName, out Object cached))
            {
                return (T)cached;
            }

            if (PluginFactory.TryFindPlugin<T>(out var plugin, pluginName))
            {
                plugin.Initialize(this, null);
                register(plugin);
                return plugin;
            }

            return null;
        }

        public override bool TryCalculateShortestPath<T>(
            out List<Path> result,
            ShortestPathDefinition definition)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            Type shortestPathType = typeof(T);
            var algo = Activator.CreateInstance(shortestPathType, false) as IShortestPathAlgorithm;

            if (algo != null)
            {
                Object cachedAlgo;
                if (!_pluginCache.ShortestPath.TryGetValue(algo.PluginName, out cachedAlgo))
                {
                    //Shortest path plugin was not cached
                    algo.Initialize(this, null);
                    _pluginCache.AddShortestPath(algo);
                }
                else
                {
                    algo = (IShortestPathAlgorithm)cachedAlgo;
                }

                return algo.TryCalculateShortestPath(out result, definition);
            }

            result = new List<Path>();
            return false;
        }

        public override bool TryRunAnalytics(
            out Algorithms.Analytics.GraphAnalyticsResult result,
            string algorithmName,
            Algorithms.Analytics.GraphAnalyticsDefinition definition)
        {
            if (string.IsNullOrWhiteSpace(algorithmName))
            {
                throw new ArgumentException("Algorithm name cannot be null or whitespace.", nameof(algorithmName));
            }

            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            var algo = ResolveCachedPlugin<Algorithms.Analytics.IGraphAnalyticsAlgorithm>(
                _pluginCache.Analytics, algorithmName, _pluginCache.AddAnalytics);
            if (algo != null)
            {
                return algo.TryRunAnalytics(out result, definition);
            }

            result = null;
            return false;
        }

        internal EdgeModel CreateEdge_internal(Int32 sourceVertexId, String edgePropertyId, Int32 targetVertexId,
            UInt32 creationDate, String label, Dictionary<String, Object> properties)
        {
            EdgeModel outgoingEdge = null;

            // Verify both endpoints exist and are live BEFORE wiring. A missing/removed/out-of-range
            // endpoint resolves to null here (no throw), so the edge is simply not created and the
            // caller rolls back cleanly with NotFound - instead of the old bounds-check throw -> 500.
            var sourceVertex = TryResolveLiveVertexForEdge(sourceVertexId);
            var targetVertex = TryResolveLiveVertexForEdge(targetVertexId);

            //get the related vertices
            if (sourceVertex != null && targetVertex != null)
            {
                //intern the label, edge-property-id and property keys (finding M2)
                edgePropertyId = Intern(edgePropertyId);
                outgoingEdge = new EdgeModel(_currentId, creationDate, targetVertex, sourceVertex, Intern(label), edgePropertyId, InternPropertyKeys(properties));

                //add the edge to the graph elements
                AppendGraphElement(outgoingEdge);

                //increment the ids (single-writer field, plain increment - finding P10)
                _currentId++;

                //add the edge to the source vertex
                sourceVertex.AddOutEdge(edgePropertyId, outgoingEdge);

                //link the vertices
                targetVertex.AddIncomingEdge(edgePropertyId, outgoingEdge);

                //increase the edgeCount
                EdgeCount++;

                // Bound-index projection of creation-time embeddings (feature element-embeddings).
                ProjectAllEmbeddingsOf(outgoingEdge);
            }

            return outgoingEdge;
        }

        internal void CreateEdges_internal(List<EdgeDefinition> definitions, List<EdgeModel> createdEdges,
            out Boolean inputValid, out Boolean allEndpointsResolved)
        {
            // Construct-then-commit with store-then-adjacency (feature transaction-atomicity). The old
            // code bumped _currentId/EdgeCount and wired adjacency per edge BEFORE the batch append,
            // so a throw mid-loop left the id space corrupt (_currentId > Count) plus dangling
            // adjacency. Now: validate the whole batch, build all models against a local id counter,
            // append to the store FIRST, then wire adjacency (matching the single-edge order so a
            // reader can never traverse to an edge TryGetEdge cannot resolve). The appended edges are
            // recorded into the caller's <paramref name="createdEdges"/> list BEFORE wiring, so if a
            // wiring step still throws (OOM), the transaction's Rollback removes exactly the edges
            // that reached the store.
            inputValid = true;
            allEndpointsResolved = true;

            if (definitions == null || definitions.Count == 0)
            {
                return;
            }

            // 1. Structural validation (no null definitions) - a clean InvalidInput, no throw.
            foreach (var aEdgeDefinition in definitions)
            {
                if (aEdgeDefinition == null)
                {
                    inputValid = false;
                    return; // nothing built, nothing mutated
                }
            }

            // 2. Endpoint validation: EVERY referenced vertex must be live BEFORE anything is built or
            //    wired, so a missing/removed endpoint rolls the whole batch back cleanly (NotFound) and
            //    atomically. Single-writer, so no endpoint can be removed between here and the wiring.
            foreach (var aEdgeDefinition in definitions)
            {
                if (TryResolveLiveVertexForEdge(aEdgeDefinition.SourceVertexId) == null
                    || TryResolveLiveVertexForEdge(aEdgeDefinition.TargetVertexId) == null)
                {
                    allEndpointsResolved = false;
                    return; // nothing wired
                }
            }

            // 3. Build every model against a LOCAL id counter, WITHOUT touching store/adjacency/counters.
            var newEdges = new List<EdgeModel>(definitions.Count);
            var nextId = _currentId;
            foreach (var aEdgeDefinition in definitions)
            {
                // Guaranteed non-null by the pre-validation passes above.
                var sourceVertex = TryResolveLiveVertexForEdge(aEdgeDefinition.SourceVertexId);
                var targetVertex = TryResolveLiveVertexForEdge(aEdgeDefinition.TargetVertexId);

                //intern the label, edge-property-id and property keys (finding M2)
                var edgePropertyId = Intern(aEdgeDefinition.EdgePropertyId);
                newEdges.Add(new EdgeModel(nextId, aEdgeDefinition.CreationDate, targetVertex, sourceVertex,
                    Intern(aEdgeDefinition.Label), edgePropertyId, InternPropertyKeys(aEdgeDefinition.Properties)));
                nextId++;
            }

            // 4. Commit: append to the store FIRST (store-then-adjacency), advance the counters, record
            //    the now-committed edges for rollback, THEN wire adjacency. Recording before wiring
            //    means a wiring throw is fully recoverable (Rollback removes every appended edge,
            //    detaching any partial adjacency and restoring EdgeCount).
            AppendGraphElements(newEdges);
            _currentId = nextId;
            EdgeCount += newEdges.Count;
            createdEdges.AddRange(newEdges);

            // Bound-index projection of creation-time embeddings (feature element-embeddings);
            // recorded in createdEdges already, so a residual wiring throw still purges these.
            foreach (var newEdge in newEdges)
            {
                ProjectAllEmbeddingsOf(newEdge);
            }

            // Batch adjacency wiring (feature supernode-adjacency-build Step 1). The old loop wired one
            // edge at a time (source.AddOutEdge + target.AddIncomingEdge), so k edges landing on one
            // vertex/direction in this batch cost k separate whole-group array rebuilds - O(d²) to build
            // a hub. Group the new edges by (vertex, direction, edge-property-id) FIRST, preserving
            // encounter order, then publish each vertex/direction adjacency ONCE via the batch
            // AddOutEdges/AddIncomingEdges. The edges are already recorded in createdEdges above, so a
            // wiring throw stays fully recoverable (Rollback removes every appended edge, detaching any
            // partial adjacency); a vertex touched under several keys chains the per-key builds and
            // publishes its final instance once.
            var outByVertex = new Dictionary<VertexModel, Dictionary<String, List<EdgeModel>>>();
            var inByVertex = new Dictionary<VertexModel, Dictionary<String, List<EdgeModel>>>();
            for (var i = 0; i < newEdges.Count; i++)
            {
                var edge = newEdges[i];
                GroupEdgeForWiring(outByVertex, edge.SourceVertex, edge.EdgePropertyId, edge);
                GroupEdgeForWiring(inByVertex, edge.TargetVertex, edge.EdgePropertyId, edge);
            }

            foreach (var vertexGroups in outByVertex)
            {
                vertexGroups.Key.AddOutEdges(vertexGroups.Value);
            }
            foreach (var vertexGroups in inByVertex)
            {
                vertexGroups.Key.AddIncomingEdges(vertexGroups.Value);
            }
        }

        /// <summary>
        ///   Buckets an edge under <c>vertex -&gt; edge-property-id -&gt; edges</c>, creating the inner
        ///   maps/lists on demand and preserving encounter order within each group, so
        ///   <see cref="CreateEdges_internal" /> can wire a whole batch with one adjacency publish per
        ///   vertex/direction (feature supernode-adjacency-build Step 1).
        /// </summary>
        private static void GroupEdgeForWiring(Dictionary<VertexModel, Dictionary<String, List<EdgeModel>>> byVertex,
            VertexModel vertex, String edgePropertyId, EdgeModel edge)
        {
            if (!byVertex.TryGetValue(vertex, out var byKey))
            {
                byKey = new Dictionary<String, List<EdgeModel>>();
                byVertex[vertex] = byKey;
            }

            if (!byKey.TryGetValue(edgePropertyId, out var list))
            {
                list = new List<EdgeModel>();
                byKey[edgePropertyId] = list;
            }

            list.Add(edge);
        }

        internal void SetProperty_internal(Int32 graphElementId, String propertyId, Object property)
        {
            AGraphElementModel graphElement = GetGraphElementForMutation(graphElementId);
            if (graphElement != null)
            {
                //intern the property key (finding M2)
                graphElement.SetProperty(Intern(propertyId), property);
            }
        }

        /// <summary>
        ///   Sets a single property and records its inverse into <paramref name="undo"/> ONLY after the
        ///   set has succeeded (feature transaction-atomicity). <see cref="AGraphElementModel.SetProperty"/>
        ///   throws before mutating on a value conflict, so a rolled-back single set leaves nothing to
        ///   undo; the recorded inverse guards the invariant uniformly and covers a residual post-set
        ///   throw. An out-of-range id throws here before any mutation, exactly as the plain setter did.
        /// </summary>
        internal void SetPropertyWithUndo_internal(Int32 graphElementId, String propertyId, Object property,
            List<Transaction.PropertyMutationUndo> undo)
        {
            AGraphElementModel graphElement = GetGraphElementForMutation(graphElementId);
            if (graphElement == null)
            {
                return; // no-op target (empty slot): nothing set, nothing to undo
            }

            var hadValueBefore = graphElement.TryGetProperty<Object>(out var priorValue, propertyId);

            //intern the property key (finding M2)
            graphElement.SetProperty(Intern(propertyId), property);

            // Recorded only after SetProperty returns, so a conflict throw leaves undo empty.
            undo.Add(new Transaction.PropertyMutationUndo(graphElementId, propertyId, hadValueBefore, priorValue));

            // A raw property write to a reserved embedding key feeds bound indices too
            // (feature element-embeddings) - the bulk/import surface writes embeddings this way.
            ProjectEmbeddingPropertyWrite(graphElement, propertyId, property);
        }

        /// <summary>
        ///   Removes a property from an element. Returns whether a property was ACTUALLY removed
        ///   (false for an empty slot or a key the element does not carry), so the change feed can
        ///   report exactly what changed.
        /// </summary>
        internal bool RemoveProperty_internal(Int32 graphElementId, String propertyId)
        {
            var graphElement = GetGraphElementForMutation(graphElementId);
            var removed = graphElement != null && graphElement.RemoveProperty(propertyId);

            if (removed)
            {
                // Removing a reserved embedding key purges the element from bound vector
                // indices of that name (feature element-embeddings).
                ProjectEmbeddingPropertyWrite(graphElement, propertyId, null);
            }

            return removed;
        }

        internal bool TryRemoveGraphElement_private(Int32 graphElementId)
        {
            AGraphElementModel graphElement = GetGraphElementForMutation(graphElementId);

            if (graphElement == null || graphElement._removed)
            {
                return false;
            }

            //used if an edge is removed
            List<String> inEdgeRemovals = null;
            List<String> outEdgeRemovals = null;

            try
            {
                #region remove element

                graphElement.MarkAsRemoved();

                if (graphElement is VertexModel)
                {
                    #region remove vertex

                    var vertex = (VertexModel)graphElement;

                    // Count the DISTINCT cascaded edges that actually transition from live to removed,
                    // so the counters can be maintained incrementally instead of via an O(n) recount
                    // (finding P3). Guarding each MarkAsRemoved on !_removed makes the count exact even
                    // for a self-loop (present in both OutEdges and InEdges): the out-edge pass marks
                    // and counts it, and the target-side detach removes it from InEdges before the
                    // in-edge pass is captured, so it is never double-counted.
                    int removedEdgeCount = 0;

                    #region out edges

                    var outgoingEdgeContainer = vertex.GetRawOutEdges();
                    if (outgoingEdgeContainer != null)
                    {
                        foreach (var aOutEdgeProperty in outgoingEdgeContainer)
                        {
                            foreach (var aOutEdge in aOutEdgeProperty.Value)
                            {
                                //remove from incoming edges of target vertex
                                aOutEdge.TargetVertex.RemoveIncomingEdge(aOutEdgeProperty.Key, aOutEdge);

                                //remove the edge itself (counting only a genuine live->removed transition)
                                if (!aOutEdge._removed)
                                {
                                    aOutEdge.MarkAsRemoved();
                                    removedEdgeCount++;
                                }
                            }
                        }
                    }

                    #endregion

                    #region in edges

                    var incomingEdgeContainer = vertex.GetRawInEdges();
                    if (incomingEdgeContainer != null)
                    {
                        foreach (var aInEdgeProperty in incomingEdgeContainer)
                        {
                            foreach (var aInEdge in aInEdgeProperty.Value)
                            {
                                //remove from outgoing edges of source vertex
                                aInEdge.SourceVertex.RemoveOutGoingEdge(aInEdgeProperty.Key, aInEdge);

                                //remove the edge itself (counting only a genuine live->removed transition)
                                if (!aInEdge._removed)
                                {
                                    aInEdge.MarkAsRemoved();
                                    removedEdgeCount++;
                                }
                            }
                        }
                    }

                    #endregion

                    // Maintain the counts incrementally (finding P3): the vertex itself and its distinct
                    // cascaded edges leave the live set. This runs only on the commit path; if any step
                    // above threw, control is in the catch/finally below, which restores adjacency and
                    // does a full RecalculateGraphElementCounter, so a rolled-back removal is unaffected
                    // and the counts stay exactly correct.
                    VertexCount--;
                    EdgeCount -= removedEdgeCount;

                    #endregion
                }
                else
                {
                    #region remove edge

                    var edge = (EdgeModel)graphElement;

                    //remove from incoming edges of target vertex
                    inEdgeRemovals = edge.TargetVertex.RemoveIncomingEdge(edge);

                    //remove from outgoing edges of source vertex
                    outEdgeRemovals = edge.SourceVertex.RemoveOutGoingEdge(edge);

                    //update the EdgeCount --> easy way
                    EdgeCount--;

                    #endregion
                }

                #endregion
            }
            catch (Exception)
            {
                #region restore

                // Restoring the graph can itself fault (for example on a half-constructed edge).
                // Guard the restore so that a secondary failure neither skips the counter recompute
                // nor masks the original removal failure, which must remain the observed exception.
                try
                {
                    // Removal is a soft-delete: the element is only flagged via MarkAsRemoved and is
                    // never taken out of _graphElements. The correct rollback is therefore to clear that
                    // flag again. (Re-inserting into _graphElements would duplicate the still-present
                    // element and break the id==index invariant, and would not clear the removed flag.)
                    graphElement.MarkAsNotRemoved();

                    if (graphElement is VertexModel)
                    {
                        #region restore vertex

                        var vertex = (VertexModel)graphElement;

                        #region out edges

                        var outgoingEdgeContainer = vertex.GetRawOutEdges();
                        if (outgoingEdgeContainer != null)
                        {
                            foreach (var aOutEdgeProperty in outgoingEdgeContainer)
                            {
                                foreach (var aOutEdge in aOutEdgeProperty.Value)
                                {
                                    //restore into the incoming edges of the target vertex
                                    aOutEdge.TargetVertex.AddIncomingEdge(aOutEdgeProperty.Key, aOutEdge);

                                    //reset the edge
                                    aOutEdge.MarkAsNotRemoved();
                                }
                            }
                        }

                        #endregion

                        #region in edges

                        var incomingEdgeContainer = vertex.GetRawInEdges();
                        if (incomingEdgeContainer != null)
                        {
                            foreach (var aInEdgeProperty in incomingEdgeContainer)
                            {
                                foreach (var aInEdge in aInEdgeProperty.Value)
                                {
                                    //restore into the outgoing edges of the source vertex
                                    //(removal detached it via RemoveOutGoingEdge, so the inverse is AddOutEdge)
                                    aInEdge.SourceVertex.AddOutEdge(aInEdgeProperty.Key, aInEdge);

                                    //reset the edge
                                    aInEdge.MarkAsNotRemoved();
                                }
                            }
                        }

                        #endregion

                        #endregion
                    }
                    else
                    {
                        #region restore edge

                        var edge = (EdgeModel)graphElement;

                        if (inEdgeRemovals != null)
                        {
                            for (var i = 0; i < inEdgeRemovals.Count; i++)
                            {
                                edge.TargetVertex.AddIncomingEdge(inEdgeRemovals[i], edge);
                            }
                        }

                        if (outEdgeRemovals != null)
                        {
                            for (var i = 0; i < outEdgeRemovals.Count; i++)
                            {
                                edge.SourceVertex.AddOutEdge(outEdgeRemovals[i], edge);
                            }
                        }

                        #endregion
                    }
                }
                catch (Exception restoreException)
                {
                    // Swallow (but log) the restore failure so it does not replace the original
                    // removal failure that is rethrown below.
                    _logger.LogError(restoreException,
                        "Failed to fully restore graph element {GraphElementId} after a faulted removal; rolling back with the original failure.",
                        graphElementId);
                }
                finally
                {
                    //recalculate the counter (must run even when the restore above faulted)
                    RecalculateGraphElementCounter();
                }

                #endregion

                throw;
            }

            // Write-end index purge (feature index-lifecycle 3.3). The live->removed transition has now
            // COMMITTED (we are past the try/catch, so a rolled-back removal never reaches here), so drop
            // the element - and, for a vertex, its cascaded-removed incident edges - from every registered
            // index. This stops a removed element being pinned by an index bucket (its body becomes
            // collectable) and complements the read-end FilterLive floor. RemoveValue is O(affected keys)
            // via each index's reverse map, so the fan-out over indices is bounded; it runs here on the
            // single writer, serialised against request-thread index writes by the index's own lock.
            PurgeRemovedElementFromIndices(graphElement);

            return true;
        }

        /// <summary>
        ///   Removes <paramref name="removedElement" /> (and, when it is a vertex, every edge in its
        ///   adjacency - the edges the cascade just removed) from every registered index. Enumerates a
        ///   snapshot of the indices so it cannot race a concurrent create/delete. A best-effort per
        ///   index: an index whose <c>RemoveValue</c> throws is logged and skipped so one faulty index
        ///   cannot fail an otherwise-committed removal.
        /// </summary>
        private void PurgeRemovedElementFromIndices(AGraphElementModel removedElement)
        {
            var indices = IndexFactory?.GetIndicesSnapshot();
            if (indices == null || indices.Count == 0)
            {
                return;
            }

            foreach (var index in indices)
            {
                PurgeValueFromIndex(index, removedElement);

                // A removed vertex takes its incident edges out of the live set too; purge those from
                // the index as well. The removed vertex's OWN adjacency still lists them at this point
                // (it is freed only later, on trim), so it is the authoritative source of the cascaded
                // edges. RemoveValue is an O(1) reverse-map miss for any edge the index never held.
                if (removedElement is VertexModel vertex)
                {
                    PurgeVertexEdgesFromIndex(index, vertex);
                }
            }
        }

        private void PurgeVertexEdgesFromIndex(IIndex index, VertexModel vertex)
        {
            var outEdges = vertex.GetRawOutEdges();
            if (outEdges != null)
            {
                foreach (var group in outEdges)
                {
                    foreach (var edge in group.Value)
                    {
                        PurgeValueFromIndex(index, edge);
                    }
                }
            }

            var inEdges = vertex.GetRawInEdges();
            if (inEdges != null)
            {
                foreach (var group in inEdges)
                {
                    foreach (var edge in group.Value)
                    {
                        PurgeValueFromIndex(index, edge);
                    }
                }
            }
        }

        private void PurgeValueFromIndex(IIndex index, AGraphElementModel element)
        {
            try
            {
                index.RemoveValue(element);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to purge graph element {GraphElementId} from an index after removal; the read-end live filter still hides it, but its body may stay pinned.",
                    element?.Id);
            }
        }

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

        /// <summary>
        ///   Applies a batch of property sets atomically (feature transaction-atomicity). Pre-validates
        ///   the WHOLE batch before mutating anything - structural validity (no null definitions,
        ///   populated to a clean <see cref="TransactionFailureReason.InvalidInput"/>) and
        ///   conflict-freedom, accounting for intra-batch pending writes so a self-conflicting batch is
        ///   caught too (<see cref="TransactionFailureReason.Conflict"/>). An out-of-range id keeps the
        ///   historical throw (<see cref="ArgumentOutOfRangeException"/> - the worker maps it to
        ///   InternalError/500, per transaction-failure-reasons), but now during validation, before any
        ///   set is applied, so the batch is still atomic. On the happy path each set is applied and its
        ///   inverse recorded into <paramref name="undo"/> (in apply order), so a residual post-validation
        ///   throw (e.g. OOM) is undone by <see cref="RestoreProperties_internal"/>.
        /// </summary>
        internal Boolean SetProperties_internal(List<PropertyAddDefinition> definitions,
            List<Transaction.PropertyMutationUndo> undo, out Transaction.TransactionFailureReason reason)
        {
            reason = Transaction.TransactionFailureReason.None;

            if (definitions == null)
            {
                reason = Transaction.TransactionFailureReason.InvalidInput;
                return false;
            }

            if (definitions.Count == 0)
            {
                return true;
            }

            // 1. Structural validation (no null definitions) - a clean InvalidInput, no throw.
            foreach (var aDefinition in definitions)
            {
                if (aDefinition == null)
                {
                    reason = Transaction.TransactionFailureReason.InvalidInput;
                    return false;
                }
            }

            // 2. Conflict validation. Simulate the batch against the live store WITHOUT mutating,
            //    tracking each (element,key)'s effective value after prior items in this batch, so an
            //    intra-batch conflict (two different values for the same new key) is caught as well as
            //    a conflict with the element's existing value. An out-of-range id throws here (before
            //    any apply), preserving the InternalError/500 boundary while keeping the batch atomic.
            //    A missing (null) slot stays a no-op, matching SetProperty_internal.
            var pending = new Dictionary<(Int32, String), Object>();
            foreach (var aDefinition in definitions)
            {
                var graphElement = GetGraphElementForMutation(aDefinition.GraphElementId);
                if (graphElement == null)
                {
                    continue; // no-op target (empty slot), as today
                }

                // Canonicalize the candidate so the comparison is canonical-to-canonical (the store
                // holds canonical values, and SetProperty compares after canonicalizing): a genuine
                // no-op update (equal value) is not a conflict, a different value is.
                var candidate = AGraphElementModel.CanonicalizeProperty(aDefinition.Property);
                var key = (aDefinition.GraphElementId, aDefinition.PropertyId);

                // The effective current value is the one a prior item in THIS batch set (pending), or
                // else the element's stored value. Either is already canonical.
                Boolean hasEffective;
                Object effective;
                if (!pending.TryGetValue(key, out effective))
                {
                    hasEffective = graphElement.TryGetProperty<Object>(out effective, aDefinition.PropertyId);
                }
                else
                {
                    hasEffective = true;
                }

                if (hasEffective && !AGraphElementModel.ArePropertyValuesEqual(effective, candidate))
                {
                    reason = Transaction.TransactionFailureReason.Conflict;
                    return false;
                }

                pending[key] = candidate;
            }

            // 3. Apply, recording the inverse of each set (in apply order) for a residual-throw undo.
            foreach (var aDefinition in definitions)
            {
                var graphElement = GetGraphElementForMutation(aDefinition.GraphElementId);
                if (graphElement == null)
                {
                    continue;
                }

                var hadValueBefore = graphElement.TryGetProperty<Object>(out var priorValue, aDefinition.PropertyId);
                undo.Add(new Transaction.PropertyMutationUndo(aDefinition.GraphElementId, aDefinition.PropertyId, hadValueBefore, priorValue));

                //intern the property key (finding M2)
                graphElement.SetProperty(Intern(aDefinition.PropertyId), aDefinition.Property);
            }

            // 4. Project reserved embedding keys into bound vector indices (feature
            //    element-embeddings), after every set applied - see SetEmbeddings_internal.
            foreach (var aDefinition in definitions)
            {
                var graphElement = GetGraphElementForMutation(aDefinition.GraphElementId);
                if (graphElement != null)
                {
                    ProjectEmbeddingPropertyWrite(graphElement, aDefinition.PropertyId, aDefinition.Property);
                }
            }

            return true;
        }

        /// <summary>
        ///   Restores the property state recorded by <see cref="SetProperties_internal"/> when the
        ///   batch is rolled back (feature transaction-atomicity). Replays the recorded inverses in
        ///   REVERSE apply order so a key set more than once in the batch is returned to its original
        ///   value/absence.
        /// </summary>
        internal void RestoreProperties_internal(List<Transaction.PropertyMutationUndo> undo)
        {
            if (undo == null)
            {
                return;
            }

            for (var i = undo.Count - 1; i >= 0; i--)
            {
                var entry = undo[i];
                var graphElement = GetGraphElementForMutation(entry.GraphElementId);
                graphElement?.RestoreProperty(entry.PropertyId, entry.HadValueBefore, entry.PriorValue);

                if (graphElement != null)
                {
                    // Keep bound vector indices in step with the restored embedding state
                    // (feature element-embeddings): the restored prior value re-projects, an
                    // absent prior purges.
                    ProjectEmbeddingPropertyWrite(graphElement, entry.PropertyId,
                        entry.HadValueBefore ? entry.PriorValue : null);
                }
            }
        }

        /// <summary>
        ///   Applies a batch of embedding writes atomically (feature element-embeddings). Validates
        ///   the WHOLE batch before mutating anything - structural validity and per-write bounds
        ///   (valid name, dimension within [1, <see cref="Index.Vector.VectorIndex.MaxDimension" />],
        ///   finite components) to a clean <see cref="TransactionFailureReason.InvalidInput" />.
        ///   Embedding writes have REPLACE semantics, so unlike
        ///   <see cref="SetProperties_internal" /> there is no conflict validation - the last write
        ///   for a (element, name) pair wins, intra-batch included. A missing (null) slot stays a
        ///   no-op, matching the property path. On the happy path each write is applied and its
        ///   inverse recorded into <paramref name="undo" /> (in apply order) for a
        ///   residual-throw rollback via <see cref="RestoreEmbeddings_internal" />.
        /// </summary>
        internal Boolean SetEmbeddings_internal(List<EmbeddingSetDefinition> definitions,
            List<Transaction.PropertyMutationUndo> undo, out Transaction.TransactionFailureReason reason)
        {
            reason = Transaction.TransactionFailureReason.None;

            if (definitions == null)
            {
                reason = Transaction.TransactionFailureReason.InvalidInput;
                return false;
            }

            if (definitions.Count == 0)
            {
                return true;
            }

            // 1. Validate the whole batch before mutating anything (atomicity).
            foreach (var aDefinition in definitions)
            {
                if (aDefinition == null || !AGraphElementModel.IsValidEmbeddingName(aDefinition.Name))
                {
                    reason = Transaction.TransactionFailureReason.InvalidInput;
                    return false;
                }

                var vector = aDefinition.Vector;
                if (vector == null)
                {
                    continue; // removal - always structurally valid
                }

                if (vector.Length < 1 || vector.Length > Index.Vector.VectorIndex.MaxDimension ||
                    Index.Vector.VectorIndex.HasNonFiniteComponent(vector))
                {
                    reason = Transaction.TransactionFailureReason.InvalidInput;
                    return false;
                }
            }

            // 2. Apply, recording the inverse of each write. The mutation primitive is
            //    RestoreProperty (remove + conditional set on the reserved key): it IS the
            //    "set to exactly this state" operation, so replace semantics never hit
            //    SetProperty's same-key conflict throw.
            foreach (var aDefinition in definitions)
            {
                var graphElement = GetGraphElementForMutation(aDefinition.GraphElementId);
                if (graphElement == null)
                {
                    continue; // no-op target (empty slot), matching the property path
                }

                var propertyId = Intern(AGraphElementModel.GetEmbeddingPropertyId(aDefinition.Name));
                var hadValueBefore = graphElement.TryGetProperty<Object>(out var priorValue, propertyId);
                undo.Add(new Transaction.PropertyMutationUndo(aDefinition.GraphElementId, propertyId, hadValueBefore, priorValue));

                graphElement.RestoreProperty(propertyId, aDefinition.Vector != null, aDefinition.Vector);

                // The model stamp replaces with every write (feature embedding-provider): a
                // provider write carries its stamp, a raw write clears a stale one, a removal
                // drops it - the stamp always reflects the LAST write. A write that neither
                // sets nor clears a stamp is a no-op here (no undo entry, no feed event).
                var stampId = Intern(AGraphElementModel.GetEmbeddingModelStampPropertyId(aDefinition.Name));
                var stampValue = aDefinition.Vector != null ? aDefinition.ModelStamp : null;
                var hadStampBefore = graphElement.TryGetProperty<Object>(out var priorStamp, stampId);
                if (stampValue != null || hadStampBefore)
                {
                    undo.Add(new Transaction.PropertyMutationUndo(aDefinition.GraphElementId, stampId, hadStampBefore, priorStamp));
                    graphElement.RestoreProperty(stampId, stampValue != null, stampValue);
                }
            }

            // 3. Project into bound vector indices AFTER every mutation applied: a mid-apply
            //    throw rolls back plain property state with no projections to compensate, and a
            //    projection fault is best-effort (logged) and never fails the commit.
            foreach (var aDefinition in definitions)
            {
                var graphElement = GetGraphElementForMutation(aDefinition.GraphElementId);
                if (graphElement != null)
                {
                    ProjectEmbeddingToBoundIndices(graphElement, aDefinition.Name, aDefinition.Vector);
                }
            }

            return true;
        }

        /// <summary>
        ///   Restores the embedding state recorded by <see cref="SetEmbeddings_internal" /> when the
        ///   batch is rolled back - the reserved keys are properties, so the property restore
        ///   applies verbatim (reverse apply order).
        /// </summary>
        internal void RestoreEmbeddings_internal(List<Transaction.PropertyMutationUndo> undo)
        {
            RestoreProperties_internal(undo);
        }

        /// <summary>
        ///   Removes a batch of graph elements atomically (feature transaction-atomicity). Every id is
        ///   range-checked BEFORE any removal, so an out-of-range id still throws
        ///   <see cref="ArgumentOutOfRangeException"/> (InternalError/500, per transaction-failure-reasons)
        ///   but leaves the batch atomic (nothing removed). Each id that genuinely transitions
        ///   live -> removed is recorded into <paramref name="removedIds"/> so that, if a later removal
        ///   throws (e.g. a poisoned/corrupt adjacency), <see cref="RestoreRemovedElements_private"/>
        ///   undoes the earlier removals of the same batch.
        /// </summary>
        internal void RemoveGraphElements_internal(List<Int32> graphElementIds, List<Int32> removedIds,
            out Transaction.TransactionFailureReason reason)
        {
            reason = Transaction.TransactionFailureReason.None;

            if (graphElementIds == null)
            {
                reason = Transaction.TransactionFailureReason.InvalidInput;
                return;
            }

            if (graphElementIds.Count == 0)
            {
                return;
            }

            // Range-check the whole batch up front (out-of-range throws BEFORE any removal, so the
            // batch is atomic - the historical throw contract is preserved, just moved earlier).
            var snap = _snapshot;
            foreach (var id in graphElementIds)
            {
                if (id < 0 || id >= snap.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(graphElementIds));
                }
            }

            // Apply, tracking genuine live -> removed transitions. If a removal throws, it self-restores
            // that element and rethrows; the throw escapes to the worker, whose Rollback restores the
            // earlier tracked ids of this batch.
            foreach (var id in graphElementIds)
            {
                if (TryRemoveGraphElement_private(id))
                {
                    removedIds.Add(id);
                }
            }
        }

        /// <summary>
        ///   Restores a set of fully-removed elements when a remove batch is rolled back (feature
        ///   transaction-atomicity), then recomputes the counters. Restores in REVERSE removal order so
        ///   a vertex removed before one of its (cascaded) edges is restored after that edge.
        /// </summary>
        internal void RestoreRemovedElements_private(List<Int32> removedIds)
        {
            if (removedIds == null || removedIds.Count == 0)
            {
                return;
            }

            for (var i = removedIds.Count - 1; i >= 0; i--)
            {
                var element = GetGraphElementForMutation(removedIds[i]);
                if (element != null)
                {
                    RestoreRemovedElement_private(element);
                }
            }

            //the removals maintained counts incrementally; a full recompute after the restore keeps
            //them exactly correct without re-deriving the inverse of each cascade.
            RecalculateGraphElementCounter();
        }

        /// <summary>
        ///   Inverse of a COMPLETED soft-removal (feature transaction-atomicity): clears the removed
        ///   flag and re-attaches the element to the adjacency the removal detached it from. For a
        ///   vertex, the raw out/in adjacency snapshots are captured up front (both are immutable
        ///   copy-on-write references, so re-attaching a self-loop via the out pass does not leak it
        ///   into the in pass), then each edge is re-attached to the OTHER endpoint's adjacency it was
        ///   detached from - the exact inverse of the removal cascade, including self-loops without
        ///   duplication. For an edge, it is re-attached to both endpoints.
        /// </summary>
        private void RestoreRemovedElement_private(AGraphElementModel element)
        {
            element.MarkAsNotRemoved();

            if (element is VertexModel vertex)
            {
                // Capture BOTH adjacency snapshots before mutating either: the removal detached each
                // out-edge from its target's InEdges and each in-edge from its source's OutEdges, so
                // re-attaching those (and only those) exactly inverts it. A self-loop sits in this
                // vertex's OutEdges only after removal (the out pass detached it from InEdges), so it
                // is absent from the in-snapshot and is not re-attached twice.
                var outSnapshot = vertex.GetRawOutEdges();
                var inSnapshot = vertex.GetRawInEdges();

                if (outSnapshot != null)
                {
                    foreach (var group in outSnapshot)
                    {
                        foreach (var outEdge in group.Value)
                        {
                            outEdge.MarkAsNotRemoved();
                            outEdge.TargetVertex.AddIncomingEdge(group.Key, outEdge);
                        }
                    }
                }

                if (inSnapshot != null)
                {
                    foreach (var group in inSnapshot)
                    {
                        foreach (var inEdge in group.Value)
                        {
                            inEdge.MarkAsNotRemoved();
                            inEdge.SourceVertex.AddOutEdge(group.Key, inEdge);
                        }
                    }
                }
            }
            else if (element is EdgeModel edge)
            {
                edge.TargetVertex.AddIncomingEdge(edge.EdgePropertyId, edge);
                edge.SourceVertex.AddOutEdge(edge.EdgePropertyId, edge);
            }
        }

        internal void TabulaRasa_internal()
        {
            _currentId = 0;
            _snapshot = EmptySnapshot;
            IndexFactory.DeleteAllIndices();
            VertexCount = 0;
            EdgeCount = 0;
            // A full reset clears every tombstone, so the auto-trim body-free bookkeeping starts fresh.
            _freedTombstoneCount = 0;
            // Reclaim the intern table on a full reset: its entries are only referenced by the
            // graph elements being discarded here, so a reset should release them too. (It is
            // deliberately NOT cleared on Trim, where interned strings are still referenced by the
            // surviving elements.)
            _internTable.Clear();
        }

        /// <summary>
        ///   Publishes the flat graph-element array produced by a load into the master store.
        ///   Invoked by the persistency factory once every element (and its edge fix-up) is
        ///   built, but BEFORE indices and services are rehydrated, because they resolve element
        ///   ids through <see cref="TryGetGraphElement" /> against the published store. The array
        ///   is dense and id-ordered (index == id), matching the on-disk format.
        /// </summary>
        internal void PublishLoadedGraphElements(AGraphElementModel[] graphElements)
        {
            // The loaded array is dense and id-ordered (index == id) and may contain null slots
            // for ids that were null/removed at save time (readers filter them). Copy it into the
            // segmented layout and publish atomically.
            _snapshot = BuildSnapshotFromDenseArray(graphElements, graphElements.Length);
        }

        #region write-ahead log (opt-in durability between snapshots)

        /// <summary>
        ///   Appends a committed transaction to the write-ahead log. Called by the transaction
        ///   manager on the single writer thread AFTER the transaction has reached its committed
        ///   (Finished) terminal state and BEFORE its input is released, so the still-present
        ///   definition can be serialized. A no-op when the WAL is disabled or while replaying (a
        ///   replayed operation must not be re-logged). Only data-mutating transactions and the
        ///   id-space lifecycle transactions (Trim/TabulaRasa) are logged; others are ignored.
        ///
        ///   The append fsyncs before returning, so a committed transaction's log entry is durable
        ///   before its <c>WaitUntilFinished()</c> returns (the task completes only after this call).
        /// </summary>
        /// <summary>
        ///   Appends a committed transaction to the write-ahead log. Returns whether the transaction is
        ///   durable in the log: <c>true</c> when the WAL is disabled (durability is then via the next
        ///   Save) or the entry was appended and fsynced; <c>false</c> when logging is suspended because
        ///   the log is anchored and awaiting its paired snapshot Load (D3) or the failure fence has
        ///   tripped (D1). A first append failure throws (the caller records it); once the fence is
        ///   tripped, subsequent calls return <c>false</c> without throwing.
        /// </summary>
        internal bool LogCommittedTransaction(ATransaction tx)
        {
            var wal = _wal;
            if (wal == null)
            {
                return true;
            }

            if (_walSuspended)
            {
                // Replaying: the operation came from the log and must not be re-appended. Not a new
                // commit, so durability is not degraded.
                return true;
            }

            if (_walAwaitingPairedLoad)
            {
                // D3: an anchored log is waiting for its paired snapshot; a pre-load mutation is applied
                // in memory but must not be logged against the wrong baseline. Report it non-durable.
                return false;
            }

            if (wal.HasFailed)
            {
                // D1: the log is degraded until the next Save; the transaction stays committed but is
                // not durable in the log.
                return false;
            }

            if (!WalTransactionCodec.TryGetEntryType(tx, out var type))
            {
                // Not a loggable transaction (e.g. Save/Load); durability is via the snapshot.
                return true;
            }

            // Buffer the frame for the current commit group (feature write-path-throughput); it becomes
            // durable when the manager calls FlushWal after the batch. Returning true here means
            // "buffered"; the caller ANDs it with FlushWal's result for the final durability.
            wal.AppendBuffered(WalTransactionCodec.SerializeEntry(type, tx));
            return true;
        }

        /// <summary>
        ///   Flushes the current write-ahead-log commit group with a single fsync and returns whether
        ///   the group is durable (feature write-path-throughput). Called by the transaction manager
        ///   once per drained batch, AFTER every transaction body in the batch has buffered its frame
        ///   and BEFORE their completion signals are set - so the durable-before-ack contract holds,
        ///   just amortised. Returns true when the WAL is disabled (durability is via the snapshot).
        /// </summary>
        internal bool FlushWal()
        {
            var wal = _wal;
            if (wal == null)
            {
                return true;
            }

            // Honest flush metrics (feature observability): only a REAL flush attempt - pending
            // frames on a non-tripped fence - is measured. The empty fast path would pollute the
            // duration percentiles with ~0s samples, and the already-degraded path would count
            // ONE real failure as N (every group "fails" until the next Save); the degraded state
            // is what fallen8.wal.degraded and .nondurable report. Timestamp is Enabled-gated.
            var isRealAttempt = wal.PendingFrameCount > 0 && !wal.HasFailed;
            var metrics = Metrics;
            var start = isRealAttempt && metrics != null && metrics.WalFlushDurationEnabled
                ? System.Diagnostics.Stopwatch.GetTimestamp()
                : 0L;

            var durable = wal.FlushGroup();

            if (start != 0L)
            {
                metrics.RecordWalFlushDuration(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds);
            }
            if (isRealAttempt && !durable)
            {
                metrics?.RecordWalFlushFailure();
            }

            return durable;
        }

        /// <summary>
        ///   Appends a payload-less lifecycle marker (used for an automatic Trim, which is not a
        ///   transaction). A no-op when the WAL is disabled, while replaying, while awaiting a paired
        ///   load (D3), or once the failure fence has tripped (D1).
        /// </summary>
        private void LogWriteAheadLogMarker(Persistency.WalEntryType type)
        {
            var wal = _wal;
            if (wal == null || _walSuspended || _walAwaitingPairedLoad || wal.HasFailed)
            {
                return;
            }

            // Buffer the marker in commit order with the surrounding group; it is flushed with them
            // (feature write-path-throughput).
            wal.AppendBuffered(WalTransactionCodec.SerializeEntry(type, null));
        }

        /// <summary>
        ///   Re-executes the write-ahead log's entries, in commit order, against the current graph to
        ///   reconstruct the committed state. Runs with <see cref="_walSuspended" /> set so the
        ///   re-executed operations are not themselves re-logged. Correctness rests on id-determinism
        ///   established by the caller: <see cref="_currentId" /> is restored to the log's baseline and
        ///   the store is padded so <c>id == index</c> holds, so replayed creates re-assign the SAME
        ///   ids and replayed edges/removals/property-changes resolve the SAME elements as before the
        ///   crash. A torn/corrupt tail is handled by <see cref="WriteAheadLog.ReadEntries" /> (it
        ///   stops at the last complete entry), so this loop only ever sees whole, CRC-valid entries.
        /// </summary>
        private int ReplayWriteAheadLog()
        {
            _walSuspended = true;
            try
            {
                var replayed = 0;
                foreach (var payload in _wal.ReadEntries())
                {
                    Persistency.WalEntryType type;
                    ATransaction tx;
                    try
                    {
                        tx = WalTransactionCodec.Deserialize(payload, out type);
                    }
                    catch (Exception ex)
                    {
                        // A CRC-valid entry that nonetheless fails to decode indicates a genuine
                        // format problem; stop replay at the last good entry rather than risk
                        // misapplying it.
                        _logger.LogError(ex, "A write-ahead-log entry could not be decoded; recovery stops at the last good entry ({Count} replayed).", replayed);
                        break;
                    }

                    if (tx != null)
                    {
                        // Fail-stop for CORE DATA entries (feature crash-durability-hardening D4): a
                        // false return or a thrown exception is treated exactly like a decode failure -
                        // stop at the last good entry, because continuing would misapply every later
                        // entry against a diverged id space. Subgraph and stored-query entries
                        // allocate no ids (derived / library state), so a RemoveSubGraph or
                        // RemoveStoredQuery that fails skips-and-continues (like the CreateSubGraph /
                        // RegisterStoredQuery paths below) rather than halting recovery.
                        var isDerivedSubGraphEntry = type == Persistency.WalEntryType.RemoveSubGraph ||
                                                     type == Persistency.WalEntryType.RemoveStoredQuery;
                        bool applied;
                        try
                        {
                            applied = tx.TryExecute(this);
                        }
                        catch (Exception ex)
                        {
                            if (isDerivedSubGraphEntry)
                            {
                                _logger.LogWarning(ex, "Re-executing a logged {Type} entry during recovery threw; skipping it and continuing ({Count} replayed).", type, replayed);
                                replayed++;
                                continue;
                            }

                            _logger.LogError(ex, "Re-executing a logged {Type} transaction during recovery threw; recovery STOPS at the last good entry ({Count} replayed).", type, replayed);
                            break;
                        }

                        if (!applied)
                        {
                            if (isDerivedSubGraphEntry)
                            {
                                _logger.LogWarning("Re-executing a logged {Type} entry during recovery returned false; skipping it and continuing ({Count} replayed).", type, replayed);
                            }
                            else
                            {
                                _logger.LogError("Re-executing a logged {Type} transaction during recovery returned false; recovery STOPS at the last good entry ({Count} replayed).", type, replayed);
                                break;
                            }
                        }
                    }
                    else if (type == Persistency.WalEntryType.Trim)
                    {
                        Trim_internal();
                    }
                    else if (type == Persistency.WalEntryType.TabulaRasa)
                    {
                        TabulaRasa_internal();
                    }
                    else if (type == Persistency.WalEntryType.CreateSubGraph)
                    {
                        ReplaySubGraphCreate(payload);
                    }
                    else if (type == Persistency.WalEntryType.RegisterStoredQuery)
                    {
                        ReplayStoredQueryRegister(payload);
                    }

                    replayed++;
                }

                _logger.LogInformation("Recovered {Count} transaction(s) from the write-ahead log.", replayed);
                return replayed;
            }
            finally
            {
                _walSuspended = false;
            }
        }

        /// <summary>
        ///   Replays one logged <see cref="Persistency.WalEntryType.CreateSubGraph" /> entry:
        ///   recompiles the persisted recipe (via the registered <see cref="SubGraphRecipeCompiler" />)
        ///   and re-executes the equivalent create against the graph as replayed so far. Because
        ///   entries replay in commit order, every vertex/edge the subgraph matched already exists and
        ///   a nested subgraph's source is already registered (resolved by its stable name), so the
        ///   recomputed subgraph matches the identical elements without any id remapping. Any problem -
        ///   an undecodable entry, no compiler registered, a compile failure, or a create that returns
        ///   false - is logged and SKIPPED so recovery continues with later entries (subgraphs are
        ///   rebuildable derived state). Subgraph creation allocates no ids in this graph, so it does
        ///   not perturb the vertex/edge id-determinism the surrounding replay relies on.
        ///
        ///   <para><b>Trust boundary (feature crash-durability-hardening D7).</b> Replaying a recipe
        ///   RECOMPILES the persisted C# fragment via Roslyn and runs it in-process. The WAL/snapshot
        ///   CRC is <em>integrity</em> (against corruption), NOT <em>authenticity</em> (against
        ///   tampering): anyone who can write the save/WAL files gains code execution in the loading
        ///   process at next start. The save/WAL directory is therefore a trust boundary equivalent to
        ///   the application binaries; the mitigation is operational (restrict write access to that
        ///   directory). Recovery reuses the content-keyed compile cache (see
        ///   <c>collectible-codegen-assemblies</c>), so K subgraphs sharing one recipe spec compile
        ///   once and recovery time scales with the number of DISTINCT specs, not the subgraph count.</para>
        /// </summary>
        private void ReplaySubGraphCreate(byte[] payload)
        {
            if (!WalTransactionCodec.TryDecodeSubGraphCreate(payload, out var recipe, out var sourceSubGraphName))
            {
                _logger.LogWarning("A logged CreateSubGraph entry could not be decoded during recovery and was skipped.");
                return;
            }

            var compiler = SubGraphRecipeCompiler;
            if (compiler == null)
            {
                _logger.LogWarning(
                    "The write-ahead log holds a subgraph \"{Name}\" but no recipe compiler is registered; it is skipped on replay. Register IFallen8.SubGraphRecipeCompiler before load to recover logged subgraphs.",
                    recipe.Name);
                return;
            }

            // The create transaction always uses the default subgraph algorithm, so a recipe naming a
            // different algorithm cannot be reproduced faithfully via this path. In the current engine
            // the transaction/REST create is BFS-only, so this never fires; the guard makes the
            // assumption visible if a future multi-algorithm create regresses it.
            if (!string.IsNullOrEmpty(recipe.AlgorithmPluginName) &&
                !string.Equals(recipe.AlgorithmPluginName,
                    Algorithms.SubGraph.BreathFirstSearchSubgraphAlgorithm.AlgorithmPluginName, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Logged subgraph \"{Name}\" was created with algorithm \"{Algorithm}\", but replay recreates it with the default algorithm.",
                    recipe.Name, recipe.AlgorithmPluginName);
            }

            // Compile + re-execute are guarded: a registered ISubGraphRecipeCompiler is third-party
            // code, and if it throws (violating the Try contract) the throw must NOT abort recovery of
            // later entries. Any failure here - a compile failure, a create returning false, or an
            // unexpected throw from the compiler or the create - is warned and skipped so recovery
            // continues; subgraphs are rebuildable derived state. (The built-in compiler + factory
            // already catch internally, so this guard only matters for a misbehaving custom compiler.)
            try
            {
                if (!compiler.TryCompile(recipe, out var definition, out var error))
                {
                    _logger.LogWarning(
                        "Could not compile the recipe for logged subgraph \"{Name}\" during recovery: {Error}; it is skipped.",
                        recipe.Name, error);
                    return;
                }

                var tx = new CreateSubGraphTransaction
                {
                    Definition = definition,
                    SourceSubGraphName = string.IsNullOrEmpty(sourceSubGraphName) ? null : sourceSubGraphName,
                    SpecificationJson = recipe.SpecificationJson
                };

                if (!tx.TryExecute(this))
                {
                    _logger.LogWarning(
                        "Re-executing a logged CreateSubGraph transaction for subgraph \"{Name}\" during recovery returned false (reason {Reason}); it is skipped.",
                        recipe.Name, tx.FailureReason);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Recovering logged subgraph \"{Name}\" threw during recovery; it is skipped and recovery continues with later entries.",
                    recipe.Name);
            }
        }

        /// <summary>
        ///   Builds the in-memory entry for a persisted stored query definition (feature
        ///   stored-query-library): recompiles the source through the registered
        ///   <see cref="StoredQueryCompiler" /> when one is present. Unlike subgraph recipes, a
        ///   stored query is OPERATOR-REGISTERED state, not derived state - so a failure never
        ///   drops the definition: a compile failure (or a compiler that throws, violating its Try
        ///   contract) keeps the entry as <see cref="StoredQueryCompileState.Failed" /> with its
        ///   diagnostics (visible via list/get, 409 on invoke, recoverable by delete+re-register),
        ///   and a missing compiler keeps it as source-only. Loud, never silent loss.
        /// </summary>
        private StoredQueryEntry BuildRehydratedStoredQueryEntry(StoredQueryDefinition definition)
        {
            var compiler = StoredQueryCompiler;
            if (compiler == null)
            {
                return new StoredQueryEntry(definition, StoredQueryCompileState.SourceOnly, null);
            }

            try
            {
                if (compiler.TryCompile(definition, out var artifact, out var error))
                {
                    return new StoredQueryEntry(definition, StoredQueryCompileState.Compiled, artifact);
                }

                _logger.LogError(
                    "Stored query \"{Name}\" failed to recompile on load and is kept as Failed (delete + re-register to recover): {Error}",
                    definition.Name, error);
                return new StoredQueryEntry(definition, StoredQueryCompileState.Failed, null, error);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Recompiling stored query \"{Name}\" threw; it is kept as Failed (delete + re-register to recover).",
                    definition.Name);
                return new StoredQueryEntry(definition, StoredQueryCompileState.Failed, null, ex.Message);
            }
        }

        /// <summary>
        ///   Replaces the stored query library with the definitions of a loaded snapshot manifest,
        ///   eagerly recompiling each via <see cref="BuildRehydratedStoredQueryEntry" />. Warns once
        ///   when definitions exist but no compiler is registered (embedded engine use: entries load
        ///   as source-only; there is no invocation surface without a hosting layer anyway).
        /// </summary>
        private void RehydrateStoredQueries(List<StoredQueryDefinition> definitions)
        {
            var entries = new List<StoredQueryEntry>(definitions.Count);

            if (definitions.Count > 0 && StoredQueryCompiler == null)
            {
                _logger.LogWarning(
                    "The savegame holds {Count} stored query definition(s) but no stored query compiler is registered; they are loaded as source-only. Register IFallen8.StoredQueryCompiler before load to recompile them.",
                    definitions.Count);
            }

            foreach (var definition in definitions)
            {
                if (definition == null || !StoredQueryLibrary.IsValidName(definition.Name))
                {
                    _logger.LogError("A stored query definition in the manifest has an invalid name and was skipped.");
                    continue;
                }

                entries.Add(BuildRehydratedStoredQueryEntry(definition));
            }

            StoredQueries.ReplaceAll(entries);

            if (entries.Count > 0)
            {
                _logger.LogInformation("Rehydrated {Count} stored query definition(s).", entries.Count);
            }
        }

        /// <summary>
        ///   Replays one logged <see cref="Persistency.WalEntryType.RegisterStoredQuery" /> entry:
        ///   decodes the persisted definition, recompiles it (keep-and-mark-Failed on failure, per
        ///   <see cref="BuildRehydratedStoredQueryEntry" /> - operator state is never silently
        ///   dropped) and re-executes the equivalent registration against the library as replayed so
        ///   far, in commit order. An undecodable entry is warned and skipped so recovery continues;
        ///   registrations allocate no element ids, so skipping one cannot perturb the surrounding
        ///   replay's id-determinism. The D7 trust-boundary note on <see cref="ReplaySubGraphCreate" />
        ///   applies identically: replay RECOMPILES persisted C# via Roslyn in-process, so the
        ///   save/WAL directory is a trust boundary equivalent to the application binaries.
        /// </summary>
        private void ReplayStoredQueryRegister(byte[] payload)
        {
            if (!WalTransactionCodec.TryDecodeStoredQueryRegister(payload, out var definition))
            {
                _logger.LogWarning("A logged RegisterStoredQuery entry could not be decoded during recovery and was skipped.");
                return;
            }

            try
            {
                var tx = new RegisterStoredQueryTransaction
                {
                    Entry = BuildRehydratedStoredQueryEntry(definition),
                    // A replayed registration was already quota-checked at its original commit;
                    // recovery may run before the operator's configured ceiling is applied, so
                    // re-enforcing here could silently drop committed operator state.
                    BypassQuota = true
                };

                if (!tx.TryExecute(this))
                {
                    _logger.LogWarning(
                        "Re-executing a logged RegisterStoredQuery transaction for \"{Name}\" during recovery returned false (reason {Reason}); it is skipped.",
                        definition.Name, tx.FailureReason);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Recovering logged stored query \"{Name}\" threw during recovery; it is skipped and recovery continues with later entries.",
                    definition.Name);
            }
        }

        /// <summary>
        ///   Grows the published snapshot's live slot count to <paramref name="targetCount" /> (padding
        ///   the tail with null slots), used before a WAL replay so that <c>_currentId == Count</c>
        ///   holds. This is required when the snapshot's id-space size is smaller than the log's
        ///   baseline id - which happens when the highest-id element(s) were soft-removed (without a
        ///   Trim) before the snapshot: the snapshot then omits those top ids, but a replayed create
        ///   must still be appended at the SAME index as its original id. A no-op when the target does
        ///   not exceed the current count.
        /// </summary>
        private void SetSnapshotCountForReplay(int targetCount)
        {
            var snap = _snapshot;
            if (targetCount <= snap.Count)
            {
                return;
            }

            var dense = new AGraphElementModel[targetCount];
            var segments = snap.Segments;
            for (int i = 0; i < snap.Count; i++)
            {
                dense[i] = segments[i >> SegmentShift][i & SegmentMask];
            }
            _snapshot = BuildSnapshotFromDenseArray(dense, targetCount);
        }

        #endregion

        internal void Load_internal(String path, Boolean startServices = false)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                _logger.LogInformation("There is no path given, so nothing will be loaded.");
                return;
            }

            // Cold-path instrumentation (feature observability): duration + bytes + span,
            // failure counter on a rejected load - whether it threw (corrupt/invalid file) or
            // returned false (e.g. a non-existent path). The load itself is unchanged.
            using var span = Diagnostics.Fallen8Diagnostics.Source.StartActivity("fallen8.checkpoint.load");
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            int replayedEntries;
            bool loaded;
            try
            {
                loaded = LoadCore(path, startServices, out replayedEntries);
            }
            catch
            {
                Metrics?.RecordCheckpointFailure("load");
                span?.SetStatus(System.Diagnostics.ActivityStatusCode.Error);
                throw;
            }

            if (!loaded)
            {
                Metrics?.RecordCheckpointFailure("load");
                span?.SetStatus(System.Diagnostics.ActivityStatusCode.Error);
                return;
            }

            var bytes = MeasureCheckpointBytes(path);
            Metrics?.RecordCheckpointLoad(
                System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds, bytes);
            span?.SetTag("checkpoint.bytes", bytes);
            span?.SetTag("checkpoint.wal.replayed", replayedEntries);
        }

        private bool LoadCore(String path, Boolean startServices, out int replayedEntries)
        {
            replayedEntries = 0;

            _logger.LogInformation("Fallen-8 now loads a savegame from path \"{Path}\"", path);

            var oldIndexFactory = IndexFactory;
            var oldServiceFactory = ServiceFactory;
            var oldSubGraphFactory = SubGraphFactory;
            oldServiceFactory.ShutdownAllServices();
            var oldSnapshot = _snapshot;
            var oldCurrentId = _currentId;
            var oldId = Id;

            _snapshot = EmptySnapshot;

            // Load publishes the graph elements into _snapshot itself (via
            // PublishLoadedGraphElements) before it rehydrates indices, because index
            // rehydration resolves element ids through TryGetGraphElement against the
            // published master store. Publication goes through that method rather than a
            // by-ref out-parameter so the volatile snapshot field is written atomically.
            bool success;
            try
            {
                success = _persistencyFactory.Load(this, path, ref _currentId, startServices);
            }
            catch (Exception ex)
            {
                // A rejected load - the file was not a Fallen-8 save (missing magic / unknown
                // version), failed its integrity check, or was truncated/corrupt (findings C2/C4/C5)
                // - must leave the engine exactly as it was, then surface as a rolled-back
                // transaction (the worker maps a throw to RolledBack + Error, which the REST layer
                // maps to 500). Restore the pre-load state and rethrow. The single-writer worker
                // survives this (C3/B6): only the transaction rolls back, the thread keeps running.
                _logger.LogError(ex, "Loading the savegame from \"{Path}\" was rejected; the database is left unchanged.", path);

                _currentId = oldCurrentId;
                _snapshot = oldSnapshot;
                IndexFactory = oldIndexFactory;
                ServiceFactory = oldServiceFactory;
                SubGraphFactory = oldSubGraphFactory;
                // Restore the engine identity too: PersistencyFactory.Load sets it via SetId(...) once
                // it trusts the file, so a load that then failed must not leave the DB carrying the
                // rejected save's Guid. (In practice SetId runs only after clean-reject validation, so
                // this is rarely reachable - but a rolled-back load must leave EVERYTHING unchanged.)
                SetId(oldId);
                ServiceFactory.StartAllServices();
                throw;
            }

            var walHandledIdSpace = false;

            if (success)
            {
                oldIndexFactory.DeleteAllIndices();
                oldSubGraphFactory.DeleteAllSubGraphs();

                // P5: the load has committed and published its own snapshot; the previous graph is no
                // longer reachable for rollback (neither the catch nor the else-restore below can run
                // now), so drop our last reference to it here - BEFORE the closing Trim_internal
                // rebuilds (and transiently doubles) the store - rather than holding the old graph,
                // the new store and the trim's temporaries all at once.
                oldSnapshot = null;

                // Rebuild persisted subgraphs against the freshly loaded graph. Requires a
                // registered recipe compiler; without one, persisted subgraphs are skipped.
                var recipes = _persistencyFactory.LoadSubGraphRecipes(path);
                if (recipes.Count > 0)
                {
                    SubGraphFactory.RehydrateFromRecipes(recipes, SubGraphRecipeCompiler);
                }

                // Rehydrate the stored query library from its manifest (feature
                // stored-query-library): the load REPLACES the library wholesale, exactly like the
                // graph itself, BEFORE any WAL replay applies later Register/Remove entries on top.
                RehydrateStoredQueries(_persistencyFactory.LoadStoredQueryDefinitions(path));

                // WAL (spec P4/§5). When the WAL is enabled it OWNS the loaded snapshot's id-space
                // handling: it deliberately does NOT run the closing compaction, so the in-memory id
                // space stays IDENTICAL to the on-disk snapshot - which is what keeps a future reload
                // + replay id-consistent (the log's baseline id is meaningful only against the exact
                // snapshot id space). At this point _currentId is the snapshot's id-space size.
                if (_wal != null)
                {
                    if (_wal.PairsWith(path))
                    {
                        // The log pairs with THIS snapshot: replay the transactions committed after
                        // it, in commit order. Restore _currentId to the log's baseline (the
                        // snapshot-time high-water mark, which may exceed the snapshot's id-space size
                        // if top ids were soft-removed) and pad the store so id==index holds, so
                        // replayed creates re-assign the SAME ids and edges/removals resolve the SAME
                        // elements. The result equals the exact pre-crash committed state.
                        var baseline = (int)_wal.BaselineCurrentId;
                        SetSnapshotCountForReplay(baseline);
                        _currentId = baseline;
                        replayedEntries = ReplayWriteAheadLog();
                    }
                    else
                    {
                        // The log does not pair with this snapshot (a different/older snapshot, or a
                        // pre-snapshot log). If it STILL HOLDS committed entries, re-anchoring drops
                        // them - work committed since the log's own snapshot that is NOT present in the
                        // snapshot now being loaded. That is legitimate (e.g. loading an older snapshot,
                        // or bootstrapping onto a foreign one), but it must never be silent: warn
                        // loudly so a mispaired reload - a snapshot loaded via a path the log was not
                        // anchored to - surfaces as a signal rather than as silent data loss.
                        if (_wal.HasEntries())
                        {
                            _logger.LogWarning(
                                "The write-ahead log holds committed entries but does not pair with the snapshot being loaded from \"{Path}\"; those entries will be DISCARDED (not replayed). If this snapshot was meant to pair with the log, reload it via the exact path the log was anchored to.",
                                path);
                        }

                        // Discard the stale entries and re-anchor the log to THIS snapshot, baselined at
                        // the snapshot's own id-space size (the current _currentId, before any
                        // compaction), so future commits are logged against the correct baseline and a
                        // later reload of this snapshot stays id-consistent.
                        _wal.ResetToSnapshot(path, _currentId);
                    }

                    _txManager.Trim();
                    RecalculateGraphElementCounter();
                    walHandledIdSpace = true;

                    // The paired snapshot is now loaded (or the log was re-anchored to it): logging is
                    // no longer suspended (feature crash-durability-hardening D3).
                    _walAwaitingPairedLoad = false;
                }
            }
            else
            {
                // A failed load (e.g. a non-existent path returning false) must leave EVERYTHING as it
                // was - symmetric with the catch branch above, which restores _currentId. Restore it
                // here too so a partially-advanced counter cannot survive a failed load.
                _currentId = oldCurrentId;
                _snapshot = oldSnapshot;
                IndexFactory = oldIndexFactory;
                ServiceFactory = oldServiceFactory;
                SubGraphFactory = oldSubGraphFactory;
                ServiceFactory.StartAllServices();

                // D2: with the WAL enabled, the restored snapshot already sits in the exact id space the
                // log baseline was recorded against. Running the closing Trim_internal would reassign
                // ids WITHOUT logging a Trim marker, so a later reload + replay (which does not know
                // about that trim) would resolve the wrong elements. Skip it, symmetric with the
                // success path (feature crash-durability-hardening D2).
                if (_wal != null)
                {
                    walHandledIdSpace = true;
                }
            }

            // WAL-disabled (and failed-load) path: compact as before - behaviour is unchanged. The
            // WAL path above deliberately skips this so the loaded id space matches the on-disk
            // snapshot for replay consistency.
            if (!walHandledIdSpace)
            {
                Trim_internal();
            }

            return success;
        }

        public override TransactionInformation EnqueueTransaction(ATransaction tx)
        {
            return _txManager.AddTransaction(tx);
        }

        public override TransactionState GetTransactionState(String txId)
        {
            return _txManager.GetState(txId);
        }

        public override void Dispose()
        {
            // Unregister the metric instruments BEFORE anything is torn down (feature
            // observability, pinned by test): an exporter's collection thread may invoke a
            // gauge callback at any moment, and the callbacks read the transaction manager and
            // WAL state disposed below. Disposing the Meter first unregisters the callbacks
            // up front; the residual in-flight-callback window is additionally covered by the
            // callbacks' own containment (Guarded / the disposed-queue and file guards).
            Metrics?.Dispose();
            Metrics = null;

            // Stop the single transaction-writer thread FIRST (finding P2), so no in-flight or queued
            // transaction can run concurrently with - or after - the state teardown below. This both
            // gives the worker a clean shutdown and removes any race between a late transaction and
            // the factory/snapshot reset that follows.
            _txManager.Dispose();

            // Release the write-ahead log and drop the reference BEFORE the TabulaRasa below, so the
            // teardown reset is not mistaken for a logged operation. Dispose does NOT reset/truncate
            // the log: the on-disk entries remain (fsync'd per commit) so that dropping the instance
            // is a faithful stand-in for a crash - a fresh instance opening the same log recovers
            // them. (Only a Save resets the log.)
            _wal?.Dispose();
            _wal = null;

            TabulaRasa_internal();

            // Publish the empty snapshot rather than null on teardown: a reader racing Dispose then
            // captures a holder with Count == 0 (a clean "not found"/empty result) instead of
            // dereferencing a null holder and throwing a NullReferenceException on snap.Count.
            _snapshot = EmptySnapshot;

            IndexFactory.DeleteAllIndices();
            IndexFactory = null;

            ServiceFactory.ShutdownAllServices();
            ServiceFactory = null;

            SubGraphFactory.DeleteAllSubGraphs();
            SubGraphFactory = null;

            StoredQueries.Clear();
            StoredQueries = null;

            // After the writer thread has stopped (no more publishes): stop the dispatcher and
            // complete every subscriber stream.
            ChangeFeed?.Dispose();
            ChangeFeed = null;
        }

        #region private helper methods

        /// <summary>
        ///   Finds the elements.
        /// </summary>
        /// <returns> The elements. </returns>
        /// <param name='finder'> Finder. </param>
        /// <param name='literal'> Literal. </param>
        /// <param name='propertyId'> Property identifier. </param>
        /// <param name='interestingLabel'> The interesting label. </param>
        private List<AGraphElementModel> FindElements(BinaryOperatorDelegate finder, IComparable literal, String propertyId,
            String interestingLabel = null)
        {
            return FindElements(
                aGraphElement =>
                {
                    Object property;
                    return aGraphElement.TryGetProperty(out property, propertyId) &&
                           finder(property as IComparable, literal);
                });
        }

        /// <summary>
        /// Find elements by scanning the list
        /// </summary>
        /// <param name="seeker">A delegate to search for the right element</param>
        /// <param name='interestingLabel'> The interesting label. </param>
        /// <returns>A list of matching graph elements</returns>
        private List<AGraphElementModel> FindElements(ElementSeeker seeker, String interestingLabel = null)
        {
            // One fused predicate over the parallel live scan instead of three chained Where stages:
            // identical short-circuit order (null/removed first, then label, then seeker), so removed
            // or null elements never reach the seeker and the result multiset is unchanged - but only
            // one PLINQ operator (and no per-query intermediate closures) instead of three.
            var labelMatches = CheckLabel(interestingLabel);
            return LiveElements(_snapshot)
                .Where(_ => _ != null && !_._removed && labelMatches(_) && seeker(_))
                .ToList();
        }

        private static Func<AGraphElementModel, Boolean> CheckLabel(String interestingLabel = null)
        {
            return _ => interestingLabel == null || interestingLabel != null && _.Label != null && _.Label.Equals(interestingLabel);
        }

        /// <summary>
        ///   Finds elements via an index.
        /// </summary>
        /// <returns> The elements. </returns>
        /// <param name='finder'> Finder delegate. </param>
        /// <param name='literal'> Literal. </param>
        /// <param name='index'> Index. </param>
        /// <summary>
        ///   Filters an index bucket down to its LIVE elements (feature index-lifecycle 3.2): drops any
        ///   <c>null</c> or <c>_removed</c> element so an index-serving scan returns exactly the live set
        ///   <see cref="FindElements(ElementSeeker)" /> / GraphScan would for the same logical query -
        ///   index membership is only valid while its element is live, and this is the read-end floor
        ///   that holds that contract even before the write-end purge runs. Returns the SAME reference
        ///   when nothing is dead (the common case), so the Equals fast path keeps handing back the
        ///   index's shared bucket with no allocation.
        /// </summary>
        private static IReadOnlyList<AGraphElementModel> FilterLive(IReadOnlyList<AGraphElementModel> bucket)
        {
            if (bucket == null)
            {
                return null;
            }

            var dead = 0;
            for (var i = 0; i < bucket.Count; i++)
            {
                var element = bucket[i];
                if (element == null || element._removed)
                {
                    dead++;
                }
            }

            if (dead == 0)
            {
                return bucket;
            }

            var live = new List<AGraphElementModel>(bucket.Count - dead);
            for (var i = 0; i < bucket.Count; i++)
            {
                var element = bucket[i];
                if (element != null && !element._removed)
                {
                    live.Add(element);
                }
            }
            return live;
        }

        private static List<AGraphElementModel> FindElementsIndex(BinaryOperatorDelegate finder,
                                                                  IComparable literal, IIndex index)
        {
            // Sequential (feature scan-result-representation): the finder is a light IComparable.CompareTo,
            // so the former .AsParallel() paid PLINQ partition/merge overhead over a cheap predicate; and
            // the result is a per-call throwaway, so a right-sized de-duplicating List replaces the AVL
            // tree. A reference-identity HashSet reproduces the former cross-bucket .Distinct() (an element
            // indexed under several matching keys appears once) in a single pass, no re-treeing.
            var result = new List<AGraphElementModel>();
            var seen = new HashSet<AGraphElementModel>();
            foreach (var indexElement in index.GetKeyValues())
            {
                if (!finder((IComparable)indexElement.Key, literal))
                {
                    continue;
                }

                foreach (var graphElement in indexElement.Value)
                {
                    // Skip null / _removed so a removed-but-still-indexed element never surfaces
                    // (feature index-lifecycle 3.2), then dedup across buckets.
                    if (graphElement != null && !graphElement._removed && seen.Add(graphElement))
                    {
                        result.Add(graphElement);
                    }
                }
            }
            return result;
        }

        /// <summary>
        ///   P4 (engine-performance-followups): answers an ORDERED IndexScan operator
        ///   (Greater / GreaterOrEquals / Lower / LowerOrEquals) on a <see cref="IRangeIndex" /> via its
        ///   O(log n + k) sorted range methods instead of the generic O(n) <see cref="FindElementsIndex" />
        ///   scan. Returns <c>false</c> for any non-ordered operator (Equals / NotEquals) so the caller
        ///   falls back to the generic path.
        ///
        ///   Result parity with <see cref="FindElementsIndex" />: the RangeIndex's sorted methods select
        ///   EXACTLY the keys the generic finder's per-key <c>CompareTo</c> predicate would - both order
        ///   keys by <see cref="IComparable.CompareTo" />, so the suffix/prefix the binary search brackets
        ///   is the same key set the linear scan keeps (GreaterOrEquals/LowerOrEquals include the boundary
        ///   key, Greater/Lower exclude it, matching the <c>&gt;=</c>/<c>&gt;</c>/<c>&lt;=</c>/<c>&lt;</c>
        ///   predicates). Those methods, however, concatenate the matched buckets WITHOUT deduping,
        ///   whereas <see cref="FindElementsIndex" /> applies a cross-bucket <c>.Distinct()</c>. This
        ///   method therefore reapplies the SAME <c>.Distinct()</c>, so a graph element indexed under
        ///   several matching keys appears exactly once - byte-identical to the generic output set.
        /// </summary>
        private static bool TryOrderedRangeIndexScan(out IReadOnlyList<AGraphElementModel> result,
                                                     IRangeIndex rangeIndex, IComparable literal, BinaryOperator binOp)
        {
            ImmutableList<AGraphElementModel> matched;
            switch (binOp)
            {
                case BinaryOperator.Greater:
                    rangeIndex.GreaterThan(out matched, literal, false);
                    break;

                case BinaryOperator.GreaterOrEquals:
                    rangeIndex.GreaterThan(out matched, literal, true);
                    break;

                case BinaryOperator.Lower:
                    rangeIndex.LowerThan(out matched, literal, false);
                    break;

                case BinaryOperator.LowerOrEquals:
                    rangeIndex.LowerThan(out matched, literal, true);
                    break;

                default:
                    // Equals / NotEquals are not ordered range operators - fall back to the generic path.
                    result = null;
                    return false;
            }

            // Reapply FindElementsIndex's cross-bucket .Distinct() so the deduped set is identical -
            // into a right-sized List (feature scan-result-representation), no throwaway tree - and skip
            // null / _removed so a removed element never surfaces (feature index-lifecycle 3.2).
            var deduped = new List<AGraphElementModel>(matched.Count);
            var seen = new HashSet<AGraphElementModel>();
            foreach (var graphElement in matched)
            {
                if (graphElement != null && !graphElement._removed && seen.Add(graphElement))
                {
                    deduped.Add(graphElement);
                }
            }
            result = deduped;
            return true;
        }

        /// <summary>
        ///   Method for binary comparison
        /// </summary>
        /// <returns> <c>true</c> for equality; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryEqualsMethod(IComparable property, IComparable literal)
        {
            return property.Equals(literal);
        }

        /// <summary>
        ///   Method for binary comparison
        /// </summary>
        /// <returns> <c>true</c> for inequality; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryNotEqualsMethod(IComparable property, IComparable literal)
        {
            return !property.Equals(literal);
        }

        /// <summary>
        ///   Method for binary comparison
        /// </summary>
        /// <returns> <c>true</c> for greater property; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryGreaterMethod(IComparable property, IComparable literal)
        {
            return property.CompareTo(literal) > 0;
        }

        /// <summary>
        ///   Method for binary comparison
        /// </summary>
        /// <returns> <c>true</c> for lower property; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryLowerMethod(IComparable property, IComparable literal)
        {
            return property.CompareTo(literal) < 0;
        }

        /// <summary>
        ///   Method for binary comparison
        /// </summary>
        /// <returns> <c>true</c> for lower or equal property; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryLowerOrEqualMethod(IComparable property, IComparable literal)
        {
            return property.CompareTo(literal) <= 0;
        }

        /// <summary>
        ///   Method for binary comparison
        /// </summary>
        /// <returns> <c>true</c> for greater or equal property; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryGreaterOrEqualMethod(IComparable property, IComparable literal)
        {
            return property.CompareTo(literal) >= 0;
        }

        /// <summary>
        ///   Whether the automatic, post-removal tombstone reclamation is enabled (feature
        ///   trim-reader-safety Part B). Default OFF: auto-trim NO LONGER renumbers ids (that silent,
        ///   no-client-action id reassignment was the P1 reader/REST-handle hazard). When enabled, it
        ///   frees tombstone bodies (properties + adjacency) WITHOUT reassigning any id or removing any
        ///   slot; id renumbering is reserved for the explicit, operator-scheduled <c>TrimTransaction</c>.
        /// </summary>
        internal bool _autoTrimEnabled = false;

        /// <summary>
        ///   Tombstone count (soft-deleted or load-gap slots) whose growth since the last reclamation
        ///   triggers an automatic body-free pass, when auto-trim is enabled (feature trim-reader-safety
        ///   Part B). A field (not a const) so a soak test can lower it.
        /// </summary>
        internal int _autoTrimTombstoneThreshold = 100_000;

        /// <summary>
        ///   Tombstone slots already body-freed by a prior auto-trim pass. Because free-fields keeps the
        ///   slot (no id reassignment, <c>Count</c> unchanged), the raw tombstone count stays high after
        ///   a pass; triggering on <c>tombstones - _freedTombstoneCount</c> so a pass fires only once per
        ///   <see cref="_autoTrimTombstoneThreshold"/> NEWLY accumulated tombstones, not on every removal.
        ///   Reset when the id space is rebuilt (<see cref="Trim_internal"/> / <see cref="TabulaRasa_internal"/>).
        /// </summary>
        private int _freedTombstoneCount;

        /// <summary>
        ///   Enables or disables the automatic post-removal tombstone reclamation and sets its
        ///   threshold (feature trim-reader-safety). Auto-trim frees tombstone bodies WITHOUT
        ///   reassigning ids; it is off by default. A non-positive threshold is clamped to 1.
        /// </summary>
        public override void ConfigureAutoTrim(bool enabled, int tombstoneThreshold)
        {
            _autoTrimEnabled = enabled;
            _autoTrimTombstoneThreshold = Math.Max(1, tombstoneThreshold);
        }

        /// <summary>
        ///   Considers automatic tombstone reclamation after a committed element removal (feature
        ///   trim-reader-safety Part B; formerly finding M4). Runs ONLY on the single transaction-worker
        ///   thread, after the removal has committed. When enabled and enough NEW tombstones have
        ///   accumulated, it frees each tombstone's heavy body (properties + adjacency) via
        ///   <see cref="AGraphElementModel.ReleaseBodyForTombstone"/> - a per-field volatile write, safe
        ///   for lock-free readers - while KEEPING the slot, so no id is reassigned and the id space is
        ///   unchanged. Because it changes no id space, it emits NO WAL <c>Trim</c> marker (replay
        ///   reconstructs the identical id space without one). Off by default.
        /// </summary>
        internal void MaybeAutoTrim()
        {
            if (!_autoTrimEnabled)
            {
                return;
            }

            var snap = _snapshot;
            // Live elements are counted in VertexCount + EdgeCount; the remainder of the published slots
            // are tombstones (removed) or load gaps. Trigger only on tombstones accumulated SINCE the
            // last body-free pass (free-fields keeps slots, so the raw count does not fall).
            int tombstones = snap.Count - VertexCount - EdgeCount;

            if (tombstones - _freedTombstoneCount >= _autoTrimTombstoneThreshold)
            {
                _logger.LogInformation("Auto-trim: {New} newly reclaimable tombstone(s) >= threshold {Threshold}; freeing their bodies (ids unchanged).",
                    tombstones - _freedTombstoneCount, _autoTrimTombstoneThreshold);
                ReleaseTombstoneBodies(snap);
                _freedTombstoneCount = tombstones;
            }
        }

        /// <summary>
        ///   Frees the heavy body of every removed (tombstone) slot in the published snapshot, keeping
        ///   the slot so ids stay stable (feature trim-reader-safety Part B). Idempotent (nulling an
        ///   already-freed field is a no-op) and reader-safe (each release is a per-field volatile
        ///   publish). Runs on the single writer thread.
        /// </summary>
        private void ReleaseTombstoneBodies(Snapshot snap)
        {
            var segments = snap.Segments;
            int count = snap.Count;
            for (int i = 0; i < count; i++)
            {
                var graphElement = segments[i >> SegmentShift][i & SegmentMask];
                if (graphElement != null && graphElement._removed)
                {
                    graphElement.ReleaseBodyForTombstone();
                }
            }
        }

        /// <summary>
        ///   Trims the Fallen-8 by removing null/tombstone entries and compacting the id space. This
        ///   REASSIGNS every surviving element's <c>Id</c> to its new dense index, so it must be invoked
        ///   only knowingly, via the explicit <c>TrimTransaction</c> - NOT automatically (feature
        ///   trim-reader-safety): a caller or reader holding an element id across this call is remapped
        ///   to a different element. In-flight readers holding the PREVIOUS snapshot keep a fully
        ///   consistent old-id-space view (the previous segments are never mutated), but any id captured
        ///   before the trim and resolved after it points at a different element. Automatic reclamation
        ///   uses the renumber-free <see cref="MaybeAutoTrim"/> instead.
        /// </summary>
        internal void Trim_internal()
        {
            // Runs on the single writer thread. Capture the current snapshot once.
            var snap = _snapshot;
            int count = snap.Count;
            var segments = snap.Segments;

            // Trim individual graph elements and gather the survivors (non-null, not removed).
            var survivors = new List<AGraphElementModel>(count);
            for (var i = 0; i < count; i++)
            {
                AGraphElementModel graphElement = segments[i >> SegmentShift][i & SegmentMask];
                graphElement?.Trim();
                if (graphElement != null && !graphElement._removed)
                {
                    survivors.Add(graphElement);
                }
            }

            // Reassign IDs sequentially (index == id).
            for (int i = 0; i < survivors.Count; i++)
            {
                survivors[i].SetId(i);
            }

            var compacted = survivors.ToArray();

            // Update current ID and publish the compacted snapshot with a single atomic write.
            // In-flight readers holding the previous snapshot keep a fully consistent (old-id-space)
            // view and never index out of range, because their bound check used that snapshot's
            // Count and the previous segments are never mutated by this rebuild.
            _currentId = compacted.Length;
            _snapshot = BuildSnapshotFromDenseArray(compacted, compacted.Length);

            // The compaction removed every tombstone slot, so the auto-trim body-free bookkeeping
            // (feature trim-reader-safety) starts fresh against the new, tombstone-free id space.
            _freedTombstoneCount = 0;

            // Trim transaction manager
            _txManager.Trim();

            // Recalculate counters
            RecalculateGraphElementCounter();
        }

        /// <summary>
        /// Recalculates the count of the graph elements
        /// </summary>
        private void RecalculateGraphElementCounter()
        {
            EdgeCount = GetCountOf<EdgeModel>();
            VertexCount = GetCountOf<VertexModel>();
        }

        public int GetCountOf<TInteresting>()
        {
            // Sequential count over the captured snapshot (finding P7): counting is a light
            // per-element predicate, so a direct walk avoids PLINQ's partition/merge overhead. A
            // single volatile capture keeps the Count consistent with the segments it indexes.
            var snap = _snapshot;
            var segments = snap.Segments;
            int count = snap.Count;
            int result = 0;
            for (int i = 0; i < count; i++)
            {
                var ge = segments[i >> SegmentShift][i & SegmentMask];
                if (ge != null && !ge._removed && ge is TInteresting)
                {
                    result++;
                }
            }
            return result;
        }

        public override IReadOnlyList<VertexModel> GetAllVertices(String interestingLabel = null)
        {
            // Fill a right-sized List directly instead of packing the sequential scan into an
            // ImmutableList (an AVL tree) the caller immediately drops (feature scan-result-representation).
            // The walk is the same cheap, cache-friendly, id-ordered scan (finding P7); only the result
            // packaging changes - a flat reference array (8 B/slot, contiguous) instead of ~48 B/node
            // tree. Capture the snapshot once (volatile read); VertexCount is a capacity hint only, so a
            // stale count costs at most a resize, never a wrong result (the snapshot walk is authoritative).
            var snap = _snapshot;
            var labelMatches = CheckLabel(interestingLabel);
            var segments = snap.Segments;
            int count = snap.Count;
            var result = new List<VertexModel>(interestingLabel == null ? VertexCount : 0);
            for (int i = 0; i < count; i++)
            {
                if (segments[i >> SegmentShift][i & SegmentMask] is VertexModel vertex && !vertex._removed && labelMatches(vertex))
                {
                    result.Add(vertex);
                }
            }
            return result;
        }

        public override IReadOnlyList<EdgeModel> GetAllEdges(String interestingLabel = null)
        {
            var snap = _snapshot;
            var labelMatches = CheckLabel(interestingLabel);
            var segments = snap.Segments;
            int count = snap.Count;
            var result = new List<EdgeModel>(interestingLabel == null ? EdgeCount : 0);
            for (int i = 0; i < count; i++)
            {
                if (segments[i >> SegmentShift][i & SegmentMask] is EdgeModel edge && !edge._removed && labelMatches(edge))
                {
                    result.Add(edge);
                }
            }
            return result;
        }

        public override IReadOnlyList<AGraphElementModel> GetAllGraphElements(String interestingLabel = null)
        {
            var snap = _snapshot;
            var labelMatches = CheckLabel(interestingLabel);
            var segments = snap.Segments;
            int count = snap.Count;
            var result = new List<AGraphElementModel>(interestingLabel == null ? VertexCount + EdgeCount : 0);
            for (int i = 0; i < count; i++)
            {
                var graphElement = segments[i >> SegmentShift][i & SegmentMask];
                if (graphElement != null && !graphElement._removed && labelMatches(graphElement))
                {
                    result.Add(graphElement);
                }
            }
            return result;
        }

        #endregion
    }
}
