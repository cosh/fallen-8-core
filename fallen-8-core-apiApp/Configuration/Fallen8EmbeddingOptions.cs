// MIT License
//
// Fallen8EmbeddingOptions.cs
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

namespace NoSQL.GraphDB.App.Configuration
{
    /// <summary>
    ///   The embedding provider configuration (feature embedding-provider), section
    ///   <c>Fallen8:Embedding</c>. Default OFF: no model loads, nothing downloads, the
    ///   embedding endpoints answer 403 - the model-free default deployment stays intact.
    ///   Swapping <see cref="Backend" /> is the whole backend swap; no code changes.
    ///   Weights are NEVER downloaded by Fallen-8: paths point at files the operator provides,
    ///   the Ollama backend uses models the operator pulled.
    /// </summary>
    public sealed class Fallen8EmbeddingOptions
    {
        public const String SectionName = "Fallen8:Embedding";

        /// <summary>The authorization policy gating the embedding surface
        /// (<see cref="Security.DynamicCapabilityRequirement.Capability.EmbeddingProvider" />).</summary>
        public const String EmbeddingPolicy = "Fallen8.EmbeddingProvider";

        /// <summary>The capability flag. Default off.</summary>
        public Boolean Enabled
        {
            get; set;
        }

        /// <summary>The backend: <c>Onnx</c>, <c>LLamaSharp</c> or <c>Ollama</c>.</summary>
        public String Backend { get; set; } = "Onnx";

        /// <summary>The model name of the identity (FR-8); required when enabled.</summary>
        public String ModelName
        {
            get; set;
        }

        /// <summary>Optional free-form version/quantization/revision part of the identity.</summary>
        public String ModelVersion
        {
            get; set;
        }

        /// <summary>The declared output dimension; validated against actual output - a
        /// mismatch is a hard error, never coercion.</summary>
        public Int32 Dimension
        {
            get; set;
        }

        /// <summary>The metric the model's embeddings are intended for: <c>Cosine</c> (default),
        /// <c>DotProduct</c> or <c>L2</c>.</summary>
        public String IntendedMetric { get; set; } = "Cosine";

        /// <summary>Maximum texts per request batch.</summary>
        public Int32 MaxBatchSize { get; set; } = 64;

        /// <summary>Maximum characters per text item.</summary>
        public Int32 MaxTextLength { get; set; } = 8192;

        /// <summary>Optional retrieval-instruction prefix applied to QUERY-time embeddings
        /// (semantic search, queryText) - never to element embeddings.</summary>
        public String QueryPrefix { get; set; } = String.Empty;

        /// <summary>ONNX backend settings.</summary>
        public OnnxOptions Onnx { get; set; } = new OnnxOptions();

        /// <summary>LLamaSharp backend settings.</summary>
        public LLamaSharpOptions LLamaSharp { get; set; } = new LLamaSharpOptions();

        /// <summary>Ollama backend settings.</summary>
        public OllamaOptions Ollama { get; set; } = new OllamaOptions();

        public sealed class OnnxOptions
        {
            /// <summary>Path to the .onnx model file (operator-provided; nothing is downloaded).</summary>
            public String ModelPath
            {
                get; set;
            }

            /// <summary>Path to the WordPiece vocab file (e.g. vocab.txt of the bge family).</summary>
            public String VocabPath
            {
                get; set;
            }

            /// <summary>Token budget per text; longer inputs are truncated.</summary>
            public Int32 MaxTokens { get; set; } = 512;

            /// <summary>Pooling: <c>Cls</c> (the bge contract, default) or <c>Mean</c>.</summary>
            public String Pooling { get; set; } = "Cls";

            /// <summary>Whether to L2-normalize the pooled vector (the bge contract).</summary>
            public Boolean Normalize { get; set; } = true;
        }

        public sealed class LLamaSharpOptions
        {
            /// <summary>Path to an embedding-capable GGUF file - typically a blob already on the
            /// f8-ollama-models volume, so large weights exist once on disk.</summary>
            public String ModelPath
            {
                get; set;
            }
        }

        public sealed class OllamaOptions
        {
            /// <summary>The Ollama endpoint (the compose-shipped container by default). Using this
            /// backend couples embedding availability to that container: when it is down the
            /// embedding endpoints answer 503 while everything else keeps running.</summary>
            public String Endpoint { get; set; } = "http://localhost:11434";

            /// <summary>The embedding model to invoke (pull an MIT model, e.g. bge-m3).</summary>
            public String Model { get; set; } = "bge-m3";
        }
    }
}
