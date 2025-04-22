// MIT License
//
// FulltextSearchResultElementREST.cs
//
// Copyright (c) 2025 Henning Rauch
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
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
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using NoSQL.GraphDB.Core.Index.Fulltext;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   Represents a single match in fulltext search results with relevance scoring and highlighting
    /// </summary>
    /// <remarks>
    ///   Each element contains the graph element ID that matched, its relevance score, and highlighted text fragments
    /// </remarks>
    /// <example>
    /// {
    ///   "graphElementId": 123,
    ///   "score": 0.87,
    ///   "highlights": [
    ///     "This is a &lt;em&gt;graph&lt;/em&gt; &lt;em&gt;database&lt;/em&gt; document",
    ///     "Another fragment with &lt;em&gt;NoSQL&lt;/em&gt; mention"
    ///   ]
    /// }
    /// </example>
    public sealed class FulltextSearchResultElementREST
    {
        #region data

        /// <summary>
        /// The identifier of the graph element (vertex or edge) that matched the search query
        /// </summary>
        /// <example>123</example>
        [Required]
        [JsonPropertyName("graphElementId")]
        public Int32 GraphElementId
        {
            get; set;
        }

        /// <summary>
        /// Collection of highlighted text fragments showing the context of search term matches
        /// </summary>
        /// <remarks>
        /// The search terms are typically wrapped in &lt;em&gt; tags for highlighting
        /// </remarks>
        /// <example>["This is a &lt;em&gt;graph&lt;/em&gt; &lt;em&gt;database&lt;/em&gt; document"]</example>
        [Required]
        [JsonPropertyName("highlights")]
        public IList<string> Highlights
        {
            get; set;
        }

        /// <summary>
        /// The relevance score of this result, indicating how well it matches the search query
        /// </summary>
        /// <remarks>
        /// Higher scores indicate better matches, relative to the maximum score in the result set
        /// </remarks>
        /// <example>0.87</example>
        [Required]
        [JsonPropertyName("score")]
        public Double Score
        {
            get; set;
        }

        #endregion

        #region constructor

        /// <summary>
        /// Creates a new FulltextSearchResultElementREST instance from an internal FulltextSearchResultElement
        /// </summary>
        /// <param name="toBeTransferredResult">The internal search result element to convert</param>
        public FulltextSearchResultElementREST(FulltextSearchResultElement toBeTransferredResult)
        {
            if (toBeTransferredResult != null)
            {
                GraphElementId = toBeTransferredResult.GraphElement.Id;
                Highlights = toBeTransferredResult.Highlights;
                Score = toBeTransferredResult.Score;
            }
        }

        #endregion
    }
}
