// MIT License
//
// PropertySetDefinition.cs
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

namespace NoSQL.GraphDB.Core.Model
{
    /// <summary>
    ///   One write in a <see cref="Transaction.SetPropertiesTransaction" /> batch: set a property to
    ///   exactly <see cref="Property" />, or REMOVE it when <see cref="Remove" /> is <c>true</c>
    ///   (feature platform-integrity-audit W2).
    ///
    ///   <para>Distinct from <see cref="PropertyAddDefinition" /> in two ways, both deliberate.
    ///   (1) SEMANTICS: an add is "insert, or verify the existing value is equal" and rejects a
    ///   CHANGE with <see cref="Transaction.TransactionFailureReason.Conflict" />; a set REPLACES,
    ///   so last write wins, intra-batch included. (2) SHAPE: a set can also be a removal, so one
    ///   batch expresses "these properties now hold these values and these others are gone" in one
    ///   atomic transaction. Both matter for a caller reconciling an element against an external
    ///   source: without replace there is no update path at all, and without set-or-remove a
    ///   partially applied reconciliation leaves the element in a state no source describes.</para>
    ///
    ///   <para><see cref="Remove" /> is an explicit flag rather than "a null value means remove",
    ///   mirroring <see cref="AGraphElementModel.RestoreProperty" />'s explicit has-value parameter,
    ///   so a caller can still store a null value if it means to.</para>
    /// </summary>
    public class PropertySetDefinition
    {
        /// <summary>The element to write to. An empty or out-of-range slot behaves exactly as it does
        /// on the add path (a no-op target, respectively the historical throw).</summary>
        public Int32 GraphElementId
        {
            get;
            set;
        }

        /// <summary>The property key.</summary>
        public String PropertyId
        {
            get;
            set;
        }

        /// <summary>The value to set. Ignored when <see cref="Remove" /> is <c>true</c>.</summary>
        public Object Property
        {
            get;
            set;
        }

        /// <summary>When <c>true</c> the property is removed instead of set. Removing an absent
        /// property is a no-op (no undo entry, no change event), which is what makes a withdrawal
        /// replay-safe.</summary>
        public Boolean Remove
        {
            get;
            set;
        }
    }
}
