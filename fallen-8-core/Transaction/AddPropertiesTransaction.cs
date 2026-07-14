// MIT License
//
// AddPropertiesTransaction.cs
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
    public class AddPropertiesTransaction : ATransaction
    {
        public List<PropertyAddDefinition> Properties
        {
            get; set;
        } = new List<PropertyAddDefinition>();

        // Inverse of each set applied by TryExecute, in apply order (feature transaction-atomicity),
        // so Rollback restores the pre-transaction state if a residual step throws after validation.
        private List<PropertyMutationUndo> _undo = new List<PropertyMutationUndo>();

        internal override void Rollback(Fallen8 f8)
        {
            // Undoes the applied sets in reverse. After the validate-then-apply pass this is only
            // needed for a residual post-validation throw (e.g. OOM); a clean pre-validation false
            // (InvalidInput/Conflict) leaves _undo empty, so this is a no-op then.
            f8.RestoreProperties_internal(_undo);
        }

        internal override Boolean TryExecute(Fallen8 f8)
        {
            // Validate the whole batch (structure + conflict-freedom, incl. intra-batch) before
            // applying anything; then apply, recording each inverse for a residual-throw rollback.
            if (!f8.SetProperties_internal(Properties, _undo, out var reason))
            {
                FailureReason = reason;
                return false;
            }

            return true;
        }

        public AddPropertiesTransaction AddProperty(Int32 graphElementId, String propertyId, Object property)
        {
            Properties.Add(new PropertyAddDefinition() { GraphElementId = graphElementId, PropertyId = propertyId, Property = property });

            return this;
        }

        public AddPropertiesTransaction AddEdge(PropertyAddDefinition definition)
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
        }
    }
}
