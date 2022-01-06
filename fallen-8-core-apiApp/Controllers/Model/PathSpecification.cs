// MIT License
//
// PathSpecification.cs
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
using System.ComponentModel.DataAnnotations;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The path specification
    /// </summary>
    public sealed class PathSpecification
    {
        /// <summary>
        ///   The desired path algorithm plugin name
        /// </summary>
        [Required]
        public String PathAlgorithmName { get; set; } = "BLS";

        /// <summary>
        ///   The maximum depth
        /// </summary>
        [Required]
        public UInt16 MaxDepth { get; set; } = 7;

        /// <summary>
        ///   The maximum result count
        /// </summary>
        public UInt16 MaxResults { get; set; } = UInt16.MaxValue;

        /// <summary>
        ///   The maximum path weight
        /// </summary>
        public Double MaxPathWeight { get; set; } = Double.MaxValue;

        /// <summary>
        ///   The path filter specification
        /// </summary>
        public PathFilterSpecification Filter { get; set; }

        /// <summary>
        ///   The path cost specification
        /// </summary>
        public PathCostSpecification Cost { get; set; }
    }
}
