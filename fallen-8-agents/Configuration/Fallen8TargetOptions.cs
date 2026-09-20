// MIT License
//
// Fallen8TargetOptions.cs
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

using NoSQL.GraphDB.Rest.Configuration;

namespace NoSQL.GraphDB.Agents.Configuration
{
    /// <summary>
    ///   The Fallen-8 this host asks for completions (config section <c>Fallen8Target</c>). The
    ///   base URL, the credential, its header and the deadline are
    ///   <see cref="AFallen8TargetOptions" />' - one section spelling for every sidecar, including
    ///   the rule that a caller's credential is never forwarded, so an agent cannot reach past what
    ///   this deployable may already do. This host adds no knob of its own; only its default
    ///   deadline differs, and the constructor says why.
    ///
    ///   <para><b>What this host asks that instance for is narrower than the other two sidecars</b>
    ///   and is pinned as such: the chat gateway, and nothing else. It reads no vertex, writes no
    ///   edge and calls no graph route - every graph capability arrives as an MCP tool instead. A
    ///   REST call added here that is not the chat gateway fails
    ///   <c>CodeQualityTest</c>.</para>
    /// </summary>
    public sealed class Fallen8TargetOptions : AFallen8TargetOptions
    {
        /// <summary>
        ///   Applies this host's own default deadline of 630 seconds.
        ///   <para>
        ///     Deliberately ABOVE the largest budget the apiApp applies to the route it calls, for
        ///     the reason <see cref="AFallen8TargetOptions.TimeoutSeconds" /> states once: two
        ///     competing deadlines make the NEARER one report a vague local failure instead of the
        ///     downstream answer that names what to change. Here the far one is
        ///     <c>Fallen8:Chat:TimeoutSeconds</c>, whose own default is 600 because the shipped chat
        ///     backend may spend that budget waiting for a cold model to be pulled onto a worker.
        ///   </para>
        ///   <para>
        ///     There is deliberately no way to switch the deadline off, because a call with no
        ///     deadline is bounded only by <c>Agents:Limits:MaxRunSeconds</c>, which can itself be
        ///     switched off.
        ///   </para>
        /// </summary>
        public Fallen8TargetOptions()
            : base(630)
        {
        }
    }
}
