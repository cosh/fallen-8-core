// MIT License
//
// Fallen8.Trim.cs
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
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.Core
{
    public sealed partial class Fallen8
    {
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
    }
}
