// MIT License
//
// AGraphElement.cs
//
// Copyright (c) 2022 Henning Rauch
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
using System.Linq;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The Fallen-8 REST properties
    /// </summary>
    public abstract class AGraphElement
    {
        protected AGraphElement(Int32 id, UInt32 creationDate, UInt32 modificationDate, String label, ImmutableDictionary<String, Object> properties)
        {
            Id = id;
            CreationDate = DateHelper.GetDateTimeFromUnixTimeStamp(creationDate);
            ModificationDate = DateHelper.GetDateTimeFromUnixTimeStamp(modificationDate);
            Label = label;
            Properties = properties.Select(_ => new PropertySpecification
            {
                PropertyId = _.Key,
                PropertyValue = _.Value.ToString(),
                FullQualifiedTypeName = _.Value.GetType().FullName
            }).ToList();
        }

        /// <summary>
        ///   The identifier
        /// </summary>
        [Required]
        public Int32 Id
        {
            get; set;
        }

        /// <summary>
        ///   The creation date
        /// </summary>
        [Required]
        public DateTime CreationDate
        {
            get; set;
        }

        /// <summary>
        ///   The modification date
        /// </summary>
        [Required]
        public DateTime ModificationDate
        {
            get; set;
        }

        /// <summary>
        ///   The label of the vertex
        /// </summary>
        public String Label
        {
            get; set;
        }

        /// <summary>
        ///   The properties of the vertex
        /// </summary>
        public List<PropertySpecification> Properties
        {
            get; set;
        }
    }
}
