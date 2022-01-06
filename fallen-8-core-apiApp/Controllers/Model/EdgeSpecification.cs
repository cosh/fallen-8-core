// MIT License
//
// EdgeSpecification.cs
//
// Copyright (c) 2021 Henning Rauch
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

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The edge specification
    /// </summary>
    public class EdgeSpecification
    {
        /// <summary>
        ///   The creation date
        /// </summary>
        [Required]
        public UInt32 CreationDate { get; set; }

        /// <summary>
        ///   The source vertex
        /// </summary>
        [Required]
        public Int32 SourceVertex { get; set; }

        /// <summary>
        ///   The target vertex
        /// </summary>
        [Required]
        public Int32 TargetVertex { get; set; }

        /// <summary>
        ///   The edge property identifier
        /// </summary>
        [Required]
        public UInt16 EdgePropertyId { get; set; }

        /// <summary>
        ///   The properties of the vertex
        /// </summary>
        public List<PropertySpecification> Properties { get; set; }
    }
}
