// MIT License
//
// RestSeam.cs
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
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NoSQL.GraphDB.Rest
{
    /// <summary>
    ///   A request that produced no answer at all, and which of the two reasons it was. They are not
    ///   interchangeable: unreachable says nothing was applied, while a deadline that expired first leaves
    ///   open that the target applied the request and only the answer was lost.
    /// </summary>
    public enum RestSendFailure
    {
        /// <summary>The transport failed.</summary>
        Unreachable,

        /// <summary>This client's own deadline expired before the target answered.</summary>
        TimedOut,
    }

    /// <summary>
    ///   Names a no-answer outcome in the CALLING deployable's own failure vocabulary. Each consumer's
    ///   exception types and wording are user-facing and deliberately different, so this seam classifies
    ///   and the caller names.
    /// </summary>
    public delegate Exception RestSendFailureNaming(RestSendFailure failure, Exception cause);

    /// <summary>
    ///   Names a non-success status in the calling deployable's own failure vocabulary. Asynchronous
    ///   because naming it reads the response body (a problem+json title, a plain-string detail).
    /// </summary>
    public delegate Task<Exception> RestRefusalNaming(HttpResponseMessage response, CancellationToken cancellationToken);

    /// <summary>
    ///   The one home for the REST-client seam of the deployables that may reach a Fallen-8 only over its
    ///   public HTTP contract (<c>fallen-8-mcp</c>, <c>fallen-8-integrations</c>). It references neither the
    ///   engine nor the API app, which is what lets both consumers keep that rule while sharing this.
    ///
    ///   <para>It owns the CLASSIFICATION of an answer and nothing about the vocabulary: unreachable, timed
    ///   out, a status the caller interprets, or a body that is absent. Two of those are the reason it is
    ///   shared at all.</para>
    ///
    ///   <para>ABSENT BODY: a 204, an empty body and a literal <c>null</c> document all mean "no such
    ///   thing" on this REST contract rather than a failure, so a caller must not be able to tell them
    ///   apart. Every getter answers a missing element that way.</para>
    ///
    ///   <para>TIMEOUT VERSUS CANCELLATION: <see cref="HttpClient"/> reports its OWN deadline as a
    ///   <see cref="TaskCanceledException"/>, which IS an <see cref="OperationCanceledException"/>. The
    ///   TOKEN decides which of the two happened, never the exception type: letting a client-side timeout
    ///   escape as a cancellation presents "the target was too slow" to every layer above as "the caller
    ///   walked away", and those two license opposite statements about what was written, while turning a
    ///   cancellation the caller DID ask for into this seam's own failure is the same error mirrored.</para>
    /// </summary>
    public static class RestSeam
    {
        /// <summary>
        ///   The wire serialization both consumers speak against the same REST contract. One instance, and
        ///   the platform seals it: a <see cref="JsonSerializerOptions"/> becomes immutable the moment it has
        ///   been used to serialize, so past the first request no consumer can retune the other's wire
        ///   format. It is NOT sealed up front, because doing so demands an explicit type-info resolver and
        ///   that is a reflection dependency a future trimmed host would pay for nothing.
        /// </summary>
        public static JsonSerializerOptions JsonOptions { get; } = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        /// <summary>
        ///   Sends one request and classifies the two outcomes that produce no response, leaving a
        ///   cancellation the caller asked for to propagate as itself.
        /// </summary>
        /// <param name="client">The configured client; its base address and credential headers are the
        /// caller's business.</param>
        /// <param name="method">The HTTP method.</param>
        /// <param name="relativePath">The path relative to the client's base address.</param>
        /// <param name="body">The JSON request body, or null for a request that carries none.</param>
        /// <param name="naming">How the calling deployable names an answer that never came.</param>
        /// <param name="cancellationToken">The caller's token, and the only thing that distinguishes its
        /// cancellation from this client's deadline.</param>
        /// <returns>The response, which the CALLER owns and disposes.</returns>
        public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method,
            String relativePath, Object? body, RestSendFailureNaming naming, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(naming);

            // The default completion option buffers the whole response before this returns, so the request
            // (and the content it owns) is finished with by then.
            using var request = new HttpRequestMessage(method, relativePath);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, mediaType: null, JsonOptions);
            }

            try
            {
                return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw naming(RestSendFailure.Unreachable, ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw naming(RestSendFailure.TimedOut, ex);
            }
        }

        /// <summary>
        ///   Sends one request and returns its body text, or null when the answer carries no body (the
        ///   absent-body convention on <see cref="RestSeam"/>). A non-success status is handed to
        ///   <paramref name="refusal"/> and thrown as whatever that names it.
        /// </summary>
        public static async Task<String?> SendForBodyAsync(HttpClient client, HttpMethod method,
            String relativePath, Object? body, RestSendFailureNaming naming, RestRefusalNaming refusal,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(refusal);

            var response = await SendAsync(client, method, relativePath, body, naming, cancellationToken)
                .ConfigureAwait(false);

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw await refusal(response, cancellationToken).ConfigureAwait(false);
                }

                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    return null;
                }

                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return String.IsNullOrWhiteSpace(text) || text.Trim() == "null" ? null : text;
            }
        }
    }
}
