// MIT License
//
// LiteralSpecification.cs
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
    ///   Specification for literal values used in graph queries and scans
    /// </summary>
    /// <remarks>
    ///   LiteralSpecification represents a typed value that can be used for comparison operations
    /// </remarks>
    /// <example>
    /// {
    ///   "value": "John Doe",
    ///   "fullQualifiedTypeName": "System.String"
    /// }
    /// </example>
    public sealed class LiteralSpecification
    {
        /// <summary>
        ///   The string representation of the value to use in comparisons
        /// </summary>
        /// <example>John Doe</example>
        [Required]
        [JsonPropertyName("value")]
        public String Value
        {
            get; set;
        }

        /// <summary>
        ///   The fully qualified .NET type name of the value
        /// </summary>
        /// <remarks>
        ///   Used for type conversion before comparison operations
        /// </remarks>
        /// <example>System.String</example>
        [DefaultValue("System.String")]
        [JsonPropertyName("fullQualifiedTypeName")]
        public String FullQualifiedTypeName
        {
            get; set;
        }
    }
}
