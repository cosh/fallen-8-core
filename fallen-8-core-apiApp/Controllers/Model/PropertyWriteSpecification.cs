// MIT License
//
// PropertyWriteSpecification.cs
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
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   One write in a <c>PUT /graphelements/properties</c> batch (feature
    ///   platform-integrity-audit W2): set <see cref="PropertySpecification.PropertyId" /> on
    ///   <see cref="GraphElementId" /> to the given value, or REMOVE it when <see cref="Remove" />
    ///   is <c>true</c>.
    ///
    ///   <para>Writes have REPLACE semantics, so a key that already exists is overwritten. Setting a
    ///   key to the value it already holds, or removing one that is already absent, is a TRUE no-op:
    ///   it bumps no modification date and publishes no change-feed event. That is what lets a caller
    ///   re-assert the state an external source describes without producing mutations.</para>
    /// </summary>
    /// <example>
    /// {
    ///   "graphElementId": 42,
    ///   "propertyId": "ip",
    ///   "fullQualifiedTypeName": "System.String",
    ///   "propertyValue": "10.0.0.9"
    /// }
    /// </example>
    public sealed class PropertyWriteSpecification : PropertySpecification
    {
        /// <summary>
        ///   The element to write to. An in-range but absent id is a committed no-op (matching the
        ///   single-element routes); an out-of-range id rolls the WHOLE batch back.
        /// </summary>
        /// <example>42</example>
        [Required]
        [DefaultValue(0)]
        [JsonPropertyName("graphElementId")]
        public Int32 GraphElementId
        {
            get; set;
        }

        /// <summary>
        ///   When <c>true</c> the property is REMOVED and the value fields are ignored. Removing an
        ///   absent property succeeds and changes nothing, which makes a replayed batch safe.
        /// </summary>
        /// <example>false</example>
        [DefaultValue(false)]
        [JsonPropertyName("remove")]
        public Boolean Remove
        {
            get; set;
        }
    }
}
