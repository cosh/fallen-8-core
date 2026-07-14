// MIT License
//
// RemoveGraphElementsTransaction.cs
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

using NoSQL.GraphDB.Core.Model;
using System;
using System.Collections.Generic;

namespace NoSQL.GraphDB.Core.Transaction
{
    public class RemoveGraphElementsTransaction : ATransaction
    {
        public List<Int32> GraphElementIds
        {
            get;
            set;
        }

        // The ids this batch genuinely transitioned live -> removed, in removal order (feature
        // transaction-atomicity), so Rollback can restore them if a later removal throws.
        private List<Int32> _removedIds = new List<Int32>();

        internal override Boolean TriggersAutoTrim
        {
            get { return true; }
        }

        internal override void Rollback(Fallen8 f8)
        {
            // Restore the removals that had already been applied when a later one threw, so the whole
            // batch is all-or-nothing (a per-element self-restore only covers the single faulting
            // element - it cannot undo the earlier, already-committed removals of the same batch).
            f8.RestoreRemovedElements_private(_removedIds);
        }

        internal override Boolean TryExecute(Fallen8 f8)
        {
            // Range-check the whole batch before removing anything (an out-of-range id throws here,
            // atomically), then remove, tracking each genuine removal for Rollback.
            f8.RemoveGraphElements_internal(GraphElementIds, _removedIds, out var reason);
            if (reason != TransactionFailureReason.None)
            {
                FailureReason = reason;
                return false;
            }

            return true;
        }

        internal override void Cleanup()
        {
            _removedIds = null;
        }
    }
}
