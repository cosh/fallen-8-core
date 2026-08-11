// MIT License
//
// CredentialStores.cs
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
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.Integrations.Configuration;

namespace NoSQL.GraphDB.Integrations.Credentials
{
    /// <summary>
    ///   Where a credential VALUE comes from, as a seam, so the whole credential path - fetch, lease, redaction,
    ///   fingerprint - can be exercised offline against a fixture. The seam is deliberately narrow: a name in, a
    ///   value or a reason out.
    /// </summary>
    public interface ICredentialStore
    {
        /// <summary>Reads one credential by name.</summary>
        Boolean TryRead(String credentialName, out String? value, out String? failure);
    }

    /// <summary>
    ///   The two content rules a credential value obeys WHEREVER IT CAME FROM: from a file the operator wrote,
    ///   from a fixture, or supplied inline in the job. One home, because a value accepted by one route and
    ///   refused by another is a credential that works from cron and fails from a button.
    /// </summary>
    internal static class CredentialContent
    {
        /// <summary>
        ///   Applies the two rules.
        ///
        ///   <para>Content is verbatim except EXACTLY ONE trailing line ending, with leading, internal and
        ///   trailing spaces untouched: <c>printf 'pw' &gt; f</c>, <c>echo pw &gt; f</c> and a projected secret
        ///   differ by one byte, and the symptom is an authentication failure from somebody's controller with
        ///   nothing to explain it. Any of those spaces can be part of a real password, and so can the newline a
        ///   copy-paste out of a console appends, which is why exactly one is dropped rather than all trailing
        ///   whitespace.</para>
        ///
        ///   <para>An empty or whitespace-only value is a FAILURE, never "no credential", because a rotation
        ///   script that truncated a file - or an operator who submitted the form before pasting - would
        ///   otherwise produce a run that reads what the source shows the public, declares it complete, and
        ///   withdraws every claim the instance ever made.</para>
        /// </summary>
        internal static Boolean TryAccept(String raw, out String? value, out String? failure)
        {
            value = null;

            var trimmed = raw;
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
                          "'no credential': a truncated value would otherwise produce a run that reads what the " +
                          "source shows the public and then withdraws everything";
                return false;
            }

