// MIT License
//
// Fallen8EmbeddingProvider.cs
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

        /// <summary>The per-call backend options; <c>null</c> for every backend that does not speak
        /// Ollama's protocol. See <see cref="BuildGenerationOptions" /> - it carries exactly one
        /// thing.</summary>
        private readonly EmbeddingGenerationOptions _generationOptions;

        /// <summary>Latched fatal validation failure (e.g. the first output's dimension
        /// contradicts the configuration) - the provider stays down until config changes.</summary>
        private volatile String _latchedFailure;

        public Fallen8EmbeddingProvider(IOptions<Fallen8EmbeddingOptions> options,
            Lazy<IEmbeddingGenerator<String, Embedding<Single>>> generator)
        {
            _options = options.Value;
            _generator = generator;
            _generationOptions = BuildGenerationOptions(_options.Backend);
            Identity = BuildIdentity(_options);

            // A typo'd metric must not silently become Cosine INSIDE the identity stamp that
            // FR-8 compares - latch the provider (503 with the reason) instead of guessing.
            // Latched rather than thrown: the provider is constructed on DI resolution, and a
            // config typo must not turn /statistics into a 500.
            if (!IsKnownMetric(_options.IntendedMetric))
            {
                _latchedFailure = String.Format(
                    "'{0}' is not a valid Fallen8:Embedding:IntendedMetric. Expected Cosine, DotProduct or L2.",
                    _options.IntendedMetric);
            }
        }

        private static Boolean IsKnownMetric(String metric)
        {
            return metric is null or "Cosine" or "DotProduct" or "L2";
        }

        /// <summary>
        ///   THE one home for what the Ollama-protocol backends are asked to do per call, and it is
        ///   one thing: <c>truncate: false</c>.
        ///
        ///   <para><b>Why.</b> That flag defaults to TRUE on both the local Ollama sidecar and on
        ///   Nahil, and what it really means is "shorten anything that does not fit, and answer as
        ///   though it had fitted". Neither backend honours the 8192-token window <c>bge-m3</c>'s own
        ///   <c>/api/show</c> advertises - measured, both stop at 2048 - so an over-long chunk came
        ///   back as a perfectly valid-looking 1024-dimension vector describing only its first ~2046
        ///   tokens. Nothing distinguished that from a correct embedding: not the response, not the
        ///   dimension check below, not any log line. The chunk was indexed, searchable, and quietly
        ///   wrong about its own tail. With the flag off the backend refuses instead, which is the
        ///   whole point - a failed ingest is re-runnable, a silently truncated vector is not even
        ///   visible. <c>Fallen8:Ingestion:ChunkMaxChars</c> is what keeps ordinary documents away
        ///   from that refusal; this is the backstop for everything else.</para>
        ///
        ///   <para><b>How.</b> Via <see cref="EmbeddingGenerationOptions.AdditionalProperties" />,
        ///   which OllamaSharp's abstraction mapper binds onto <c>EmbedRequest.Truncate</c> by name
        ///   (verified against 5.4.27: the key reaches the request body as <c>"truncate": false</c>,
        ///   case-insensitively, while an unrecognised key is dropped rather than passed through).
        ///   <see cref="EmbeddingGenerationOptions.RawRepresentationFactory" /> reads like the
        ///   intended route and is IGNORED by that mapper - it produced a body with no
        ///   <c>truncate</c> member at all, i.e. the silent-truncation default, which is exactly the
        ///   failure this method exists to remove. Re-check both on an OllamaSharp upgrade.</para>
        ///
        ///   <para>The in-process backends get <c>null</c>: ONNX truncates deliberately at
        ///   <c>Fallen8:Embedding:Onnx:MaxTokens</c> (operator-chosen and documented as such), and
        ///   neither it nor LLamaSharp reads these options at all.</para>
        ///
        ///   <para><c>OpenAI</c> gets <c>null</c> too, and the omission is not a gap: <c>truncate</c>
        ///   is an Ollama-protocol key, and <c>/v1/embeddings</c> has no truncation knob of any name
        ///   to switch off. It refuses an over-long input instead, which is the same promise kept by
        ///   the service rather than by a flag - see
        ///   <see cref="OpenAIEmbeddingGenerator" />.</para>
        /// </summary>
        private static EmbeddingGenerationOptions BuildGenerationOptions(String backend)
        {
            if (backend is not ("Ollama" or "Nahil"))
            {
                return null;
            }

            return new EmbeddingGenerationOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary { ["truncate"] = false }
            };
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

            // Fallen8:Embedding:TimeoutSeconds is the single deadline on the call (the Ollama
            // transport is built without one). Before this budget existed, the backend's own
            // undocumented 100s transport timeout was the only bound, and the TaskCanceledException
            // it raised matched neither catch below - it escaped as an unhandled HTTP 500 and, on
            // the ingestion path, left the Document stuck at "processing" forever.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            GeneratedEmbeddings<Embedding<Single>> generated;
            try
            {
                generated = await generator.GenerateAsync(texts, _generationOptions, timeoutCts.Token);
            }
            catch (Helper.ModelRetryTimeoutException ex)
            {
                // A remote backend spent the whole budget asking to be retried - warming up, rate
                // limited or overloaded. Same 503 as any other backend that is not usable right
                // now, with the reason it kept giving named, and a caller who went away still gets
                // their cancellation rather than a fault report.
                cancellationToken.ThrowIfCancellationRequested();
                throw new EmbeddingProviderUnavailableException(String.Format(
                    "The embedding backend '{0}' did not respond within Fallen8:Embedding:TimeoutSeconds ({1}s). {2}",
                    _options.Backend, _options.TimeoutSeconds, ex.Message));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Any cancellation that is not the CALLER's is a backend timeout. 503 like every
                // other "not usable right now": the embedding contract deliberately has no 504
                // (EmbeddingProviderProblem.Map is the single home for that decision), so widening
                // it would ripple through /embedding, /path, /subgraph and /documents/search for no
                // caller benefit. Never latched - a slow batch says nothing about model identity.
                throw new EmbeddingProviderUnavailableException(String.Format(
                    "The embedding backend '{0}' did not respond within Fallen8:Embedding:TimeoutSeconds ({1}s).",
                    _options.Backend, _options.TimeoutSeconds));
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // Transient by assumption (e.g. the Ollama sidecar is down): 503, NOT latched.
                throw new EmbeddingProviderUnavailableException(
                    String.Format("The embedding backend '{0}' failed to generate: {1}{2}",
                        _options.Backend, ex.Message, OverLongInputHint(ex)), ex);
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

        /// <summary>
        ///   Names what to change when the backend refuses an over-long input. That refusal exists
        ///   only because <see cref="BuildGenerationOptions" /> turns truncation off, and the
        ///   backend's own wording ("the input length exceeds the context length") says nothing
        ///   about which Fallen-8 setting produced the input - so this is a 503 "not usable right
        ///   now" that carries a fix, rather than an operator guessing which of three surfaces was
        ///   too long.
        ///   <para>Matching on the message is deliberate and degrades safely: the sentence belongs
        ///   to the backend, so if it ever changes the operator still gets the raw reason exactly as
        ///   before, only without the pointer.</para>
        /// </summary>
        private static String OverLongInputHint(Exception ex)
        {
            if (ex.Message == null
                || !ex.Message.Contains("exceeds the context length", StringComparison.OrdinalIgnoreCase))
            {
                return String.Empty;
            }

            // The backend's sentence ends without punctuation, so the two would otherwise run
            // together into one unreadable line.
            var separator = ex.Message.TrimEnd().EndsWith('.') ? " " : ". ";

            return separator
                + "One input exceeds the model's per-input token ceiling (2048 for bge-m3 on both the"
                + " Ollama sidecar and Nahil, whatever /api/show advertises). Fallen-8 asks the"
                + " backend NOT to truncate, so this is reported instead of a vector for part of the"
                + " input: lower Fallen8:Ingestion:ChunkMaxChars for documents, or shorten the text"
                + " for /embedding and semantic queryText.";
        }

        /// <summary>Applies the configured query prefix (query-time embeddings only).</summary>
        public String ApplyQueryPrefix(String text)
        {
            return String.IsNullOrEmpty(_options.QueryPrefix) ? text : _options.QueryPrefix + text;
        }
    }
}
