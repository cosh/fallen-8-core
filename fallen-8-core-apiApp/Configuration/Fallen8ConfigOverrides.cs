// MIT License
//
// Fallen8ConfigOverrides.cs
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
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Persistency;

namespace NoSQL.GraphDB.App.Configuration
{
    /// <summary>Which configuration layer a key's effective value came from.</summary>
    public enum Fallen8SettingSource
    {
        /// <summary>No layer sets it; the value is the options class's own default.</summary>
        Default,

        /// <summary><c>appsettings.json</c> or an environment-specific variant.</summary>
        AppSettings,

        /// <summary>Development user secrets.</summary>
        UserSecrets,

        /// <summary>An environment variable, which no stored override may outrank.</summary>
        Environment,

        /// <summary>The command line, which no stored override may outrank.</summary>
        CommandLine,

        /// <summary>
        ///   An in-process host setting, which a stored override MAY outrank. Reported separately from
        ///   the command line on purpose: arbitration only stands down for an environment variable or a
        ///   real command line, so calling this "commandLine" would tell an operator a row is locked
        ///   when a write to it would in fact succeed. In practice only a test host or an embedding host
        ///   supplies a Fallen8 key this way.
        /// </summary>
        Host,

        /// <summary>This instance's stored overrides file.</summary>
        Override
    }

    /// <summary>
    ///   The read model over this instance's configuration (feature writable-instance-config): where
    ///   each catalogued key's value comes from, and which keys have been written since boot but need a
    ///   restart to take effect.
    ///
    ///   <para><b>The pending-restart signal is derived, never stored.</b> This type captures the
    ///   effective value of every catalogued key once, at boot, and a key is pending when its tier is
    ///   restart and its current effective value differs from that snapshot. There is deliberately no
    ///   marker file, no applied flag and no cleanup path: the pending set clears exactly when the
    ///   process restarts, because the reference values only ever existed in memory. It is recomputed
    ///   on every read, so it survives a page reload, a different browser and a reconnect without any
    ///   client cache to synchronise.</para>
    ///
    ///   <para>One nuance the copy this feeds must respect: <c>appsettings.json</c> reloads on change
    ///   and nothing observes it, so the signal also lights up when an operator hand-edits that file.
    ///   The honest wording is "differs from the value this process started with", never "you changed
    ///   this".</para>
    /// </summary>
    public sealed class Fallen8ConfigOverrides
    {
        private readonly IConfigurationRoot _configuration;
        private readonly Fallen8ConfigOverridesSource _source;
        private readonly IReadOnlyDictionary<String, String> _bootValues;
        private readonly ILogger _logger;

        /// <summary>
        ///   Serialises writes. The file is one document read, modified and replaced, so two concurrent
        ///   writers would each persist their own view and the later would drop the earlier's key.
        /// </summary>
        private readonly Object _writeGate = new Object();

        /// <summary>
        ///   Captures the boot snapshot. Constructed once, immediately after the namespace collection
        ///   has been built, which is the real latch moment: six sections bake their values into
        ///   long-lived state during that construction, so a snapshot taken earlier would record values
        ///   the process had not yet committed to.
        /// </summary>
        public Fallen8ConfigOverrides(IConfigurationRoot configuration, Fallen8ConfigOverridesSource source,
            ILogger logger = null)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _source = source;
            _logger = logger;
            _bootValues = Snapshot();
        }

        /// <summary>Whether this instance can persist an override at all (see the source's path rule).</summary>
        public Boolean CanPersist => _source != null && _source.State.IsActive;

        /// <summary>The overrides file, or <c>null</c> when this instance persists none.</summary>
        public String Path => _source?.State.Path;

        /// <summary>What the last load of the overrides file did, or <c>null</c> when there is no layer.</summary>
        public Fallen8ConfigOverridesState State => _source?.State;

