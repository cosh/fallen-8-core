// MIT License
//
// AddPropertyTransaction.cs
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
    public class AddPropertyTransaction : ATransaction
    {
        public PropertyAddDefinition Definition
        {
            get;
            set;
        }

        // The inverse of the applied set, recorded only after it succeeds (feature
        // transaction-atomicity), so Rollback restores the pre-transaction state.
        private List<PropertyMutationUndo> _undo = new List<PropertyMutationUndo>();

        internal override void ReleaseAfterCompletion()
        {
            // Drop the input definition (and the boxed property value it carries) once the
            // transaction completes (M3).
            Definition = null;
        }

        internal override void Cleanup()
        {
            Definition = null;
            _undo = null;
        }

        internal override void Rollback(Fallen8 f8)
        {
            // A single set throws before mutating on a value conflict, so a rolled-back set usually
            // has nothing to undo (_undo stays empty). This restores the prior value/absence for the
            // residual case where a step after the successful set throws, keeping the
            // "RolledBack => no observable effect" invariant uniform.
            f8.RestoreProperties_internal(_undo);
        }

        internal override Boolean TryExecute(Fallen8 f8)
        {
            f8.SetPropertyWithUndo_internal(Definition.GraphElementId, Definition.PropertyId, Definition.Property, _undo);
            return true;
        }

        internal override void DescribeChanges(Fallen8 f8, ChangeFeed.ChangeDescriptor.Builder builder)
        {
            // One undo entry exists per APPLIED set (a no-op target records none), so the feed
            // reports exactly what changed. Only the id/key primitives are read - never the value.
            if (_undo == null)
            {
                return;
            }

            foreach (var applied in _undo)
            {
                if (f8.TryDescribeElement(applied.GraphElementId, out var elementType, out var label))
                {
                    builder.PropertySet(elementType, applied.GraphElementId, label, applied.PropertyId);
                }
            }
        }
    }
}
