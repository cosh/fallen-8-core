// MIT License
//
// RemoteModelRetryHandler.cs
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
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace NoSQL.GraphDB.App.Helper
{
    /// <summary>
    ///   The retry for a metered remote provider (feature model-providers), serving OpenAI and
    ///   Anthropic alike: the SAME waiting as <see cref="RetryAfterHandler" />, with the retryable
    ///   statuses supplied per provider because they do not agree on which ones there are. It is OUR
    ///   retry precisely because each SDK ships one of its own that we switch off: a hidden retry
    ///   multiplies metered spend and logs nothing, so the attempts have to be ours, bounded by the
    ///   caller's budget and visible in the log.
    /// </summary>
    public sealed class RemoteModelRetryHandler : RetryAfterHandler
    {
        private readonly String _providerName;
        private readonly IReadOnlyCollection<HttpStatusCode> _retryable;

        /// <param name="providerName">How the provider is named to an operator.</param>
        /// <param name="model">Named in the log lines and the give-up message.</param>
        /// <param name="logger">One information-level line per retry; never the credential.</param>
        /// <param name="retryable">The statuses this provider uses to mean "ask again". Anything else
        /// reaches the caller unchanged, so a <c>400</c> fails at once instead of being asked four
        /// more times.</param>
        /// <param name="delay">The wait itself, injected so a test asserts the schedule without
        /// spending it.</param>
        public RemoteModelRetryHandler(String providerName, String model, ILogger logger,
            IReadOnlyCollection<HttpStatusCode> retryable, Func<TimeSpan, CancellationToken, Task> delay = null)
            : base(model, logger, delay)
        {
            _providerName = providerName;
            _retryable = retryable ?? Array.Empty<HttpStatusCode>();
        }

        protected override String ProviderName => _providerName;

        protected override Boolean ShouldRetry(HttpStatusCode status)
        {
            return _retryable.Contains(status);
        }

        protected override String Explain(HttpStatusCode status)
        {
            switch ((Int32)status)
            {
                case 429:
                    return "rate limited";
                case 529:
                    return "overloaded";
                case 503:
                    return "unavailable";
                default:
                    return "try again later";
            }
        }

        protected override ModelRetryTimeoutException GaveUp(HttpStatusCode last, TimeSpan waited,
            Int32 retries, OperationCanceledException cancelled)
        {
            return new RemoteModelRetryTimeoutException(String.Format(
                "The model '{0}' did not answer in time: {1} answered {2} ({3}) on {4} attempt(s) "
                + "over {5:F0}s of waiting.",
                Model, _providerName, (Int32)last, Explain(last), retries, waited.TotalSeconds), cancelled);
        }
    }

    /// <summary>A remote provider kept asking to be retried until the caller's budget ran out - see
    /// <see cref="ModelRetryTimeoutException" /> for why this is not a cancellation type.</summary>
    public sealed class RemoteModelRetryTimeoutException : ModelRetryTimeoutException
    {
        public RemoteModelRetryTimeoutException(String message, Exception inner = null)
            : base(message, inner)
        {
        }
    }
}
