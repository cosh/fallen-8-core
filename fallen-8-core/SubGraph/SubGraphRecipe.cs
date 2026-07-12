// MIT License
//
// SubGraphRecipe.cs
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

namespace NoSQL.GraphDB.Core.SubGraph
{
    /// <summary>
    /// A persistable description of how to rebuild a subgraph.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A subgraph's filter/pattern predicates are delegates compiled at runtime and cannot
    /// be serialized directly. Instead, a subgraph created from a declarative
    /// specification carries a recipe: the metadata needed to recreate it plus the opaque
    /// specification text it was built from. On load, a registered
    /// <see cref="ISubGraphRecipeCompiler"/> turns the recipe back into a definition and the
    /// subgraph is recomputed against the restored source graph.
    /// </para>
    /// <para>
    /// <see cref="SpecificationJson"/> is intentionally opaque to the engine: its shape is
    /// owned by the layer that produced it (for example the REST API's subgraph
    /// specification). Only the recipe compiler needs to understand it. Subgraphs created
    /// programmatically from arbitrary delegates have no recipe and are not persisted.
    /// </para>
    /// </remarks>
    public sealed class SubGraphRecipe
    {
        /// <summary>The registered name of the subgraph.</summary>
        [JsonPropertyName("name")]
        public String Name
        {
            get; set;
        }

        /// <summary>
        /// The subgraph's own graph id at save time. Used on load to resolve nested
        /// subgraphs whose source is this subgraph (their <see cref="SourceFallen8Id"/>
        /// equals this value).
        /// </summary>
        [JsonPropertyName("subGraphId")]
        public Guid SubGraphId
        {
            get; set;
        }

        /// <summary>The plugin name of the algorithm used to create the subgraph.</summary>
        [JsonPropertyName("algorithmPluginName")]
        public String AlgorithmPluginName
        {
            get; set;
        }

        /// <summary>The id of the source graph the subgraph was created from.</summary>
        [JsonPropertyName("sourceFallen8Id")]
        public Guid SourceFallen8Id
        {
            get; set;
        }

        /// <summary>
        /// The serialized specification the subgraph was built from, opaque to the engine
        /// and interpreted by the <see cref="ISubGraphRecipeCompiler"/>.
        /// </summary>
        [JsonPropertyName("specificationJson")]
        public String SpecificationJson
        {
            get; set;
        }
    }
}
