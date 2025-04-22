// MIT License
//
// FulltextIndexScanSpecification.cs
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
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    /// Specification for performing fulltext search queries on indexed content
    /// </summary>
    /// <remarks>
    /// Used to search for text patterns in textual properties indexed by a fulltext index
    /// </remarks>
    /// <example>
    /// {
    ///   "indexId": "documentIndex",
    ///   "requestString": "graph database nosql"
    /// }
    /// </example>
    public class FulltextIndexScanSpecification
    {
        #region data

        /// <summary>
        /// The identifier of the fulltext index to search
        /// </summary>
        /// <example>documentIndex</example>
        [Required]
        [DefaultValue("documentIndex")]
        [JsonPropertyName("indexId")]
        public String IndexId
        {
            get; set;
        }

        /// <summary>
        /// The search query text to look for in the indexed content
        /// </summary>
        /// <remarks>
        /// The syntax depends on the underlying fulltext index implementation
        /// </remarks>
        /// <example>graph database nosql</example>
        [Required]
        [JsonPropertyName("requestString")]
        public String RequestString
        {
            get; set;
        }

        #endregion
    }
}
