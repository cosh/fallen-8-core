// MIT License
//
// Fallen8ConfigOverridesSource.cs
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
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace NoSQL.GraphDB.App.Configuration
{
    /// <summary>
    ///   What one load of <c>config.overrides.json</c> did, so boot can report it and the read surface
    ///   can explain itself. Shared by the source and every provider it builds, because a
    ///   <c>ConfigurationManager</c> rebuilds its providers whenever a source is added and a reference
    ///   to one provider instance would go stale.
    /// </summary>
    public sealed class Fallen8ConfigOverridesState
    {
        private readonly Object _gate = new Object();
        private IReadOnlyDictionary<String, String> _stored = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyDictionary<String, String> _applied = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<String> _shadowed = Array.Empty<String>();
        private IReadOnlyList<String> _ignored = Array.Empty<String>();
        private String _loadError;

        /// <summary>The overrides file, or <c>null</c> when this instance persists no overrides.</summary>
        public String Path { get; }

        /// <summary>Whether an overrides file can be read and written at all.</summary>
        public Boolean IsActive => Path != null;

        internal Fallen8ConfigOverridesState(String path)
        {
            Path = path;
        }

        /// <summary>
        ///   Every valid stored pair the file holds, canonically keyed: the applied ones AND the ones
        ///   an authority currently shadows. This is what a rewrite starts from, because rebuilding
        ///   from <see cref="Applied"/> alone would silently drop a shadowed key's stored value, which
        ///   is an operator's intent waiting for the variable that outranks it to be removed.
        /// </summary>
        public IReadOnlyDictionary<String, String> Stored
        {
            get { lock (_gate) { return _stored; } }
        }

        /// <summary>The keys this layer actually contributed, with the values it contributed.</summary>
        public IReadOnlyDictionary<String, String> Applied
        {
            get { lock (_gate) { return _applied; } }
        }

        /// <summary>
        ///   Keys present in the file that an environment variable or the command line also declares, so
        ///   this layer stood down. Reported at boot: a stored value that silently loses to the
        ///   environment is the failure this feature exists to prevent.
        /// </summary>
        public IReadOnlyList<String> Shadowed
        {
            get { lock (_gate) { return _shadowed; } }
        }

        /// <summary>
        ///   Keys present in the file that this layer refuses to apply because the catalog does not list
        ///   them as writable. The write route can never produce one, so a key here means the file was
        ///   edited by hand.
        /// </summary>
        public IReadOnlyList<String> Ignored
        {
            get { lock (_gate) { return _ignored; } }
        }

        /// <summary>Why the file could not be read, or <c>null</c>. A load failure is never fatal.</summary>
        public String LoadError
        {
            get { lock (_gate) { return _loadError; } }
        }

        internal void Record(IReadOnlyDictionary<String, String> stored,
            IReadOnlyDictionary<String, String> applied, IReadOnlyList<String> shadowed,
            IReadOnlyList<String> ignored, String loadError)
        {
            lock (_gate)
            {
                _stored = stored;
                _applied = applied;
                _shadowed = shadowed;
                _ignored = ignored;
                _loadError = loadError;
            }
        }
    }

    /// <summary>
    ///   The stored-overrides configuration layer (feature writable-instance-config): a real
    ///   <see cref="IConfigurationSource"/> so a restart-tier write genuinely applies at the next boot
    ///   with no further machinery.
    ///
    ///   <para><b>It arbitrates per key rather than relying on source order.</b> The source is appended
    ///   last, which is what lets it beat <c>appsettings.json</c> (that file ships much of the writable
    ///   set at its code defaults, so a layer underneath would be dead on most of this feature). But
    ///   ordering alone would also beat the environment, which must never happen: the shipped compose
    ///   file declares roughly two dozen <c>Fallen8__</c> variables and the docs tell operators to set
    ///   them by hand. So the provider emits a key only when no environment-variable or command-line
    ///   provider DECLARES it, probed by asking those providers directly. A declared empty string is
    ///   still a declaration, which is what makes compose's <c>${VAR:-}</c> idiom work by construction:
    ///   "unset" is never used as a proxy for "the operator has no opinion".</para>
    ///
    ///   <para><b>It never guesses its own path</b>, see
    ///   <see cref="Resolve(IConfigurationRoot, String)"/>.</para>
    ///
    ///   <para>There is deliberately no file watcher. The write path reloads explicitly, and a watcher
    ///   would race the pending-restart derivation against the writer that caused it.</para>
    /// </summary>
    public sealed class Fallen8ConfigOverridesSource : IConfigurationSource
    {
        /// <summary>The file this layer reads and writes, inside the configured metadata directory.</summary>
        public const String FileName = "config.overrides.json";

        /// <summary>The document's shape version, so a future format change is detectable.</summary>
        public const Int32 FormatVersion = 1;

        private const String SettingsProperty = "settings";
        private const String VersionProperty = "version";

        private readonly IReadOnlyList<IConfigurationProvider> _authorities;

        private Fallen8ConfigOverridesSource(String path, IReadOnlyList<IConfigurationProvider> authorities)
        {
            State = new Fallen8ConfigOverridesState(path);
            _authorities = authorities;
        }

        /// <summary>What the most recent load of the file did.</summary>
        public Fallen8ConfigOverridesState State { get; }

        /// <summary>
        ///   Builds the layer for a configuration root, or returns <c>null</c> when this instance must
        ///   not have one.
        ///
        ///   <para><b>The path comes only from an explicitly configured metadata directory.</b> It must
        ///   never fall back the way <see cref="Fallen8MetadataOptions.ResolveDirectory"/> does, to a
        ///   <c>metadata</c> folder under <see cref="AppContext.BaseDirectory"/>. Under the unit suite
        ///   that folder is the one shared test output directory: an appended-last layer reading a file
        ///   there would outrank the settings dozens of test hosts inject, for the whole run and for
        ///   every later run, and on a developer's machine it would quietly swallow their saved
        ///   configuration under <c>bin</c>. So an instance that has not been told where its metadata
        ///   lives persists no overrides at all, which costs nothing real: the shipped compose
        ///   deployment sets the directory, and an instance that has not configured one also has no API
        ///   key, and without a key there is no configuration write to persist.</para>
        /// </summary>
        public static Fallen8ConfigOverridesSource Resolve(IConfigurationRoot configuration, String metadataDirectory)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            if (String.IsNullOrWhiteSpace(metadataDirectory))
            {
                return null;
            }

            return new Fallen8ConfigOverridesSource(
                System.IO.Path.Combine(metadataDirectory, FileName),
                CollectAuthorities(configuration).ToList());
        }

        /// <summary>
        ///   THE definition of an authority: a provider whose declaration outranks a stored override.
        ///   One home on purpose. The write-refusal (409), the boot-time arbitration and the published
        ///   per-key source all derive from this predicate, so a provider type added here changes all
        ///   three together instead of desynchronising them.
        /// </summary>
        internal static Boolean IsAuthority(IConfigurationProvider provider)
        {
            return provider is EnvironmentVariablesConfigurationProvider
                || provider is CommandLineConfigurationProvider;
        }

        /// <summary>
        ///   Every leaf provider of a configuration root, in configuration order, with chained
        ///   providers unwrapped. Unwrapping matters: a host that composes its configuration would
        ///   otherwise hide the real environment provider behind a wrapper and every check over the
        ///   provider list would silently miss it.
        /// </summary>
        internal static IEnumerable<IConfigurationProvider> Flatten(IConfigurationRoot configuration)
        {
            foreach (var provider in configuration.Providers)
            {
                if (provider is ChainedConfigurationProvider chained
                    && chained.Configuration is IConfigurationRoot inner)
                {
                    foreach (var nested in Flatten(inner))
                    {
                        yield return nested;
                    }
                }
                else
                {
                    yield return provider;
                }
            }
        }

        private static IEnumerable<IConfigurationProvider> CollectAuthorities(IConfigurationRoot configuration)
        {
            return Flatten(configuration).Where(IsAuthority);
        }

        /// <inheritdoc />
        public IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            return new Fallen8ConfigOverridesProvider(this);
        }

        /// <summary>
        ///   Reads the file and decides, key by key, what this layer contributes. Never throws: a
        ///   preferences file that cannot be read must not stop a graph database from starting, so a
        ///   failure is recorded on <see cref="State"/> and reported at boot instead.
        /// </summary>
        internal void LoadInto(IDictionary<String, String> data)
        {
            var stored = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
            var applied = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
            var shadowed = new List<String>();
            var ignored = new List<String>();
            String loadError = null;

            try
            {
                if (State.IsActive && File.Exists(State.Path))
                {
                    foreach (var pair in Read(State.Path))
                    {
                        // Bounded by the catalog's writable set. The write route cannot produce any
                        // other key, so anything else was hand-edited into the file and this layer
                        // declines it rather than becoming a way around the never-writable rules.
                        if (!Fallen8SettingCatalog.TryGet(pair.Key, out var entry) || !entry.IsWritable)
                        {
                            ignored.Add(pair.Key);
                        }
                        else if (IsDeclaredByAuthority(entry.Key))
                        {
                            // Kept in Stored even though it contributes nothing right now: the value
                            // is operator intent waiting for the outranking variable to be removed,
                            // and a rewrite that started from Applied alone would delete it.
                            stored[entry.Key] = pair.Value;
                            shadowed.Add(entry.Key);
                        }
                        else
                        {
                            stored[entry.Key] = pair.Value;
                            applied[entry.Key] = pair.Value;
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is JsonException || exception is IOException
                || exception is UnauthorizedAccessException)
            {
                loadError = exception.Message;
                stored.Clear();
                applied.Clear();
                shadowed.Clear();
                ignored.Clear();
            }

            foreach (var pair in applied)
            {
                data[pair.Key] = pair.Value;
            }

            shadowed.Sort(StringComparer.Ordinal);
            ignored.Sort(StringComparer.Ordinal);
            State.Record(stored, applied, shadowed, ignored, loadError);
        }

        /// <summary>
        ///   Whether an environment variable or the command line declares this key. A declaration is
        ///   what counts, not a non-empty value.
        /// </summary>
        private Boolean IsDeclaredByAuthority(String key)
        {
            foreach (var provider in _authorities)
            {
                if (provider.TryGet(key, out _))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        ///   Reads the stored pairs. Values are kept as text because that is what configuration is; a
        ///   number or boolean written by hand is accepted and used verbatim, so a hand-edited file
        ///   behaves the way its author expects.
        /// </summary>
        private static IEnumerable<KeyValuePair<String, String>> Read(String path)
        {
            using var document = JsonDocument.Parse(ReadAllTextShared(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(SettingsProperty, out var settings)
                || settings.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<KeyValuePair<String, String>>();
            }

            if (root.TryGetProperty(VersionProperty, out var version)
                && version.ValueKind == JsonValueKind.Number
                && version.TryGetInt32(out var stamped)
                && stamped > FormatVersion)
            {
                // A newer writer's document is not guessed at.
                throw new JsonException("config.overrides.json declares format version " + stamped
                    + ", which this build does not understand (it writes version " + FormatVersion + ").");
            }

            var pairs = new List<KeyValuePair<String, String>>();
            foreach (var property in settings.EnumerateObject())
            {
                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        pairs.Add(new KeyValuePair<String, String>(property.Name, property.Value.GetString()));
                        break;
                    case JsonValueKind.Number:
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        pairs.Add(new KeyValuePair<String, String>(property.Name, property.Value.GetRawText()));
                        break;
                    default:
                        // An object, an array or null carries no configuration value. Null in the FILE
                        // is not the write route's "clear this override": clearing removes the key.
                        break;
                }
            }

            return pairs;
        }

        /// <summary>
        ///   Reads with <see cref="FileShare.ReadWrite"/> and a brief retry, the same discipline the
        ///   save-game registry uses: the write path replaces this file atomically and then reloads it
        ///   in the same process, so a plain read would race its own replace window.
        /// </summary>
        private static String ReadAllTextShared(String path)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    return reader.ReadToEnd();
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(25);
                }
            }
        }
    }

    /// <summary>
    ///   The provider half of <see cref="Fallen8ConfigOverridesSource"/>. All decisions live on the
    ///   source, so a rebuilt provider reports into the same state.
    /// </summary>
    public sealed class Fallen8ConfigOverridesProvider : ConfigurationProvider
    {
        private readonly Fallen8ConfigOverridesSource _source;

        internal Fallen8ConfigOverridesProvider(Fallen8ConfigOverridesSource source)
        {
            _source = source;
        }

        /// <inheritdoc />
        public override void Load()
        {
            var data = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
            _source.LoadInto(data);
            Data = data;
        }
    }
}
