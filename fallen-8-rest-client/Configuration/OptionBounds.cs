// MIT License
//
// OptionBounds.cs
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

namespace NoSQL.GraphDB.Rest.Configuration
{
    /// <summary>
    ///   What a configured number of SECONDS becomes before anything arms a timer or a client
    ///   deadline with it, and the one home for both ends of that.
    ///
    ///   <para>
    ///     <b>The floor.</b> A setting whose non-positive value means "off" says so on its own
    ///     property. A duration that goes through here has no off: a deadline of none is a caller
    ///     that hangs, so zero is floored at one second. The floor lives here rather than at each
    ///     enforcement site because a raw setting was being printed and reported as though it were
    ///     in force, which said "no deadline" for a host giving every call one second.
    ///   </para>
    ///   <para>
    ///     <b>The ceiling.</b> A very large value is the other way an operator asks for "off", and
    ///     it is the worse of the two, because each API this value reaches rejects it with
    ///     <c>ArgumentOutOfRangeException</c> naming a PARAMETER rather than the setting. Their
    ///     limits are NOT the same, and these were measured on net10.0 rather than assumed:
    ///     <see cref="System.Net.Http.HttpClient.Timeout" /> accepts up to 2,147,483 s, which is
    ///     <see cref="Int32.MaxValue" /> ms or about 24.9 days; <c>CancelAfter</c> and
    ///     <c>PeriodicTimer</c> both accept up to 4,294,967 s, which is <c>UInt32.MaxValue - 1</c>
    ///     ms or about 49.7 days.
    ///   </para>
    ///   <para>
    ///     <see cref="MaxSeconds" /> is therefore the TIGHTEST of the three, deliberately: one
    ///     clamp that is safe for every consumer beats three that each know only their own API.
    ///     An earlier version of this paragraph claimed all three shared the
    ///     <see cref="Int32.MaxValue" /> ms limit. That was false for both timers, and it is the
    ///     kind of plausible detail that survives a reading and not a measurement.
    ///   </para>
    ///   <para>
    ///     It sits at the shared seam rather than in one deployable because that is where the
    ///     measurement generalised to: the agent host learned the ceiling from an incident on
    ///     <c>CancelAfter</c>, and the MCP server and the integrations runtime were still building
    ///     an <see cref="System.Net.Http.HttpClient.Timeout" /> with a floor and no ceiling, which
    ///     is the same crash one API call further along and at half the value.
    ///   </para>
    /// </summary>
    public static class OptionBounds
    {
        /// <summary>
        ///   The largest delay every consumer of this clamp can safely be armed with, in seconds.
        ///   It is <see cref="System.Net.Http.HttpClient.Timeout" />'s limit because that is the
        ///   lowest of the three (see the type's summary for all three, measured); the timers
        ///   tolerate twice this, and deliberately do not get their own looser clamp.
        /// </summary>
        public const Int32 MaxSeconds = Int32.MaxValue / 1000;

        /// <summary>The duration a configured number of seconds actually produces.</summary>
        /// <param name="configured">The operator's value, in seconds, unclamped.</param>
        /// <returns>The same value clamped to 1..<see cref="MaxSeconds" />.</returns>
        public static TimeSpan Seconds(Int32 configured)
        {
            return TimeSpan.FromSeconds(Math.Clamp(configured, 1, MaxSeconds));
        }
    }
}
