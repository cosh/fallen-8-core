// MIT License
//
// RangeIndexScanSpecification.cs
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
    ///   Specification for performing a range-based scan on an index
    /// </summary>
    /// <example>
    /// {
    ///   "indexId": "ageIndex",
    ///   "leftLimit": "18",
    ///   "rightLimit": "30",
    ///   "fullQualifiedTypeName": "System.Int32",
    ///   "includeLeft": true,
    ///   "includeRight": false,
    ///   "resultType": "Vertices"
    /// }
    /// </example>
    public sealed class RangeIndexScanSpecification
    {
        /// <summary>
        ///   The identifier of the index to scan
        /// </summary>
        /// <example>ageIndex</example>
        [Required]
        [DefaultValue("ageIndex")]
        [JsonPropertyName("indexId")]
        public String IndexId
        {
            get; set;
        }

        /// <summary>
        ///   The lower bound of the range as a string representation
        /// </summary>
        /// <example>18</example>
        [Required]
        [DefaultValue("18")]
        [JsonPropertyName("leftLimit")]
        public String LeftLimit
        {
            get; set;
        }

        /// <summary>
        ///   The upper bound of the range as a string representation
        /// </summary>
        /// <example>30</example>
        [Required]
        [DefaultValue("30")]
        [JsonPropertyName("rightLimit")]
        public String RightLimit
        {
            get; set;
        }

        /// <summary>
        ///   The fully qualified .NET type name of the range values
        /// </summary>
        /// <example>System.Int32</example>
        [Required]
        [DefaultValue("System.Int32")]
        [JsonPropertyName("fullQualifiedTypeName")]
        public String FullQualifiedTypeName
        {
            get; set;
        }

        /// <summary>
        ///   Whether to include the lower bound value in the range (inclusive)
        /// </summary>
        /// <example>true</example>
        [DefaultValue(true)]
        [JsonPropertyName("includeLeft")]
        public Boolean IncludeLeft
        {
            get; set;
        }

        /// <summary>
        ///   Whether to include the upper bound value in the range (inclusive)
        /// </summary>
        /// <example>false</example>
        [DefaultValue(false)]
        [JsonPropertyName("includeRight")]
        public Boolean IncludeRight
        {
            get; set;
        }

        /// <summary>
        ///   Specifies which types of graph elements to include in the results
        /// </summary>
        /// <example>Vertices</example>
        [Required]
        [DefaultValue(ResultTypeSpecification.Vertices)]
        [JsonPropertyName("resultType")]
        public ResultTypeSpecification ResultType
        {
            get; set;
        }
    }
}
