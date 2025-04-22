// MIT License
//
// Property.cs
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
using System.ComponentModel.DataAnnotations;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   Represents a property with an ID and value
    /// </summary>
    /// <example>
    /// {
    ///   "propertyId": "name",
    ///   "propertyValue": "John Doe"
    /// }
    /// </example>
    public class Property
    {
        /// <summary>
        ///   The identifier of the property
        /// </summary>
        /// <example>name</example>
        [Required]
        [DefaultValue("name")]
        [JsonPropertyName("propertyId")]
        public String PropertyId { get; set; } = "name";

        /// <summary>
        ///   The string representation of the property value
        /// </summary>
        /// <example>John Doe</example>
        [Required]
        [DefaultValue("John Doe")]
        [JsonPropertyName("propertyValue")]
        public String PropertyValue { get; set; } = "John Doe";
    }
}
