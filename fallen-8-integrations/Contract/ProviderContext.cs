// MIT License
//
// ProviderContext.cs
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
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Integrations.Credentials;

namespace NoSQL.GraphDB.Integrations.Contract
{
    /// <summary>
    ///   THE WHOLE OF WHAT A PROVIDER IS HANDED, and therefore the deliverable the whole feature exists
    ///   for: every irreversible decision (claim canonicalisation, resolution, reconciliation, index repair,
    ///   deletion safety) is on the runtime's side of this line, so the worst a wrong provider can do is
    ///   describe its source wrongly, which is visible in its snapshot. Move any of it across the line and
    ///   reviewing a new integration means re-reviewing identity, which is exactly the review this boundary
    ///   exists to remove.
    ///
    ///   <para>Note what is NOT here: the graph, a target, an element id, whether an entity was created or
    ///   matched, a file path, a credential in <see cref="Settings"/>, and any notion of time or schedule.</para>
    /// </summary>
    public sealed class ProviderContext
    {
        private readonly IReadOnlyDictionary<String, String> _settings;
        private readonly CredentialLease _credentials;
        private readonly Func<String, CancellationToken, Task<String>> _readFile;
        private readonly Func<String, String?> _resolveFileFailure;

        internal ProviderContext(String providerId, String instanceId,
            IReadOnlyDictionary<String, String> settings, CredentialLease credentials, HttpClient http,
            ILogger logger, IList<DiagnosticDto> diagnostics,
            Func<String, CancellationToken, Task<String>> readFile, Func<String, String?> resolveFileFailure)
        {
            ProviderId = providerId;
            InstanceId = instanceId;
            _settings = settings;
            _credentials = credentials;
            Http = http;
            Logger = logger;
            Diagnostics = diagnostics;
            _readFile = readFile;
            _resolveFileFailure = resolveFileFailure;
        }

        /// <summary>The provider this run is running.</summary>
        public String ProviderId { get; }

        /// <summary>The identity this run asserts as. A provider records it on its snapshot and does
        /// nothing else with it: every claim key that needs it is composed by the runtime.</summary>
        public String InstanceId { get; }

        /// <summary>
        ///   The non-credential settings, keyed as the descriptor declares them and re-keyed
        ///   case-insensitively. Credentials are deliberately not among them: a setting is neither leased
        ///   nor redacted, so a credential arriving as one would be logged and reported like any other value.
        /// </summary>
        public IReadOnlyDictionary<String, String> Settings => _settings;

        /// <summary>
        ///   The client a provider reaches its source with. A delegating handler on it enforces the
        ///   allowed-host list on the way OUT, so the guard need not know which setting is the address.
        /// </summary>
        public HttpClient Http { get; }

        /// <summary>The provider's logger. Every sink it reaches runs behind the credential redaction wrap.</summary>
        public ILogger Logger { get; }

        /// <summary>
        ///   What the source could not tell this run. Adding here is equivalent to putting the diagnostic on
        ///   the returned snapshot: the runner merges both into the report's one list, and diagnostics are
        ///   never dropped.
        /// </summary>
        public IList<DiagnosticDto> Diagnostics { get; }

        /// <summary>Whether the run's credential lease has ended, which it has once the run is over.</summary>
        public Boolean CredentialsEnded => _credentials.Ended;

