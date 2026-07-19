// MIT License
//
// AGraphElement.cs
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

using NoSQL.GraphDB.Core.Helper;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    /// Abstract base class for all graph elements (vertices and edges)
    /// </summary>
    /// <remarks>
    /// Provides common properties shared by all graph elements such as
    /// identifiers, timestamps, labels, and property collections
    /// </remarks>
    public abstract class AGraphElement
    {
        /// <summary>
        /// Base constructor for graph elements
        /// </summary>
        /// <param name="id">The unique identifier of the graph element</param>
        /// <param name="creationDate">The creation timestamp</param>
        /// <param name="modificationDate">The last modification timestamp</param>
        /// <param name="label">The element's type label</param>
        /// <param name="properties">The element's properties</param>
        protected AGraphElement(Int32 id, UInt32 creationDate, UInt32 modificationDate, String label, ImmutableDictionary<String, Object> properties)
        {
            Id = id;
            CreationDate = DateHelper.GetDateTimeFromUnixTimeStamp(creationDate);
            ModificationDate = DateHelper.GetDateTimeFromUnixTimeStamp(modificationDate);
            Label = label;
            Properties = properties.Select(_ => new PropertySpecification
            {
                PropertyId = _.Key,
                PropertyValue = FormatPropertyValue(_.Value),
                FullQualifiedTypeName = _.Value.GetType().FullName
            }).ToList();
        }

        /// <summary>
        ///   EGRESS HOME (feature property-ingestion-culture): faithful string form of a property
        ///   value for the REST projection. Every value that carries a locale-sensitive rendering is
        ///   formatted with <see cref="CultureInfo.InvariantCulture"/> so the wire value round-trips
        ///   through <c>PUT</c> (ingest mirrors this in <c>ServiceHelper.CreateObject</c>; the bulk
        ///   path does the same in <c>JsonlGraphFormat</c>): a <see cref="Single"/>[] (e.g. an element
        ///   embedding, else the literal "System.Single[]") becomes its bracketed element list, a
        ///   <see cref="DateTime"/> a round-trippable ISO-8601 ("O") string, and any other
        ///   <see cref="IFormattable"/> scalar (<c>double</c>/<c>float</c>/<c>decimal</c>) its
        ///   invariant form ("0.8", never the server-locale "0,8"). Genuinely non-formattable objects
        ///   keep plain <see cref="Object.ToString()"/>.
        /// </summary>
        private static String FormatPropertyValue(Object value)
        {
            switch (value)
            {
                case null:
                    return null;
                case Single[] vector:
                    return "[" + String.Join(",", vector.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]";
                case DateTime dateTime:
                    return dateTime.ToString("O", CultureInfo.InvariantCulture);
                case IFormattable formattable:
                    return formattable.ToString(null, CultureInfo.InvariantCulture);
                default:
                    return value.ToString();
            }
        }

        /// <summary>
        /// The unique identifier of the graph element
        /// </summary>
        /// <example>123</example>
        [Required]
        [DefaultValue(123)]
        [JsonPropertyName("id")]
        public Int32 Id
        {
            get; set;
        }

        /// <summary>
        /// The date and time when the element was created
        /// </summary>
        /// <example>2025-04-22T10:00:00Z</example>
        [Required]
        [JsonPropertyName("creationDate")]
        public DateTime CreationDate
        {
            get; set;
        }

        /// <summary>
        /// The date and time when the element was last modified
        /// </summary>
        /// <example>2025-04-22T10:00:00Z</example>
        [Required]
        [JsonPropertyName("modificationDate")]
        public DateTime ModificationDate
        {
            get; set;
        }

        /// <summary>
        /// The type label of the graph element used for categorization
        /// </summary>
        /// <example>person</example>
        [DefaultValue("person")]
        [JsonPropertyName("label")]
        public String Label
        {
            get; set;
        }

        /// <summary>
        /// The collection of properties (key-value pairs) associated with the graph element
        /// </summary>
        [JsonPropertyName("properties")]
        public List<PropertySpecification> Properties
        {
            get; set;
        }
    }
}
