// MIT License
//
// Program.cs
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
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NoSQL.GraphDB.Agents.Configuration;
using NoSQL.GraphDB.Agents.Hosting;
using NoSQL.GraphDB.Agents.Runtime;

namespace NoSQL.GraphDB.Agents
{
    /// <summary>
    ///   Entry point of the agent host. Explicitly namespaced (not a top-level-statement global
    ///   <c>Program</c>) so that a <c>WebApplicationFactory</c> over it in the test suite is
    ///   unambiguous against the apiApp's <c>NoSQL.GraphDB.App.Program</c>, the MCP server's
    ///   <c>NoSQL.GraphDB.Mcp.Program</c> and the integrations runtime's
    ///   <c>NoSQL.GraphDB.Integrations.Program</c>.
    /// </summary>
    public sealed class Program
    {
        public static async Task Main(String[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            var options = builder.Configuration.GetSection(AgentsOptions.SectionName).Get<AgentsOptions>()
                          ?? new AgentsOptions();
            builder.WebHost.UseUrls($"http://{options.BindAddress}:{options.Port}");

            // Without this the framework's 30 MB default applies, so one spawn could hand this
            // process 30 MB of body to read and deserialize before anything judged it. Retention is
            // not what this buys: AgentSpawn.MaxTaskBytes bounds a task at 8192 bytes, asked in the
            // endpoint and again in the registry, so 30 MB of task text never reaches retention.
            // See TransportBound.
            builder.WebHost.ConfigureKestrel(kestrel =>
                kestrel.Limits.MaxRequestBodySize = TransportBound);

            AgentsHost.AddFallen8Agents(builder.Services, builder.Configuration);

            var app = builder.Build();

            AgentEndpoints.Map(app);

            // Cancels every live agent on the way down, so a step in flight is not left running
            // against a metered provider by a process that is already gone. Nothing here is
            // durable, and the posture line says so.
            app.Lifetime.ApplicationStopping.Register(() =>
                app.Services.GetRequiredService<AgentRegistry>()
                    .CancelAll("The agent host is shutting down."));

            // The posture line is said by AgentsStartupProbe rather than here, because it reports
            // what the two startup probes FOUND and not what was configured.
            await app.RunAsync().ConfigureAwait(false);
        }

        /// <summary>
        ///   The bound on a request body reaching this host: 2 MiB, chosen only to sit ABOVE the
        ///   apiApp proxy's own fixed bound (1 MiB), which is the way in from outside the compose
        ///   network, so an absurd body is refused at the front door rather than midway through a
        ///   forward. That 413 does not name a number, and it is worth not claiming it does:
        ///   <c>POST /agents</c> carries a bare <c>[RequestSizeLimit]</c> and builds no body of its
        ///   own, so the refusal is whatever the instance's error pipeline renders, where
        ///   <c>POST /integrations/job</c> checks <c>Content-Length</c> itself and names both
        ///   numbers. Equal bounds would leave a body at
        ///   the proxy's own limit refused here instead, with a bare 413 the proxy reports as a host
        ///   that did not answer.
        ///
        ///   <para>It is not a statement about how big a spawn may be. That is
        ///   <see cref="NoSQL.GraphDB.Agents.Runtime.AgentSpawn.MaxTaskBytes" /> per task,
        ///   <see cref="NoSQL.GraphDB.Agents.Runtime.AgentSpawn.MaxNameBytes" /> per name and
        ///   <see cref="NoSQL.GraphDB.Agents.Runtime.AgentSpawn.MaxAppendixBytes" /> per appendix,
        ///   each refused with a message naming its own number, because one legal body can carry
        ///   all of it in one field.</para>
        /// </summary>
        internal const Int64 TransportBound = 2_097_152;
    }
}