        /// <summary>A required setting, or a configuration failure naming the key.</summary>
        public String Required(String key)
        {
            if (_settings.TryGetValue(key, out var value) && !String.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            throw new ProviderConfigurationException(String.Format(
                "Setting '{0}' is required and was not supplied.", key));
        }

        /// <summary>An optional setting, or <paramref name="fallback"/>.</summary>
        public String? Optional(String key, String? fallback = null)
        {
            return _settings.TryGetValue(key, out var value) && !String.IsNullOrWhiteSpace(value)
                ? value
                : fallback;
        }

        /// <summary>An optional whole-number setting, or <paramref name="fallback"/> when absent; a
        /// configuration failure when present and not a number.</summary>
        public Int32 OptionalNumber(String key, Int32 fallback)
        {
            var text = Optional(key);
            if (text == null)
            {
                return fallback;
            }

            if (Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            throw new ProviderConfigurationException(String.Format(
                "Setting '{0}' must be a whole number; '{1}' is not.", key, text));
        }

        /// <summary>An optional yes-or-no setting, or <paramref name="fallback"/> when absent; a
        /// configuration failure when present and not a boolean.</summary>
        public Boolean OptionalBoolean(String key, Boolean fallback)
        {
            var text = Optional(key);
            if (text == null)
            {
                return fallback;
            }

            if (Boolean.TryParse(text, out var value))
            {
                return value;
            }

            throw new ProviderConfigurationException(String.Format(
                "Setting '{0}' must be true or false; '{1}' is not.", key, text));
        }

        /// <summary>
        ///   The value of a required credential setting. It has a LIFETIME: the lease is disposed in a
        ///   <c>finally</c> spanning both the source read and the graph write, and a read after that throws,
        ///   which is how a provider that kept the context finds out rather than quietly authenticating with
        ///   a password the operator rotated away.
        /// </summary>
        public String RequiredCredential(String settingKey)
        {
            return _credentials.Require(settingKey);
        }

        /// <summary>The value of an optional credential setting.</summary>
        public Boolean TryGetCredential(String settingKey, out String? value)
        {
            return _credentials.TryGet(settingKey, out value);
        }

        /// <summary>
        ///   Reads the file a job carried for that setting. A provider never opens a file itself, and since
        ///   feature integration-file-upload that is structural rather than guarded: the runtime opens
        ///   nothing on disk, so there is no path to be pointed anywhere and no directory to contain a name
        ///   within. It also means a provider needs no file system to be tested, which is what lets the
        ///   conformance suite exercise the whole path offline. What a file IS lives on
        ///   <c>SettingKind.File</c>.
        /// </summary>
        public Task<String> ReadFileAsync(String settingKey, CancellationToken cancellationToken)
        {
            return _readFile(settingKey, cancellationToken);
        }

        /// <summary>
        ///   The text of the file a REQUIRED setting names, with anything that went wrong on the way turned
        ///   into a source failure naming the setting. A cancellation and a configuration failure pass
        ///   through untouched: both already name the right system, and calling a bad file name a source
        ///   failure sends an operator to look at the file rather than at the job.
        /// </summary>
        /// <exception cref="ProviderConfigurationException">The setting was not supplied.</exception>
        /// <exception cref="ProviderSourceException">The file could not be read.</exception>
        public async Task<String> RequireFileTextAsync(String settingKey, CancellationToken cancellationToken)
        {
            var fileName = Required(settingKey);
            try
            {
                return await _readFile(settingKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException
                                            && failure is not ProviderConfigurationException
                                            && failure is not ProviderSourceException)
            {
                throw new ProviderSourceException(String.Format(CultureInfo.InvariantCulture,
                    "The file '{0}', named by setting '{1}', could not be read: {2}. The run fails and " +
                    "withdraws nothing: reporting an empty source would withdraw every element this " +
                    "identity claimed, because \"I could not look\" must never become \"there is nothing " +
                    "there\".", fileName, settingKey, failure.Message), failure);
            }
        }

        /// <summary>
        ///   Whether the file a setting names can be resolved at all, without reading it: null when it can,
        ///   otherwise why not. For a provider that wants to fail early with a good message.
        /// </summary>
        public Boolean TryResolveFile(String settingKey, out String? failure)
        {
            failure = _resolveFileFailure(settingKey);
            return failure == null;
        }
    }

    /// <summary>
    ///   "This job cannot be run as written." Raised by <see cref="ProviderContext.Required(String)"/> and
    ///   by a provider that finds a setting unusable, and reported with <c>errorKind</c>
    ///   <c>configuration</c>: "the job is wrong", "the password is wrong", "the console will not
    ///   answer" and "the graph will not answer" send a reader to four different places, and only a named
    ///   kind gets them there.
    /// </summary>
    public sealed class ProviderConfigurationException : Exception
    {
        public ProviderConfigurationException(String message)
            : base(message)
        {
        }

        public ProviderConfigurationException(String message, Exception inner)
            : base(message, inner)
        {
        }
    }

    /// <summary>
    ///   "The source did not answer, or answered unusably." A provider raises it rather than returning an
    ///   empty snapshot, because an answer that cannot be trusted is a failure and not an empty source: "I
    ///   could not look" must never become "there is nothing there". A run that fails withdraws nothing.
    /// </summary>
    public sealed class ProviderSourceException : Exception
    {
        public ProviderSourceException(String message)
            : base(message)
        {
        }

        public ProviderSourceException(String message, Exception inner)
            : base(message, inner)
        {
        }
    }

    /// <summary>
    ///   "The source REJECTED the credential." Reported with <c>errorKind</c> <c>credential</c>, which is
    ///   the whole reason that field exists: an unreachable console and a refused key send a reader to two
    ///   different places, and a provider that raised <see cref="ProviderSourceException"/> for a 401 would
    ///   send them both to the network.
    ///
    ///   <para>It is a failure kind and not a diagnostic: the run fails and withdraws nothing, exactly as an
    ///   unreadable credential does. A source that answers "who are you" is not a source that answered, so
    ///   treating it as an empty read would withdraw every claim the instance ever made.</para>
    ///
    ///   <para>A provider raising this must say what to check and must never quote the credential: this
    ///   message reaches the job report and every log sink.</para>
    /// </summary>
    public sealed class ProviderCredentialRejectedException : Exception
    {
        public ProviderCredentialRejectedException(String message)
            : base(message)
        {
        }

        public ProviderCredentialRejectedException(String message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
