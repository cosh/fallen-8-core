// MIT License
//
// Fallen8EmbeddingProvider.cs
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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.Core.Index.Vector;

namespace NoSQL.GraphDB.App.Embedding
{
    /// <summary>
    ///   THE thin Fallen-8 wrapper around the configured
    ///   <see cref="IEmbeddingGenerator{TInput,TEmbedding}" /> (feature embedding-provider,
    ///   FR-1). The abstraction IS <c>Microsoft.Extensions.AI</c>'s - this type exists ONLY to
    ///   add (a) the required model-identity metadata and (b) index add-contract validation at
    ///   the provider boundary. Everything else (batching semantics, middleware, telemetry
    ///   composition) belongs to that ecosystem, so swapping the backend stays a configuration
    ///   change.
    ///
    ///   <para>Model load is LAZY (FR-2): nothing resolves or loads until the first
    ///   generation. A failed load latches with its reason (the <see cref="Lazy{T}" />
    ///   publication caches the exception), so a broken model path answers 503 without a
    ///   retry storm; a transient per-call backend failure (e.g. the Ollama sidecar being
    ///   down) is NOT latched and maps to 503 per call.</para>
    /// </summary>
    public sealed class Fallen8EmbeddingProvider
    {
        private readonly Fallen8EmbeddingOptions _options;
        private readonly Lazy<IEmbeddingGenerator<String, Embedding<Single>>> _generator;

        /// <summary>Latched fatal validation failure (e.g. the first output's dimension
        /// contradicts the configuration) - the provider stays down until config changes.</summary>
        private volatile String _latchedFailure;

        public Fallen8EmbeddingProvider(IOptions<Fallen8EmbeddingOptions> options,
            Lazy<IEmbeddingGenerator<String, Embedding<Single>>> generator)
        {
            _options = options.Value;
            _generator = generator;
            Identity = BuildIdentity(_options);
        }

        /// <summary>Whether the capability flag is on.</summary>
        public Boolean IsEnabled => _options.Enabled;

        /// <summary>The backend selector (config value).</summary>
        public String Backend => _options.Backend;

        /// <summary>Whether the backend has been created (lazy load happened).</summary>
        public Boolean IsLoaded => _generator.IsValueCreated && _latchedFailure == null;

        /// <summary>The declared model identity (validated against real output on use).</summary>
        public EmbeddingModelIdentity Identity
        {
            get;
        }

        private static EmbeddingModelIdentity BuildIdentity(Fallen8EmbeddingOptions options)
        {
            var metric = options.IntendedMetric switch
            {
                null or "Cosine" => VectorDistanceMetric.Cosine,
                "DotProduct" => VectorDistanceMetric.DotProduct,
                "L2" => VectorDistanceMetric.L2,
                _ => VectorDistanceMetric.Cosine
            };

            return new EmbeddingModelIdentity(options.ModelName ?? String.Empty, options.ModelVersion,
                options.Dimension, metric);
        }

        /// <summary>
        ///   Embeds a batch and validates every vector against the index add contract at the
        ///   provider boundary (FR-8): finite components, exactly <see cref="Identity" />'s
        ///   dimension, non-zero norm when the intended metric is Cosine. Throws
        ///   <see cref="EmbeddingProviderUnavailableException" /> (503) or
        ///   <see cref="EmbeddingProviderOutputException" /> (502); never coerces.
        /// </summary>
        public async Task<Single[][]> EmbedAsync(IReadOnlyList<String> texts, CancellationToken cancellationToken)
        {
            if (!IsEnabled)
            {
                throw new EmbeddingProviderUnavailableException("The embedding provider is disabled (Fallen8:Embedding:Enabled).");
            }

            if (_latchedFailure != null)
            {
                throw new EmbeddingProviderUnavailableException(_latchedFailure);
            }

            IEmbeddingGenerator<String, Embedding<Single>> generator;
            try
            {
                generator = _generator.Value;
            }
            catch (Exception ex)
            {
                // Lazy(ExecutionAndPublication) caches the creation exception: the load
                // failure is latched by construction, every later call lands here cheaply.
                throw new EmbeddingProviderUnavailableException(
                    String.Format("The embedding backend '{0}' failed to initialize: {1}", _options.Backend, ex.Message), ex);
            }

            GeneratedEmbeddings<Embedding<Single>> generated;
            try
            {
                generated = await generator.GenerateAsync(texts, options: null, cancellationToken);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // Transient by assumption (e.g. the Ollama sidecar is down): 503, NOT latched.
                throw new EmbeddingProviderUnavailableException(
                    String.Format("The embedding backend '{0}' failed to generate: {1}", _options.Backend, ex.Message), ex);
            }

            if (generated == null || generated.Count != texts.Count)
            {
                throw new EmbeddingProviderOutputException(String.Format(
                    "The embedding backend returned {0} vector(s) for {1} input(s).", generated?.Count ?? 0, texts.Count));
            }

            var vectors = new Single[generated.Count][];
            for (var i = 0; i < generated.Count; i++)
            {
                var vector = generated[i].Vector.ToArray();

                if (vector.Length != Identity.Dimension)
                {
                    // A dimension contradiction is a CONFIGURATION fault, permanent for this
                    // process: latch it so the operator sees one clear failure mode.
                    _latchedFailure = String.Format(
                        "The embedding backend produced dimension {0}, but Fallen8:Embedding:Dimension declares {1}. " +
                        "Fix the configuration; output is never truncated or padded.",
                        vector.Length, Identity.Dimension);
                    throw new EmbeddingProviderUnavailableException(_latchedFailure);
                }

                if (VectorIndex.HasNonFiniteComponent(vector))
                {
                    throw new EmbeddingProviderOutputException(
                        "The embedding backend produced NaN or Infinity components.");
                }

                if (Identity.IntendedMetric == VectorDistanceMetric.Cosine && VectorIndex.IsZeroNorm(vector))
                {
                    throw new EmbeddingProviderOutputException(
                        "The embedding backend produced a zero-norm vector, which cannot rank under Cosine.");
                }

                vectors[i] = vector;
            }

            return vectors;
        }

        /// <summary>Applies the configured query prefix (query-time embeddings only).</summary>
        public String ApplyQueryPrefix(String text)
        {
            return String.IsNullOrEmpty(_options.QueryPrefix) ? text : _options.QueryPrefix + text;
        }
    }
}
