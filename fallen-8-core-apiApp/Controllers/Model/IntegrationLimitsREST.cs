// MIT License
//
// IntegrationLimitsREST.cs
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
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   What a job may carry, as the ceiling that actually BINDS for a caller of this instance
    ///   (feature integration-file-transport).
    ///
    ///   <para>The numbers originate in the other deployable's configuration
    ///   (<c>Integrations:MaxFileBytes</c>, <c>Integrations:MaxJobFileBytes</c>,
    ///   <c>Integrations:MaxJobFiles</c>), but every request arrives through this proxy's own transport
    ///   bound, so a runtime ceiling above that bound is not what a caller would experience. The two
    ///   byte numbers are therefore reconciled here and served as one answer per question. A caller
    ///   combining two ceilings itself was the shape that let Studio carry a ceiling of its own,
    ///   BELOW the runtime's, and refuse jobs the instance would have accepted.</para>
    ///
    ///   <para>Zero or less means that ceiling is switched off, which only the count can report: the
    ///   byte ceilings always have this proxy's transport bound behind them.</para>
    /// </summary>
    public sealed class IntegrationLimitsREST
    {
        /// <summary>The most DECODED bytes one file may carry.</summary>
        /// <example>134217728</example>
        [JsonPropertyName("maxFileBytes")]
        public Int64 MaxFileBytes
        {
            get; set;
        }

        /// <summary>The most DECODED bytes one job's files may come to in total, across every file
        /// setting on it. One request carries a whole run, so this is the sum the runtime holds at once.</summary>
        /// <example>587202560</example>
        [JsonPropertyName("maxJobFileBytes")]
        public Int64 MaxJobFileBytes
        {
            get; set;
        }

        /// <summary>How many files one job may carry, counted across every file setting. Bounds the
        /// number of payloads rather than their size, which the byte ceilings cannot: a one-byte file is
        /// legal, so a set can satisfy both of them and still be unreasonable.</summary>
        /// <example>256</example>
        [JsonPropertyName("maxJobFiles")]
        public Int32 MaxJobFiles
        {
            get; set;
        }
    }
}
