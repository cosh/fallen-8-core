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
using System.Threading;
using System.Threading.Tasks;
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
    ///   Best-effort residency probe against an Ollama endpoint (feature instance-config). It asks
    ///   <c>/api/ps</c> whether the configured model is currently loaded and, if so, whether it
    ///   holds VRAM. Deliberately uses a TRANSIENT client so a config read never touches a
    ///   provider's lazy backend state (probing must not flip a provider's "loaded" flag), and
    ///   swallows every failure to <c>null</c> ("unknown") so a hung or absent sidecar can never
    ///   stall or fail the read.
    /// </summary>
    internal static class OllamaModelProbe
    {
        /// <summary>
        ///   Resident + GPU when the <c>/api/ps</c> call succeeds (Resident=false when the call
        ///   answers but the model is not loaded right now — Ollama loads on demand and unloads when
        ///   idle); <c>null</c> when the endpoint/model is unset or the call fails (unknown).
        /// </summary>
        internal static async Task<OllamaModelState> ProbeAsync(String endpoint, String model, CancellationToken cancellationToken)
        {
            if (String.IsNullOrWhiteSpace(endpoint) || String.IsNullOrWhiteSpace(model))
            {
                return null;
            }

            try
            {
                var client = new OllamaApiClient(new Uri(endpoint), model);
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
            catch
            {
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
