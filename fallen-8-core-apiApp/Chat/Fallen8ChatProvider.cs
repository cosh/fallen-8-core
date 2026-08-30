// MIT License
//
// Fallen8ChatProvider.cs
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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;

namespace NoSQL.GraphDB.App.Chat
{
    /// <summary>
    ///   The thin Fallen-8 chat gateway (feature instance-config): it proxies a chat completion to
    ///   the configured <see cref="IChatBackend" /> (the Ollama sidecar by default) so the instance
    ///   is the default model gateway. Unlike the embedding provider it carries NO model-identity
    ///   stamp or fatal-validation latch: a chat completion is not stored or indexed and has no
    ///   dimension/metric contract, so there is nothing fatal to latch. The backend resolves LAZILY
    ///   (nothing is constructed while the capability is off), the model is SERVER-owned, and
    ///   <c>Fallen8:Chat:TimeoutSeconds</c> is the SINGLE deadline on the call: the transport is
    ///   built without one so the configured value cannot be pre-empted by a shorter, undocumented
    ///   bound (see <see cref="Helper.OllamaHttpClientFactory" /> for the deadline rule).
    /// </summary>
    public sealed class Fallen8ChatProvider
    {
        private readonly Fallen8ChatOptions _options;
        private readonly Lazy<IChatBackend> _backend;

        public Fallen8ChatProvider(IOptions<Fallen8ChatOptions> options, Lazy<IChatBackend> backend)
        {
            _options = options.Value;
            _backend = backend;
        }

        /// <summary>Whether the capability flag is on.</summary>
        public Boolean IsEnabled => _options.Enabled;

        /// <summary>The backend selector (config value).</summary>
        public String Backend => _options.Backend;

        /// <summary>
        ///   The server-owned model (config value), whichever backend serves it. Deliberately NOT
        ///   read from <see cref="ProbeTarget" />, which answers the narrower question of which
        ///   Ollama-protocol target can be asked about residency and is null for a backend that has
        ///   no such API - which once made <c>/status</c> and <c>/config</c> report a null model on a
        ///   working deployment. Which key holds it stays
        ///   <see cref="ChatBackendFactory.ResolveModel" />'s single answer.
        /// </summary>
        public String Model => ChatBackendFactory.ResolveModel(_options);

        /// <summary>Whether the backend client has been created (a chat call happened). A config
        /// read never flips this: residency is probed via a transient client (OllamaModelProbe).</summary>
        public Boolean IsLoaded => _backend.IsValueCreated;

        /// <summary>
        ///   What the residency probe should ask, or <c>null</c> when the configured backend speaks
        ///   no protocol that can be asked. Resolved through the backend factory so the config view
        ///   can never report on a different target than a completion would reach - including the
        ///   credential, without which Nahil answers 401 and residency would read "unknown"
        ///   forever with nothing saying why.
        /// </summary>
        internal Helper.OllamaConnection ProbeTarget => ChatBackendFactory.ResolveConnection(_options);

        /// <summary>
        ///   Runs one chat completion, bounded by <c>Fallen8:Chat:TimeoutSeconds</c>. Faults map to:
        ///   <see cref="ChatProviderUnavailableException" /> (503, backend down/init failure),
        ///   <see cref="ChatProviderTimeoutException" /> (504, ANY cancellation that is not the
        ///   caller's - our budget or a transport-originated one), or
        ///   <see cref="ChatProviderOutputException" /> (502, the backend returned no content). A
        ///   caller-driven cancellation propagates as <see cref="OperationCanceledException" />.
        /// </summary>
        public async Task<ChatBackendResult> ChatAsync(IReadOnlyList<ChatTurn> messages,
            ChatBackendOptions options, CancellationToken cancellationToken)
        {
            if (!IsEnabled)
            {
                // Defensive: the endpoint's policy already answers 403 when off, so this is not
                // reached in practice.
                throw new ChatProviderUnavailableException("The chat provider is disabled (Fallen8:Chat:Enabled).");
            }

            IChatBackend backend;
            try
            {
                backend = _backend.Value;
            }
            catch (Exception ex)
            {
                throw new ChatProviderUnavailableException(
                    String.Format("The chat backend '{0}' failed to initialize: {1}", _options.Backend, ex.Message), ex);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            ChatBackendResult result;
            try
            {
                result = await backend.ChatAsync(messages, options, timeoutCts.Token);
            }
            catch (ChatBackendOutputException ex)
            {
                // The backend answered, badly (a truncated stream). The fault is in the response, so
                // it lands on the same 502 an empty response does rather than the 503 that would
                // invite an identical retry.
                throw new ChatProviderOutputException(ex.Message);
            }
            catch (Helper.ModelRetryTimeoutException ex)
            {
                // The backend spent the whole budget asking to be retried. Still a 504 - the
                // caller's configured deadline is what expired - but the message names the model
                // that never answered, so nobody goes looking for a slow generation that never
                // started. A caller who went away in the meantime gets the cancellation they asked
                // for instead: only OUR budget running out is a timeout.
                cancellationToken.ThrowIfCancellationRequested();
                throw new ChatProviderTimeoutException(String.Format(
                    "The chat backend did not respond within Fallen8:Chat:TimeoutSeconds ({0}s). {1}",
                    _options.TimeoutSeconds, ex.Message));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Every cancellation that is NOT the caller's means "the backend did not answer in
                // time" -> 504. The filter deliberately does not test timeoutCts: when it did, a
                // cancellation raised by the transport itself (rather than by our budget) matched
                // neither this filter nor the type-excluding one below, and escaped the provider as
                // an unhandled TaskCanceledException -> HTTP 500. The transport now carries no
                // deadline of its own, so in practice this IS our budget; the widened filter keeps
                // any future inner deadline from re-opening that hole.
                throw new ChatProviderTimeoutException(String.Format(
                    "The chat backend did not respond within Fallen8:Chat:TimeoutSeconds ({0}s).",
                    _options.TimeoutSeconds));
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // Transient by assumption (e.g. the Ollama sidecar is down): 503, never latched.
                throw new ChatProviderUnavailableException(
                    String.Format("The chat backend '{0}' failed to generate: {1}", _options.Backend, ex.Message), ex);
            }

            if (result == null || String.IsNullOrEmpty(result.Content))
            {
                throw new ChatProviderOutputException("The chat backend returned an empty response.");
            }

            return result;
        }

    }

    /// <summary>The chat provider or its backend is not usable right now - a configuration error,
    /// initialization failure, or an unreachable sidecar. Maps to 503.</summary>
    public sealed class ChatProviderUnavailableException : Exception
    {
        public ChatProviderUnavailableException(String message, Exception inner = null)
            : base(message, inner)
        {
        }
    }

    /// <summary>The proxy exceeded <c>Fallen8:Chat:TimeoutSeconds</c> waiting for the backend.
    /// Maps to 504.</summary>
    public sealed class ChatProviderTimeoutException : Exception
    {
        public ChatProviderTimeoutException(String message)
            : base(message)
        {
        }
    }

    /// <summary>The backend returned a garbled/empty response (no assistant content). Maps to 502.</summary>
    public sealed class ChatProviderOutputException : Exception
    {
        public ChatProviderOutputException(String message)
            : base(message)
        {
        }
    }
}
