// MIT License
//
// IndexBatchResultREST.cs
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
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The result of a <c>PUT /index/{indexId}/batch</c> (feature cheap-withdrawal): how many
    ///   entries the index took, and WHICH of the submitted ones it refused.
    ///
    ///   <para>A batch is NOT atomic, deliberately. The single-entry route reports a miss as
    ///   <c>200</c> with a <c>false</c> body rather than a <c>404</c>, because a missing index or a
    ///   missing graph element is an answer and not a transport failure; this route keeps that
    ///   reading and simply reports it per entry. Refusing the whole batch because one element had
    ///   already been deleted would be worse for the only caller that needs this route, which is
    ///   indexing a just-written claim and wants the other several hundred entries to land.</para>
    ///
    ///   <para><b>Why declined entries are reported as POSITIONS.</b> The obvious alternative,
    ///   echoing back the refused entries or their graph element ids, does not identify them: one
    ///   element may legitimately appear several times in one batch under different keys, so an id
    ///   is not unique per entry. The zero-based position in the submitted array is, and it is also
    ///   the smallest thing that can be returned. The server never reorders, so position is stable.</para>
    /// </summary>
    /// <example>
    /// {
    ///   "accepted": 498,
    ///   "declined": [12, 341]
    /// }
    /// </example>
    public sealed class IndexBatchResultREST
    {
        /// <summary>
        ///   How many of the submitted entries the index took. <c>accepted</c> plus the length of
        ///   <c>declined</c> always equals the number of entries submitted.
        /// </summary>
        /// <example>498</example>
        [JsonPropertyName("accepted")]
        public Int32 Accepted
        {
            get; set;
        }

        /// <summary>
        ///   The ZERO-BASED positions, in the submitted array, of the entries the index refused.
        ///   Empty when every entry was taken. An entry is refused when the index does not exist or
        ///   the graph element does not exist; when the index itself is missing, every position is
        ///   listed rather than the request failing.
        /// </summary>
        [JsonPropertyName("declined")]
        public List<Int32> Declined
        {
            get; set;
        } = new List<Int32>();
    }
}
