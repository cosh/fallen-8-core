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
    ///   <see cref="OllamaHttpClientFactory" />), so a local sidecar keeps failing fast. The waiting
    ///   itself is <see cref="RetryAfterHandler" />.
    /// </summary>
    public sealed class NahilWarmupRetryHandler : RetryAfterHandler
    {
        public NahilWarmupRetryHandler(String model, ILogger logger,
            Func<TimeSpan, CancellationToken, Task> delay = null)
            : base(model, logger, delay)
        {
        }

        /// <summary>
        ///   The sentence Nahil uses for a model it catalogues but cannot route: no worker is
        ///   attached that serves the model's class. Matched as a substring, case-insensitively,
        ///   because the surrounding text carries counts and advice that will change.
        /// </summary>
        internal const String UnservablePhrase = "no attached worker serves";

        /// <summary>How much of a <c>503</c> body is enough to find <see cref="UnservablePhrase" />
        /// and quote the provider's own sentence back. Nahil's is a single short JSON object; the
        /// bound is generous for that and still nothing next to a model response.</summary>
        private const Int32 ProbeBytes = 2048;

        protected override String ProviderName => "Nahil";

        protected override Int32 RefusalProbeBytes => ProbeBytes;

        /// <summary>The two Nahil answers that mean "ask again". A <c>503</c> is deliberately NOT
        /// retried for a local sidecar, which never warms up and must keep failing fast.</summary>
        protected override Boolean ShouldRetry(HttpStatusCode status)
        {
            return status == HttpStatusCode.ServiceUnavailable || status == HttpStatusCode.TooManyRequests;
        }

        /// <summary>
        ///   The one <c>503</c> that is a refusal rather than a warm-up: Nahil has the model in its
        ///   catalog but no attached worker serves its class, and it says so in the body - "retrying
        ///   will not help until a worker subscribes to that class". Measured against the live
        ///   service on 2026-09-09, and it arrives with NO <c>Retry-After</c>, which is exactly the
        ///   case the backoff exists for, so waiting it out consumed the caller's entire budget and
        ///   then blamed a warm-up that was never happening.
        ///
        ///   <para><b>The discriminator is the body, deliberately, and not the missing header.</b> A
        ///   provider that omits <c>Retry-After</c> is only declining to say HOW LONG, which every
        ///   ordinary warm-up is entitled to do; reading absence as permanence would turn every one
        ///   of those into an immediate failure. So an unrecognised body waits exactly as before,
        ///   and this narrows to one sentence one service documents.</para>
        /// </summary>
        protected override Exception Refusal(HttpStatusCode status, String bodyPrefix)
        {
            if (status != HttpStatusCode.ServiceUnavailable || String.IsNullOrEmpty(bodyPrefix)
                || bodyPrefix.IndexOf(UnservablePhrase, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return null;
            }

            return new NahilModelUnservableException(String.Format(
                "Nahil cannot serve the model '{0}' and says retrying will not help: {1}",
                Model, Reason(bodyPrefix)));
        }

        /// <summary>
        ///   The provider's own sentence, unwrapped from the <c>{"error": "..."}</c> envelope it
        ///   arrives in when it arrives in one, and otherwise the raw prefix. Quoted rather than
        ///   paraphrased because it names the fix - a worker has to subscribe to that class - and
        ///   nothing on this side of the request knows that. Already bounded by
        ///   <see cref="ProbeBytes" />, so there is no second length to enforce here.
        /// </summary>
        private static String Reason(String bodyPrefix)
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(bodyPrefix);
                if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && document.RootElement.TryGetProperty("error", out var error)
                    && error.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return error.GetString();
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // A body truncated at the probe bound is not valid JSON, which is expected rather
                // than exceptional: fall through and quote what arrived.
            }

            return bodyPrefix.Trim();
        }

        /// <summary>Wait for a pull to finish, versus wait for a quota to refill: an operator acts
        /// differently on each, so the two never read the same.</summary>
        protected override String Explain(HttpStatusCode status)
        {
            return status == HttpStatusCode.TooManyRequests ? "rate limited" : "warming up";
        }

        protected override ModelRetryTimeoutException GaveUp(HttpStatusCode last, TimeSpan waited,
            Int32 retries, OperationCanceledException cancelled)
        {
            return new NahilWarmupTimeoutException(String.Format(
                "The model '{0}' was not available in time: Nahil answered {1} ({2}) on {3} attempt(s) "
                + "over {4:F0}s of waiting.",
                Model, (Int32)last, Explain(last), retries, waited.TotalSeconds), cancelled);
        }
    }

    /// <summary>Nahil kept saying "not yet" until the caller's budget ran out - see
    /// <see cref="ModelRetryTimeoutException" /> for why this is not a cancellation type.</summary>
    public sealed class NahilWarmupTimeoutException : ModelRetryTimeoutException
    {
        public NahilWarmupTimeoutException(String message, Exception inner = null)
            : base(message, inner)
        {
        }
    }

    /// <summary>
    ///   Nahil will not serve the model at all, and said so on the first answer
    ///   (<see cref="NahilWarmupRetryHandler.Refusal" />). The opposite of its sibling above: that
    ///   one means the budget expired while waiting, this one means there was never anything to
    ///   wait for.
    ///
    ///   <para>An <see cref="HttpRequestException" /> ON PURPOSE, rather than a new type of its
    ///   own: the chat and embedding providers already treat an unclassified transport failure as
    ///   the backend being unavailable and answer <c>503</c> with the message attached, which is
    ///   exactly what this is. So it needs no arm in either provider and no caller learns a new
    ///   exception to catch - the only thing that changes is that the sentence arrives in seconds
    ///   instead of after a spent budget.</para>
    /// </summary>
    public sealed class NahilModelUnservableException : HttpRequestException
    {
        public NahilModelUnservableException(String message, Exception inner = null)
            : base(message, inner)
        {
        }
    }
}
