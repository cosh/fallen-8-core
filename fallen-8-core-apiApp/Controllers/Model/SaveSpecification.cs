// MIT License
//
// SaveSpecification.cs
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
    ///   Specification for saving a Fallen-8 database to disk
    /// </summary>
    /// <example>
    /// {
    ///   "saveGameLocation": "C:/Fallen8/database.f8s",
    ///   "savePartitions": 8
    /// }
    /// </example>
    public sealed class SaveSpecification
    {
        /// <summary>
        ///   The file path location where the Fallen-8 database should be saved
        /// </summary>
        /// <example>C:/Fallen8/database.f8s</example>
        [JsonPropertyName("saveGameLocation")]
        public String SaveGameLocation { get; set; }

        /// <summary>
        ///   The number of partitions to use when saving (optional, defaults to optimal based on processor count)
        /// </summary>
        /// <example>8</example>
        [JsonPropertyName("savePartitions")]
        public UInt32? SavePartitions { get; set; }
    }
}
