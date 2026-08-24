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
        ///   the host (jobs hand this container third-party credentials, so the browser reaches it
        ///   through the apiApp, which is already the authenticated front door).
        /// </summary>
        public String BindAddress { get; set; } = "127.0.0.1";

        /// <summary>The listen port.</summary>
        public Int32 Port { get; set; } = 8110;

        /// <summary>
        ///   The biggest file a job may carry, per file setting, measured on the DECODED bytes. There is
        ///   deliberately no files directory beside it: a file arrives with the job that needs it and is
        ///   dropped when the run ends, so this container mounts nothing, opens nothing and has no name to
        ///   resolve. The ceiling exists because the alternative is a caller deciding how much memory this
        ///   process spends.
        ///
        ///   <para>128 MiB, and that number came from a real file rather than from symmetry with something
        ///   else. It was first sized like the instance's document-upload ceiling
        ///   (<c>Fallen8:Ingestion:MaxUploadBytes</c>, 32 MiB), which turned out to refuse the very thing
        ///   this feature exists to read: an AUTOSAR system extract for one vehicle platform runs to tens
        ///   of megabytes, and the first one anybody pointed at it was a large size.</para>
        ///
        ///   <para>Zero or less switches the ceiling OFF rather than refusing every file, and the runtime
        ///   warns at startup when it is. Raising it past about 144 MiB has no effect in the shipped
        ///   deployment: the apiApp's proxy is the only way in and carries its own fixed body bound.</para>
        ///
        ///   <para>A file this big is not free, and the cost is not hidden: it arrives base64 (a third
        ///   larger), is decoded to bytes, and is decoded again to TEXT for the provider - two bytes per
        ///   character for XML - so a run over a maximal extract peaks in the high hundreds of megabytes
        ///   before the provider has parsed anything. The mount this replaced cost the same; what is new
        ///   is that a caller rather than an operator picks the size, which is why the ceiling is here at
        ///   all.</para>
        /// </summary>
        public Int64 MaxFileBytes { get; set; } = 134_217_728;

        /// <summary>Where a run holding a credential may send it.</summary>
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
    ///   Where a run holding a credential may send it (<c>Integrations:Credentials</c>). There is
    ///   deliberately no credential SOURCE here: see <c>CredentialResolver</c> for why one arrives with
    ///   the job and nowhere else.
    /// </summary>
    public sealed class CredentialsOptions
    {
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
