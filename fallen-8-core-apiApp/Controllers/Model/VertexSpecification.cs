// MIT License
//
// VertexSpecification.cs
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
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   Specification for creating a new vertex in the graph
    /// </summary>
    /// <example>
    /// {
    ///   "label": "person",
    ///   "creationDate": 1713862800,
    ///   "properties": [
    ///     {
    ///       "propertyId": "name",
    ///       "propertyValue": "John Doe",
    ///       "fullQualifiedTypeName": "System.String"
    ///     },
    ///     {
    ///       "propertyId": "age",
    ///       "propertyValue": 30,
    ///       "fullQualifiedTypeName": "System.Int32"
    ///     }
    ///   ]
    /// }
    /// </example>
    public class VertexSpecification
    {
        /// <summary>
        ///   The creation date of the vertex as a Unix timestamp
        /// </summary>
        /// <example>1713862800</example>
        [Required]
        [DefaultValue((ushort)0)]
        [JsonPropertyName("creationDate")]
        public UInt32 CreationDate
        {
            get; set;
        }

        /// <summary>
        ///   The label of the vertex used for categorization
        /// </summary>
        /// <example>person</example>
        [JsonPropertyName("label")]
        public String Label
        {
            get; set;
        }

        /// <summary>
        ///   The properties of the vertex as key-value pairs
        /// </summary>
        [JsonPropertyName("properties")]
        public List<PropertySpecification> Properties
        {
            get; set;
        }
    }
}
