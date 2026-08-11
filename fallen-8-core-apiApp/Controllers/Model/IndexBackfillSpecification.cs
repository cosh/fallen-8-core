// MIT License
//
// IndexBackfillSpecification.cs
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
    ///   Body of <c>POST /index/backfill/{indexId}</c> (feature platform-integrity-audit W4): which
    ///   property supplies the index keys, and whether to rebuild exactly or merely repair.
    /// </summary>
    /// <example>
    /// {
    ///   "propertyId": "name",
    ///   "replace": false
    /// }
    /// </example>
    public sealed class IndexBackfillSpecification
    {
        /// <summary>
        ///   The property key whose VALUE becomes the index key. Elements not carrying it are skipped.
        /// </summary>
        /// <example>name</example>
        [Required]
        [DefaultValue("name")]
        [JsonPropertyName("propertyId")]
        public String PropertyId
        {
            get; set;
        }

        /// <summary>
        ///   <c>false</c> (default) REPAIRS: add-only and idempotent, so it is safe on every start and
        ///   nothing is briefly missing, but keys the elements no longer justify are left alone.
        ///   <c>true</c> REBUILDS exactly: the index is wiped first, so stale keys go, at the cost of a
        ///   window in which a concurrent scan sees an empty index.
        /// </summary>
        /// <example>false</example>
        [DefaultValue(false)]
        [JsonPropertyName("replace")]
        public Boolean Replace
        {
            get; set;
        }

        /// <summary>
        ///   <c>false</c> (default) selects ONE exact property key. <c>true</c> means
        ///   <c>propertyId</c> is a KEY PREFIX and EVERY property whose key starts with it is indexed
        ///   by its value, so one element can contribute several entries. Prefix mode exists because a
        ///   set of values is spread across dense ordinal keys (<c>$identity:0</c>,
        ///   <c>$identity:1</c>, ...): the property surface accepts scalars and no array, so a set is
        ///   not expressible under one key, and an exact-key repair then restores only the first value
        ///   of each element - leaving it findable by one and invisible by the rest.
        /// </summary>
        /// <example>false</example>
        [DefaultValue(false)]
        [JsonPropertyName("prefix")]
        public Boolean Prefix
        {
            get; set;
        }

        /// <summary>
        ///   Optional label restriction, for when the property only occurs on one kind of element.
        ///   Omit to scan every live element.
        /// </summary>
        /// <example>person</example>
        [JsonPropertyName("label")]
        public String Label
        {
            get; set;
        }
    }

    /// <summary>
    ///   Outcome of an index backfill. Reports the numbers rather than a bare boolean so a caller can
    ///   tell a no-op from real work, and can spot having named the wrong property (scanned many,
    ///   indexed none).
    /// </summary>
    public sealed class IndexRebuildREST
    {
        /// <summary>The index that was repopulated.</summary>
        [JsonPropertyName("indexId")]
        public String IndexId
        {
            get; set;
        }

        /// <summary>The property whose values became the keys.</summary>
        [JsonPropertyName("propertyId")]
        public String PropertyId
        {
            get; set;
        }

        /// <summary>Whether the index was wiped first (an exact rebuild rather than a repair).</summary>
        [JsonPropertyName("replaced")]
        public Boolean Replaced
        {
            get; set;
        }

        /// <summary>Live elements scanned.</summary>
        [JsonPropertyName("scannedElements")]
        public Int32 ScannedElements
        {
            get; set;
        }

        /// <summary>Live elements that carried the property and were indexed.</summary>
        [JsonPropertyName("indexedElements")]
        public Int32 IndexedElements
        {
            get; set;
        }

        /// <summary>
        ///   Elements carrying the property whose value cannot be an index key (not comparable, for
        ///   example a vector written through the raw property surface). Skipped, and counted here so the
        ///   skip is not silent.
        /// </summary>
        [JsonPropertyName("skippedUnindexableValues")]
        public Int32 SkippedUnindexableValues
        {
            get; set;
        }
    }
}
