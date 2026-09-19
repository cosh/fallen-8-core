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

namespace NoSQL.GraphDB.Agents.Configuration
{
    /// <summary>
    ///   What a configured number of SECONDS becomes before anything on this host arms a timer with
    ///   it, and the one home for both ends of that.
    ///
    ///   <para>
    ///     <b>The floor.</b> A setting whose non-positive value means "off" says so on its own
    ///     property. The two durations that go through here have no off: a deadline of none and a
    ///     keep-alive of never are both a host that hangs, so zero is floored at one second. The
    ///     floor lives here rather than at each enforcement site because a raw setting was being
    ///     printed and reported as though it were in force, which said "no deadline" for a host
    ///     giving every call one second.
    ///   </para>
    ///   <para>
    ///     <b>The ceiling.</b> A very large value is the other way an operator asks for "off", and
    ///     it used to be worse than the floor was: <c>CancelAfter</c> and <c>PeriodicTimer</c> both
    ///     refuse a delay past <c>Int32.MaxValue</c> milliseconds, so every model call threw
    ///     <c>ArgumentOutOfRangeException</c> naming a parameter rather than the setting, and every
    ///     feed connection did the same. Clamped, the largest number an operator can write is about
    ///     24 days, which is the practical shape of "no deadline" anyway.
    ///   </para>
    /// </summary>
    internal static class OptionBounds
    {
        /// <summary>The largest delay a timer here can be armed with, in seconds. Milliseconds are
        /// what both APIs take, and both take them as an <see cref="Int32" />.</summary>
        internal const Int32 MaxSeconds = Int32.MaxValue / 1000;

        /// <summary>The duration a configured number of seconds actually produces.</summary>
        internal static TimeSpan Seconds(Int32 configured)
        {
            return TimeSpan.FromSeconds(Math.Clamp(configured, 1, MaxSeconds));
        }
    }
}
