// MIT License
//
// TransactionFailureReason.cs
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

namespace NoSQL.GraphDB.Core.Transaction
{
    /// <summary>
    ///   The structured category of a rolled-back transaction, letting a caller that waited on the
    ///   outcome map a clean rollback to the right response (rather than collapsing every failure
    ///   into an opaque "rolled back"). It is set in place on the caller's
    ///   <see cref="TransactionInformation"/> before the transaction task completes, under the same
    ///   happens-before edge as <see cref="TransactionInformation.Error"/> and
    ///   <see cref="TransactionState"/>.
    /// </summary>
    /// <remarks>
    ///   <see cref="None"/> is the default and the value on a committed transaction. An exception
    ///   that escapes a transaction's execution implies <see cref="InternalError"/>. The remaining
    ///   values are recorded by an engine operation / transaction when it rolls back cleanly for a
    ///   known, client-attributable reason.
    /// </remarks>
    public enum TransactionFailureReason
    {
        /// <summary>No failure reason recorded (the default; also the value on a committed transaction).</summary>
        None,

        /// <summary>An unexpected internal fault (e.g. an exception that escaped execution).</summary>
        InternalError,

        /// <summary>The request was structurally invalid (e.g. a malformed subgraph pattern).</summary>
        InvalidInput,

        /// <summary>A referenced element did not exist (e.g. an edge endpoint vertex is missing/removed).</summary>
        NotFound,

        /// <summary>A resource quota (count or materialized-element ceiling) was exceeded.</summary>
        QuotaExceeded,

        /// <summary>The request conflicted with the current state (e.g. a name already in use).</summary>
        Conflict
    }
}
