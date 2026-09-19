// MIT License
//
// IAgentToolSource.cs
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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace NoSQL.GraphDB.Agents.Runtime
{
    /// <summary>
    ///   Where an agent's tools come from. One implementation, <see cref="McpToolset" />; the seam
    ///   exists so a runner test drives a real tool loop with a local function and never a network,
    ///   which is the same reason the apiApp's sidecar proxies have theirs.
    ///
    ///   <para>
    ///     Three members rather than one, because "no tools" is ambiguous and the ambiguity matters:
    ///     a server that advertises none and a server that did not answer look identical through
    ///     <see cref="Tools" /> alone, and an agent running with an empty toolset for the second
    ///     reason is a deployment problem somebody has to see.
    ///   </para>
    /// </summary>
    public interface IAgentToolSource
    {
        /// <summary>The tools last fetched, before any role allowlist narrows them.</summary>
        IReadOnlyList<AITool> Tools
        {
            get;
        }

        /// <summary>True once a fetch has succeeded and has not since failed.</summary>
        Boolean Connected
        {
            get;
        }

        /// <summary>Why the last fetch failed, or null. What tells an empty toolset apart from an
        /// unreachable one.</summary>
        String? Failure
        {
            get;
        }

        /// <summary>
        ///   Tries once more to reach the server, if it is not reached and enough time has passed
        ///   since the last try. Never throws: the outcome is <see cref="Connected" />.
        ///
        ///   <para>
        ///     A run calls this before it composes its tools, because a host can lose the startup
        ///     race and there is no other way back: a reboot restarts the containers in an
        ///     unspecified order, and a compose <c>depends_on</c> edge orders <c>up</c> only. The
        ///     host used to stay toolless for the life of the process while reporting healthy, so
        ///     every agent answered that it cannot reach a graph until somebody restarted it.
        ///   </para>
        /// </summary>
        Task EnsureConnectedAsync(CancellationToken cancellationToken = default);
    }
}
