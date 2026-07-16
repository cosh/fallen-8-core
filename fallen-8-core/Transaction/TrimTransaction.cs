// MIT License
//
// TrimTransaction.cs
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

namespace NoSQL.GraphDB.Core.Transaction
{
    /// <summary>
    ///   Compacts the master store, dropping tombstone/empty slots and REASSIGNING every surviving
    ///   element's <c>Id</c> to its new dense index. This is the ONLY id-renumbering path (automatic
    ///   reclamation does not renumber - feature trim-reader-safety): element ids are otherwise stable
    ///   handles. Because it renumbers, a caller or REST client holding element ids across an explicit
    ///   Trim is remapped to different elements, so schedule it knowingly and not concurrently with
    ///   readers/clients that hold ids. In-flight readers on the previous snapshot keep a consistent
    ///   old-id-space view; only ids captured before and resolved after the Trim are affected.
    /// </summary>
    public class TrimTransaction : ATransaction
    {
        internal override void Cleanup()
        {
            //NOP
        }

        internal override void Rollback(Fallen8 f8)
        {
            //NOP
        }

        internal override Boolean TryExecute(Fallen8 f8)
        {
            f8.Trim_internal();
            return true;
        }

        internal override void DescribeChanges(Fallen8 f8, ChangeFeed.ChangeDescriptor.Builder builder)
        {
            // Ids were reassigned in place: every client-held id is invalid, which no element
            // delta can express.
            builder.Resync(ChangeFeed.ChangeFeedDispatcher.ResyncReasonTrim);
        }
    }
}
