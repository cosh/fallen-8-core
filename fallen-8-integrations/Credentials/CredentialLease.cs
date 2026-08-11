// MIT License
//
// CredentialLease.cs
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
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NoSQL.GraphDB.Integrations.Credentials
{
    /// <summary>
    ///   The credential values ONE run holds, for exactly as long as that run lasts.
    ///
    ///   <para>Run scoped and disposed in a <c>finally</c> spanning BOTH the source read and the graph
    ///   write, so the value stays redactable for as long as anything can mention it, and a provider that
    ///   squirrelled the context away fails loudly instead of quietly authenticating with a password the
    ///   operator rotated away. Nothing is cached: the values belong to other people and other systems
    ///   who rotate them on their own timetable, so a stored copy silently becomes the wrong value the
    ///   moment one is rotated and the integration then fails for a reason invisible from the graph.</para>
    ///
    ///   <para>WHAT THIS DOES NOT CLAIM, stated exactly: no zeroed buffers and no
    ///   <c>SecureString</c>. A provider runs in process at full trust and must receive the credential as
    ///   a string to put it in a request, and .NET strings are immutable, so those would be theatre and a
    ///   hostile contract for every author. The guarantee is that no credential is written to disk,
    ///   returned by any route or emitted to any log sink, and that none is reachable through the
    ///   runtime's seams once the run that fetched it has ended. Against a careless provider the design
    ///   buys time-boxing rather than prevention.</para>
    /// </summary>
    public sealed class CredentialLease : IDisposable
    {
        /// <summary>
        ///   The fingerprint key: random per process and never written down. Deliberately not stable
        ///   across processes, because a stable one would be an offline verifier for guessing a short
        ///   credential.
        /// </summary>
        private static readonly Byte[] FingerprintKey = RandomNumberGenerator.GetBytes(32);

        /// <summary>A separator no setting key can contain, so two different (key, value) sets cannot
        /// hash alike by concatenating into one identical string.</summary>
        private const String FingerprintSeparator = "\u001F";

        private readonly Dictionary<String, String> _values;
        private readonly ActiveCredentials? _active;
        private Boolean _ended;

        private CredentialLease(Dictionary<String, String> values, ActiveCredentials? active)
        {
            _values = values;
            _active = active;

            if (_active != null)
            {
                foreach (var value in _values.Values)
                {
                    _active.Hold(value);
                }
            }
        }

        /// <summary>
        ///   A lease holding nothing, for a provider that needs no credential. A FACTORY and not a shared
        ///   instance: one caller putting a static lease in a <c>using</c> would end it permanently for
        ///   every uncredentialed provider afterwards.
        /// </summary>
        public static CredentialLease Empty()
        {
            return new CredentialLease(NewMap(), null);
        }

        /// <summary>
        ///   The lease for a run that fetched values, keyed by the credential SETTING key the provider
        ///   asks for. Keys are folded case-insensitively.
        /// </summary>
        public static CredentialLease For(IDictionary<String, String> valuesBySettingKey, ActiveCredentials active)
        {
            if (valuesBySettingKey == null)
            {
                throw new ArgumentNullException(nameof(valuesBySettingKey));
            }

            var map = NewMap();
            foreach (var pair in valuesBySettingKey)
            {
                map[pair.Key] = pair.Value;
            }

            return new CredentialLease(map, active);
        }

        /// <summary>Whether the run has ended, after which every value read refuses.</summary>
        public Boolean Ended => _ended;

        /// <summary>Whether this lease holds anything at all.</summary>
        public Boolean IsEmpty => _values.Count == 0;

        /// <summary>
        ///   A keyed hash of what this run held, under a key random per process. A credential file
        ///   replaced by MOVING a new file over it gives the file a new inode and a bind-mounted
        ///   container keeps reading the old one, so the job succeeds with the credential the operator
        ///   believes they revoked; a fingerprint that does not change after a rotation is how that is
        ///   seen, which is why rotation is documented as overwriting in place. Null when nothing is held.
        /// </summary>
        public String? Fingerprint()
        {
            if (_values.Count == 0)
            {
                return null;
            }

            var keys = new List<String>(_values.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);

            var material = new StringBuilder();
            foreach (var key in keys)
            {
                material.Append(key.ToLowerInvariant())
                    .Append(FingerprintSeparator)
                    .Append(_values[key])
                    .Append(FingerprintSeparator);
            }

            var hash = HMACSHA256.HashData(FingerprintKey, Encoding.UTF8.GetBytes(material.ToString()));
            var text = new StringBuilder(24);
            for (var i = 0; i < 12; i++)
            {
                text.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            }

            return text.ToString();
        }

        /// <summary>
        ///   The value for a credential setting key. Refuses after the run has ended, which is how a
        ///   provider that kept the context finds out.
        /// </summary>
        public Boolean TryGet(String settingKey, out String? value)
        {
            ThrowIfEnded();

            if (settingKey != null && _values.TryGetValue(settingKey, out var held))
            {
                value = held;
                return true;
            }

            value = null;
            return false;
        }

        /// <summary>The value for a credential setting key, or a failure naming the key.</summary>
        public String Require(String settingKey)
        {
            if (TryGet(settingKey, out var value) && value != null)
            {
                return value;
            }

            throw new InvalidOperationException(String.Format(
                "No credential was supplied for setting '{0}'.", settingKey));
        }

        /// <summary>Ends the run's hold: the values stop being redactable and stop being readable.</summary>
        public void Dispose()
        {
            if (_ended)
            {
                return;
            }

            _ended = true;

            if (_active != null)
            {
                foreach (var value in _values.Values)
                {
                    _active.Release(value);
                }
            }

            _values.Clear();
        }

        private void ThrowIfEnded()
        {
            if (_ended)
            {
                throw new InvalidOperationException(
                    "The credential lease for this run has ended: a credential may not be held past the run " +
                    "that fetched it, because the value belongs to a system that rotates it.");
            }
        }

        /// <summary>
        ///   Case-insensitive because a job arrives as JSON and deserialising into a dictionary yields an
        ///   ordinal comparer whatever the initialiser says, so <c>Password</c> would otherwise slip past
        ///   a lookup for <c>password</c> and defeat the guard with the shift key.
        /// </summary>
        private static Dictionary<String, String> NewMap()
        {
            return new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
