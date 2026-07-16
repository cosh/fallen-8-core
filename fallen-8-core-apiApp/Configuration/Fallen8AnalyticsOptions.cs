// MIT License
//
// Fallen8AnalyticsOptions.cs
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

namespace NoSQL.GraphDB.App.Configuration
{
    /// <summary>
    ///   Graph analytics configuration, bound from the <c>Fallen8:Analytics</c> section
    ///   (feature graph-analytics). Non-positive values reset to the defaults.
    /// </summary>
    public sealed class Fallen8AnalyticsOptions
    {
        /// <summary>The configuration section this binds from.</summary>
        public const String SectionName = "Fallen8:Analytics";

        private Int32 _defaultTimeBudgetSeconds = 30;
        private Int32 _maxTimeBudgetSeconds = 300;
        private Int32 _maxConcurrentRuns = 1;

        /// <summary>
        ///   The wall-clock budget applied when a request does not name one. Default 30 s -
        ///   synchronous-with-budget is the right size for a single-operator instance whose
        ///   graph fits in RAM (spec §3.6); re-run with a bigger budget instead of a job queue.
        /// </summary>
        public Int32 DefaultTimeBudgetSeconds
        {
            get { return _defaultTimeBudgetSeconds; }
            set { _defaultTimeBudgetSeconds = value > 0 ? value : 30; }
        }

        /// <summary>
        ///   The ceiling a request's <c>timeBudgetSeconds</c> may ask for; higher is a 400.
        ///   Default 300 s.
        /// </summary>
        public Int32 MaxTimeBudgetSeconds
        {
            get { return _maxTimeBudgetSeconds; }
            set { _maxTimeBudgetSeconds = value > 0 ? value : 300; }
        }

        /// <summary>
        ///   Concurrent analytics runs; a run holds a slot and the controller answers 429 when
        ///   none is free. Default 1 - a whole-graph pass saturates cores and memory bandwidth,
        ///   so a single-operator instance gains nothing from overlapping runs.
        /// </summary>
        public Int32 MaxConcurrentRuns
        {
            get { return _maxConcurrentRuns; }
            set { _maxConcurrentRuns = value > 0 ? value : 1; }
        }
    }
}
