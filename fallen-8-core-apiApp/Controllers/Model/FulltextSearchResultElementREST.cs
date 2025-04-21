﻿// MIT License
//
// FulltextSearchResultElementREST.cs
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
using NoSQL.GraphDB.Core.Index.Fulltext;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The pendant to the embedded FulltextSearchResultElement
    /// </summary>
    public sealed class FulltextSearchResultElementREST
    {
        #region data

        /// <summary>
        /// Gets or sets the graph element id.
        /// </summary>
        /// <value>
        /// The graph element.
        /// </value>
        [Required]
        public Int32 GraphElementId
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the highlights.
        /// </summary>
        /// <value>
        /// The highlights.
        /// </value>
        [Required]
        public IList<string> Highlights
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the score.
        /// </summary>
        /// <value>
        /// The score.
        /// </value>
        [Required]
        public Double Score
        {
            get; set;
        }

        #endregion

        #region constructor

        /// <summary>
        /// Creates a new FulltextSearchResultElementREST instance
        /// </summary>
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
