// MIT License
//
// PluginRegistrationSpecification.cs
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
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   Request body for <c>POST /plugins/algorithm</c> (feature plugin-registration): register a
    ///   runtime-authored algorithm plugin from whole-type C# source.
    /// </summary>
    /// <example>
    /// {
    ///   "name": "MyDijkstra",
    ///   "contract": "Path",
    ///   "description": "a custom weighted shortest path",
    ///   "sourceCode": "using ...; public sealed class MyDijkstra : IShortestPathAlgorithm { ... }"
    /// }
    /// </example>
    public sealed class AlgorithmPluginRegistration
    {
        /// <summary>The unique name to register under (per namespace). Must equal the type's <c>PluginName</c>.</summary>
        [JsonPropertyName("name")]
        public String Name
        {
            get; set;
        }

        /// <summary>The contract the source implements: "Path", "SubGraph" or "Analytics".</summary>
        [JsonPropertyName("contract")]
        public String Contract
        {
            get; set;
        }

        /// <summary>An optional human-readable description.</summary>
        [JsonPropertyName("description")]
        public String Description
        {
            get; set;
        }

        /// <summary>The whole-type C# source implementing the contract's interface.</summary>
        [JsonPropertyName("sourceCode")]
        public String SourceCode
        {
            get; set;
        }
    }

    /// <summary>
    ///   Request body for <c>POST /plugins/function</c> (feature plugin-registration): register a
    ///   stored graph function (an <c>IGraphFunction</c>) from whole-type C# source. The function
    ///   category has a single contract, so no discriminator is needed.
    /// </summary>
    /// <example>
    /// {
    ///   "name": "NeighboursOfLabel",
    ///   "description": "all vertices of a label and their edges",
    ///   "sourceCode": "using ...; public sealed class NeighboursOfLabel : IGraphFunction { ... }"
    /// }
    /// </example>
    public sealed class FunctionPluginRegistration
    {
        /// <summary>The unique name to register under (per namespace). Must equal the type's <c>PluginName</c>.</summary>
        [JsonPropertyName("name")]
        public String Name
        {
            get; set;
        }

        /// <summary>An optional human-readable description.</summary>
        [JsonPropertyName("description")]
        public String Description
        {
            get; set;
        }

        /// <summary>The whole-type C# source implementing <c>IGraphFunction</c>.</summary>
        [JsonPropertyName("sourceCode")]
        public String SourceCode
        {
            get; set;
        }
    }

    /// <summary>
    ///   Request body for <c>POST /plugins/function/{name}/invoke</c> (feature plugin-registration):
    ///   the call-time parameter bag. Values are STRINGS in v1 (a function parses what it needs);
    ///   richer typed parameters are a later refinement.
    /// </summary>
    public sealed class GraphFunctionInvocation
    {
        /// <summary>The parameters passed to the function (may be null/empty).</summary>
        [JsonPropertyName("parameters")]
        public Dictionary<String, String> Parameters
        {
            get; set;
        }
    }

    /// <summary>
    ///   Request body for the side-effect-free validate endpoints
    ///   <c>POST /plugins/algorithm/validate</c> and <c>POST /plugins/function/validate</c> (feature
    ///   plugin-registration): compile + contract-validate source WITHOUT registering it, so the
    ///   Studio editor can surface diagnostics. Shares the fields of the matching registration body.
    /// </summary>
    public sealed class PluginValidationSpecification
    {
        /// <summary>The name the source must expose as <c>PluginName</c> (validated for equality).</summary>
        [JsonPropertyName("name")]
        public String Name
        {
            get; set;
        }

        /// <summary>The contract for an algorithm plugin ("Path"/"SubGraph"/"Analytics"); ignored for the function endpoint.</summary>
        [JsonPropertyName("contract")]
        public String Contract
        {
            get; set;
        }

        /// <summary>The whole-type C# source to compile-check.</summary>
        [JsonPropertyName("sourceCode")]
        public String SourceCode
        {
            get; set;
        }
    }
}
