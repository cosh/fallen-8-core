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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.Integrations.Configuration;
using NoSQL.GraphDB.Integrations.Hosting;

namespace NoSQL.GraphDB.Integrations
{
    /// <summary>
    ///   Entry point of the integration job runner. Explicitly namespaced (not a top-level-statement
    ///   global <c>Program</c>) so that a <c>WebApplicationFactory</c> over it in the test suite is
    ///   unambiguous against the apiApp's <c>NoSQL.GraphDB.App.Program</c> and the MCP server's
    ///   <c>NoSQL.GraphDB.Mcp.Program</c>.
    /// </summary>
    public sealed class Program
    {
        public static async Task Main(String[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            var options = builder.Configuration.GetSection(IntegrationsOptions.SectionName).Get<IntegrationsOptions>()
                          ?? new IntegrationsOptions();
            builder.WebHost.UseUrls($"http://{options.BindAddress}:{options.Port}");

            // A job now carries a file, base64, so the framework's 30 MB default would refuse a job at the
            // configured ceiling with a bare 413 and no body - which the apiApp's proxy reports as a
            // runtime that did not answer, sending the caller to look at a healthy sidecar.
            //
            // FIXED rather than derived from Integrations:MaxFileBytes, and that is the whole point: the
            // proxy in front of this container has its own fixed bound (768 MiB), and the two are only
            // useful if THIS one is always the larger, so an absurd body is refused at the front door
            // where the 413 means something. A bound that scaled with the ceiling would invert that
            // ordering the moment an operator LOWERED the ceiling. Size refusals a caller can actually
            // read are therefore exactly three: the proxy's 413 for an absurd body, this runtime's
            // MaxFileBytes message for one file over the per-file ceiling, and its MaxJobFileBytes message
            // for a set of legal files whose total is not. Each names its own numbers.
            builder.WebHost.ConfigureKestrel(kestrel =>
                kestrel.Limits.MaxRequestBodySize = TransportBound);

            IntegrationsHost.AddFallen8Integrations(builder.Services, builder.Configuration);

            var app = builder.Build();

            IntegrationEndpoints.Map(app);

            IntegrationsHost.LogStartupPosture(
                app.Services.GetRequiredService<ILogger<Program>>(),
                app.Services.GetRequiredService<IOptions<IntegrationsOptions>>().Value,
                app.Services.GetRequiredService<IOptions<Fallen8TargetOptions>>().Value);

            await app.RunAsync().ConfigureAwait(false);
        }

        /// <summary>
        ///   The bound on a request body reaching this runtime: 832 MiB, chosen only to sit ABOVE the
        ///   apiApp proxy's own fixed bound (768 MiB), which is the only way in because this container
        ///   publishes no port. It is not a statement about how big a file may be - that is
        ///   <c>Integrations:MaxFileBytes</c> per file and <c>Integrations:MaxJobFileBytes</c> for their
        ///   total, both enforced on the decoded bytes with messages naming their own numbers.
        ///
        ///   <para>It grew with multi-file input: one request still carries a whole run, so a job carrying a
        ///   vehicle's worth of extracts is one body, and 512 MiB of decoded files is about 683 MiB of
        ///   base64 before the JSON around it.</para>
        /// </summary>
        internal const Int64 TransportBound = 872_415_232;
    }
}
