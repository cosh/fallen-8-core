// MIT License
//
// RetryAfterHandler.cs
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
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace NoSQL.GraphDB.App.Helper
{
    /// <summary>
    ///   Asks a model provider again when it answered "ask again" rather than failing the caller's
    ///   first request. WHICH statuses mean that, and what each of them reads as, is the provider's
    ///   own answer (<see cref="ShouldRetry" /> / <see cref="Explain" />); the replay, the wait
    ///   schedule and the giving-up are the same everywhere and live here.
    ///
    ///   <para><b>Not every retryable status is worth retrying,</b> which a status alone cannot
    ///   say. A provider may reuse one of them for a refusal it already knows is permanent, so
    ///   <see cref="Refusal" /> gets a bounded look at the body and may fail the call at once
    ///   instead. Without it the caller's whole budget is spent re-asking a question the first
    ///   response had already answered, and the honest reason - which only the provider has - is
    ///   thrown away with the response it came in.</para>
    ///
    ///   <para><b>It owns no deadline, deliberately.</b> The caller's budget
    ///   (<c>Fallen8:Chat:TimeoutSeconds</c> / <c>Fallen8:Embedding:TimeoutSeconds</c>, applied as a
    ///   linked token) stays the single authoritative one and every wait here runs INSIDE it - which
    ///   is the same deadline rule <see cref="OllamaHttpClientFactory" /> states, honoured rather
    ///   than worked around. That is also why there is no retry-count cap: a count would either be
    ///   unreachable under the budget or cut the wait short of it, and it would make the honest
    ///   answer arrive at a time no configured value explains. What the budget cannot bound is a
    ///   single hostile <c>Retry-After</c>, so each individual wait is clamped
    ///   (<see cref="MaxWaitSeconds" />).</para>
    /// </summary>
    public abstract class RetryAfterHandler : DelegatingHandler
    {
        /// <summary>The ceiling on ONE wait. A provider asking for more than a minute at a time is
        /// either broken or hostile, and the caller's budget still bounds the total.</summary>
        public const Double MaxWaitSeconds = 60d;

        /// <summary>The first wait when the provider names no <c>Retry-After</c>.</summary>
        public const Double FirstBackoffSeconds = 2d;

        /// <summary>The ceiling on a BACKED-OFF wait, below <see cref="MaxWaitSeconds" /> because a
        /// a backend that told us nothing has not earned a longer pause than one that did.</summary>
        public const Double MaxBackoffSeconds = 30d;

        /// <summary>How much jitter the backoff adds, as a fraction of the wait: enough to spread a
        /// fleet of instances that all started waiting on the same cold model.</summary>
        private const Double JitterFraction = 0.25d;

        private readonly ILogger _logger;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        /// <param name="model">Named in the log lines and in the give-up message, because "the model
        /// was not resident" is only actionable if it says which.</param>
        /// <param name="logger">One information-level line per retry; never the credential.</param>
        /// <param name="delay">The wait itself. Injected so a test can assert the schedule this
        /// computes without spending it - the arithmetic is the behaviour, the sleeping is not.</param>
        protected RetryAfterHandler(String model, ILogger logger,
            Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            Model = model;
            _logger = logger;
            _delay = delay ?? ((wait, token) => Task.Delay(wait, token));
        }

        /// <summary>The model this transport asks for, for the log line and the give-up message.</summary>
        protected String Model
        {
            get;
        }

        /// <summary>How the provider is named to an operator. A display value, not a switch key.</summary>
        protected abstract String ProviderName
        {
            get;
        }

        /// <summary>Whether <paramref name="status" /> means "ask again" for this provider. A status
        /// that means anything else must reach the caller unchanged and immediately.</summary>
        protected abstract Boolean ShouldRetry(HttpStatusCode status);

        /// <summary>
        ///   How many bytes of a retryable response's BODY this provider needs in order to tell
        ///   "ask again" from "asking again will never work". Zero, the default, means the status is
        ///   the whole answer and no body is ever read.
        ///   <para>It exists because a status alone cannot always carry that difference:
        ///   <see cref="ShouldRetry" /> sees only a <see cref="HttpStatusCode" />, and one provider
        ///   reuses a single retryable status for both a wait and a permanent refusal
        ///   (<see cref="NahilWarmupRetryHandler" />). Bounded rather than "read the body" because
        ///   the body belongs to a service on the far side of a credential: a hostile or merely
        ///   enormous one must not become this process's memory.</para>
        /// </summary>
        protected virtual Int32 RefusalProbeBytes => 0;

        /// <summary>
        ///   The failure to raise INSTEAD of waiting, when <paramref name="bodyPrefix" /> shows the
        ///   provider has already answered the question a retry would ask; <c>null</c> to wait and
        ///   retry exactly as before. Only called when <see cref="RefusalProbeBytes" /> is positive.
        ///   <para>The returned exception reaches the caller as the provider's own failure, so it
        ///   should carry the provider's own sentence: what it names (a class no worker serves, a
        ///   model withdrawn from a catalog) is a fact only the provider has.</para>
        /// </summary>
        /// <param name="status">The retryable status that arrived.</param>
        /// <param name="bodyPrefix">Up to <see cref="RefusalProbeBytes" /> bytes of the body, decoded
        /// as UTF-8; empty when the body was absent or could not be read.</param>
        protected virtual Exception Refusal(HttpStatusCode status, String bodyPrefix)
        {
            return null;
        }

        /// <summary>What <paramref name="status" /> reads as to an operator. Retryable statuses stay
        /// distinguishable everywhere they surface, because they call for different actions.</summary>
        protected abstract String Explain(HttpStatusCode status);

        /// <summary>The failure to raise when the caller's budget ran out while we were still being
        /// told to ask again.</summary>
        protected abstract ModelRetryTimeoutException GaveUp(HttpStatusCode last, TimeSpan waited,
            Int32 retries, OperationCanceledException cancelled);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // Buffered up front and re-cloned per attempt: a request message cannot be sent twice,
            // and a content stream read once cannot be replayed, so the FIRST attempt already goes
            // out as a clone rather than leaving a retry to discover that.
            var body = request.Content == null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);

            var attempt = 0;
            var waited = TimeSpan.Zero;
            var last = default(HttpStatusCode);

            while (true)
            {
                attempt++;

                HttpResponseMessage response;
                try
                {
                    response = await base.SendAsync(Clone(request, body), cancellationToken);
                }
                catch (OperationCanceledException cancelled) when (waited > TimeSpan.Zero)
                {
                    // The budget ran out on a request we had already been told to retry, so the
                    // waiting IS the story - report it rather than a bare cancellation.
                    throw GaveUp(last, waited, attempt - 1, cancelled);
                }

                if (!ShouldRetry(response.StatusCode))
                {
                    return response;
                }

                last = response.StatusCode;

                // Read the body ONLY on a response already destined for the bin: this arm either
                // throws or disposes and retries, so consuming the content here can never hand a
                // half-read stream to a caller. The check comes before the wait for the whole
                // point of it - a refusal must not be paid for in the caller's budget first.
                if (RefusalProbeBytes > 0)
                {
                    var prefix = await ReadPrefixAsync(response, RefusalProbeBytes, cancellationToken);
                    if (Refusal(last, prefix) is { } refusal)
                    {
                        response.Dispose();
                        throw refusal;
                    }
                }

                var wait = WaitFor(response.Headers.RetryAfter, attempt);
                response.Dispose();

                // ONE line per retry, not per poll: a cold multi-gigabyte model can take minutes and
                // the operator needs to see progress, not a wall of identical lines.
                _logger?.LogInformation(
                    "{Provider} answered {Status} ({Reason}) for model {Model}; waiting {WaitSeconds:F1}s "
                    + "before attempt {Attempt} ({WaitedSeconds:F0}s waited so far).",
                    ProviderName, (Int32)last, Explain(last), Model, wait.TotalSeconds, attempt + 1,
                    waited.TotalSeconds);

                try
                {
                    await _delay(wait, cancellationToken);
                }
                catch (OperationCanceledException cancelled)
                {
                    throw GaveUp(last, waited, attempt, cancelled);
                }

                waited += wait;
            }
        }

        /// <summary>
        ///   How long to wait before the next attempt: what the provider asked for when it said, our
        ///   own backoff when it did not.
        ///
        ///   <para>A <c>Retry-After</c> in the past (or a zero/negative delta) falls back to the
        ///   backoff rather than retrying immediately: a server repeating a stale date would
        ///   otherwise turn this into a hot loop that spends the caller's whole budget on
        ///   round-trips.</para>
        /// </summary>
        public static TimeSpan WaitFor(System.Net.Http.Headers.RetryConditionHeaderValue retryAfter, Int32 attempt)
        {
            // Both header forms are honoured, and neither is parsed here: the typed accessor already
            // understands delta-seconds AND an HTTP-date, and reports null for a value it cannot
            // read - which is exactly the "unparseable falls back to backoff" case.
            if (retryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
            {
                return Clamp(delta);
            }

            if (retryAfter?.Date is DateTimeOffset date)
            {
                var until = date - DateTimeOffset.UtcNow;
                if (until > TimeSpan.Zero)
                {
                    return Clamp(until);
                }
            }

            return Backoff(attempt);
        }

        /// <summary>
        ///   Up to <paramref name="maxBytes" /> bytes of a response body, decoded as UTF-8, for
        ///   <see cref="Refusal" /> to classify. Never the whole body - see
        ///   <see cref="RefusalProbeBytes" /> - and never a reason to fail: a body that cannot be
        ///   read yields the empty string, which classifies as "no refusal" and leaves the retry
        ///   exactly as it was before anything was read. A truncated multi-byte character at the
        ///   bound decodes to a replacement character, which is harmless for phrase matching and is
        ///   why the bound is not aligned to anything.
        /// </summary>
        private static async Task<String> ReadPrefixAsync(HttpResponseMessage response, Int32 maxBytes,
            CancellationToken cancellationToken)
        {
            try
            {
                var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new Byte[maxBytes];
                var read = 0;

                while (read < maxBytes)
                {
                    var got = await stream.ReadAsync(buffer.AsMemory(read, maxBytes - read), cancellationToken);
                    if (got == 0)
                    {
                        break;
                    }

                    read += got;
                }

                return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return String.Empty;
            }
        }

        /// <summary>Exponential backoff with jitter, from <see cref="FirstBackoffSeconds" /> and
        /// capped at <see cref="MaxBackoffSeconds" />. <paramref name="attempt" /> is 1-based.</summary>
        public static TimeSpan Backoff(Int32 attempt)
        {
            var seconds = Math.Min(FirstBackoffSeconds * Math.Pow(2d, Math.Max(0, attempt - 1)), MaxBackoffSeconds);
            var jittered = seconds + (seconds * JitterFraction * Random.Shared.NextDouble());
            return TimeSpan.FromSeconds(Math.Min(jittered, MaxBackoffSeconds));
        }

        private static TimeSpan Clamp(TimeSpan wait)
        {
            return wait.TotalSeconds > MaxWaitSeconds ? TimeSpan.FromSeconds(MaxWaitSeconds) : wait;
        }

        /// <summary>
        ///   A fresh message per attempt, carrying the headers the client already merged in (the
        ///   credential among them) and a replayable copy of the body.
        /// </summary>
        private static HttpRequestMessage Clone(HttpRequestMessage request, Byte[] body)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
                VersionPolicy = request.VersionPolicy
            };

            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            foreach (var option in (IDictionary<String, Object>)request.Options)
            {
                clone.Options.Set(new HttpRequestOptionsKey<Object>(option.Key), option.Value);
            }

            if (body != null)
            {
                clone.Content = new ByteArrayContent(body);
                clone.Content.Headers.Clear();
                foreach (var header in request.Content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return clone;
        }
    }

    /// <summary>
    ///   A provider kept saying "not yet" until the caller's budget ran out.
    ///
    ///   <para><b>Deliberately NOT an <see cref="OperationCanceledException" />,</b> which is the
    ///   obvious-looking choice and does not work: <see cref="HttpClient" /> replaces any
    ///   cancellation coming out of its handler chain with a <see cref="TaskCanceledException" /> of
    ///   its own, so a subclass carrying the retry detail would be discarded before any provider
    ///   could read it. Every other exception type passes through untouched, so this arrives intact
    ///   and keeps the cancellation it came from as <see cref="Exception.InnerException" /> - which
    ///   is what lets a provider tell the two apart: the CALLER going away is still a cancellation,
    ///   only the budget expiring is a timeout.</para>
    /// </summary>
    public abstract class ModelRetryTimeoutException : Exception
    {
        protected ModelRetryTimeoutException(String message, Exception inner = null)
            : base(message, inner)
        {
        }
    }
}
