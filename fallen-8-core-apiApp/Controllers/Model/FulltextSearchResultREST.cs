// MIT License
//
// FulltextSearchResultREST.cs
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
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using NoSQL.GraphDB.Core.Index.Fulltext;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   Result container for fulltext search operations with scoring and highlighting
    /// </summary>
    /// <remarks>
    ///   Contains scored results with text fragments showing search term matches
    /// </remarks>
    /// <example>
    /// {
    ///   "maximumScore": 0.87,
    ///   "elements": [
    ///     {
    ///       "graphElementId": 123,
    ///       "score": 0.87,
    ///       "highlight": "This is a <em>graph</em> <em>database</em> document about <em>NoSQL</em>"
    ///     },
    ///     {
    ///       "graphElementId": 456,
    ///       "score": 0.65,
    ///       "highlight": "Introduction to <em>graph</em> theory and <em>database</em> systems"
    ///     }
    ///   ]
    /// }
    /// </example>
    public sealed class FulltextSearchResultREST
    {
        #region data

        /// <summary>
        /// The highest relevance score among all matching elements
        /// </summary>
        /// <remarks>
        /// Can be used for normalizing scores across different queries
        /// </remarks>
        /// <example>0.87</example>
        [Required]
        [JsonPropertyName("maximumScore")]
        public Double MaximumScore
        {
            get; set;
        }

        /// <summary>
        /// Collection of matching elements with their relevance scores and highlighted content
        /// </summary>
        [Required]
        [JsonPropertyName("elements")]
        public List<FulltextSearchResultElementREST> Elements
        {
            get; set;
        }

        #endregion

        #region constructor

        /// <summary>
        /// Creates a new FulltextSearchResultREST instance from an internal FulltextSearchResult
        /// </summary>
        /// <param name="toBeTransferredResult">The internal search result to convert</param>
        public FulltextSearchResultREST(FulltextSearchResult toBeTransferredResult)
        {
            if (toBeTransferredResult != null)
            {
                MaximumScore = toBeTransferredResult.MaximumScore;
                Elements = new List<FulltextSearchResultElementREST>(toBeTransferredResult.Elements.Count);
                for (int i = 0; i < toBeTransferredResult.Elements.Count; i++)
                {
                    Elements.Add(new FulltextSearchResultElementREST(toBeTransferredResult.Elements[i]));
                }
            }
        }

        #endregion
    }
}
