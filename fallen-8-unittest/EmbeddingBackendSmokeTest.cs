// MIT License
//
// EmbeddingBackendSmokeTest.cs
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
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Embedding;
using NoSQL.GraphDB.App.Helper;
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
    ///     F8_TEST_NAHIL_API_KEY / F8_TEST_NAHIL_ENDPOINT / F8_TEST_NAHIL_EMBED_MODEL (Nahil)
    ///     F8_TEST_OPENAI_API_KEY / F8_TEST_OPENAI_ENDPOINT / F8_TEST_OPENAI_EMBED_MODEL /
    ///     F8_TEST_OPENAI_EMBED_DIM (OpenAI)
    ///
    ///   <para>The <c>F8_TEST_</c> prefix is not decoration: the unprefixed forms are the compose
    ///   variables, so keying a smoke off one would make an ordinary <c>dotnet test</c> place live
    ///   billed calls on any machine with a working deployment.</para>
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

        [TestMethod]
        [Ignore("Live-endpoint smoke: set F8_TEST_NAHIL_API_KEY and remove [Ignore] to run.")]
        [TestCategory("LiveModel")]
        public async Task Nahil_Bge_EmbedsRealText()
        {
            var apiKey = Env("F8_TEST_NAHIL_API_KEY");
            if (String.IsNullOrEmpty(apiKey))
            {
                Assert.Inconclusive("F8_TEST_NAHIL_API_KEY not set.");
            }

            var endpoint = Env("F8_TEST_NAHIL_ENDPOINT") ?? "https://api.nahil.dev";
            var model = Env("F8_TEST_NAHIL_EMBED_MODEL") ?? "bge-m3:latest";
            var dimension = Int32.Parse(Env("F8_TEST_NAHIL_EMBED_DIM") ?? "1024");

            var connection = OllamaConnection.Nahil("Fallen8:Embedding:Nahil", endpoint, model, apiKey);
            using var generator = new OllamaSharp.OllamaApiClient(
                OllamaHttpClientFactory.CreateForProvider(connection, logger: null),
                connection.Model);
            await AssertBackendContract(generator, dimension);
        }

        /// <summary>
        ///   OpenAI is a DIFFERENT embedding function from the bge-m3 every other backend here
        ///   serves: a different dimension and a different identity stamp, so a graph does not move
        ///   between them without re-embedding. This smoke therefore proves only the contract every
        ///   backend owes - declared width, finite, non-zero, self-similar - against whatever model
        ///   and dimension the environment names.
        /// </summary>
        [TestMethod]
        [Ignore("Live-endpoint smoke: set F8_TEST_OPENAI_API_KEY and remove [Ignore] to run.")]
        [TestCategory("LiveModel")]
        public async Task OpenAI_TextEmbedding3_EmbedsRealText()
        {
            var apiKey = Env("F8_TEST_OPENAI_API_KEY");
            if (String.IsNullOrEmpty(apiKey))
            {
                Assert.Inconclusive("F8_TEST_OPENAI_API_KEY not set.");
            }

            var dimension = Int32.Parse(Env("F8_TEST_OPENAI_EMBED_DIM") ?? "1536");
            var options = new Fallen8EmbeddingOptions { Backend = "OpenAI", Dimension = dimension };
            options.OpenAI.Endpoint = Env("F8_TEST_OPENAI_ENDPOINT") ?? "https://api.openai.com";
            options.OpenAI.Model = Env("F8_TEST_OPENAI_EMBED_MODEL") ?? "text-embedding-3-small";
            options.OpenAI.ApiKey = apiKey;

            using var generator = EmbeddingBackendFactoryAccessor.Create(options);
            await AssertBackendContract(generator, dimension);
        }

        /// <summary>Reaches the internal factory without widening its visibility, so a smoke
        /// exercises the branch a deployment would take rather than a client it built itself.</summary>
        private static class EmbeddingBackendFactoryAccessor
        {
            internal static IEmbeddingGenerator<string, Embedding<float>> CreateOllama(string endpoint, string model)
            {
                return new OllamaSharp.OllamaApiClient(new Uri(endpoint), model);
            }

            internal static IEmbeddingGenerator<string, Embedding<float>> Create(Fallen8EmbeddingOptions options)
            {
                var create = typeof(Fallen8EmbeddingProvider).Assembly
                    .GetType("NoSQL.GraphDB.App.Embedding.EmbeddingBackendFactory")
                    .GetMethod("Create", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                try
                {
                    return (IEmbeddingGenerator<string, Embedding<float>>)create.Invoke(null, new object[] { options, null });
                }
                catch (System.Reflection.TargetInvocationException ex)
                {
                    throw ex.InnerException;
                }
            }
        }
    }
}
