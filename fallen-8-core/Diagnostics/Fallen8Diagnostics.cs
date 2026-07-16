// MIT License
//
// Fallen8Diagnostics.cs
//
// Copyright (c) 2025 Henning Rauch
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
using System.Diagnostics;

namespace NoSQL.GraphDB.Core.Diagnostics
{
    /// <summary>
    ///   The engine's trace source (feature observability): one STATIC
    ///   <see cref="ActivitySource"/> for the whole process — spans capture no engine state, so
    ///   per-engine instances would gain nothing (unlike the metrics, whose observable gauges
    ///   capture the engine and are therefore per-engine, see <c>Fallen8Metrics</c>).
    ///
    ///   <para>BCL only — <c>System.Diagnostics.DiagnosticSource</c> ships in the shared
    ///   framework, so the engine gains no package reference. When nothing listens,
    ///   <c>StartActivity</c> returns null and spans cost nothing.</para>
    /// </summary>
    public static class Fallen8Diagnostics
    {
        /// <summary>The engine source/meter name.</summary>
        public const String SourceName = "NoSQL.GraphDB.Core";

        /// <summary>The engine's activity source.</summary>
        public static readonly ActivitySource Source = new ActivitySource(SourceName);
    }
}
