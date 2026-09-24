// MIT License
//
// Fallen8LiveSettings.cs
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace NoSQL.GraphDB.App.Configuration
{
    /// <summary>
    ///   Pushes live-tier settings into the running process (feature writable-instance-config phase 4).
    ///
    ///   <para><b>It runs on every configuration reload, not only after a write.</b> A write is not the
    ///   only way configuration moves: <c>appsettings.json</c> is registered with reload-on-change in
    ///   production, so a hand-edited file changes what <c>GET /config</c> reports. If the apply ran only
    ///   from the write path, a live key's published value would then differ from the value actually in
    ///   force, and the pending-restart signal deliberately says nothing about live keys, so nothing would
    ///   flag it. Driving the apply from the reload token instead makes "live" true however the value
    ///   changed.</para>
    ///
    ///   <para><b>A failing delegate never fails the write.</b> By the time it runs the value is already
    ///   persisted and reloaded, so the honest outcome is not an error but a live key that did not take
    ///   effect: the failure is recorded per key, logged, and reported back by the write path, which
    ///   downgrades that key's promise from applied to restart-required.</para>
    /// </summary>
    public sealed class Fallen8LiveSettings
    {
        private readonly IServiceProvider _services;
        private readonly IConfigurationRoot _configuration;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<String, String> _failures =
            new ConcurrentDictionary<String, String>(StringComparer.Ordinal);

        private IDisposable _subscription;

        public Fallen8LiveSettings(IServiceProvider services, IConfigurationRoot configuration, ILogger logger = null)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger;
        }

        /// <summary>
        ///   Starts applying live settings on every reload. Called once the host is built, so a reload
        ///   during startup cannot run a delegate against half-constructed services.
        /// </summary>
        public void Start()
        {
            _subscription ??= ChangeToken.OnChange(_configuration.GetReloadToken, ApplyAll);
        }

        /// <summary>Why a live key did not take effect, or <c>null</c> when it did.</summary>
        public String FailureFor(String key)
        {
            return key != null && _failures.TryGetValue(key, out var failure) ? failure : null;
        }

        /// <summary>
        ///   Pushes every live key into the running process. Each key is independent: one failing delegate
        ///   must not stop the rest from applying, because the alternative is a batch where the keys that
        ///   could have applied silently did not.
        /// </summary>
        public void ApplyAll()
        {
            foreach (var entry in Fallen8SettingCatalog.Entries.Where(e => e.Tier == Fallen8SettingTier.Live))
            {
                Apply(entry);
            }
        }

        private void Apply(Fallen8SettingEntry entry)
        {
            try
            {
                entry.ApplyNow(_services);
                _failures.TryRemove(entry.Key, out _);
            }
            catch (Exception exception)
            {
                // Deliberately broad: an apply delegate reaches into running subsystems, and no failure
                // there justifies leaving the remaining live keys unapplied or taking the process down.
                _failures[entry.Key] = exception.Message;
                if (_logger != null)
                {
                    _logger.LogError(exception,
                        "The live setting {Key} could not be applied to the running process, so it now needs a "
                        + "restart to take effect. Its value IS stored and will be used at the next boot.",
                        entry.Key);
                }
            }
        }
    }
}
