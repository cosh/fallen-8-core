// MIT License
//
// ResultTypeSpecification.cs
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

using System.ComponentModel;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   Specifies which types of graph elements to include in query results
    /// </summary>
    /// <example>Vertices</example>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ResultTypeSpecification
    {
        /// <summary>
        /// Only include vertices in the result set
        /// </summary>
        [EnumMember(Value = "Vertices")]
        [Description("Return only vertex elements")]
        Vertices,

        /// <summary>
        /// Only include edges in the result set
        /// </summary>
        [EnumMember(Value = "Edges")]
        [Description("Return only edge elements")]
        Edges,

        /// <summary>
        /// Include both vertices and edges in the result set
        /// </summary>
        [EnumMember(Value = "Both")]
        [Description("Return both vertex and edge elements")]
        Both,
    }
}
