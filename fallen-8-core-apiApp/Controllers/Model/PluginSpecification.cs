// MIT License
//
// PluginSpecification.cs
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
using System.ComponentModel.DataAnnotations;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   Specification for creating and configuring a plugin service
    /// </summary>
    /// <example>
    /// {
    ///   "uniqueId": "indexService1",
    ///   "pluginType": "DictionaryIndex",
    ///   "pluginOptions": {
    ///     "indexProperty": {
    ///       "propertyId": "propertyName",
    ///       "fullQualifiedTypeName": "System.String",
    ///       "propertyValue": "propertyValue"
    ///     }
    ///   }
    /// }
    /// </example>
    public sealed class PluginSpecification
    {
        /// <summary>
        ///   A unique identifier for this plugin instance
        /// </summary>
        /// <example>indexService1</example>
        [Required]
        [DefaultValue("indexService1")]
        [JsonPropertyName("uniqueId")]
        public String UniqueId { get; set; } = "indexService1";

        /// <summary>
        ///   The name of the plugin type to instantiate
        /// </summary>
        /// <example>DictionaryIndex</example>
        [Required]
        [DefaultValue("DictionaryIndex")]
        [JsonPropertyName("pluginType")]
        public String PluginType { get; set; } = "DictionaryIndex";

        /// <summary>
        ///   Configuration options for the plugin as key-value pairs
        /// </summary>
        [JsonPropertyName("pluginOptions")]
        public Dictionary<String, PropertySpecification> PluginOptions { get; set; } = new Dictionary<String, PropertySpecification>();
    }
}
