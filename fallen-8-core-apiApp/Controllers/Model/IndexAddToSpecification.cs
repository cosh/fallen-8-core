// MIT License
//
// IndexAddToSpecification.cs
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
    ///   Specification for adding or updating a graph element in an index
    /// </summary>
    /// <remarks>
    ///   Used to manually associate a graph element with a key in an index
    /// </remarks>
    /// <example>
    /// {
    ///   "graphElementId": 123,
    ///   "key": {
    ///     "propertyValue": "John Doe",
    ///     "fullQualifiedTypeName": "System.String"
    ///   }
    /// }
    /// </example>
    public class IndexAddToSpecification
    {
        /// <summary>
        ///   The identifier of the graph element to index
        /// </summary>
        /// <example>123</example>
        [Required]
        [DefaultValue(123)]
        [JsonPropertyName("graphElementId")]
        public Int32 GraphElementId
        {
            get; set;
        }

        /// <summary>
        ///   The key under which to index the graph element
        /// </summary>
        /// <remarks>
        ///   The key's type must match the index's expected type
        /// </remarks>
        [Required]
        [JsonPropertyName("key")]
        public PropertySpecification Key
        {
            get; set;
        }
    }
}
