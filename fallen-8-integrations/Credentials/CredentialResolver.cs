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

namespace NoSQL.GraphDB.Integrations.Credentials
{
    /// <summary>
    ///   Turns the credentials a job carries into the run's lease, ONCE and EAGERLY, before the provider is
    ///   invoked.
    ///
    ///   <para>Eagerly, because a value judged in the middle of a source read fails after the run has begun
    ///   making withdrawal-relevant decisions. Once, because there is no cache of any kind including "resolve
    ///   once per run and keep it".</para>
    ///
    ///   <para>There is no credential SOURCE to choose between: a job carries the values it needs and the
    ///   runtime has no mount, no store and nothing to rotate. That is why this type is thin - it exists to
    ///   apply the content rules and to put the values in the lease, which is what makes them redactable.</para>
    /// </summary>
    public sealed class CredentialResolver
    {
        private readonly ActiveCredentials _active;

        /// <param name="active">The process-wide set redaction substitutes against.</param>
        public CredentialResolver(ActiveCredentials active)
        {
            _active = active ?? throw new ArgumentNullException(nameof(active));
        }

        /// <summary>
        ///   Accepts every credential the job carries and hands back the run's lease.
        /// </summary>
        /// <param name="valuesBySettingKey">The credential each credential setting uses, by VALUE.</param>
        /// <exception cref="CredentialUnavailableException">A value the job carried is not usable. That is a
        /// failure of its own kind, never "no credential": a form submitted before the paste would otherwise
        /// produce a run that reads what the source shows the public, declares it complete, and withdraws
        /// every claim the instance ever made.</exception>
        public CredentialLease Resolve(IReadOnlyDictionary<String, String> valuesBySettingKey)
        {
            if (valuesBySettingKey == null || valuesBySettingKey.Count == 0)
            {
                // A provider needing none gets an EMPTY lease from a factory, never a shared instance: one
                // caller putting a static lease in a using would end it permanently for every uncredentialed
                // provider afterwards.
                return CredentialLease.Empty();
            }

            var values = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in valuesBySettingKey)
            {
                // The reason a value was refused is reported; the value it judged never is. A value rejected
                // HERE never entered the lease, so redaction knows nothing about it and anything quoted would
                // travel out on the report in the clear.
                if (!TryAccept(pair.Value, out var accepted, out var rejected) || accepted == null)
                {
                    throw new CredentialUnavailableException(String.Format(
                        "The credential supplied for setting '{0}' is not usable: {1}", pair.Key, rejected));
                }

                values[pair.Key] = accepted;
            }

            return CredentialLease.For(values, _active);
        }

        /// <summary>
        ///   The two content rules, in one place because a credential is accepted by exactly one route.
        ///
        ///   <para>Content is verbatim except EXACTLY ONE trailing line ending, with leading, internal and
        ///   trailing spaces untouched. A value pasted out of a console arrives with the newline that came
        ///   with it, and every one of those spaces can be part of a real password; the symptom of getting
        ///   this wrong is an authentication failure from somebody's controller with nothing to explain it.
        ///   Exactly one line ending is dropped rather than all trailing whitespace, for the same reason.</para>
        ///
        ///   <para>An empty or whitespace-only value is a FAILURE, never "no credential": a form submitted
        ///   before the paste would otherwise produce a run that reads what the source shows the public,
        ///   declares that complete, and withdraws every claim the instance ever made.</para>
        /// </summary>
        private static Boolean TryAccept(String? raw, out String? value, out String? failure)
        {
            value = null;

            var trimmed = raw ?? String.Empty;
            if (trimmed.EndsWith("\r\n", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 2);
            }
            else if (trimmed.EndsWith("\n", StringComparison.Ordinal) ||
                     trimmed.EndsWith("\r", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            }

            if (String.IsNullOrWhiteSpace(trimmed))
            {
                failure = "it is empty or holds only whitespace, which is a failure rather than " +
                          "'no credential': a truncated value would otherwise produce a run that reads what " +
                          "the source shows the public and then withdraws everything";
                return false;
            }

            foreach (var character in trimmed)
            {
                // A credential reaches a source as text in an HTTP header, and .NET refuses to put a control
                // or non-ASCII character on the wire at all. Refused HERE, eagerly, so it fails as the
                // credential problem it is: left to the send, it throws from inside the provider and the
                // runner can only report "the source did not answer", which is the wrong system entirely.
                if (Char.IsControl(character) || character > 127)
                {
                    failure = "it contains a character that cannot be sent in an HTTP header, either a " +
                              "control character or one outside ASCII. The character is deliberately not " +
                              "quoted. Look for what a copy brought along with it: a line break, a " +
                              "non-breaking space, or a quotation mark an editor turned into a curly one";
                    return false;
                }
            }

            value = trimmed;
            failure = null;
            return true;
        }
    }

    /// <summary>
    ///   "A credential the job carried cannot be used." Its own failure kind, reported as <c>credential</c>,
    ///   because a value the runtime could not accept and a source that refused one send a reader to the same
    ///   place and a source that would not answer does not.
    /// </summary>
    public sealed class CredentialUnavailableException : Exception
    {
        public CredentialUnavailableException(String message)
            : base(message)
        {
        }
    }
}
