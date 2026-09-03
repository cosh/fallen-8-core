// MIT License
//
// DiagnosticBudget.cs
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
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Integrations.Contract;

namespace NoSQL.GraphDB.Integrations.Run
{
    /// <summary>
    ///   HOW MANY DIAGNOSTICS OF ONE CODE A REPORT CARRIES, and where the rest of them go. The report keeps
    ///   the first <see cref="PerCode" /> of each code and one
    ///   <see cref="DiagnosticCodes.DiagnosticsElided" /> entry counting what it left off; the LOG carries
    ///   every one of them, at <see cref="LogLevel.Debug" /> under <see cref="LogCategory" />.
    ///
    ///   <para>It exists because the report is READ BY A PERSON. A system extract set resolves references
    ///   across its union, so a partial export raises one unresolved-reference diagnostic per dangling
    ///   reference and a real vehicle job produced tens of thousands of them: a table nobody scrolls,
    ///   several megabytes of the response, and the two diagnostics that meant something buried in the
    ///   middle of it. Ten of a code is enough to see the pattern - they are all under one package, or all
    ///   one relation type - and the count is what says how big the thing is.</para>
    ///
    ///   <para>RUNTIME-WIDE rather than a property of the integration that provoked it: the same shape is
    ///   reachable by any provider whose source is large enough, and a per-provider rule would be re-argued
    ///   for each one and forgotten by the next. The providers that already aggregate deliberately
    ///   (<see cref="DiagnosticCodes.ArxmlRedeclaredPaths" />,
    ///   <see cref="DiagnosticCodes.ClientsWithoutHardwareIdentity" />) are unaffected, because they never
    ///   reach the cap; this is the floor under the ones that did not think to.</para>
    ///
    ///   <para>Nothing is LOST, which is why the log line comes first: an operator who needs the individual
    ///   subjects raises this one category to debug and runs the job again, and the report's elision entry
    ///   is what tells them the detail exists. The category is its own so that asking for the diagnostics
    ///   does not also turn on every other debug line this runtime has.</para>
    /// </summary>
    public static class DiagnosticBudget
    {
        /// <summary>
        ///   The log category the detail is written under, and the one an operator raises to <c>Debug</c>
        ///   (<c>Logging__LogLevel__NoSQL.GraphDB.Integrations.Run.DiagnosticBudget=Debug</c>). Spelled out
        ///   rather than taken from a type: a static class cannot be a logger's type argument, and a
        ///   renamed class must not silently change what an operator configured.
        /// </summary>
        public const String LogCategory = "NoSQL.GraphDB.Integrations.Run.DiagnosticBudget";

        /// <summary>How many diagnostics of ONE code a report keeps, as examples of the rest.</summary>
        public const Int32 PerCode = 10;

        /// <summary>
        ///   Writes every diagnostic to the log and leaves the report with at most <see cref="PerCode" /> of
        ///   each code, plus one elision entry per code that had more, in the order the codes first
        ///   appeared.
        ///
        ///   <para>Called with the report already SCRUBBED, which is not an ordering detail: a diagnostic a
        ///   provider quoted a credential into must not become a log line the redaction net has to
        ///   catch.</para>
        /// </summary>
        /// <param name="diagnostics">The report's list, edited in place.</param>
        /// <param name="logger">A logger on <see cref="LogCategory" />.</param>
        /// <returns>How many diagnostics were left off the report.</returns>
        public static Int32 Apply(IList<DiagnosticDto> diagnostics, ILogger logger)
        {
            if (diagnostics == null)
            {
                throw new ArgumentNullException(nameof(diagnostics));
            }

            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            // BEFORE anything is taken off, so the log is the whole account however the cap falls. Guarded
            // rather than left to the sink, because formatting tens of thousands of lines for a level
            // nobody enabled is the one cost this is not allowed to have.
            if (logger.IsEnabled(LogLevel.Debug))
            {
                foreach (var diagnostic in diagnostics)
                {
                    logger.LogDebug("Integration diagnostic {Code} on {Subject}: {Message}",
                        diagnostic.Code, diagnostic.Subject ?? "the run", diagnostic.Message);
                }
            }

            var seen = new Dictionary<String, Int32>(StringComparer.Ordinal);
            var elided = new Dictionary<String, Int32>(StringComparer.Ordinal);
            var elidedOrder = new List<String>();
            var kept = new List<DiagnosticDto>(diagnostics.Count);

            foreach (var diagnostic in diagnostics)
            {
                // A null code is a defect in whoever raised it rather than something to throw over here: it
                // counts as its own group, and the report still carries the message.
                var code = diagnostic.Code ?? String.Empty;
                seen.TryGetValue(code, out var soFar);
                seen[code] = soFar + 1;

                if (soFar < PerCode)
                {
                    kept.Add(diagnostic);
                    continue;
                }

                if (!elided.TryGetValue(code, out var dropped))
                {
                    elidedOrder.Add(code);
                }

                elided[code] = dropped + 1;
            }

            if (elidedOrder.Count == 0)
            {
                return 0;
            }

            var total = 0;
            foreach (var code in elidedOrder)
            {
                var count = elided[code];
                total += count;
                kept.Add(new DiagnosticDto(DiagnosticCodes.DiagnosticsElided, String.Format(
                    CultureInfo.InvariantCulture,
                    "{0} further diagnostics with this code were left off the report, which keeps the " +
                    "first {1} of each code. Every one of them is in this run's log at debug level, under " +
                    "the category {2}.", count, PerCode, LogCategory), code));
            }

            diagnostics.Clear();
            foreach (var diagnostic in kept)
            {
                diagnostics.Add(diagnostic);
            }

            // At INFORMATION, and only when something was actually elided: it is how an operator reading an
            // ordinary log learns both that there was more and which knob produces it. The elision entries
            // on the report say as much per code, but a report is read once by whoever ran the job, while
            // the log is what an alert arrives from.
            logger.LogInformation(
                "{Elided} of this run's diagnostics were left off the report, which keeps the first " +
                "{PerCode} of each of {Codes} code(s). Raise the log category {Category} to Debug for " +
                "every one of them.",
                total, PerCode, elidedOrder.Count, LogCategory);

            return total;
        }
    }
}
