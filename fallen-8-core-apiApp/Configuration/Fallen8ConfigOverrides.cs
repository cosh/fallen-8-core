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
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Logging;

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

        /// <summary>
        ///   Captures the boot snapshot. Constructed once, immediately after the namespace collection
        ///   has been built, which is the real latch moment: six sections bake their values into
        ///   long-lived state during that construction, so a snapshot taken earlier would record values
        ///   the process had not yet committed to.
        /// </summary>
        public Fallen8ConfigOverrides(IConfigurationRoot configuration, Fallen8ConfigOverridesSource source)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _source = source;
            _bootValues = Snapshot(configuration);
        }

        /// <summary>Whether this instance can persist an override at all (see the source's path rule).</summary>
        public Boolean CanPersist => _source != null && _source.State.IsActive;

        /// <summary>The overrides file, or <c>null</c> when this instance persists none.</summary>
        public String Path => _source?.State.Path;

        /// <summary>What the last load of the overrides file did, or <c>null</c> when there is no layer.</summary>
        public Fallen8ConfigOverridesState State => _source?.State;

        /// <summary>The effective value of a catalogued key right now, as configuration text.</summary>
        public String CurrentValue(String key)
        {
            return _configuration[key];
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
        /// </summary>
        public Boolean IsRestartPending(Fallen8SettingEntry entry)
        {
            if (entry == null || entry.Tier != Fallen8SettingTier.Restart)
            {
                return false;
            }

            return !String.Equals(BootValue(entry.Key), CurrentValue(entry.Key), StringComparison.Ordinal);
        }

        /// <summary>Every catalogued key whose written value is waiting for a restart, in catalog order.</summary>
        public IReadOnlyList<Fallen8SettingEntry> PendingRestart()
        {
            return Fallen8SettingCatalog.Entries.Where(IsRestartPending).ToList();
        }

        /// <summary>
        ///   Which layer supplies a key's effective value, resolved by walking the providers in reverse
        ///   and asking the first one that declares it. Reverse order is what makes the answer the
        ///   EFFECTIVE source rather than merely a source that mentions the key.
        /// </summary>
        public Fallen8SettingSource SourceOf(String key)
        {
            foreach (var provider in _configuration.Providers.Reverse())
            {
                var resolved = Classify(provider, key);
                if (resolved.HasValue)
                {
                    return resolved.Value;
                }
            }

            return Fallen8SettingSource.Default;
        }

        private static Fallen8SettingSource? Classify(IConfigurationProvider provider, String key)
        {
            if (provider is ChainedConfigurationProvider chained && chained.Configuration is IConfigurationRoot inner)
            {
                foreach (var nested in inner.Providers.Reverse())
                {
                    var resolved = Classify(nested, key);
                    if (resolved.HasValue)
                    {
                        return resolved;
                    }
                }

                return null;
            }

            if (!provider.TryGet(key, out _))
            {
                return null;
            }

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

        private static IReadOnlyDictionary<String, String> Snapshot(IConfiguration configuration)
        {
            var values = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in Fallen8SettingCatalog.Entries)
            {
                values[entry.Key] = configuration[entry.Key];
            }

            return values;
        }

        /// <summary>
        ///   Reports what the overrides layer did at boot: one line per key the environment or command
        ///   line outranked, one per key the file carried that the catalog will not accept, and one for
        ///   a file that could not be read. A stored value that silently loses to the environment is
        ///   exactly the failure this feature exists to remove, so it is never left unsaid.
        /// </summary>
        public void LogState(ILogger logger)
        {
            if (logger == null || _source == null)
            {
                return;
            }

            var state = _source.State;
            if (state.LoadError != null)
            {
                logger.LogError("The stored configuration overrides at {Path} could not be read and were "
                    + "ignored for this boot: {Error}. No setting written through PATCH /config is in "
                    + "effect until the file is valid again.", state.Path, state.LoadError);
            }

            foreach (var key in state.Shadowed)
            {
                logger.LogWarning("The stored override for {Key} is NOT in effect: it is declared in the "
                    + "environment or on the command line, which outranks this instance's stored "
                    + "configuration. Remove {EnvironmentKey} to let the stored value apply.",
                    key, EnvironmentSpelling(key));
            }

            foreach (var key in state.Ignored)
            {
                logger.LogWarning("The stored override for {Key} was ignored: the setting catalog does not "
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
    }
}
