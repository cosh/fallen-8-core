// MIT License
//
// IndexDescriptionREST.cs
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
    ///   A registered index: its id and the index plugin type. Appears on
    ///   <see cref="StatusREST"/> (live inventory) and in save-game KPIs (inventory at save time).
    /// </summary>
    public sealed class IndexDescriptionREST
    {
        /// <summary>The index id used by the scan and index endpoints.</summary>
        /// <example>nameIndex</example>
        [JsonPropertyName("indexId")]
        public String IndexId
        {
            get; set;
        }

        /// <summary>The index plugin type the index was created with.</summary>
        /// <example>DictionaryIndex</example>
        [JsonPropertyName("pluginType")]
        public String PluginType
        {
            get; set;
        }

        /// <summary>
        ///   For a vector index BOUND to an element embedding (feature element-embeddings), the
        ///   embedding name it derives its projection from; <c>null</c> for an unbound (raw)
        ///   vector index and for every other family. Lets a client show that the index is a
        ///   self-maintained projection and that explicit adds are rejected. Not captured in
        ///   save-game KPIs (only the live <c>/status</c> inventory populates it).
        /// </summary>
        /// <example>default</example>
        [JsonPropertyName("embeddingName")]
        public String EmbeddingName
        {
            get; set;
        }

        /// <summary>
        ///   For a vector index, the declared model-identity string it expects its vectors to
        ///   come from (feature embedding-provider), or <c>null</c>. Diagnostic only.
        /// </summary>
        /// <example>bge-micro-v2#384#Cosine</example>
        [JsonPropertyName("model")]
        public String Model
        {
            get; set;
        }

        /// <summary>
        ///   The query families this index answers (feature index-workspace), derived from the
        ///   interfaces the index implements so third-party plugins report honestly:
        ///   <c>IVectorIndex</c> → <c>vector</c>; <c>ISpatialIndex</c> → <c>spatial</c>;
        ///   <c>IFulltextIndex</c> → <c>equality</c> + <c>fulltext</c>; <c>IRangeIndex</c> →
        ///   <c>equality</c> + <c>range</c>; any other index → <c>equality</c>. Vector and
        ///   spatial indexes do NOT report <c>equality</c> because their keys (float[],
        ///   geometry) cannot travel as the scan endpoints' typed literal. Like
        ///   <see cref="EmbeddingName"/>, only the live <c>/status</c> inventory populates it;
        ///   save-game KPIs leave it <c>null</c>.
        /// </summary>
        /// <example>["equality","range"]</example>
        [JsonPropertyName("capabilities")]
        public List<String> Capabilities
        {
            get; set;
        }

        /// <summary>
        ///   <c>CountOfKeys()</c> at snapshot time — live <c>/status</c> inventory only,
        ///   <c>null</c> in save-game KPIs and when the index reports a negative
        ///   "count not supported" sentinel (e.g. the spatial R-Tree).
        /// </summary>
        /// <example>1000</example>
        [JsonPropertyName("keys")]
        public Int32? Keys
        {
            get; set;
        }

        /// <summary>
        ///   <c>CountOfValues()</c> at snapshot time (O(entries) on bucket indexes — fine for
        ///   the status poll at self-hosted scale) — live <c>/status</c> inventory only,
        ///   <c>null</c> in save-game KPIs and for a negative sentinel (see <see cref="Keys"/>).
        /// </summary>
        /// <example>1200</example>
        [JsonPropertyName("values")]
        public Int32? Values
        {
            get; set;
        }
    }
}
