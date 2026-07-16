// MIT License
//
// RemoveGraphElementTransaction.cs
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
    public class RemoveGraphElementTransaction : ATransaction
    {
        public Int32 GraphElementId
        {
            get;
            set;
        }

        /// <summary>Whether the element was actually removed by THIS transaction (drives the
        /// change feed; removing a missing/already-removed element commits but reports nothing).</summary>
        private Boolean _didRemove;

        internal override Boolean TriggersAutoTrim
        {
            get { return true; }
        }

        internal override void Rollback(Fallen8 f8)
        {
            //rollback is implemented in the TryRemoveGraphElement_private method
        }

        internal override Boolean TryExecute(Fallen8 f8)
        {
            _didRemove = f8.TryRemoveGraphElement_private(GraphElementId);

            return true;
        }

        internal override void DescribeChanges(Fallen8 f8, ChangeFeed.ChangeDescriptor.Builder builder)
        {
            if (_didRemove)
            {
                f8.DescribeRemovedElement(GraphElementId, builder);
            }
        }

        internal override void Cleanup()
        {
            //NOOP
        }
    }
}
