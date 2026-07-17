// MIT License
//
// EmbeddingBackendSmokeTest.cs
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
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Embedding;
using NoSQL.GraphDB.Core.Index.Vector;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Feature embedding-provider: OPT-IN live smokes, one per real backend (the repo's
    ///   gated-test pattern: remove the [Ignore] and provide the environment below). Each
    ///   skips cleanly (Inconclusive) when its model file / endpoint is absent, and asserts
    ///   the contract every backend must meet: declared-length finite vectors, non-zero norm,
    ///   and cosine self-similarity of 1 through the shared VectorMath.
    ///
    ///   Environment:
    ///     F8_TEST_ONNX_MODEL / F8_TEST_ONNX_VOCAB / F8_TEST_ONNX_DIM   (bge-family export)
    ///     F8_TEST_GGUF_MODEL / F8_TEST_GGUF_DIM                        (embedding-capable GGUF)
    ///     F8_TEST_OLLAMA_ENDPOINT / F8_TEST_OLLAMA_MODEL / F8_TEST_OLLAMA_DIM
    /// </summary>
    [TestClass]
    public class EmbeddingBackendSmokeTest
    {
        private static async Task AssertBackendContract(IEmbeddingGenerator<string, Embedding<float>> generator, int dimension)
        {
            var generated = await generator.GenerateAsync(new[] { "a red bicycle", "a blue whale" });
            Assert.AreEqual(2, generated.Count);

            foreach (var embedding in generated)
            {
                var vector = embedding.Vector.ToArray();
                Assert.AreEqual(dimension, vector.Length, "the backend must produce the declared dimension");
                Assert.IsFalse(VectorIndex.HasNonFiniteComponent(vector));
                Assert.IsFalse(VectorIndex.IsZeroNorm(vector));
                Assert.AreEqual(1f, VectorMath.Score(vector, vector, VectorDistanceMetric.Cosine), 1e-3f,
                    "cosine self-similarity must be 1");
            }
        }

        private static string Env(string name) => Environment.GetEnvironmentVariable(name);

        [TestMethod]
        [Ignore("Live-model smoke: provide F8_TEST_ONNX_* and remove [Ignore] to run.")]
        [TestCategory("LiveModel")]
        public async Task Onnx_Bge_EmbedsRealText()
        {
            var model = Env("F8_TEST_ONNX_MODEL");
            var vocab = Env("F8_TEST_ONNX_VOCAB");
            if (String.IsNullOrEmpty(model) || String.IsNullOrEmpty(vocab))
            {
                Assert.Inconclusive("F8_TEST_ONNX_MODEL / F8_TEST_ONNX_VOCAB not set.");
            }

            using var generator = new OnnxEmbeddingGenerator(new Fallen8EmbeddingOptions.OnnxOptions
            {
                ModelPath = model,
                VocabPath = vocab
            });
            await AssertBackendContract(generator, Int32.Parse(Env("F8_TEST_ONNX_DIM") ?? "384"));
        }

        [TestMethod]
        [Ignore("Live-model smoke: provide F8_TEST_GGUF_* and remove [Ignore] to run.")]
        [TestCategory("LiveModel")]
        public async Task LLamaSharp_Gguf_EmbedsRealText()
        {
            var model = Env("F8_TEST_GGUF_MODEL");
            if (String.IsNullOrEmpty(model))
            {
                Assert.Inconclusive("F8_TEST_GGUF_MODEL not set.");
            }

            using var generator = new LLamaSharpEmbeddingGenerator(new Fallen8EmbeddingOptions.LLamaSharpOptions
            {
                ModelPath = model
            });
            await AssertBackendContract(generator, Int32.Parse(Env("F8_TEST_GGUF_DIM") ?? "1024"));
        }

        [TestMethod]
        [Ignore("Live-endpoint smoke: provide F8_TEST_OLLAMA_* and remove [Ignore] to run.")]
        [TestCategory("LiveModel")]
        public async Task Ollama_Endpoint_EmbedsRealText()
        {
            var endpoint = Env("F8_TEST_OLLAMA_ENDPOINT");
            if (String.IsNullOrEmpty(endpoint))
            {
                Assert.Inconclusive("F8_TEST_OLLAMA_ENDPOINT not set.");
            }

            using var generator = EmbeddingBackendFactoryAccessor.CreateOllama(endpoint, Env("F8_TEST_OLLAMA_MODEL") ?? "bge-m3");
            await AssertBackendContract(generator, Int32.Parse(Env("F8_TEST_OLLAMA_DIM") ?? "1024"));
        }

        /// <summary>Reaches the internal factory's Ollama branch without widening its visibility.</summary>
        private static class EmbeddingBackendFactoryAccessor
        {
            internal static IEmbeddingGenerator<string, Embedding<float>> CreateOllama(string endpoint, string model)
            {
                return new OllamaSharp.OllamaApiClient(new Uri(endpoint), model);
            }
        }
    }
}
