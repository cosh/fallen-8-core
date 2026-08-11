// MIT License
//
// ProviderHttpFactory.cs
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
using System.Collections.Immutable;
using System.Net.Http;
using System.Net.Security;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.Integrations.Configuration;
using NoSQL.GraphDB.Integrations.Credentials;

namespace NoSQL.GraphDB.Integrations.Run
{
    /// <summary>
    ///   Where the client a provider reaches its source with comes from. A seam, because the conformance suite
    ///   substitutes a recording handler behind it: <c>RunsOffline</c> means the run completed against
    ///   substituted seams ALONE, which can only be observed by owning the handler a provider is given.
    /// </summary>
    public interface IProviderHttpFactory
    {
        /// <summary>
        ///   Creates the client for one run. The caller disposes it.
        /// </summary>
        /// <param name="holdsCredential">Whether this run holds a credential, which is what turns the host guard
        /// on. A run holding none is not restricted: an unexpected host learns nothing it could not read from the
        /// source itself.</param>
        HttpClient Create(Boolean holdsCredential);
    }

    /// <summary>
    ///   The live client: the credential host guard on top, the named self-signed hosts underneath, and no
    ///   automatic redirects anywhere.
    /// </summary>
    public sealed class ProviderHttpFactory : IProviderHttpFactory
    {
        /// <summary>Per-request budget for a source read. Long enough for a slow console, short enough that a
        /// hung source does not hold the identity's run gate for the caller's whole patience.</summary>
        internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

        private readonly IOptions<IntegrationsOptions> _options;

        public ProviderHttpFactory(IOptions<IntegrationsOptions> options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <inheritdoc />
        public HttpClient Create(Boolean holdsCredential)
        {
            var options = _options.Value;
            return Wrap(options.Credentials.AllowedHostSet(), holdsCredential, BuildNetworkHandler(options));
        }

        /// <summary>
        ///   Puts the host guard on top of whatever actually sends, which is the ONE place the guard is
        ///   installed: the conformance suite reuses it so a candidate is judged against the same boundary the
        ///   live path applies.
        /// </summary>
        public static HttpClient Wrap(ImmutableHashSet<String> allowedHosts, Boolean holdsCredential,
            HttpMessageHandler inner)
        {
            var guarded = new CredentialHostGuard(allowedHosts, holdsCredential, inner);
            return new HttpClient(guarded, disposeHandler: true)
            {
                Timeout = RequestTimeout,
            };
        }

        private static HttpMessageHandler BuildNetworkHandler(IntegrationsOptions options)
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),

                // Redirects are NOT followed automatically: they would be followed here, BELOW the host guard,
                // so a source answering 302 to another host would walk a credential off the allowed list.
                AllowAutoRedirect = false,
            };

            var selfSigned = options.SelfSignedHostSet();
            if (selfSigned.Count == 0)
            {
                // Not installed at all when the list is empty, so normal validation is the only code path.
                return handler;
            }

            handler.SslOptions.RemoteCertificateValidationCallback = (sender, _, _, errors) =>
                Accept(selfSigned, sender as HttpRequestMessage, errors);

            return handler;
        }

        /// <summary>
        ///   Accepts an unvalidated certificate ONLY for a named host, and validates normally for every other.
        ///
        ///   <para>This is the only place in the feature where trust is reduced, and it is not pinning: a named
        ///   host is trusted for whatever certificate it presents, which over a private address the operator owns
        ///   states the existing situation rather than weakening it. A UniFi console and a Fronius inverter serve
        ///   HTTPS with a self-signed certificate for a private address no authority will sign, and both
        ///   alternatives are worse: a provider that turns validation off for itself, or a user told to send an
        ///   admin credential over plain http.</para>
        /// </summary>
        private static Boolean Accept(ImmutableHashSet<String> selfSignedHosts, HttpRequestMessage? request,
            SslPolicyErrors errors)
        {
            if (errors == SslPolicyErrors.None)
            {
                return true;
            }

            // SocketsHttpHandler passes the REQUEST as the callback's sender, so the host is read from the
            // address actually being contacted rather than guessed from the certificate's own subject: a
            // certificate naming an allowed host would otherwise be accepted for any host that presented it.
            var host = request?.RequestUri?.Host;
            if (host == null)
            {
                return false;
            }

            return selfSignedHosts.Contains(host.ToLowerInvariant());
        }
    }
}
