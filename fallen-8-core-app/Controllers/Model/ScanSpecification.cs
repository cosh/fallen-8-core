// MIT License
//
// ScanSpecification.cs
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
using NoSQL.GraphDB.Core.Expression;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   Specification for scanning the graph for elements with specific property values
    /// </summary>
    /// <example>
    /// {
    ///   "operator": 0,
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
        ///   The binary operator to use for comparing property values, sent as its integer code:
        ///   0 Equals, 1 Greater, 2 GreaterOrEquals, 3 Lower, 4 LowerOrEquals, 5 NotEquals.
        ///   A member name is not accepted: unlike resultType, this enum carries no string-enum
        ///   converter, so the code is the wire form (this summary is the mapping's one home,
        ///   since the generated schema shows only "integer").
        /// </summary>
        /// <example>0</example>
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
        ///   Optional restrictor: only elements with exactly this label match
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
