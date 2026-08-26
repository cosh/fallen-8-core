// MIT License
//
// ProviderRequest.cs
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
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NoSQL.GraphDB.Integrations.Contract
{
    /// <summary>
    ///   Issuing one request on the client the runtime handed a provider, and turning the two ways the
    ///   network fails to answer at all into a <see cref="ProviderSourceException"/>.
    ///
    ///   <para>One home because there is no provider-specific judgement in that translation: a source that
    ///   did not answer and a source that did not answer IN TIME both fail the run and withdraw nothing,
    ///   because "I could not look" must never become "there is nothing there". What a provider still owns is
    ///   the answer it got - every status and every body comes back for it to judge, since what a 404 means
    ///   differs per resource and nothing here can know that.</para>
    /// </summary>
    public static class ProviderRequest
    {
        /// <summary>Sends one request, or fails the run naming what did not answer.</summary>
        /// <param name="http">The client from <see cref="ProviderContext.Http"/>, whose delegating handler
        /// enforces the allowed-host list on the way out.</param>
        /// <param name="request">The request. Its method and URL are what the failure names.</param>
        /// <param name="source">What did not answer, in the provider's own words ("The console").</param>
        /// <param name="cancellationToken">The run's token, and the only thing that tells a CALLER'S
        /// cancellation from a timeout: both arrive as <see cref="TaskCanceledException"/>, so the type
        /// cannot, and a cancelled run must not be reported as a source that would not answer.</param>
        public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request,
            String source, CancellationToken cancellationToken)
        {
            if (http == null)
            {
                throw new ArgumentNullException(nameof(http));
            }

            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            try
            {
                return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException failure)
            {
                throw new ProviderSourceException(String.Format(
                    "{0} did not answer {1} {2}: {3}. The run fails and withdraws nothing.",
                    source, request.Method.Method, request.RequestUri, failure.Message), failure);
            }
            catch (TaskCanceledException failure) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ProviderSourceException(String.Format(
                    "{0} did not answer {1} {2} in time. The run fails and withdraws nothing.",
                    source, request.Method.Method, request.RequestUri), failure);
            }
        }
    }
}
