// MIT License
//
// CredentialHostGuard.cs
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
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NoSQL.GraphDB.Integrations.Credentials
{
    /// <summary>
    ///   Where a credential may be SENT, enforced ON THE WAY OUT.
    ///
    ///   <para>It is a delegating handler on the client a provider reaches its source with, rather than a check
    ///   over configuration, because a source address arrives in the job's settings from whoever can reach the
    ///   API: without this, a caller who edits a base URL aims somebody's admin password at a host of their
    ///   choosing and the runtime authenticates to it. Policing what actually LEAVES also means the guard need
    ///   not know which setting is the address, which keeps it correct as providers are added by people who
    ///   never read it.</para>
    ///
    ///   <para>Redirects are not followed automatically by the client this sits on, because they would be
    ///   followed by the INNER handler, below this one, and a source answering 302 to another host would then
    ///   walk a credential off the list.</para>
    /// </summary>
    public sealed class CredentialHostGuard : DelegatingHandler
    {
        private readonly ImmutableHashSet<String> _allowedHosts;
        private readonly Boolean _holdsCredential;

        /// <param name="allowedHosts">The hosts a credentialed run may contact. EMPTY MEANS NO RESTRICTION, and
        /// the runtime warns at startup, because otherwise the control reads as present and is not.</param>
        /// <param name="holdsCredential">Whether this run holds a credential at all. A run holding none is not
        /// restricted: an unexpected host learns nothing it could not read from the source itself.</param>
        /// <param name="inner">The handler that actually sends.</param>
        public CredentialHostGuard(ImmutableHashSet<String> allowedHosts, Boolean holdsCredential,
            HttpMessageHandler inner)
            : base(inner)
        {
            _allowedHosts = allowedHosts ?? ImmutableHashSet<String>.Empty;
            _holdsCredential = holdsCredential;
        }

        /// <summary>Whether a request to this address would be refused, for a provider that wants to fail early.</summary>
        public Boolean Refuses(Uri? address, out String? reason)
        {
            reason = Evaluate(address);
            return reason != null;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var refusal = Evaluate(request?.RequestUri);
            if (refusal != null)
            {
                throw new CredentialHostRefusedException(refusal);
            }

            return base.SendAsync(request!, cancellationToken);
        }

        private String? Evaluate(Uri? address)
        {
            if (!_holdsCredential)
            {
                return null;
            }

            if (address == null || !address.IsAbsoluteUri)
            {
                return "A credentialed run may only send to an absolute address.";
            }

            var host = address.Host.ToLowerInvariant();

            // Plain http is refused for a credentialed run, loopback excepted: a caller who can edit a base URL
            // would otherwise downgrade an allowed host and read the credential off the wire. Loopback has no
            // wire.
            if (String.Equals(address.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !IsLoopback(address))
            {
                return String.Format(
                    "This run holds a credential, so it may not send to plain http://{0}. Use https, or reach " +
                    "the source over loopback.", address.Host);
            }

            if (_allowedHosts.Count == 0)
            {
                return null;
            }

            if (_allowedHosts.Contains(host))
            {
                return null;
            }

            return String.Format(
                "This run holds a credential and '{0}' is not in Integrations:Credentials:AllowedHosts ({1}). " +
                "The address a provider is given comes from whoever submitted the job, so the list is what stops " +
                "a credential being aimed at a host of their choosing.",
                address.Host, String.Join(", ", _allowedHosts));
        }

        private static Boolean IsLoopback(Uri address)
        {
            if (address.IsLoopback)
            {
                return true;
            }

            return IPAddress.TryParse(address.Host, out var parsed) && IPAddress.IsLoopback(parsed);
        }
    }

    /// <summary>
    ///   A credentialed run tried to contact a host it may not. Surfaced as a CONFIGURATION failure rather than
    ///   a source failure: the job named an address the runtime is not allowed to reach, which is something the
    ///   caller or the operator can fix.
    /// </summary>
    public sealed class CredentialHostRefusedException : Exception
    {
        public CredentialHostRefusedException(String message)
            : base(message)
        {
        }
    }
}
