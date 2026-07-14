// MIT License
//
// PropertyMutationUndo.cs
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

namespace NoSQL.GraphDB.Core.Transaction
{
    /// <summary>
    ///   Records what a single applied property set changed on one element, so a rolled-back batch
    ///   (feature transaction-atomicity) can restore the element to its pre-transaction state if a
    ///   later step throws. Captured on the writer thread as each set is applied, in apply order;
    ///   the rollback replays them in reverse.
    /// </summary>
    internal readonly struct PropertyMutationUndo
    {
        internal readonly Int32 GraphElementId;
        internal readonly String PropertyId;

        /// <summary>Whether the element carried this key BEFORE the batch applied it.</summary>
        internal readonly Boolean HadValueBefore;

        /// <summary>The element's value for this key before the batch (only meaningful when <see cref="HadValueBefore"/>).</summary>
        internal readonly Object PriorValue;

        internal PropertyMutationUndo(Int32 graphElementId, String propertyId, Boolean hadValueBefore, Object priorValue)
        {
            GraphElementId = graphElementId;
            PropertyId = propertyId;
            HadValueBefore = hadValueBefore;
            PriorValue = priorValue;
        }
    }
}
