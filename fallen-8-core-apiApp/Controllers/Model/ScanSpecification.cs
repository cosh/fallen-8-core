// MIT License
//
// ScanSpecification.cs
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
using NoSQL.GraphDB.Core.Expression;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   Specification for scanning the graph for elements with specific property values
    /// </summary>
    /// <example>
    /// {
    ///   "operator": "Equal",
    ///   "literal": {
    ///     "value": "John Doe",
    ///     "fullQualifiedTypeName": "System.String"
    ///   },
    ///   "label": "person",
    ///   "resultType": "Vertices"
    /// }
    /// </example>
    public class ScanSpecification
    {
        /// <summary>
        ///   The binary operator to use for comparing property values
        /// </summary>
        /// <example>Equal</example>
        [Required]
        [JsonPropertyName("operator")]
        public BinaryOperator Operator
        {
            get; set;
        }

        /// <summary>
        ///   The literal value to compare against
        /// </summary>
        [Required]
        [JsonPropertyName("literal")]
        public LiteralSpecification Literal
        {
            get; set;
        }

        /// <summary>
        ///   Optional label filter to restrict the scan to specific element types
        /// </summary>
        /// <example>person</example>
        [JsonPropertyName("label")]
        public String Label
        {
            get; set;
        } = null;

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
