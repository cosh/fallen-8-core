// MIT License
//
// PropertySearchSpecification.cs
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

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   Specification for a cold, un-indexed discovery scan across EVERY property of every
    ///   element: an element matches when any of its property values, rendered to text, contains
    ///   the search term (case-insensitive). Unlike <see cref="ScanSpecification" /> there is no
    ///   operator or typed literal - just a substring term.
    /// </summary>
    /// <example>
    /// {
    ///   "searchTerm": "acme",
    ///   "label": "company",
    ///   "resultType": "Both"
    /// }
    /// </example>
    public class PropertySearchSpecification
    {
        /// <summary>
        ///   The substring to look for across every property value (case-insensitive). Required
        ///   and non-blank.
        /// </summary>
        /// <example>acme</example>
        [Required]
        [JsonPropertyName("searchTerm")]
        public String SearchTerm
        {
            get; set;
        }

        /// <summary>
        ///   Optional restrictor: only elements with exactly this label match.
        /// </summary>
        /// <example>company</example>
        [JsonPropertyName("label")]
        public String Label
        {
            get; set;
        } = null;

        /// <summary>
        ///   Specifies which types of graph elements to include in the results. Defaults to
        ///   <see cref="ResultTypeSpecification.Both" /> (discovery wants everything); the explicit
        ///   initializer is load-bearing because the enum's zero value is
        ///   <see cref="ResultTypeSpecification.Vertices" />, so an omitted field would otherwise
        ///   deserialize to Vertices.
        /// </summary>
        /// <example>Both</example>
        [Required]
        [DefaultValue(ResultTypeSpecification.Both)]
        [JsonPropertyName("resultType")]
        public ResultTypeSpecification ResultType
        {
            get; set;
        } = ResultTypeSpecification.Both;
    }
}
