// MIT License
//
// CredentialResolver.cs
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
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.Integrations.Configuration;

namespace NoSQL.GraphDB.Integrations.Credentials
{
    /// <summary>
    ///   Fetches every credential a run needs, ONCE and EAGERLY, before the provider is invoked.
    ///
    ///   <para>Eagerly, because a lazy fetch moves the failure into the middle of a source read, after the run
    ///   has begun making withdrawal-relevant decisions. Once, because there is no cache of any kind including
    ///   "resolve once per run and keep it": these credentials belong to other people and other systems who
    ///   rotate them on their own timetable, so a stored copy silently becomes the wrong value the moment one
    ///   is rotated and the integration then fails for a reason invisible from the graph.</para>
    /// </summary>
    public sealed class CredentialResolver
    {
        private readonly ICredentialStore _store;
        private readonly ActiveCredentials _active;

        public CredentialResolver(IOptions<IntegrationsOptions> options, ActiveCredentials active)
            : this(new DirectoryCredentialStore(options), active)
        {
        }

        /// <param name="store">Where credential values come from. The conformance suite substitutes a fixture
        /// store, which is what lets the whole credential path be exercised with no real file system.</param>
        /// <param name="active">The process-wide set redaction substitutes against.</param>
        public CredentialResolver(ICredentialStore store, ActiveCredentials active)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _active = active ?? throw new ArgumentNullException(nameof(active));
        }

        /// <summary>
        ///   Reads every named credential and hands back the run's lease.
        /// </summary>
        /// <param name="namesBySettingKey">Which credential each credential setting uses, by NAME.</param>
        /// <exception cref="CredentialUnavailableException">A named credential could not be read. "I could not
        /// look" is a failure of its own kind, never "no credential": a rotation script that truncated a file
        /// would otherwise produce a run that reads what the source shows the public, declares it complete, and
        /// withdraws every claim the instance ever made.</exception>
        public CredentialLease Resolve(IReadOnlyDictionary<String, String> namesBySettingKey)
        {
            if (namesBySettingKey == null || namesBySettingKey.Count == 0)
            {
                // A provider needing none gets an EMPTY lease from a factory, never a shared instance: one
                // caller putting a static lease in a using would end it permanently for every uncredentialed
                // provider afterwards.
                return CredentialLease.Empty();
            }

            var values = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in namesBySettingKey)
            {
                if (String.IsNullOrWhiteSpace(pair.Value))
                {
                    throw new CredentialUnavailableException(String.Format(
                        "Credential setting '{0}' names no credential.", pair.Key));
                }

                if (!_store.TryRead(pair.Value, out var value, out var failure) || value == null)
                {
                    throw new CredentialUnavailableException(String.Format(
                        "The credential named '{0}' (for setting '{1}') could not be read: {2}",
                        pair.Value, pair.Key, failure));
                }

                values[pair.Key] = value;
            }

            return CredentialLease.For(values, _active);
        }
    }

    /// <summary>
    ///   "A named credential could not be read." Its own failure kind, because an unreadable credential and an
    ///   unreachable source send a reader to different places.
    /// </summary>
    public sealed class CredentialUnavailableException : Exception
    {
        public CredentialUnavailableException(String message)
            : base(message)
        {
        }
    }
}
