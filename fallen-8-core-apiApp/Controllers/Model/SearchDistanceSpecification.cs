// MIT License
//
// SearchDistanceSpecification.cs
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
    /// Specification for performing spatial distance searches in the graph
    /// </summary>
    /// <remarks>
    /// Used to find graph elements within a specified distance from a reference element
    /// </remarks>
    /// <example>
    /// {
    ///   "indexId": "locationIndex",
    ///   "graphElementId": 123,
    ///   "distance": 5.0
    /// }
    /// </example>
    public sealed class SearchDistanceSpecification
    {
        #region data

        /// <summary>
        ///   The identifier of the spatial index to search
        /// </summary>
        /// <example>locationIndex</example>
        [Required]
        [DefaultValue("locationIndex")]
        [JsonPropertyName("indexId")]
        public String IndexId
        {
            get; set;
        }

        /// <summary>
        /// The identifier of the reference graph element for the distance calculation
        /// </summary>
        /// <remarks>
        /// Distance will be measured from this element to other elements in the spatial index
        /// </remarks>
        /// <example>123</example>
        [Required]
        [DefaultValue(123)]
        [JsonPropertyName("graphElementId")]
        public Int32 GraphElementId
        {
            get; set;
        }

        /// <summary>
        /// The maximum distance to search within
        /// </summary>
        /// <remarks>
        /// Unit depends on the spatial index implementation
        /// </remarks>
        /// <example>5.0</example>
        [Required]
        [DefaultValue(5.0f)]
        [JsonPropertyName("distance")]
        public float Distance
        {
            get; set;
        }

        #endregion
    }
}
