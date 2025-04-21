﻿// MIT License
//
// FulltextSearchResultElement.cs
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
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.Core.Index.Fulltext
{
    /// <summary>
    /// Fulltext search result element.
    /// </summary>
    public class FulltextSearchResultElement
    {
        #region data

        /// <summary>
        /// Gets or sets the graph element.
        /// </summary>
        /// <value>
        /// The graph element.
        /// </value>
        public AGraphElementModel GraphElement
        {
            get; private set;
        }

        /// <summary>
        /// Gets or sets the highlights.
        /// </summary>
        /// <value>
        /// The highlights.
        /// </value>
        public IList<string> Highlights
        {
            get; private set;
        }

        /// <summary>
        /// Gets or sets the score.
        /// </summary>
        /// <value>
        /// The score.
        /// </value>
        public Double Score
        {
            get; private set;
        }

        #endregion

        #region constructor

        /// <summary>
        /// Create a new FulltextSearchResultElement instance
        /// </summary>
        /// <param name="graphElement">Graph element</param>
        /// <param name="score">Score</param>
        /// <param name="highlights">Highlights</param>
        public FulltextSearchResultElement(AGraphElementModel graphElement, Double score, IEnumerable<String> highlights = null)
        {
            GraphElement = graphElement;
            Score = score;
            Highlights = highlights == null ? new List<string>() : new List<string>(highlights);
        }

        #endregion

        #region public methods

        /// <summary>
        /// Adds an highlight
        /// </summary>
        /// <param name="highlight">Highlight</param>
        public void AddHighlight(String highlight)
        {
            Highlights.Add(highlight);
        }

        #endregion
    }
}
