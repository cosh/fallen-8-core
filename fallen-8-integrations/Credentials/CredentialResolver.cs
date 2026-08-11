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
        ///   Resolves every credential the job supplies, from whichever source it named, and hands back the
        ///   run's lease. Both sources land in ONE lease, so everything downstream - redaction, the fingerprint,
        ///   the drop at the end of the run - is blind to where a credential came from.
        /// </summary>
        /// <param name="sourcesBySettingKey">Where each credential setting's value comes from.</param>
        /// <exception cref="CredentialUnavailableException">A credential could not be read, or the value supplied
        /// for one is not usable. "I could not look" is a failure of its own kind, never "no credential": a
        /// rotation script that truncated a file would otherwise produce a run that reads what the source shows
        /// the public, declares it complete, and withdraws every claim the instance ever made.</exception>
        public CredentialLease Resolve(IReadOnlyDictionary<String, CredentialSource> sourcesBySettingKey)
        {
            if (sourcesBySettingKey == null || sourcesBySettingKey.Count == 0)
            {
                // A provider needing none gets an EMPTY lease from a factory, never a shared instance: one
                // caller putting a static lease in a using would end it permanently for every uncredentialed
                // provider afterwards.
                return CredentialLease.Empty();
            }

            var values = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in sourcesBySettingKey)
            {
                if (pair.Value == null)
                {
                    throw new CredentialUnavailableException(String.Format(
                        "Credential setting '{0}' has no credential source.", pair.Key));
                }

                if (pair.Value.IsInline)
                {
                    // The same two content rules as a file, so a credential that works from cron works from a
                    // button. The reason is reported; the value it judged never is.
                    if (!CredentialContent.TryAccept(pair.Value.InlineValue!, out var supplied, out var rejected) ||
                        supplied == null)
                    {
                        throw new CredentialUnavailableException(String.Format(
                            "The credential supplied for setting '{0}' is not usable: {1}", pair.Key, rejected));
                    }

                    values[pair.Key] = supplied;
                    continue;
                }

                if (String.IsNullOrWhiteSpace(pair.Value.Name))
                {
                    throw new CredentialUnavailableException(String.Format(
                        "Credential setting '{0}' names no credential.", pair.Key));
                }

                if (!_store.TryRead(pair.Value.Name!, out var value, out var failure) || value == null)
                {
                    throw new CredentialUnavailableException(String.Format(
                        "The credential named '{0}' (for setting '{1}') could not be read: {2}",
                        pair.Value.Name, pair.Key, failure));
                }

                values[pair.Key] = value;
            }

            return CredentialLease.For(values, _active);
        }
    }

    /// <summary>
    ///   Where ONE credential setting's value comes from: a credential the operator put in the runtime's mount,
    ///   named by the job, or the credential ITSELF supplied inline in the job.
    ///
    ///   <para>One type rather than two parallel maps in every signature, because two maps make "the same
    ///   setting appears in both" a precedence rule nobody can see. Here it is a shape a job cannot have: the
    ///   overlap is rejected while the job is being folded, before a run starts.</para>
    ///
    ///   <para>The two sources differ in exactly one respect, and it is not how the runtime treats the value -
    ///   both are leased, redacted, fingerprinted and dropped identically. It is what the JOB is: a job naming
    ///   credentials is safe to keep, to commit, and to read back as a record of what was asked for. A job
    ///   carrying one is a secret in a document, so nothing stores it and the runtime never echoes it back.</para>
    /// </summary>
    public sealed class CredentialSource
    {
        private CredentialSource(Boolean isInline, String? name, String? inlineValue)
        {
            IsInline = isInline;
            Name = name;
            InlineValue = inlineValue;
        }

        /// <summary>A credential the operator wrote into the mount, by name.</summary>
        public static CredentialSource Named(String credentialName)
        {
            return new CredentialSource(false, credentialName, null);
        }

        /// <summary>The credential itself, supplied in the job and held only for the run.</summary>
        public static CredentialSource Inline(String credentialValue)
        {
            return new CredentialSource(true, null, credentialValue);
        }

        /// <summary>Whether the value came with the job rather than from the mount.</summary>
        public Boolean IsInline { get; }

        /// <summary>The credential's name, or null when the value was supplied inline.</summary>
        public String? Name { get; }

        /// <summary>
        ///   The credential ITSELF when it was supplied inline, null when named. Nothing may log, report or
        ///   persist this; it exists to be handed to the lease, which is what makes redaction cover it.
        /// </summary>
        public String? InlineValue { get; }

        /// <summary>
        ///   Deliberately never the value. An interpolation into a log line or an exception message is the one
        ///   accident a secret-carrying type can suffer, and the fix belongs on the type rather than on every
        ///   site that might one day format it.
        /// </summary>
        public override String ToString()
        {
            return IsInline ? "<credential supplied with the job>" : Name ?? "<unnamed credential>";
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
