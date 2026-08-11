// MIT License
//
// ProviderFileStores.cs
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
    ///   Where a provider's FILE comes from. A provider never opens a file: one taking a path could be pointed
    ///   at anything this container can read and made to hand the contents back in a report or write them into
    ///   the graph, and blocklisting a directory only moves the target.
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
