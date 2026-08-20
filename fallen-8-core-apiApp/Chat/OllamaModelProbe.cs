// MIT License
//
// OllamaModelProbe.cs
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
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NoSQL.GraphDB.App.Helper;
using OllamaSharp;

namespace NoSQL.GraphDB.App.Chat
{
    /// <summary>The live residency of a model in an Ollama sidecar (feature instance-config).</summary>
    internal sealed class OllamaModelState
    {
        /// <summary>Whether the model is currently loaded in the sidecar (present in <c>/api/ps</c>).</summary>
        public Boolean Resident { get; init; }

        /// <summary>Whether it holds VRAM (on GPU). Only meaningful when <see cref="Resident"/>.</summary>
        public Boolean Gpu { get; init; }
    }

    /// <summary>
    ///   Best-effort residency probe against an Ollama-protocol endpoint (feature instance-config).
    ///   It asks <c>/api/ps</c> whether the configured model is currently loaded and, if so, whether
    ///   it holds VRAM. Deliberately uses a TRANSIENT client so a config read never touches a
    ///   provider's lazy backend state (probing must not flip a provider's "loaded" flag), and
    ///   swallows every failure to <c>null</c> ("unknown") so a hung or absent backend can never
    ///   stall or fail the read. Both halves of that guarantee are owned HERE: the swallow below,
    ///   and <see cref="ProbeTimeout" /> on the transport. A caller cancellation is the one thing
    ///   that is NOT swallowed - it propagates, so a disconnected client's config read stops.
    ///   <para>
    ///     It probes THROUGH the connection rather than a bare endpoint, which is what carries a
    ///     Nahil credential: <c>/api/ps</c> is authenticated there too, and a keyless probe
    ///     would 401, be swallowed, and report residency "unknown" forever with nothing in the logs
    ///     to say why. It never retries a warm-up (see <see cref="OllamaHttpClientFactory" />) -
    ///     "unknown" now beats a correct answer after a model pull.
    ///   </para>
    /// </summary>
    internal static class OllamaModelProbe
    {
        /// <summary>The probe's own stall bound. A residency answer is a nice-to-have on a config
        /// read, so it is deliberately short: better "unknown" than a slow config page.</summary>
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

        /// <summary>
        ///   Resident + GPU when the <c>/api/ps</c> call succeeds (Resident=false when the call
        ///   answers but the model is not loaded right now — Ollama loads on demand and unloads when
        ///   idle); <c>null</c> when there is nothing to probe or the call fails (unknown).
        /// </summary>
        /// <param name="connection">What to ask, credential included; <c>null</c> or unusable means
        /// there is nothing to probe.</param>
        /// <param name="cancellationToken">The caller's; its cancellation propagates.</param>
        /// <param name="handler">A test-supplied transport handler; used verbatim when non-null.</param>
        internal static async Task<OllamaModelState> ProbeAsync(OllamaConnection connection,
            CancellationToken cancellationToken, HttpMessageHandler handler = null)
        {
            if (connection == null || !connection.IsValid(out _))
            {
                return null;
            }

            var model = connection.Model;

            try
            {
                // The probe owns its stall bound rather than borrowing the caller's: the
                // "can never stall" guarantee above is stated here, so a second caller (or one
                // passing CancellationToken.None) gets it too. Disposed per call because
                // OllamaSharp does NOT dispose an injected client - the previous transient
                // OllamaApiClient leaked one HttpClient and connection pool on every GET /config.
                using var http = OllamaHttpClientFactory.CreateForProbe(connection, ProbeTimeout, handler);
                var client = new OllamaApiClient(http, model);
                var running = await client.ListRunningModelsAsync(cancellationToken);
                if (running == null)
                {
                    return null;
                }

                foreach (var m in running)
                {
                    if (m?.Name == null || !ModelMatches(m.Name, model))
                    {
                        continue;
                    }

                    return new OllamaModelState { Resident = true, Gpu = m.SizeVram > 0 };
                }

                // The call answered and the model is NOT loaded right now (definitively not resident).
                return new OllamaModelState { Resident = false, Gpu = false };
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // "Unknown" for every backend failure, including this probe's own 3s bound.
                return null;
            }
        }

        /// <summary>Tolerates a <c>:tag</c> on either side ("phi4-f8-mini" vs "phi4-f8-mini:latest").</summary>
        private static Boolean ModelMatches(String resident, String configured)
        {
            if (String.Equals(resident, configured, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return String.Equals(resident.Split(':')[0], configured.Split(':')[0], StringComparison.OrdinalIgnoreCase);
        }
    }
}