            value = trimmed;
            failure = null;
            return true;
        }
    }

    /// <summary>
    ///   One file per credential in a read-only bind-mounted directory, rather than compose's <c>secrets:</c>
    ///   list: with <c>secrets:</c> adding a credential means editing compose and recreating the service, while
    ///   with a directory adding one is writing a file and rotating one is overwriting it.
    /// </summary>
    public sealed class DirectoryCredentialStore : ICredentialStore
    {
        private readonly IOptions<IntegrationsOptions> _options;

        public DirectoryCredentialStore(IOptions<IntegrationsOptions> options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <inheritdoc />
        public Boolean TryRead(String credentialName, out String? value, out String? failure)
        {
            value = null;

            var directory = _options.Value.Credentials.Directory;
            if (!RootedNames.TryResolve(directory, credentialName, "credential", out var path, out failure))
            {
                return false;
            }

            String raw;
            try
            {
                raw = File.ReadAllText(path!);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is NotSupportedException)
            {
                failure = ex.Message;
                return false;
            }

            return CredentialContent.TryAccept(raw, out value, out failure);
        }
    }

    /// <summary>
    ///   The credentials a fixture offers, by name. Used by the conformance suite so a candidate provider's
    ///   whole credential path runs with no real file system, and so a provider that logs its credential can be
    ///   caught with a value the suite knows to look for.
    /// </summary>
    public sealed class FixtureCredentialStore : ICredentialStore
    {
        private readonly IReadOnlyDictionary<String, String> _values;

        public FixtureCredentialStore(IReadOnlyDictionary<String, String>? values)
        {
            _values = values ?? new Dictionary<String, String>(StringComparer.Ordinal);
        }

        /// <inheritdoc />
        public Boolean TryRead(String credentialName, out String? value, out String? failure)
        {
            // The NAME still goes through the same shape check, so a candidate that tries to escape the
            // directory fails here exactly as it would against a real mount.
            if (!RootedNames.TryResolve("/run/secrets", credentialName, "credential", out _, out failure))
            {
                value = null;
                return false;
            }

            if (!_values.TryGetValue(credentialName, out var found))
            {
                value = null;
                failure = "the fixture offers no credential of that name";
                return false;
            }

            return CredentialContent.TryAccept(found, out value, out failure);
        }
    }

    /// <summary>
    ///   Where a provider's FILE comes from. A provider never opens a file: one taking a path could be pointed
    ///   at the credential directory and made to hand the contents back in a report or write them into the
    ///   graph, and blocklisting that directory only moves the target.
    /// </summary>
    public interface IProviderFileStore
    {
        /// <summary>Reads the file with that name.</summary>
        Task<String> ReadAsync(String fileName, CancellationToken cancellationToken);

        /// <summary>Whether the name resolves at all, without reading it.</summary>
        Boolean TryResolve(String fileName, out String? failure);
    }

    /// <summary>The read-only files mount.</summary>
    public sealed class DirectoryFileStore : IProviderFileStore
    {
        private readonly IOptions<IntegrationsOptions> _options;

        public DirectoryFileStore(IOptions<IntegrationsOptions> options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <inheritdoc />
        public Boolean TryResolve(String fileName, out String? failure)
        {
            return RootedNames.TryResolve(_options.Value.FilesDirectory, fileName, "file", out _, out failure);
        }

        /// <inheritdoc />
        public async Task<String> ReadAsync(String fileName, CancellationToken cancellationToken)
        {
            if (!RootedNames.TryResolve(_options.Value.FilesDirectory, fileName, "file", out var path,
                    out var failure))
            {
                throw new ProviderFileException(failure!);
            }

            try
            {
                return await File.ReadAllTextAsync(path!, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is NotSupportedException)
            {
                throw new ProviderFileException(String.Format(
                    "The file '{0}' could not be read: {1}", fileName, ex.Message), ex);
            }
        }
    }

    /// <summary>
    ///   The files a fixture offers, by name. A candidate naming a file the fixture does not have fails, which
    ///   is what makes the path-escape check observable rather than advisory.
    /// </summary>
    public sealed class FixtureFileStore : IProviderFileStore
    {
        private readonly IReadOnlyDictionary<String, String> _files;

        public FixtureFileStore(IReadOnlyDictionary<String, String>? files)
        {
            _files = files ?? new Dictionary<String, String>(StringComparer.Ordinal);
        }

        /// <summary>Every file name a candidate asked for, so the suite can assert it asked only for offered ones.</summary>
        public IList<String> Requested { get; } = new List<String>();

        /// <inheritdoc />
        public Boolean TryResolve(String fileName, out String? failure)
        {
            Requested.Add(fileName ?? String.Empty);

            if (!RootedNames.TryResolve("/files", fileName, "file", out _, out failure))
            {
                return false;
            }

            if (!_files.ContainsKey(fileName!))
            {
                failure = "the fixture offers no file of that name";
                return false;
            }

            return true;
        }

        /// <inheritdoc />
        public Task<String> ReadAsync(String fileName, CancellationToken cancellationToken)
        {
            if (!TryResolve(fileName, out var failure))
            {
                throw new ProviderFileException(failure!);
            }

            return Task.FromResult(_files[fileName]);
        }
    }

    /// <summary>
    ///   A file a provider named could not be opened. A missing or unreadable file FAILS THE RUN and withdraws
    ///   nothing, because "the list is empty" would withdraw every element this identity claimed.
    /// </summary>
    public sealed class ProviderFileException : Exception
    {
        public ProviderFileException(String message)
            : base(message)
        {
        }

        public ProviderFileException(String message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
