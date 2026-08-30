// MIT License
//
// Fallen8EmbeddingOptions.cs
//
// Copyright (c) 2011-2026 Henning Rauch
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

        /// <summary>The backend: <c>Onnx</c>, <c>LLamaSharp</c>, <c>Ollama</c> (the local sidecar),
        /// <c>Nahil</c> (nahil.dev, remote and authenticated) or <c>OpenAI</c>.
        /// <para>Nahil serves the same model, so moving to it changes nothing about the vectors.
        /// <c>OpenAI</c> is a DIFFERENT embedding function, so moving there means declaring its
        /// identity too (<see cref="ModelName" />, <see cref="Dimension" />,
        /// <see cref="IntendedMetric" />): THAT declaration is what makes every vector and index
        /// already stored report an honest identity mismatch until it is re-embedded. Nothing can
        /// verify a stamp against a function on the far side of an endpoint, so an old identity left
        /// in place would file the new function's vectors under it. That is why this key is never
        /// writable at runtime.</para></summary>
        public String Backend { get; set; } = "Onnx";

        /// <summary>The per-call budget for one generate (which embeds a BATCH of texts, hence the
        /// larger default than the chat gateway's single completion). It is the SINGLE deadline on
        /// the call: the Ollama transport is built without one, so this value cannot be pre-empted
        /// by a shorter undocumented bound. Exceeded calls answer 503, like any other
        /// "backend not usable right now" - the embedding contract has no 504.</summary>
        public Int32 TimeoutSeconds { get; set; } = 300;

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

        /// <summary>
        ///   Maximum characters per text item on the <c>/embedding</c> surface. 8192 is deliberately
        ///   NOT the model's token window, though it is the same number and that is where it came
        ///   from: <c>bge-m3</c> advertises 8192 tokens and neither backend honours it (measured,
        ///   both stop at 2048). Read it instead as the bound above which an input cannot fit 2048
        ///   tokens even at the most token-efficient text there is (~4.1 chars/token for Latin
        ///   prose) - it rejects the hopeless early, with a 400 naming this key.
        ///   <para>Below it, length is the backend's call and it is asked never to truncate, so a
        ///   too-dense input is refused rather than half-embedded (see
        ///   <see cref="Embedding.Fallen8EmbeddingProvider" />). Tightening this to the real
        ///   worst-case density would reject inputs the backend accepts happily, which is why the
        ///   char bound stays generous and the backend stays honest.</para>
        /// </summary>
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

        /// <summary>Nahil settings; used only when <see cref="Backend" /> is <c>Nahil</c>.</summary>
        public NahilOptions Nahil { get; set; } = new NahilOptions();

        /// <summary>OpenAI settings; used only when <see cref="Backend" /> is <c>OpenAI</c>.</summary>
        public OpenAIOptions OpenAI { get; set; } = new OpenAIOptions();

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

            /// <summary>The embedding model to invoke (pull an MIT model, e.g. bge-m3). Reaches the
            /// request body VERBATIM, so the tag is explicit; this is the request identifier, NOT
            /// the identity stamp (<see cref="Fallen8EmbeddingOptions.ModelName" />), which is
            /// compared against stored vectors and is deliberately untagged.</summary>
            public String Model { get; set; } = "bge-m3:latest";
        }

        /// <summary>
        ///   Nahil (nahil.dev), serving the same embedding model. The geometry does not change with
        ///   the hop: <see cref="Dimension" />,
        ///   <see cref="IntendedMetric" /> and the identity stamp stay exactly what they were, and
        ///   nothing re-embeds - the same model produces the same vectors wherever it runs.
        /// </summary>
        public sealed class NahilOptions
        {
            /// <summary>The Nahil base URL. Must be a host root (scheme, host, optional port);
            /// HTTPS for anything off the operator's own network.</summary>
            public String Endpoint
            {
                get; set;
            }

            /// <summary>The bearer credential Nahil requires on EVERY route. Never logged and never
            /// published on the config read surface.</summary>
            public String ApiKey
            {
                get; set;
            }

            /// <summary>The embedding model to invoke, as Nahil's catalog names it. This is the
            /// embedding FUNCTION: it must serve the same model the stored vectors were produced
            /// with, or the identity stamp beside them becomes a lie.</summary>
            public String Model
            {
                get; set;
            }
        }

        /// <summary>
        ///   OpenAI, or any gateway that serves OpenAI's embeddings protocol. Unlike
        ///   <see cref="NahilOptions" />, this is a different embedding function rather than the
        ///   same one somewhere else: selecting it means setting
        ///   <see cref="Fallen8EmbeddingOptions.ModelName" />, <see cref="Dimension" /> and
        ///   <see cref="IntendedMetric" /> to that function's identity and re-embedding what was
        ///   stored under the old one.
        /// </summary>
        public sealed class OpenAIOptions
        {
            /// <summary>The base URL. Must be a host root (scheme, host, optional port); the SDK
            /// appends the route itself.</summary>
            public String Endpoint { get; set; } = "https://api.openai.com";

            /// <summary>The credential OpenAI requires on every route. Never logged and never
            /// published on the config read surface.</summary>
            public String ApiKey
            {
                get; set;
            }

            /// <summary>The embedding model to invoke, as OpenAI's catalog names it. Reaches the
            /// request body verbatim.</summary>
            public String Model
            {
                get; set;
            }
        }
    }
}
