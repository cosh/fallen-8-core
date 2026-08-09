// MIT License
//
// SetPropertiesTransaction.cs
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
using System.Collections.Generic;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.Core.Transaction
{
    /// <summary>
    ///   Sets and/or removes element properties as one atomic batch, with REPLACE semantics
    ///   (feature platform-integrity-audit W2). This is the UPDATE path, and before it existed there
    ///   was none: <see cref="AddPropertyTransaction" /> / <see cref="AddPropertiesTransaction" />
    ///   are "insert, or verify the existing value is equal" and reject a CHANGE with
    ///   <see cref="TransactionFailureReason.Conflict" />, so the only way to change a value was a
    ///   remove transaction followed by a set transaction - two transactions, non-atomic, with a
    ///   window in which the property reads as absent.
    ///
    ///   <para>Modelled on <see cref="SetEmbeddingsTransaction" />, which already established this
    ///   shape for the reserved embedding keys: validate the whole batch, then apply through the
    ///   "set to exactly this state" primitive, recording each inverse for a residual-throw
    ///   rollback. The add transactions are deliberately NOT changed - "insert or verify" is a
    ///   useful primitive in its own right, and transaction-atomicity's requirement that a conflict
    ///   be cleanly classified rather than thrown mid-batch continues to hold for them.</para>
    ///
    ///   <para><b>A semantically empty write is a true no-op.</b> Setting a property to the value it
    ///   already holds, or removing one that is already absent, records no undo entry, publishes no
    ///   change-feed event and does not touch <c>ModificationDate</c>. That is what makes "re-asserting
    ///   the state an external source describes changes nothing" observable rather than merely true of
    ///   the stored values, and it makes a replayed batch idempotent.</para>
    /// </summary>
    public class SetPropertiesTransaction : ATransaction
    {
        public List<PropertySetDefinition> Properties
        {
            get; set;
        } = new List<PropertySetDefinition>();

        // Inverse of each APPLIED write, in apply order (the transaction-atomicity pattern),
        // replayed in reverse on rollback. A no-op write contributes no entry.
        private List<PropertyMutationUndo> _undo = new List<PropertyMutationUndo>();

        // Parallel to _undo, same order: whether that applied write was a REMOVAL, so the change
        // feed can report propertyRemoved rather than propertySet. Kept as a sibling list rather
        // than a field on PropertyMutationUndo, which is shared with three other transactions and
        // has no use for it.
        private List<Boolean> _appliedRemovals = new List<Boolean>();

        internal override Boolean TryExecute(Fallen8 f8)
        {
            if (!f8.SetProperties_replace_internal(Properties, _undo, _appliedRemovals, out var reason))
            {
                FailureReason = reason;
                return false;
            }

            return true;
        }

        internal override void Rollback(Fallen8 f8)
        {
            // Undoes the applied writes in reverse. After the validate-then-apply pass this is only
            // needed for a residual post-validation throw; a clean pre-validation false leaves _undo
            // empty, so this is a no-op then.
            f8.RestoreProperties_internal(_undo);
        }

        internal override void DescribeChanges(Fallen8 f8, ChangeFeed.ChangeDescriptor.Builder builder)
        {
            // One entry per APPLIED write, in apply order; only id/key primitives are read, never
            // the value. A no-op write is absent from _undo, so it is absent from the feed too.
            if (_undo == null || _appliedRemovals == null)
            {
                return;
            }

            for (var i = 0; i < _undo.Count && i < _appliedRemovals.Count; i++)
            {
                var applied = _undo[i];
                if (!f8.TryDescribeElement(applied.GraphElementId, out var elementType, out var label))
                {
                    continue;
                }

                if (_appliedRemovals[i])
                {
                    builder.PropertyRemoved(elementType, applied.GraphElementId, label, applied.PropertyId);
                }
                else
                {
                    builder.PropertySet(elementType, applied.GraphElementId, label, applied.PropertyId);
                }
            }
        }

        /// <summary>Fluent: set <paramref name="propertyId" /> to exactly <paramref name="property" />.</summary>
        public SetPropertiesTransaction SetProperty(Int32 graphElementId, String propertyId, Object property)
        {
            Properties.Add(new PropertySetDefinition
            {
                GraphElementId = graphElementId,
                PropertyId = propertyId,
                Property = property,
                Remove = false
            });

            return this;
        }

        /// <summary>Fluent: remove <paramref name="propertyId" />. Removing an absent property is a no-op.</summary>
        public SetPropertiesTransaction RemoveProperty(Int32 graphElementId, String propertyId)
        {
            Properties.Add(new PropertySetDefinition
            {
                GraphElementId = graphElementId,
                PropertyId = propertyId,
                Remove = true
            });

            return this;
        }

        /// <summary>Fluent overload taking a pre-built definition, mirroring the sibling batch transactions.</summary>
        public SetPropertiesTransaction Set(PropertySetDefinition definition)
        {
            Properties.Add(definition);

            return this;
        }

        internal override void ReleaseAfterCompletion()
        {
            // Drop the input definitions (and the boxed property values they carry) once the
            // transaction completes (M3).
            Properties = null;
        }

        internal override void Cleanup()
        {
            // Null-safe: ReleaseAfterCompletion() may already have dropped Properties.
            Properties = null;
            _undo = null;
            _appliedRemovals = null;
        }
    }
}
