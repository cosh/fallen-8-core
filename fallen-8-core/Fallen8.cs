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
using NoSQL.GraphDB.Core.Service;
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
        ///   The persisitency factory.
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
        /// The logger
        /// </summary>
        private readonly ILogger<Fallen8> _logger;

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
            IndexFactory.Indices.Clear();
            _txManager = new TransactionManager(this);
            _persistencyFactory = new PersistencyFactory(persistencyLogger);
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
        ///   Initializes a new instance of the Fallen-8 class with an OPT-IN write-ahead log for
        ///   durability between snapshots (spec P4 / plan Phase 5). When
        ///   <paramref name="writeAheadLogOptions" /> is null or carries no path, the WAL is disabled
        ///   and the instance behaves exactly like the default constructor. When a log path is given,
        ///   an existing log at that path is adopted: a log that predates any snapshot is replayed
        ///   immediately onto the (empty) graph, while a log anchored to a snapshot is replayed later
        ///   by <c>Load</c> of that snapshot.
        /// </summary>
        public Fallen8(ILoggerFactory loggerfactory, WriteAheadLogOptions writeAheadLogOptions)
            : this(loggerfactory)
        {
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

            return newVertex;
        }

        internal List<VertexModel> CreateVertices_internal(List<VertexDefinition> definitions)
        {
            var newVertices = new List<VertexModel>();

            if (definitions != null && definitions.Count > 0)
            {
                foreach (var aVertexDef in definitions)
                {
                    //create the new vertex (interning the label and property keys, finding M2)
                    var newVertex = new VertexModel(_currentId, aVertexDef.CreationDate, Intern(aVertexDef.Label), InternPropertyKeys(aVertexDef.Properties));

                    newVertices.Add(newVertex);

                    //increment the id (single-writer field, plain increment - finding P10)
                    _currentId++;

                    //Increase the vertex count
                    VertexCount++;
                }

                //insert all vertices in batch
                AppendGraphElements(newVertices);
            }

            return newVertices;
        }

        public override bool TryGetGraphElement(out AGraphElementModel result, int id)
        {
            // Capture the published snapshot once (volatile acquire). The bound check and the
            // indexer then operate on the same holder, so a concurrent single-writer append or
            // Trim can never make this read observe a Count that disagrees with the segments it
            // indexes (no out-of-range, no torn/null slot within [0, Count)).
            var snap = _snapshot;

            if (id < 0 || id >= snap.Count)
            {
                result = null;
                return false;
            }

            result = snap.Segments[id >> SegmentShift][id & SegmentMask];
            return result != null && !result._removed;
        }

        public override bool TryGetEdge(out EdgeModel result, int id)
        {
            var snap = _snapshot;

            if (id < 0 || id >= snap.Count)
            {
                result = null;
                return false;
            }

            result = snap.Segments[id >> SegmentShift][id & SegmentMask] as EdgeModel;
            return result != null && !result._removed;
        }

        public override bool TryGetVertex(out VertexModel result, int id)
        {
            var snap = _snapshot;

            if (id < 0 || id >= snap.Count)
            {
                result = null;
                return false;
            }

            result = snap.Segments[id >> SegmentShift][id & SegmentMask] as VertexModel;
            return result != null && !result._removed;
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

        public override bool IndexScan(out ImmutableList<AGraphElementModel> result, string indexId, IComparable literal, BinaryOperator binOp = BinaryOperator.Equals)
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

            #region binary operation

            switch (binOp)
            {
                case BinaryOperator.Equals:
                    if (!index.TryGetValue(out result, literal))
                    {
                        result = null;
                        return false;
                    }
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

        public override bool RangeIndexScan(out ImmutableList<AGraphElementModel> result, string indexId, IComparable leftLimit, IComparable rightLimit, bool includeLeft = true, bool includeRight = true)
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
                return rangeIndex.Between(out result, leftLimit, rightLimit, includeLeft, includeRight);
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

        internal string Save(string path, int savePartitions = 5)
        {
            var actualPath = _persistencyFactory.Save(this, path, savePartitions);

            // WAL compose (spec P4/§5): the snapshot is now DURABLY committed (the factory writes it
            // via temp + fsync + atomic rename). Only now reset the log to build upon this snapshot -
            // recording the current id-space high-water mark as the new baseline and pairing it with
            // the snapshot's actual path, discarding the pre-snapshot entries it superseded. Doing the
            // reset strictly AFTER the snapshot is durable is what makes "snapshot-then-truncate"
            // crash-safe: a crash in between leaves the log still paired with the PREVIOUS snapshot, so
            // loading the NEW snapshot will not replay it (no double-apply) while the new snapshot
            // already contains everything committed up to this save.
            _wal?.ResetToSnapshot(actualPath, _currentId);

            return actualPath;
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

            IShortestPathAlgorithm algo = null;

            Object cachedAlgo;
            if (!_pluginCache.ShortestPath.TryGetValue(plugin, out cachedAlgo))
            {
                //Shortest path plugin was not cached
                if (PluginFactory.TryFindPlugin(out algo, plugin))
                {
                    algo.Initialize(this, null);
                    _pluginCache.AddShortestPath(algo);
                }
            }
            else
            {
                algo = (IShortestPathAlgorithm)cachedAlgo;
            }

            if (algo != null)
            {
                return algo.TryCalculateShortestPath(out result, definition);
            }

            result = new List<Path>();
            return false;
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

        internal EdgeModel CreateEdge_internal(Int32 sourceVertexId, String edgePropertyId, Int32 targetVertexId,
            UInt32 creationDate, String label, Dictionary<String, Object> properties)
        {
            EdgeModel outgoingEdge = null;

            var sourceVertex = GetGraphElementForMutation(sourceVertexId) as VertexModel;
            var targetVertex = GetGraphElementForMutation(targetVertexId) as VertexModel;

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
            }

            return outgoingEdge;
        }

        internal List<EdgeModel> CreateEdges_internal(List<EdgeDefinition> definitions)
        {
            var newEdges = new List<EdgeModel>();

            if (definitions != null && definitions.Count > 0)
            {
                foreach (var aEdgeDefinition in definitions)
                {
                    var sourceVertex = GetGraphElementForMutation(aEdgeDefinition.SourceVertexId) as VertexModel;
                    var targetVertex = GetGraphElementForMutation(aEdgeDefinition.TargetVertexId) as VertexModel;

                    //get the related vertices
                    if (sourceVertex != null && targetVertex != null)
                    {
                        //intern the label, edge-property-id and property keys (finding M2)
                        var edgePropertyId = Intern(aEdgeDefinition.EdgePropertyId);
                        var newEdge = new EdgeModel(_currentId, aEdgeDefinition.CreationDate, targetVertex, sourceVertex,
                            Intern(aEdgeDefinition.Label), edgePropertyId, InternPropertyKeys(aEdgeDefinition.Properties));

                        newEdges.Add(newEdge);

                        //increment the ids (single-writer field, plain increment - finding P10)
                        _currentId++;

                        //add the edge to the source vertex
                        sourceVertex.AddOutEdge(edgePropertyId, newEdge);

                        //link the vertices
                        targetVertex.AddIncomingEdge(edgePropertyId, newEdge);

                        //increase the edgeCount
                        EdgeCount++;
                    }
                }

                //add the edges to the graph elements in batch
                if (newEdges.Count > 0)
                {
                    AppendGraphElements(newEdges);
                }
            }

            return newEdges;
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

        internal void RemoveProperty_internal(Int32 graphElementId, String propertyId)
        {
            var graphElement = GetGraphElementForMutation(graphElementId);

            if (graphElement != null)
            {
                graphElement.RemoveProperty(propertyId);
            }
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

            return true;
        }

        internal void TabulaRasa_internal()
        {
            _currentId = 0;
            _snapshot = EmptySnapshot;
            IndexFactory.DeleteAllIndices();
            VertexCount = 0;
            EdgeCount = 0;
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
        internal void LogCommittedTransaction(ATransaction tx)
        {
            var wal = _wal;
            if (wal == null || _walSuspended)
            {
                return;
            }

            if (!WalTransactionCodec.TryGetEntryType(tx, out var type))
            {
                return;
            }

            wal.Append(WalTransactionCodec.SerializeEntry(type, tx));
        }

        /// <summary>
        ///   Appends a payload-less lifecycle marker (used for an automatic Trim, which is not a
        ///   transaction). A no-op when the WAL is disabled or while replaying.
        /// </summary>
        private void LogWriteAheadLogMarker(Persistency.WalEntryType type)
        {
            var wal = _wal;
            if (wal == null || _walSuspended)
            {
                return;
            }

            wal.Append(WalTransactionCodec.SerializeEntry(type, null));
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
        private void ReplayWriteAheadLog()
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
                        if (!tx.TryExecute(this))
                        {
                            _logger.LogWarning("Re-executing a logged {Type} transaction during recovery returned false.", type);
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

                    replayed++;
                }

                _logger.LogInformation("Recovered {Count} transaction(s) from the write-ahead log.", replayed);
            }
            finally
            {
                _walSuspended = false;
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
                        ReplayWriteAheadLog();
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
                }
            }
            else
            {
                _snapshot = oldSnapshot;
                IndexFactory = oldIndexFactory;
                ServiceFactory = oldServiceFactory;
                SubGraphFactory = oldSubGraphFactory;
                ServiceFactory.StartAllServices();
            }

            // WAL-disabled (and failed-load) path: compact as before - behaviour is unchanged. The
            // WAL path above deliberately skips this so the loaded id space matches the on-disk
            // snapshot for replay consistency.
            if (!walHandledIdSpace)
            {
                Trim_internal();
            }
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
            return LiveElements(_snapshot)
                .Where(_ => _ != null && !_._removed)
                .Where(CheckLabel(interestingLabel))
                .Where(_ => seeker(_))
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
        private static ImmutableList<AGraphElementModel> FindElementsIndex(BinaryOperatorDelegate finder,
                                                                           IComparable literal, IIndex index)
        {
            return ImmutableList.CreateRange<AGraphElementModel>(index.GetKeyValues()
                                                             .AsParallel()
                                                             .Select(aIndexElement => new KeyValuePair<IComparable, ImmutableList<AGraphElementModel>>((IComparable)aIndexElement.Key, aIndexElement.Value))
                                                             .Where(aTypedIndexElement => finder(aTypedIndexElement.Key, literal))
                                                             .Select(_ => _.Value)
                                                             .SelectMany(__ => __)
                                                             .Distinct());
        }

        /// <summary>
        ///   Method for binary comparism
        /// </summary>
        /// <returns> <c>true</c> for equality; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryEqualsMethod(IComparable property, IComparable literal)
        {
            return property.Equals(literal);
        }

        /// <summary>
        ///   Method for binary comparism
        /// </summary>
        /// <returns> <c>true</c> for inequality; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryNotEqualsMethod(IComparable property, IComparable literal)
        {
            return !property.Equals(literal);
        }

        /// <summary>
        ///   Method for binary comparism
        /// </summary>
        /// <returns> <c>true</c> for greater property; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryGreaterMethod(IComparable property, IComparable literal)
        {
            return property.CompareTo(literal) > 0;
        }

        /// <summary>
        ///   Method for binary comparism
        /// </summary>
        /// <returns> <c>true</c> for lower property; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryLowerMethod(IComparable property, IComparable literal)
        {
            return property.CompareTo(literal) < 0;
        }

        /// <summary>
        ///   Method for binary comparism
        /// </summary>
        /// <returns> <c>true</c> for lower or equal property; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryLowerOrEqualMethod(IComparable property, IComparable literal)
        {
            return property.CompareTo(literal) <= 0;
        }

        /// <summary>
        ///   Method for binary comparism
        /// </summary>
        /// <returns> <c>true</c> for greater or equal property; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryGreaterOrEqualMethod(IComparable property, IComparable literal)
        {
            return property.CompareTo(literal) >= 0;
        }

        /// <summary>
        ///   Tombstone count (soft-deleted or load-gap slots) at or above which a committed
        ///   removal triggers an automatic compaction (finding M4). Removal is a soft delete: a
        ///   removed element keeps its full body (properties + adjacency) until a <c>Trim</c>
        ///   rebuilds the store without it. Under sustained add/remove churn those tombstones would
        ///   otherwise grow unboundedly. Auto-trim reuses the existing <see cref="Trim_internal" />
        ///   (which is rollback-agnostic and safe for lock-free readers), so it never frees a field
        ///   on the removal path itself. The default is deliberately conservative so ordinary
        ///   workloads and the test suite never trigger it (and it never surprises callers with an
        ///   id reassignment); it is a field rather than a const so a soak test can lower it.
        /// </summary>
        internal int _autoTrimTombstoneThreshold = 100_000;

        /// <summary>
        ///   Considers an automatic compaction after a committed element removal (finding M4).
        ///   Runs ONLY on the single transaction-worker thread, after the removal has committed
        ///   (never inside <c>TryExecute</c>, so a rolled-back removal - which restores adjacency
        ///   and counts - is never affected). The live counts are maintained, so the tombstone
        ///   estimate is O(1). When the store is compacted, removed elements are dropped entirely,
        ///   reclaiming their properties and adjacency and bounding memory under churn.
        /// </summary>
        internal void MaybeAutoTrim()
        {
            var snap = _snapshot;
            // Live elements are counted in VertexCount + EdgeCount; the remainder of the published
            // slots are tombstones (removed) or load gaps. Both are what a Trim would reclaim.
            int tombstones = snap.Count - VertexCount - EdgeCount;

            if (tombstones >= _autoTrimTombstoneThreshold)
            {
                _logger.LogInformation("Auto-trim: {Tombstones} reclaimable slots >= threshold {Threshold}; compacting the master store.",
                    tombstones, _autoTrimTombstoneThreshold);
                Trim_internal();

                // An auto-trim reassigns ids just as an explicit Trim transaction does; log a Trim
                // marker so a subsequent recovery replays it at the same point in commit order and
                // reproduces the exact id reassignment (keeping later logged ids resolvable).
                LogWriteAheadLogMarker(Persistency.WalEntryType.Trim);
            }
        }

        /// <summary>
        ///   Trims the Fallen-8 by removing null entries and compacting IDs.
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

        public override ImmutableList<VertexModel> GetAllVertices(String interestingLabel = null)
        {
            // Sequential, id-ordered scan (finding P7): the predicate is a light null/removed/type/
            // label check, so PLINQ overhead is not worth paying. The old parallel scan was unordered;
            // callers never relied on a particular order.
            return ImmutableList.CreateRange<VertexModel>(
                 LiveElementsSequential(_snapshot)
                 .Where(_ => _ != null && !_._removed && _ is VertexModel)
                 .Where(CheckLabel(interestingLabel))
                 .Select(_ => (VertexModel)_));
        }

        public override ImmutableList<EdgeModel> GetAllEdges(String interestingLabel = null)
        {
            return ImmutableList.CreateRange<EdgeModel>(
                        LiveElementsSequential(_snapshot)
                        .Where(_ => _ != null && !_._removed && _ is EdgeModel)
                        .Where(CheckLabel(interestingLabel))
                        .Select(_ => (EdgeModel)_));
        }

        public override ImmutableList<AGraphElementModel> GetAllGraphElements(String interestingLabel = null)
        {
            return ImmutableList.CreateRange<AGraphElementModel>(
                        LiveElementsSequential(_snapshot)
                        .Where(_ => _ != null && !_._removed)
                        .Where(CheckLabel(interestingLabel)));
        }

        #endregion
    }
}
