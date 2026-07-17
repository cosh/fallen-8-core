// MIT License
//
// ElementEmbeddingREST.cs
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
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   Sets (replace semantics) a named element embedding (feature element-embeddings).
    /// </summary>
    /// <example>
    /// { "vector": [0.12, -0.5, 0.33] }
    /// </example>
    public sealed class EmbeddingWriteSpecification
    {
        /// <summary>The embedding vector; finite components, dimension within [1, 4096].</summary>
        [Required]
        [JsonPropertyName("vector")]
        public Single[] Vector
        {
            get; set;
        }
    }

    /// <summary>
    ///   A stored element embedding (feature element-embeddings).
    /// </summary>
    public sealed class ElementEmbeddingREST
    {
        /// <summary>The embedding name.</summary>
        /// <example>default</example>
        [JsonPropertyName("name")]
        public String Name
        {
            get; set;
        }

        /// <summary>The stored vector.</summary>
        [JsonPropertyName("vector")]
        public Single[] Vector
        {
            get; set;
        }

        /// <summary>The model-identity stamp written by the embedding provider, or null for a
        /// bring-your-own-vector embedding.</summary>
        /// <example>bge-micro-v2#384#Cosine</example>
        [JsonPropertyName("model")]
        public String Model
        {
            get; set;
        }
    }
}
