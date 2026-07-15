// MIT License
//
// ValidateDelegateSpecification.cs
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
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   Request to compile-check a single delegate fragment (feature web-ui, gap G-2)
    /// </summary>
    /// <remarks>
    ///   The fragment is wrapped and compiled exactly like the path / subgraph endpoints wrap it,
    ///   but it is never emitted, loaded, or executed - validation is side-effect free. Diagnostic
    ///   positions in the response are already mapped back to fragment coordinates (line 1 =
    ///   the fragment's first line), so an editor can render markers without further mapping.
    /// </remarks>
    /// <example>
    /// {
    ///   "delegateKind": "VertexFilter",
    ///   "fragment": "return (v) => v.Label == \"person\";"
    /// }
    /// </example>
    public sealed class ValidateDelegateSpecification
    {
        /// <summary>
        /// The delegate kind the fragment must compile against
        /// </summary>
        /// <remarks>
        /// One of: <c>VertexFilter</c>, <c>EdgeFilter</c>, <c>EdgePropertyFilter</c>,
        /// <c>VertexCost</c>, <c>EdgeCost</c>, <c>GraphElementFilter</c> (case-insensitive).
        /// The kind decides the parameter type of the expected lambda and the compilation
        /// context (usings/namespace) the fragment is validated in.
        /// </remarks>
        /// <example>VertexFilter</example>
        [Required]
        [JsonPropertyName("delegateKind")]
        [DefaultValue("VertexFilter")]
        public String DelegateKind
        {
            get; set;
        }

        /// <summary>
        /// The C# fragment to validate: a method body returning a lambda
        /// </summary>
        /// <remarks>
        /// A null/empty fragment is valid by definition (it means "match everything" / "no
        /// custom cost" on the query endpoints).
        /// </remarks>
        /// <example>return (v) => v.Label == "person";</example>
        [JsonPropertyName("fragment")]
        public String Fragment
        {
            get; set;
        }
    }
}
