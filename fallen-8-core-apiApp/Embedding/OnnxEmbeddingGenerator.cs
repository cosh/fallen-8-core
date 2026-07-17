// MIT License
//
// OnnxEmbeddingGenerator.cs
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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using NoSQL.GraphDB.App.Configuration;

namespace NoSQL.GraphDB.App.Embedding
{
    /// <summary>
    ///   Self-contained in-process backend (feature embedding-provider): a BERT-family ONNX
    ///   encoder (the bge models are the tested reference) via <c>Microsoft.ML.OnnxRuntime</c> +
    ///   <c>Microsoft.ML.Tokenizers</c> WordPiece. CPU-only by design (spec non-goal: the GPU
    ///   stays with the Ollama sidecar). Construction loads the model - callers defer
    ///   construction until first use (the provider's lazy).
    /// </summary>
    public sealed class OnnxEmbeddingGenerator : IEmbeddingGenerator<String, Embedding<Single>>
    {
        private readonly InferenceSession _session;
        private readonly BertTokenizer _tokenizer;
        private readonly Fallen8EmbeddingOptions.OnnxOptions _options;
        private readonly Boolean _meanPooling;

        public OnnxEmbeddingGenerator(Fallen8EmbeddingOptions.OnnxOptions options)
        {
            _options = options;

            if (String.IsNullOrWhiteSpace(options.ModelPath) || !File.Exists(options.ModelPath))
            {
                throw new FileNotFoundException(
                    "Fallen8:Embedding:Onnx:ModelPath must point at an existing .onnx model file; nothing is downloaded.",
                    options.ModelPath);
            }

            if (String.IsNullOrWhiteSpace(options.VocabPath) || !File.Exists(options.VocabPath))
            {
                throw new FileNotFoundException(
                    "Fallen8:Embedding:Onnx:VocabPath must point at the model's WordPiece vocab file.",
                    options.VocabPath);
            }

            _meanPooling = String.Equals(options.Pooling, "Mean", StringComparison.OrdinalIgnoreCase);
            _tokenizer = BertTokenizer.Create(options.VocabPath);
            _session = new InferenceSession(options.ModelPath);
        }

        public Task<GeneratedEmbeddings<Embedding<Single>>> GenerateAsync(IEnumerable<String> values,
            EmbeddingGenerationOptions options = null, CancellationToken cancellationToken = default)
        {
            // ONNX inference is CPU-bound and synchronous; per-text loop keeps memory flat
            // (request batches are already bounded by MaxBatchSize).
            var result = new GeneratedEmbeddings<Embedding<Single>>();
            foreach (var value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(new Embedding<Single>(EmbedOne(value ?? String.Empty)));
            }

            return Task.FromResult(result);
        }

        private Single[] EmbedOne(String text)
        {
            var ids = _tokenizer.EncodeToIds(text, _options.MaxTokens, out _, out _);
            var sequenceLength = ids.Count;

            var inputIds = new DenseTensor<Int64>(new[] { 1, sequenceLength });
            var attentionMask = new DenseTensor<Int64>(new[] { 1, sequenceLength });
            var tokenTypeIds = new DenseTensor<Int64>(new[] { 1, sequenceLength });
            for (var i = 0; i < sequenceLength; i++)
            {
                inputIds[0, i] = ids[i];
                attentionMask[0, i] = 1;
                tokenTypeIds[0, i] = 0;
            }

            // Feed exactly the inputs the model declares (bge exports differ in whether
            // token_type_ids is present).
            var inputs = new List<NamedOnnxValue>();
            foreach (var name in _session.InputMetadata.Keys)
            {
                switch (name)
                {
                    case "input_ids": inputs.Add(NamedOnnxValue.CreateFromTensor(name, inputIds)); break;
                    case "attention_mask": inputs.Add(NamedOnnxValue.CreateFromTensor(name, attentionMask)); break;
                    case "token_type_ids": inputs.Add(NamedOnnxValue.CreateFromTensor(name, tokenTypeIds)); break;
                    default:
                        throw new InvalidOperationException(String.Format(
                            "The ONNX model declares an unsupported input '{0}'; supported: input_ids, attention_mask, token_type_ids.", name));
                }
            }

            using var outputs = _session.Run(inputs);
            var output = outputs.First().AsTensor<Single>();
            var dimensions = output.Dimensions;

            Single[] vector;
            if (dimensions.Length == 3)
            {
                // [batch, sequence, hidden] token embeddings: CLS (the bge contract) or mean.
                var hidden = dimensions[2];
                vector = new Single[hidden];

                if (_meanPooling)
                {
                    for (var t = 0; t < sequenceLength; t++)
                    {
                        for (var h = 0; h < hidden; h++)
                        {
                            vector[h] += output[0, t, h];
                        }
                    }
                    for (var h = 0; h < hidden; h++)
                    {
                        vector[h] /= sequenceLength;
                    }
                }
                else
                {
                    for (var h = 0; h < hidden; h++)
                    {
                        vector[h] = output[0, 0, h];
                    }
                }
            }
            else if (dimensions.Length == 2)
            {
                // [batch, hidden] - the export pooled already.
                var hidden = dimensions[1];
                vector = new Single[hidden];
                for (var h = 0; h < hidden; h++)
                {
                    vector[h] = output[0, h];
                }
            }
            else
            {
                throw new InvalidOperationException(String.Format(
                    "The ONNX model produced a rank-{0} output; expected rank 2 or 3.", dimensions.Length));
            }

            if (_options.Normalize)
            {
                var norm = 0d;
                for (var h = 0; h < vector.Length; h++)
                {
                    norm += (Double)vector[h] * vector[h];
                }
                norm = Math.Sqrt(norm);
                if (norm > 0)
                {
                    for (var h = 0; h < vector.Length; h++)
                    {
                        vector[h] = (Single)(vector[h] / norm);
                    }
                }
            }

            return vector;
        }

        public Object GetService(Type serviceType, Object serviceKey = null)
        {
            return serviceType?.IsInstanceOfType(this) == true ? this : null;
        }

        public void Dispose()
        {
            _session.Dispose();
        }
    }
}
