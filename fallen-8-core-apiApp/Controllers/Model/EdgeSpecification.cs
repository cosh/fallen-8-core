// MIT License
//
// EdgeSpecification.cs
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
    ///   Specification for creating a new edge between two vertices in the graph
    /// </summary>
    /// <example>
    /// {
    ///   "sourceVertex": 1,
    ///   "targetVertex": 2,
    ///   "edgePropertyId": "knows",
    ///   "label": "friendship",
    ///   "creationDate": 1713862800,
    ///   "properties": [
    ///     {
    ///       "propertyId": "since",
    ///       "propertyValue": "2024-01-15T00:00:00",
    ///       "fullQualifiedTypeName": "System.DateTime"
    ///     },
    ///     {
    ///       "propertyId": "strength",
    ///       "propertyValue": 0.85,
    ///       "fullQualifiedTypeName": "System.Double"
    ///     }
    ///   ]
    /// }
    /// </example>
    public class EdgeSpecification
    {
        /// <summary>
        ///   The creation date of the edge as a Unix timestamp
        /// </summary>
        /// <example>1713862800</example>
        [Required]
        [DefaultValue((uint)0)]
        [JsonPropertyName("creationDate")]
        public UInt32 CreationDate
        {
            get; set;
        }

        /// <summary>
        ///   The identifier of the source vertex where the edge starts
        /// </summary>
        /// <example>1</example>
        [Required]
        [DefaultValue(1)]
        [JsonPropertyName("sourceVertex")]
        public Int32 SourceVertex
        {
            get; set;
        }

        /// <summary>
        ///   The identifier of the target vertex where the edge ends
        /// </summary>
        /// <example>2</example>
        [Required]
        [DefaultValue(2)]
        [JsonPropertyName("targetVertex")]
        public Int32 TargetVertex
        {
            get; set;
        }

        /// <summary>
        ///   The edge property identifier that defines the relationship type
        /// </summary>
        /// <example>knows</example>
        [Required]
        [DefaultValue("knows")]
        [JsonPropertyName("edgePropertyId")]
        public String EdgePropertyId
        {
            get; set;
        }

        /// <summary>
        ///   The properties of the edge as key-value pairs
        /// </summary>
        [JsonPropertyName("properties")]
        public List<PropertySpecification> Properties
        {
            get; set;
        }

        /// <summary>
        ///   The label of the edge used for categorization
        /// </summary>
        /// <example>friendship</example>
        [DefaultValue("friendship")]
        [JsonPropertyName("label")]
        public String Label
        {
            get; set;
        }
    }
}
