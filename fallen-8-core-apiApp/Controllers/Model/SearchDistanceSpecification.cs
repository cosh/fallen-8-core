// MIT License
//
// SearchDistanceSpecification.cs
//
// Copyright (c) 2021 Henning Rauch
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
using System.ComponentModel.DataAnnotations;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    /// The search distance request object for spatial queries
    /// </summary>
    public sealed class SearchDistanceSpecification
    {
        #region data

        /// <summary>
        ///   Index identifier
        /// </summary>
        [Required]
        public String IndexId
        {
            get; set;
        }

        /// <summary>
        /// The graph element identifier.
        /// </summary>
        [Required]
        public Int32 GraphElementId
        {
            get; set;
        }

        /// <summary>
        /// The distance.
        /// </summary>
        [Required]
        public float Distance
        {
            get; set;
        }

        #endregion
    }
}
