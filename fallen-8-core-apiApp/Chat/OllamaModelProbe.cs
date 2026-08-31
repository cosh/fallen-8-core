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
    /// <summary>The live residency of a model behind an Ollama-protocol endpoint (features
    /// instance-config and nahil-backend). Absence of this object is "unknown", which is a THIRD
    /// state and not a synonym for <see cref="Resident"/> being false - see
    /// <see cref="OllamaModelProbe" />.</summary>
    /// <remarks>
    ///   Public for the test project rather than because a caller outside this assembly needs it -
    ///   the repository adds no <c>InternalsVisibleTo</c>, the same reason
    ///   <see cref="OllamaConnection" /> is public.
    /// </remarks>
    public sealed class OllamaModelState
    {
        /// <summary>Whether the model is currently loaded on the backend (present in
        /// <c>/api/ps</c>).</summary>
        public Boolean Resident { get; init; }

        /// <summary>Whether it holds VRAM (on GPU). Only meaningful when <see cref="Resident"/>, and
        /// <c>null</c> when the backend publishes no VRAM figure at all - which is every Nahil
        /// answer, where the model runs on a remote worker whose device this host cannot see.</summary>
        public Boolean? Gpu { get; init; }
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
    ///   <para>
    ///     <b>A missing entry means different things per backend, which is why this class has to know
    ///     which backend it is asking.</b> The local sidecar's <c>/api/ps</c> enumerates everything it
    ///     has loaded, so a model absent from the list is definitively not resident. Nahil's does not:
    ///     it reports only the model classes it keeps warm, and a model it is actively serving can be
    ///     absent while it serves. Measured against <c>api.nahil.dev</c> (2026-08-31): a successful
    ///     <c>/api/embed</c> for <c>bge-m3</c> (class <c>C2</c>) left <c>/api/ps</c> answering an
    ///     EMPTY model list - during the request and immediately after it - while the chat model
    ///     <c>phi4-f8-mini</c> (class <c>S1</c>) did appear, carrying <c>nahil_workers_warm</c> and an
    ///     <c>expires_at</c> a few minutes out. So on Nahil an absent model is <c>null</c>
    ///     ("unknown"), never <c>false</c>. Reporting the guess is what made <c>GET /config</c> tell a
    ///     Studio operator that an embedding provider they had just used was "not loaded (loads on
    ///     first use)" forever: a definite residency answer outranks the provider's own lazy
    ///     <c>loaded</c> flag, so the one honest signal on that card lost to a false negative.
    ///   </para>
    /// </summary>
    /// <remarks>
    ///   Public for the test project rather than because a caller outside this assembly needs it -
    ///   the repository adds no <c>InternalsVisibleTo</c>, the same reason
    ///   <see cref="OllamaConnection" /> is public. The per-backend reading of a missing
    ///   <c>/api/ps</c> entry is the part that MUST be pinned by a test: it is a claim about a third
    ///   party's behaviour, so nothing in this repository would notice it drifting.
    /// </remarks>
    public static class OllamaModelProbe
    {
        /// <summary>The probe's own stall bound. A residency answer is a nice-to-have on a config
        /// read, so it is deliberately short: better "unknown" than a slow config page.</summary>
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

        /// <summary>
        ///   Resident + GPU when the <c>/api/ps</c> call succeeds AND its answer can be read as one
        ///   about this model (Resident=false only on the local sidecar, whose list is exhaustive);
        ///   <c>null</c> when there is nothing to probe, the call fails, or the backend does not
        ///   report on this model - all three are "unknown", see the class remark.
        /// </summary>
        /// <param name="connection">What to ask, credential included; <c>null</c> or unusable means
        /// there is nothing to probe.</param>
        /// <param name="cancellationToken">The caller's; its cancellation propagates.</param>
        /// <param name="handler">A test-supplied transport handler; used verbatim when non-null.</param>
        public static async Task<OllamaModelState> ProbeAsync(OllamaConnection connection,
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

                    return new OllamaModelState
                    {
                        Resident = true,
                        Gpu = connection.IsNahil ? (Boolean?)null : m.SizeVram > 0,
                    };
                }

                // The call answered without listing the model. Definitive on the local sidecar
                // (Ollama loads on demand and unloads when idle); NOT an answer about the model on
                // Nahil, which reports only the classes it keeps warm - see the class remark.
                return connection.IsNahil
                    ? null
                    : new OllamaModelState { Resident = false, Gpu = false };
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
