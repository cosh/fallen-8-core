// MIT License
//
// AgentsObservabilityOptions.cs
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
using NoSQL.GraphDB.Rest.Configuration;

namespace NoSQL.GraphDB.Agents.Configuration
{
    /// <summary>
    ///   Agent-host observability, bound from <c>Agents:Observability</c>. The shape and the
    ///   off-by-default rule are <see cref="AFleetObservabilityOptions" />', including why the
    ///   meter is created even when export is off.
    ///
    ///   <para>What is this host's own is that the Agent Framework's GenAI telemetry is registered
    ///   rather than reimplemented, and with sensitive data off. See
    ///   <see cref="Hosting.AgentsObservability" />.</para>
    /// </summary>
    public sealed class AgentsObservabilityOptions : AFleetObservabilityOptions
    {
        /// <summary>The configuration section this binds from.</summary>
        public const String SectionName = "Agents:Observability";
    }
}
