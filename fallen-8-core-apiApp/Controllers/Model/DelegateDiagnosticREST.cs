// MIT License
//
// DelegateDiagnosticREST.cs
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
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   A single compiler diagnostic in fragment coordinates (feature web-ui, gap G-2)
    /// </summary>
    /// <remarks>
    ///   Lines and columns are 1-based and already mapped from the generated wrapper class back
    ///   to the submitted fragment: line 1 / column 1 is the first character of the fragment. A
    ///   diagnostic whose real location falls outside the fragment (for example a missing brace
    ///   reported at the wrapper's closing brace) is clamped to the nearest fragment position.
    /// </remarks>
    public sealed class DelegateDiagnosticREST
    {
        /// <summary>1-based start line in fragment coordinates</summary>
        [JsonPropertyName("line")]
        public Int32 Line
        {
            get; set;
        }

        /// <summary>1-based start column in fragment coordinates</summary>
        [JsonPropertyName("column")]
        public Int32 Column
        {
            get; set;
        }

        /// <summary>1-based end line in fragment coordinates</summary>
        [JsonPropertyName("endLine")]
        public Int32 EndLine
        {
            get; set;
        }

        /// <summary>1-based end column (exclusive) in fragment coordinates</summary>
        [JsonPropertyName("endColumn")]
        public Int32 EndColumn
        {
            get; set;
        }

        /// <summary>The diagnostic id, e.g. "CS1061" (or "F8LIMIT" for the size guard)</summary>
        [JsonPropertyName("id")]
        public String Id
        {
            get; set;
        }

        /// <summary>The human-readable compiler message</summary>
        [JsonPropertyName("message")]
        public String Message
        {
            get; set;
        }

        /// <summary>"error", "warning" or "info"</summary>
        [JsonPropertyName("severity")]
        public String Severity
        {
            get; set;
        }
    }
}