        /// <summary>
        ///   The effective value of a catalogued key right now, as text.
        ///
        ///   <para>This is the value the process would USE, not merely the value some configuration layer
        ///   set. The difference is not academic: roughly a quarter of the catalogued keys appear in no
        ///   configuration file at all, so reading configuration alone would report null for them and the
        ///   operator surface would show an empty field for a setting that is very much in force. So the
        ///   owning options class is bound, which fills in its own property defaults, and the value is
        ///   read from the property behind the key.</para>
        /// </summary>
        public String CurrentValue(String key)
        {
            return EffectiveValue(key, new Dictionary<String, Object>(StringComparer.Ordinal));
        }

        /// <summary>
        ///   Every catalogued key's effective value, binding each options class once. The read surface
        ///   publishes 102 keys per request, so it takes this rather than binding a section per key.
        /// </summary>
        public IReadOnlyDictionary<String, String> EffectiveValues()
        {
            var bound = new Dictionary<String, Object>(StringComparer.Ordinal);
            var values = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in Fallen8SettingCatalog.Entries)
            {
                values[entry.Key] = EffectiveValue(entry.Key, bound);
            }

            return values;
        }

        [UnconditionalSuppressMessage("Trimming", "IL2072:RequiresUnreferencedCode",
            Justification = "Trimming is disabled for this application; every type reached here comes from "
                + "Fallen8OptionsSections, whose entries are written out as typeof(...) and are all rooted by "
                + "Services.Configure<T> calls in Program.cs.")]
        private String EffectiveValue(String key, Dictionary<String, Object> bound)
        {
            var section = Fallen8OptionsSections.SectionOf(key);
            var type = Fallen8OptionsSections.TypeOf(section);
            if (type == null)
            {
                return _configuration[key];
            }

            var parts = key.Split(':');

            if (!bound.TryGetValue(section, out var instance))
            {
                // Created and then bound onto, rather than Get(type): a section absent from every
                // configuration file binds to NULL, and those are exactly the keys whose effective value
                // is the class default and which the operator has never been shown.
                instance = Activator.CreateInstance(type);
                _configuration.GetSection(section).Bind(instance);
                bound[section] = instance;
            }

            return instance == null ? _configuration[key] : ReadPath(instance, parts);
        }

        /// <summary>
        ///   Walks the property path a configuration key names (<c>Prometheus:Enabled</c> becomes
        ///   <c>.Prometheus.Enabled</c>) and formats what it finds the way configuration would carry it.
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2075:RequiresUnreferencedCode",
            Justification = "Trimming is disabled for this application; every options class walked here is "
                + "rooted by a Services.Configure<T> call in Program.cs and by Fallen8OptionsSections.")]
        private static String ReadPath(Object instance, String[] parts)
        {
            var current = instance;
            for (var index = 2; index < parts.Length && current != null; index++)
            {
                var property = current.GetType().GetProperty(parts[index],
                    BindingFlags.Public | BindingFlags.Instance);
                if (property == null)
                {
                    return null;
                }

                current = property.GetValue(current);
            }

            switch (current)
            {
                case null:
                    return null;
                case Boolean flag:
                    // Lowercase, so a value read here can be written straight back.
                    return flag ? "true" : "false";
                case String text:
                    return text;
                case IFormattable formattable:
                    return formattable.ToString(null, CultureInfo.InvariantCulture);
                case IEnumerable:
                    // A collection has no single value, and no collection key is writable anyway.
                    return null;
                default:
                    return current.ToString();
            }
        }

        /// <summary>The value this process started with, as configuration text.</summary>
        public String BootValue(String key)
        {
            return _bootValues.TryGetValue(key, out var value) ? value : null;
        }

        /// <summary>
        ///   Whether a written value is waiting for a restart: the key is writable at restart tier and
        ///   its effective value no longer matches what this process started with. A live-tier key is
        ///   never pending, because applying it is what made it live.
        ///
        ///   <para>Pass <paramref name="effectiveValues" /> (one <see cref="EffectiveValues"/> batch)
        ///   when judging many keys: the fallback binds the key's whole options section per call, which
        ///   is exactly the per-key bind storm the batch exists to avoid.</para>
        /// </summary>
        public Boolean IsRestartPending(Fallen8SettingEntry entry,
            IReadOnlyDictionary<String, String> effectiveValues = null)
        {
            if (entry == null || entry.Tier != Fallen8SettingTier.Restart)
            {
                return false;
            }

            var current = effectiveValues != null && effectiveValues.TryGetValue(entry.Key, out var batched)
                ? batched
                : CurrentValue(entry.Key);
            return !String.Equals(BootValue(entry.Key), current, StringComparison.Ordinal);
        }

        /// <summary>Every catalogued key whose written value is waiting for a restart, in catalog order.</summary>
        public IReadOnlyList<Fallen8SettingEntry> PendingRestart(
            IReadOnlyDictionary<String, String> effectiveValues = null)
        {
            var effective = effectiveValues ?? EffectiveValues();
            return Fallen8SettingCatalog.Entries.Where(entry => IsRestartPending(entry, effective)).ToList();
        }

        /// <summary>
        ///   Which layer supplies a key's effective value, resolved by walking the providers in reverse
        ///   and asking the first one that declares it. Reverse order is what makes the answer the
        ///   EFFECTIVE source rather than merely a source that mentions the key.
        /// </summary>
        public Fallen8SettingSource SourceOf(String key)
        {
            // The same flattened provider walk arbitration uses (one home for the unwrap), in reverse,
            // so the answer is the EFFECTIVE source rather than merely a source that mentions the key.
            foreach (var provider in Fallen8ConfigOverridesSource.Flatten(_configuration).Reverse())
            {
                if (!provider.TryGet(key, out _))
                {
                    continue;
                }

                return Classify(provider);
            }

            return Fallen8SettingSource.Default;
        }

        private static Fallen8SettingSource Classify(IConfigurationProvider provider)
        {
            switch (provider)
            {
                case Fallen8ConfigOverridesProvider:
                    return Fallen8SettingSource.Override;
                case EnvironmentVariablesConfigurationProvider:
                    return Fallen8SettingSource.Environment;
                case CommandLineConfigurationProvider:
                    return Fallen8SettingSource.CommandLine;
                case JsonConfigurationProvider json:
                    // User secrets arrive through a JSON provider too, reading secrets.json out of the
                    // user profile rather than a file beside the app.
                    return IsUserSecrets(json) ? Fallen8SettingSource.UserSecrets : Fallen8SettingSource.AppSettings;
                default:
                    // A memory provider or anything else this build does not recognise. Deliberately
                    // NOT reported as an authority: arbitration stands down only for the environment
                    // and the command line, so anything else is a layer an override can beat.
                    return Fallen8SettingSource.Host;
            }
        }

        private static Boolean IsUserSecrets(JsonConfigurationProvider provider)
        {
            var path = provider.Source?.Path;
            return path != null
                && path.IndexOf("secrets.json", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        ///   The boot snapshot, taken over EFFECTIVE values rather than configured ones. That makes the
        ///   pending signal say what it means: writing a key explicitly to the value it already had by
        ///   default changes the configuration but changes nothing about the next boot, so it is correctly
        ///   not pending.
        /// </summary>
        private IReadOnlyDictionary<String, String> Snapshot()
        {
            return EffectiveValues();
        }

        /// <summary>
        ///   Reports what the overrides layer did at boot: one line per key the environment or command
        ///   line outranked, one per key the file carried that the catalog will not accept, and one for
        ///   a file that could not be read. A stored value that silently loses to the environment is
        ///   exactly the failure this feature exists to remove, so it is never left unsaid.
        /// </summary>
        public void LogState()
        {
            if (_logger == null || _source == null)
            {
                return;
            }

            var state = _source.State;
            if (state.LoadError != null)
            {
                _logger.LogError("The stored configuration overrides at {Path} could not be read and were "
                    + "ignored for this boot: {Error}. No setting written through PATCH /config is in "
                    + "effect until the file is valid again.", state.Path, state.LoadError);
            }

            foreach (var key in state.Shadowed)
            {
                _logger.LogWarning("The stored override for {Key} is NOT in effect: it is declared in the "
                    + "environment or on the command line, which outranks this instance's stored "
                    + "configuration. Remove {EnvironmentKey} to let the stored value apply.",
                    key, EnvironmentSpelling(key));
            }

            foreach (var key in state.Ignored)
            {
                _logger.LogWarning("The stored override for {Key} was ignored: the setting catalog does not "
                    + "list it as writable, so it can only have been edited into {Path} by hand.",
                    key, state.Path);
            }
        }

        /// <summary>
        ///   The environment-variable spelling of a configuration key, which is what an operator has to
        ///   remove to let a stored value apply.
        /// </summary>
        public static String EnvironmentSpelling(String key)
        {
            return key?.Replace(":", "__", StringComparison.Ordinal);
        }

        /// <summary>
        ///   Whether an authority declares this key, in which case no stored override can ever take
        ///   effect and a write must be refused rather than stored and shadowed. THE SAME rule as the
        ///   provider's own arbitration, by shared code rather than by parallel implementation: both
        ///   sides use <see cref="Fallen8ConfigOverridesSource.IsAuthority"/> over the same flattened
        ///   provider walk, so they cannot desynchronise.
        /// </summary>
        public Boolean IsAuthorityDeclared(String key)
        {
            foreach (var provider in Fallen8ConfigOverridesSource.Flatten(_configuration))
            {
                if (Fallen8ConfigOverridesSource.IsAuthority(provider) && provider.TryGet(key, out _))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        ///   Persists a batch of overrides and reloads configuration, then returns nothing: the caller
        ///   reads the effective values back off the freshly bound options, which is the only way a
        ///   coerced value becomes visible rather than assumed.
        ///
        ///   <para>Writes are serialised, because this is a read-modify-write of one document: two
        ///   concurrent writers would otherwise each persist their own view and the later one would drop
        ///   the earlier one's key. A <c>null</c> value REMOVES a key, which is the undo, and is why no
        ///   versioning is needed.</para>
        /// </summary>
        public void Write(IReadOnlyDictionary<String, String> values)
        {
            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            if (!CanPersist)
            {
                throw new InvalidOperationException(
                    "This instance has nowhere to persist configuration: Fallen8:Metadata:Directory is not configured.");
            }

            lock (_writeGate)
            {
                // A rewrite starts from what the file holds, so a file that could not be READ must
                // refuse writes: proceeding would rebuild from an empty set and replace every setting
                // the unreadable file still contains, turning one transient corruption (or a newer
                // build's document) into permanent data loss reported as success. Checked INSIDE the
                // gate, because the reload a concurrent write triggers is what records the error.
                if (_source.State.LoadError != null)
                {
                    throw new InvalidOperationException(
                        "The stored configuration at " + Path + " could not be read (" + _source.State.LoadError
                        + "), so writing would replace settings it still holds. Fix or remove the file first.");
                }

                // From Stored, not Applied: a shadowed key contributes nothing right now, but its value
                // is operator intent waiting for the outranking variable to be removed. Keys the
                // catalog refuses (hand-edited, and warned about at boot) are dropped by the rewrite.
                var stored = new SortedDictionary<String, String>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in _source.State.Stored)
                {
                    stored[pair.Key] = pair.Value;
                }

                foreach (var pair in values)
                {
                    if (pair.Value == null)
                    {
                        stored.Remove(pair.Key);
                    }
                    else
                    {
                        stored[pair.Key] = pair.Value;
                    }
                }

                // ReplaceAllTextDurably does not create the directory, and on a fresh deployment this
                // writer can be the first thing to persist anything at all.
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                DurableFileIo.ReplaceAllTextDurably(Path, Serialize(stored), _logger);

                // Reload explicitly. The source carries no file watcher on purpose: one would race this
                // write and the pending-restart derivation that reads its result.
                _configuration.Reload();
            }
        }

        private static String Serialize(IReadOnlyDictionary<String, String> stored)
        {
            var buffer = new ArrayBufferWriter<Byte>();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", Fallen8ConfigOverridesSource.FormatVersion);
                writer.WriteStartObject("settings");
                foreach (var pair in stored)
                {
                    writer.WriteString(pair.Key, pair.Value);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }
}
