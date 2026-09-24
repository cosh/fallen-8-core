// MIT License
//
// OpenAIEmbeddingGenerator.cs
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
using System.ClientModel;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.App.Helper;

namespace NoSQL.GraphDB.App.Embedding
{
    /// <summary>
    ///   The OpenAI <c>/v1/embeddings</c> backend (feature model-providers, FR-4): the official
    ///   OpenAI SDK's <c>EmbeddingClient</c> behind a hand-written
    ///   <see cref="IEmbeddingGenerator{TInput,TEmbedding}" /> adapter.
    ///
    ///   <para><b>Why hand-written.</b> <c>Microsoft.Extensions.AI.OpenAI</c> supplies an adapter
    ///   and it is rejected on ONE point: the response's <c>index</c> field is the only thing that
    ///   says which input a vector belongs to, that adapter drops it, and
    ///   <see cref="Embedding{T}" /> has no field left to recover it from. A response that arrives
    ///   permuted then pairs element A with element C's vector, with no exception and no log line -
    ///   the graph is searchable and confidently wrong about its own neighbours. So the two guards
    ///   below are the reason this type exists at all: the vectors are ordered BY
    ///   <c>index</c>, and a response whose indexes are not exactly the inputs' positions is
    ///   refused rather than paired.</para>
    ///
    ///   <para><b>Never truncated.</b> The route has no truncation parameter to switch off: an
    ///   input over the model's token ceiling is refused by the service, which is exactly the
    ///   posture <see cref="Fallen8EmbeddingProvider" /> asks the Ollama-protocol backends for with
    ///   <c>truncate: false</c>. The refusal is translated here so it names a Fallen-8 setting.</para>
    ///
    ///   <para>It validates nothing about the vectors themselves - dimension, finiteness, norm and
    ///   the total count against what was asked for all belong to
    ///   <see cref="Fallen8EmbeddingProvider" />, which owns them for every backend. A second copy
    ///   here would answer one fault with two different sentences.</para>
    /// </summary>
    internal sealed class OpenAIEmbeddingGenerator : IEmbeddingGenerator<String, Embedding<Single>>
    {
        /// <summary>
        ///   OpenAI's own per-request input cap, which its SDK does NOT enforce (2049 inputs go out
        ///   as one request and the service rejects them). A transport limit of this provider, so a
        ///   larger batch is split here and re-joined in order.
        ///   <para>Deliberately NOT <c>Fallen8:Embedding:MaxBatchSize</c>: that setting has one
        ///   reader per surface already (the <c>/embedding</c> endpoints refuse an over-cap request,
        ///   document ingestion chunks before it reaches a provider), and a second reader inside a
        ///   transport would let one number mean two different things.</para>
        /// </summary>
        internal const Int32 MaxInputsPerRequest = 2048;

        private readonly HttpClient _http;
        private readonly OpenAI.Embeddings.EmbeddingClient _client;
        private readonly OpenAI.Embeddings.EmbeddingGenerationOptions _generationOptions;
        private readonly String _providerName;

        /// <param name="target">The endpoint, model and credential. Already validated by the factory.</param>
        /// <param name="dimension">The declared output width (<c>Fallen8:Embedding:Dimension</c>),
        /// asked for on the wire so a <c>text-embedding-3-*</c> model returns that size rather than
        /// its native one. Whether the answer HAS that width is the provider's check, not this one.</param>
        /// <param name="logger">Where the retry handler's per-wait lines go.</param>
        /// <param name="handler">A test-supplied transport handler; used verbatim when non-null, so a
        /// test exercises the real client composition rather than a stand-in.</param>
        internal OpenAIEmbeddingGenerator(RemoteModelTarget target, Int32 dimension, ILogger logger,
            HttpMessageHandler handler = null)
        {
            // Which statuses mean "ask again" is the OpenAI protocol's answer, not this generator's,
            // so it comes from the one home the chat client reads too.
            _http = RemoteModelHttpClient.Create(
                new RemoteModelRetryHandler(target.ProviderName, target.Model, logger,
                    RemoteModelHttpClient.OpenAIRetryable),
                handler);

            // The credential is attached once, by the SDK, from the ApiKeyCredential. Everything
            // else about how an OpenAI-protocol client is composed onto our transport - the route
            // prefix, and switching off both of the SDK's own budgets - lives in
            // RemoteModelHttpClient, so the chat client and this one cannot drift apart.
            _client = new OpenAI.Embeddings.EmbeddingClient(target.Model,
                new ApiKeyCredential(target.ApiKey),
                RemoteModelHttpClient.OpenAIOptions(target, _http));

            _generationOptions = new OpenAI.Embeddings.EmbeddingGenerationOptions
            {
                // 0 is "not configured", and asking for zero components is a request the service
                // refuses with a sentence about the wrong thing.
                Dimensions = dimension > 0 ? dimension : (Int32?)null
            };

            _providerName = target.ProviderName;
        }

