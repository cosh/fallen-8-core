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

        protected override String ProviderName => "Nahil";

        /// <summary>The two Nahil answers that mean "ask again". A <c>503</c> is deliberately NOT
        /// retried for a local sidecar, which never warms up and must keep failing fast.</summary>
        protected override Boolean ShouldRetry(HttpStatusCode status)
        {
            return status == HttpStatusCode.ServiceUnavailable || status == HttpStatusCode.TooManyRequests;
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
}
