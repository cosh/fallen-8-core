// MIT License
//
// GraphElementBatchREST.cs
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
using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   One element in a <c>POST /graphelements/get</c> result (feature platform-integrity-audit W6):
    ///   its identity, label, stamps and properties - and deliberately NOT its adjacency.
    ///
    ///   <para>It derives from <see cref="AGraphElement" /> so the property projection goes through the
    ///   one egress home, which is what makes every value here round-trip back through a write
    ///   unchanged. Adjacency is omitted on purpose: this route exists so a caller can compare the
    ///   values it intends to write against the values already stored, and shipping every edge id of
    ///   every element in a several-hundred-element batch would dominate the payload while answering a
    ///   question nobody asked. A caller that needs adjacency asks for one element through
    ///   <c>GET /vertex/{id}</c>, which already returns it.</para>
    /// </summary>
    public sealed class GraphElementProjectionREST : AGraphElement
    {
        /// <summary>
        ///   <c>vertex</c> or <c>edge</c>. The singular getters are addressed per kind, so a batch read
        ///   that accepts mixed ids has to say which each one turned out to be.
        /// </summary>
        /// <example>vertex</example>
        [JsonPropertyName("kind")]
        public String Kind
        {
            get; set;
        }

        public GraphElementProjectionREST(AGraphElementModel model, String kind)
            : base(model.Id, model.CreationDate, model.ModificationDate, model.Label, model.GetAllProperties())
        {
            Kind = kind;
        }

        /// <summary>
        ///   The same shape without needing a live engine element - the base's primitives plus the kind.
        ///   Exists so the projection can be constructed outside a request (the JSON parity gate builds
        ///   one representative of every registered DTO).
        /// </summary>
        public GraphElementProjectionREST(Int32 id, UInt32 creationDate, UInt32 modificationDate,
            String label, ImmutableDictionary<String, Object> properties, String kind)
            : base(id, creationDate, modificationDate, label, properties)
        {
            Kind = kind;
        }
    }

    /// <summary>
    ///   Result of <c>POST /graphelements/get</c> (feature platform-integrity-audit W6): the elements
    ///   that exist, plus the ids that do not.
    ///
    ///   <para><b>Why the route exists.</b> Every scan and every batch write returns IDS ONLY, and the
    ///   only many-element reads were a whole-namespace dump and a whole-namespace export. So a caller
    ///   that had resolved several hundred ids and needed their current property values - which is what
    ///   "write only if something actually changed" requires - had one sequential request per element,
    ///   or a full graph dump per poll. That is the read-side mirror of the batch write path.</para>
    ///
    ///   <para><see cref="NotFound" /> is explicit rather than left to the caller to infer from a
    ///   missing entry, because "this id is gone" and "this id has no properties" are different
    ///   conclusions and a reconciling caller acts differently on each.</para>
    /// </summary>
    public sealed class GraphElementBatchREST
    {
        /// <summary>The elements that exist, in the order the requested ids were given (duplicates in
        /// the request collapse to one entry).</summary>
        [JsonPropertyName("elements")]
        public List<GraphElementProjectionREST> Elements
        {
            get; set;
        } = new List<GraphElementProjectionREST>();

        /// <summary>The requested ids that resolve to no live element - removed, never created, or out
        /// of range. Stated rather than implied.</summary>
        [JsonPropertyName("notFound")]
        public List<Int32> NotFound
        {
            get; set;
        } = new List<Int32>();
    }
}
