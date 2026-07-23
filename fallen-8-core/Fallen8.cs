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
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Cache;
using NoSQL.GraphDB.Core.Index;
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
    public sealed partial class Fallen8 : AFallen8
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
        ///   Initializes a new instance of the Fallen-8 class. The optional
        ///   <paramref name="metricsScopeId"/> is an opaque, HOST-ASSIGNED (never user-supplied)
        ///   identifier attached to this engine's meter as the <c>fallen8.scope.id</c> tag, so
        ///   several engines in one process (feature graph-namespaces: one engine per namespace)
        ///   report distinguishable instruments instead of colliding on the shared meter name.
        /// </summary>
        public Fallen8(ILoggerFactory loggerfactory, String metricsScopeId = null)
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
            Metrics = new Diagnostics.Fallen8Metrics(this, metricsScopeId);
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
        public Fallen8(ILoggerFactory loggerfactory, ChangeFeedOptions changeFeedOptions,
            String metricsScopeId = null)
            : this(loggerfactory, metricsScopeId)
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
            ChangeFeedOptions changeFeedOptions = null,
            String metricsScopeId = null)
            : this(loggerfactory, metricsScopeId)
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

        #endregion

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
    }
}
