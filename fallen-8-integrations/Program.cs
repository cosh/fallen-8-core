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

            IntegrationsHost.AddFallen8Integrations(builder.Services, builder.Configuration);

            var app = builder.Build();

            IntegrationEndpoints.Map(app);

            IntegrationsHost.LogStartupPosture(
                app.Services.GetRequiredService<ILogger<Program>>(),
                app.Services.GetRequiredService<IOptions<IntegrationsOptions>>().Value,
                app.Services.GetRequiredService<IOptions<Fallen8TargetOptions>>().Value);

            await app.RunAsync().ConfigureAwait(false);
        }
    }
}