        public async Task<GeneratedEmbeddings<Embedding<Single>>> GenerateAsync(IEnumerable<String> values,
            Microsoft.Extensions.AI.EmbeddingGenerationOptions options = null,
            CancellationToken cancellationToken = default)
        {
            var inputs = values as IReadOnlyList<String> ?? values.ToList();
            var result = new GeneratedEmbeddings<Embedding<Single>>(inputs.Count);
            UsageDetails usage = null;

            for (var offset = 0; offset < inputs.Count; offset += MaxInputsPerRequest)
            {
                var chunk = new List<String>(Math.Min(MaxInputsPerRequest, inputs.Count - offset));
                for (var i = offset; i < inputs.Count && chunk.Count < MaxInputsPerRequest; i++)
                {
                    chunk.Add(inputs[i]);
                }

                OpenAI.Embeddings.OpenAIEmbeddingCollection collection;
                try
                {
                    collection = await _client.GenerateEmbeddingsAsync(chunk, _generationOptions, cancellationToken);
                }
                catch (ClientResultException ex)
                {
                    var refused = ex.Status == 400 ? ErrorOf(ex) : default;
                    if (refused.Code == "context_length_exceeded")
                    {
                        throw new InvalidOperationException(OverLongInput(refused.Reason), ex);
                    }

                    // Everything else says only which provider answered what, because the SDK's own
                    // message is the status line plus the response body VERBATIM and this sentence
                    // becomes the problem-detail of a 503 that is anonymous on a keyless instance
                    // (and, on the ingestion path, a property persisted on the Document). A body can
                    // carry the credential; a gateway's body can carry anything.
                    throw RemoteModelHttpClient.Failed(_providerName, ex);
                }

                if (collection.Count != chunk.Count)
                {
                    throw new InvalidOperationException(String.Format(
                        "The embedding backend answered {0} input(s) with {1} vector(s).",
                        chunk.Count, collection.Count));
                }

                var position = 0;
                foreach (var embedding in collection.OrderBy(e => e.Index))
                {
                    if (embedding.Index != position)
                    {
                        throw new InvalidOperationException(String.Format(
                            "The embedding backend answered {0} input(s) with a vector for input {1} "
                            + "where input {2} was expected, so no vector can be paired with the text "
                            + "it describes.",
                            chunk.Count, embedding.Index, position));
                    }

                    result.Add(new Embedding<Single>(embedding.ToFloats()));
                    position++;
                }

                if (collection.Usage is { } chunkUsage)
                {
                    // An embeddings route has no output-token concept, so OutputTokenCount stays
                    // null rather than being filled with a number that means something else.
                    usage ??= new UsageDetails();
                    usage.InputTokenCount = (usage.InputTokenCount ?? 0L) + chunkUsage.InputTokenCount;
                    usage.TotalTokenCount = (usage.TotalTokenCount ?? 0L) + chunkUsage.TotalTokenCount;
                }
            }

            result.Usage = usage;
            return result;
        }

        /// <summary>
        ///   The refusal an operator can act on. The service's own reason carries the numbers (the
        ///   model's ceiling and what was requested) and is kept; what it cannot know is which
        ///   Fallen-8 surface produced the input.
        ///   <para>This is deliberately the ONE place any provider text reaches a caller, and it is
        ///   narrow on purpose: only the <c>message</c> of an error whose <c>code</c> is exactly
        ///   <c>context_length_exceeded</c>, which is the model-serving layer counting tokens. Every
        ///   other failure is reduced to its status by
        ///   <see cref="RemoteModelHttpClient.Describe" />, because a response body is not safe to
        ///   repeat in general.</para>
        /// </summary>
        private static String OverLongInput(String reason)
        {
            return String.Format(
                "One input exceeds the embedding model's token ceiling, so the whole request was "
                + "refused: {0} Nothing is half-embedded - lower Fallen8:Ingestion:ChunkMaxChars for "
                + "documents, or shorten the text for /embedding and semantic queryText.",
                reason ?? "the model reported no further detail.");
        }

        /// <summary>
        ///   The provider's error code and reason, when the body has the shape OpenAI documents.
        ///   Both <c>null</c> when it does not, in which case the SDK's own exception stands
        ///   unchanged: a gateway answering 400 for some other cause must not be reported as an
        ///   over-long input. Only these two fields are read, so nothing else the body carries can
        ///   reach an operator-facing message.
        /// </summary>
        private static (String Code, String Reason) ErrorOf(ClientResultException ex)
        {
            try
            {
                var body = ex.GetRawResponse()?.Content;
                if (body == null)
                {
                    return (null, null);
                }

                using var document = JsonDocument.Parse(body.ToMemory());
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("error", out var error)
                    || error.ValueKind != JsonValueKind.Object)
                {
                    return (null, null);
                }

                return (StringOf(error, "code"), StringOf(error, "message"));
            }
            catch (JsonException)
            {
                return (null, null);
            }
        }

        private static String StringOf(JsonElement element, String name)
        {
            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        public Object GetService(Type serviceType, Object serviceKey = null)
        {
            return serviceType?.IsInstanceOfType(this) == true ? this : null;
        }

        /// <summary>Releases the owned transport. Neither the SDK's client nor its transport disposes
        /// an injected <see cref="HttpClient" />, so this type owns it; the DI singleton is disposed
        /// at shutdown.</summary>
        public void Dispose()
        {
            _http.Dispose();
        }
    }
}
