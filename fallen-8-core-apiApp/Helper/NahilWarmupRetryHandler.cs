// MIT License
//
// NahilWarmupRetryHandler.cs
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
    ///   Waits out Nahil's warm-up instead of failing the first request for a cold model
    ///   (feature nahil-backend). Nahil answers <c>503</c> with a real
    ///   <c>Retry-After</c> while it pulls a catalogued model onto a worker, and <c>429</c> when the
    ///   key's token budget is spent; both mean "ask again", where real Ollama would simply have
    ///   answered. Composed ONLY onto a Nahil transport (see
    ///   <see cref="OllamaHttpClientFactory" />), so a local sidecar keeps failing fast.
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
    public sealed class NahilWarmupRetryHandler : DelegatingHandler
    {
        /// <summary>The ceiling on ONE wait. Nahil asking for more than a minute at a time is
        /// either broken or hostile, and the caller's budget still bounds the total.</summary>
        public const Double MaxWaitSeconds = 60d;

        /// <summary>The first wait when Nahil names no <c>Retry-After</c>.</summary>
        public const Double FirstBackoffSeconds = 2d;

        /// <summary>The ceiling on a BACKED-OFF wait, below <see cref="MaxWaitSeconds" /> because a
        /// a backend that told us nothing has not earned a longer pause than one that did.</summary>
        public const Double MaxBackoffSeconds = 30d;

        /// <summary>How much jitter the backoff adds, as a fraction of the wait: enough to spread a
        /// fleet of instances that all started waiting on the same cold model.</summary>
        private const Double JitterFraction = 0.25d;

        private readonly String _model;
        private readonly ILogger _logger;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        /// <param name="model">Named in the log lines and in the give-up message, because "the model
        /// was not resident" is only actionable if it says which.</param>
        /// <param name="logger">One information-level line per retry; never the credential.</param>
        /// <param name="delay">The wait itself. Injected so a test can assert the schedule this
        /// computes without spending it - the arithmetic is the behaviour, the sleeping is not.</param>
        public NahilWarmupRetryHandler(String model, ILogger logger,
            Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            _model = model;
            _logger = logger;
            _delay = delay ?? ((wait, token) => Task.Delay(wait, token));
        }

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
                    // warm-up IS the story - report it rather than a bare cancellation.
                    throw GaveUp(last, waited, attempt - 1, cancelled);
                }

                if (response.StatusCode != HttpStatusCode.ServiceUnavailable
                    && response.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    return response;
                }

                last = response.StatusCode;
                var wait = WaitFor(response.Headers.RetryAfter, attempt);
                response.Dispose();

                // ONE line per retry, not per poll: a cold multi-gigabyte model can take minutes and
                // the operator needs to see progress, not a wall of identical lines.
                _logger?.LogInformation(
                    "Nahil answered {Status} ({Reason}) for model {Model}; waiting {WaitSeconds:F1}s "
                    + "before attempt {Attempt} ({WaitedSeconds:F0}s waited so far).",
                    (Int32)last, Explain(last), _model, wait.TotalSeconds, attempt + 1, waited.TotalSeconds);

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
        ///   How long to wait before the next attempt: what Nahil asked for when it said, our
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

        /// <summary>The two statuses stay distinguishable everywhere they surface, because they call
        /// for different actions: wait for a pull to finish, versus wait for a quota to refill.</summary>
        private static String Explain(HttpStatusCode status)
        {
            return status == HttpStatusCode.TooManyRequests ? "rate limited" : "warming up";
        }

        private NahilWarmupTimeoutException GaveUp(HttpStatusCode last, TimeSpan waited, Int32 retries,
            OperationCanceledException cancelled)
        {
            return new NahilWarmupTimeoutException(String.Format(
                "The model '{0}' was not available in time: Nahil answered {1} ({2}) on {3} attempt(s) "
                + "over {4:F0}s of waiting.",
                _model, (Int32)last, Explain(last), retries, waited.TotalSeconds), cancelled);
        }

        /// <summary>
        ///   A fresh message per attempt, carrying the headers the client already merged in (the
        ///   bearer credential among them) and a replayable copy of the body.
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
    ///   Nahil kept saying "not yet" until the caller's budget ran out.
    ///
    ///   <para><b>Deliberately NOT an <see cref="OperationCanceledException" />,</b> which is the
    ///   obvious-looking choice and does not work: <see cref="HttpClient" /> replaces any
    ///   cancellation coming out of its handler chain with a <see cref="TaskCanceledException" /> of
    ///   its own, so a subclass carrying the warm-up detail would be discarded before any provider
    ///   could read it. Every other exception type passes through untouched, so this arrives intact
    ///   and keeps the cancellation it came from as <see cref="Exception.InnerException" /> - which
    ///   is what lets a provider tell the two apart: the CALLER going away is still a cancellation,
    ///   only the budget expiring is a timeout.</para>
    /// </summary>
    public sealed class NahilWarmupTimeoutException : Exception
    {
        public NahilWarmupTimeoutException(String message, Exception inner = null)
            : base(message, inner)
        {
        }
    }
}
