// MIT License
//
// IntegrationsOptions.cs
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
using System.Collections.Immutable;

namespace NoSQL.GraphDB.Integrations.Configuration
{
    /// <summary>
    ///   This runtime's OWN behaviour (config section <c>Integrations</c>). The graph it writes into
    ///   lives in the separate <see cref="Fallen8TargetOptions"/> (<c>Fallen8Target</c>), split as
    ///   <c>fallen-8-mcp</c> splits <c>Mcp:*</c> from <c>Fallen8Target:*</c>: conflating them leaves a
    ///   compose reader unable to tell which values describe the process and which its target.
    /// </summary>
    public sealed class IntegrationsOptions
    {
        /// <summary>The configuration section this binds from.</summary>
        public const String SectionName = "Integrations";

        /// <summary>
        ///   The address Kestrel binds. Loopback by default; the image sets <c>0.0.0.0</c> because a
        ///   container binding loopback is unreachable, and the port is deliberately not published to
        ///   the host (this container can read third-party credentials, so the browser reaches it
        ///   through the apiApp, which is already the authenticated front door).
        /// </summary>
        public String BindAddress { get; set; } = "127.0.0.1";

        /// <summary>The listen port.</summary>
        public Int32 Port { get; set; } = 8110;

        /// <summary>
        ///   The directory a provider's file settings name a file in, mounted read only. A provider
        ///   never sees a path: it asks <c>ProviderContext.ReadFileAsync(settingKey, ...)</c> and the
        ///   runtime resolves the name under this root.
        /// </summary>
        public String FilesDirectory { get; set; } = "/files";

        /// <summary>The credential mount and the hosts a credentialed run may contact.</summary>
        public CredentialsOptions Credentials { get; set; } = new CredentialsOptions();

        /// <summary>
        ///   Hosts whose TLS certificate is not validated, comma separated. THE ONLY PLACE IN THIS
        ///   FEATURE WHERE TRUST IS REDUCED: a UniFi console and a Fronius inverter serve HTTPS with a
        ///   self-signed certificate for a private address no authority will sign, so a runtime that
        ///   validates strictly cannot reach the sources this feature exists for. It is configuration
        ///   only and must never be a job or provider setting, because a caller able to add a host
        ///   could name one it controls and a credentialed run would authenticate to that machine with
        ///   nothing looking wrong. It is not pinning: a named host is trusted for whatever certificate
        ///   it presents, which over a private address the operator owns states the existing situation
        ///   rather than weakening it.
        /// </summary>
        public String? SelfSignedHosts { get; set; }

        /// <summary>
        ///   <see cref="SelfSignedHosts"/> as a host set, lower-cased for the callback's comparison.
        ///   Empty means the callback is not installed at all.
        /// </summary>
        public ImmutableHashSet<String> SelfSignedHostSet()
        {
            return ParseHostList(SelfSignedHosts);
        }

        /// <summary>
        ///   Splits one comma-separated host list into a set, dropping blanks and folding case.
        ///   Comma separated in ONE configuration key rather than an indexed list because an operator
        ///   sets these in a compose file, where an indexed list reads as three lines to add one host.
        /// </summary>
        internal static ImmutableHashSet<String> ParseHostList(String? value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return ImmutableHashSet<String>.Empty;
            }

            var hosts = new List<String>();
            foreach (var part in value!.Split(','))
            {
                var host = part.Trim();
                if (host.Length > 0)
                {
                    hosts.Add(host.ToLowerInvariant());
                }
            }

            return hosts.ToImmutableHashSet(StringComparer.Ordinal);
        }
    }

    /// <summary>
    ///   Where credentials are read from, and where a run holding one may send it
    ///   (<c>Integrations:Credentials</c>).
    /// </summary>
    public sealed class CredentialsOptions
    {
        /// <summary>
        ///   One file per credential, in a read-only bind-mounted directory rather than compose's
        ///   <c>secrets:</c> list: with <c>secrets:</c> adding a credential means editing compose and
        ///   recreating the service, whereas with a directory adding one is writing a file and
        ///   rotating one is overwriting it.
        /// </summary>
        public String Directory { get; set; } = "/run/secrets";

        /// <summary>
        ///   The hosts a run HOLDING a credential may contact, comma separated. Enforced on the way
        ///   out by <c>CredentialHostGuard</c> rather than by inspecting configuration, because a
        ///   source address arrives in the job's settings from whoever can reach the API: without it a
        ///   caller who edits a base URL aims somebody's admin password at a host of their choosing.
        ///   An empty list means no restriction, and the runtime warns at startup.
        /// </summary>
        public String? AllowedHosts { get; set; }

        /// <summary><see cref="AllowedHosts"/> as a host set; empty means no restriction.</summary>
        public ImmutableHashSet<String> AllowedHostSet()
        {
            return IntegrationsOptions.ParseHostList(AllowedHosts);
        }
    }
}
