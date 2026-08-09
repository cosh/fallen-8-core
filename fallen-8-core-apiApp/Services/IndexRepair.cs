// MIT License
//
// IndexRepair.cs
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
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Index;

namespace NoSQL.GraphDB.App.Services
{
    /// <summary>
    ///   The ONE home for repairing a derived index from element state (feature
    ///   platform-integrity-audit W4).
    ///
    ///   <para><b>Why this is needed.</b> Index content is derived state with no durability of its own:
    ///   index writes are neither single-writer transactions nor WAL-logged (index-lifecycle 3.5/3.6,
    ///   both deferred), so index state is snapshot-only. After a hard crash the elements replay from
    ///   the WAL but every index key added since the last checkpoint is gone. Worse, three ORDINARY
    ///   operations drop an index while a process is running: a tabula rasa, loading a save game, and a
    ///   per-index serialization failure that drops it from the checkpoint manifest (deliberately
    ///   non-fatal, so one bad index never costs the whole checkpoint).</para>
    ///
    ///   <para><b>Why it lives in the apiApp and not the engine.</b> "Which property backs which index"
    ///   is a caller concern by explicit engine decision - index-lifecycle's non-goal states that
    ///   "indexing which property stays an explicit caller action" - so putting the mapping into the
    ///   engine would import a schema concept the engine deliberately does not carry. Everything this
    ///   needs is already public engine surface (<see cref="Core.IFallen8Admin.IndexFactory" /> and
    ///   <see cref="Core.IFallen8Read.GetAllGraphElements" />), and the in-process caller it exists to
    ///   subsume (the document-ingestion service's entity-index sweep) is in this project too, so no
    ///   engine interface grows a member and no delegating wrapper or test fake has to change.</para>
    ///
    ///   <para><b>Why not a WAL ordinal for index writes.</b> That would be an irreversible on-disk
    ///   format commitment to make re-derivable state durable, bought before its own stated
    ///   prerequisite. Rejected with a revisit trigger in the audit; the bound vector index already
    ///   proves a crash-durable derived index needs no ordinal at all - it persists a header and
    ///   rebuilds from element state.</para>
    /// </summary>
    public static class IndexRepair
    {
        /// <summary>The outcome, as numbers rather than a bare boolean, so a caller can tell a no-op
        /// from real work and can spot having named the wrong property (scanned many, indexed none).</summary>
        public sealed class Result
        {
            public Int32 IndexedElements
            {
                get; internal set;
            }

            public Int32 ScannedElements
            {
                get; internal set;
            }

            /// <summary>Elements carrying the property whose value cannot be an index key (not
            /// comparable - e.g. a vector written through the raw property surface). Skipped, and
            /// counted so the skip is not actually silent.</summary>
            public Int32 SkippedUnindexableValues
            {
                get; internal set;
            }

            /// <summary>Whether the index was emptied first (exact rebuild) rather than repaired.</summary>
            public Boolean Replaced
            {
                get; internal set;
            }
        }

        /// <summary>
        ///   Repopulates <paramref name="indexId" /> from the live elements' <paramref name="propertyId" />
        ///   values, so the index says what element state says.
        ///
        ///   <para><paramref name="replace" /> picks between the two honest modes. <c>false</c> (default)
        ///   REPAIRS: add-only, and idempotent because <see cref="IIndex.AddOrUpdate" /> is idempotent
        ///   per (key, element) since W3 - safe to run on every start, and nothing is ever briefly
        ///   missing. It does not remove keys element state no longer justifies. <c>true</c> REBUILDS
        ///   exactly: the index is wiped first, so stale keys go, at the cost of a window in which a
        ///   concurrent scan sees an empty index. Repair is the default because an incomplete index and a
        ///   briefly empty one fail very differently.</para>
        ///
        ///   <para>Refused, with a reason rather than a silent no-op: an index that does not answer exact
        ///   point-equality lookups (a vector index ranks approximate neighbours; a spatial index is
        ///   keyed by geometry), and a BOUND vector index - the latter for the opposite reason, because
        ///   it already maintains itself from element state and a second membership authority could only
        ///   disagree with it.</para>
        ///
        ///   <para>Concurrency: runs on the CALLING thread and takes the index's own write lock per key,
        ///   exactly as every other index write does today. Removed elements are never indexed, because
        ///   the scan reads the live snapshot. A concurrent add of a pair this pass also adds is harmless,
        ///   the add being idempotent.</para>
        /// </summary>
        public static Boolean TryRepairFromProperty(IFallen8 fallen8, ILogger logger, String indexId,
            String propertyId, out Result result, out String error, Boolean replace = false,
            String interestingLabel = null)
        {
            result = null;
            error = null;

            if (fallen8 == null)
            {
                error = "A graph is required.";
                return false;
            }

            if (String.IsNullOrWhiteSpace(indexId))
            {
                error = "An index id is required.";
                return false;
            }

            if (String.IsNullOrEmpty(propertyId))
            {
                error = "A property id is required: its VALUE becomes the index key.";
                return false;
            }

            IIndex index;
            if (!fallen8.IndexFactory.TryGetIndex(out index, indexId))
            {
                error = String.Format("There is no index with id '{0}'.", indexId);
                return false;
            }

            if (!index.SupportsPointEqualityLookup)
            {
                error = String.Format(
                    "Index '{0}' ({1}) does not answer exact point-equality lookups, so an arbitrary " +
                    "property value cannot be a key in it.", indexId, index.PluginName);
                return false;
            }

            if (index is Core.Index.Vector.IVectorIndex bound && bound.EmbeddingName != null)
            {
                error = String.Format(
                    "Index '{0}' is BOUND to embedding '{1}': it already maintains itself from element " +
                    "state and rebuilds on load, so it must not be populated from a second source.",
                    indexId, bound.EmbeddingName);
                return false;
            }

            var outcome = new Result { Replaced = replace };

            if (replace)
            {
                // Exact rebuild: keys the elements no longer justify have to go, and Wipe is the only
                // key-set-clearing operation on the plugin contract (adding an IIndex member was
                // rejected in review - it is a public contract with hand-written implementers). The
                // empty window is real and is why this is not the default.
                index.Wipe();
            }

            var elements = fallen8.GetAllGraphElements(interestingLabel);
            outcome.ScannedElements = elements.Count;

            for (var i = 0; i < elements.Count; i++)
            {
                var element = elements[i];

                // TryGetProperty<Object> is the PUBLIC read (the raw accessor is engine-internal, and
                // this deliberately lives outside the engine). Asking for Object rather than IComparable
                // directly is what lets an unindexable value be COUNTED rather than silently skipped.
                if (!element.TryGetProperty<Object>(out var value, propertyId) || value == null)
                {
                    continue;
                }

                if (!(value is IComparable))
                {
                    outcome.SkippedUnindexableValues++;
                    continue;
                }

                index.AddOrUpdate(value, element);
                outcome.IndexedElements++;
            }

            if (outcome.SkippedUnindexableValues > 0)
            {
                logger?.LogWarning(
                    "Index repair of '{IndexId}' from property '{PropertyId}' skipped {Skipped} element(s) whose " +
                    "value cannot be an index key (not comparable).",
                    indexId, propertyId, outcome.SkippedUnindexableValues);
            }

            logger?.LogInformation(
                "Index '{IndexId}' repopulated from property '{PropertyId}': {Indexed} of {Scanned} live element(s) " +
                "indexed ({Mode}).",
                indexId, propertyId, outcome.IndexedElements, outcome.ScannedElements,
                replace ? "exact rebuild, index wiped first" : "repair, add-only");

            result = outcome;
            return true;
        }
    }
}
