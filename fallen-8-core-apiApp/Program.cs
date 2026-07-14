// MIT License
//
// Program.cs
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

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.App.Services;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Persistency;
using Scalar.AspNetCore;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace NoSQL.GraphDB.App
{
    public class Program
    {
        [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "MVC Controllers use reflection (AddControllers is RequiresUnreferencedCode) which is incompatible with trimming. Trimming is disabled for this application.")]
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Configure enhanced logging
            builder.Logging.ClearProviders();
            builder.Logging.AddSimpleConsole(options =>
            {
                options.IncludeScopes = false;
                options.SingleLine = true;
                options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
                options.UseUtcTimestamp = true;
            });

            // Configure log levels
            builder.Logging.AddFilter("Microsoft.AspNetCore", Microsoft.Extensions.Logging.LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.Hosting", Microsoft.Extensions.Logging.LogLevel.Information);
            builder.Logging.AddFilter("NoSQL.GraphDB", Microsoft.Extensions.Logging.LogLevel.Information);

            // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
            // .NET 10's Microsoft.AspNetCore.OpenApi reads controller XML doc comments
            // (<summary>/<remarks>/<response>) into the document natively via its build-time
            // source generator (GenerateDocumentationFile is enabled), so no custom operation
            // transformer is required.
            builder.Services.AddOpenApi("v0.1");

            builder.Services.AddApiVersioning(o =>
                       {
                           o.AssumeDefaultVersionWhenUnspecified = true;
                           o.DefaultApiVersion = new Microsoft.AspNetCore.Mvc.ApiVersion(0, 1);
                           o.ReportApiVersions = true;
                           o.ApiVersionReader = ApiVersionReader.Combine(
                               new QueryStringApiVersionReader("api-version"),
                               new HeaderApiVersionReader("X-Version"),
                               new MediaTypeApiVersionReader("ver"));

                       });

            builder.Services.AddVersionedApiExplorer(o =>
            {
                o.GroupNameFormat = "'v'VVV";
                o.SubstituteApiVersionInUrl = true;
            });

            // Durability configuration (feature hosted-durability-lifecycle): bind the
            // Fallen8:Durability section so the hosted server persists by default (load on boot,
            // save on clean shutdown, WAL between snapshots). Volatile is an explicit opt-out.
            builder.Services.Configure<Fallen8DurabilityOptions>(
                builder.Configuration.GetSection(Fallen8DurabilityOptions.SectionName));

            // Register the engine singleton through a factory so durable mode constructs the
            // WAL-enabling overload with the recipe compiler supplied AT CONSTRUCTION - an unanchored
            // WAL replays during construction, so only a compiler present then can recover its
            // subgraph entries. Volatile mode constructs the plain in-memory engine.
            builder.Services.AddSingleton<IFallen8>(sp =>
            {
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var durability = sp.GetRequiredService<IOptions<Fallen8DurabilityOptions>>().Value;

                if (durability.Volatile)
                {
                    return new Fallen8(loggerFactory);
                }

                // Ensure the storage directory exists BEFORE the engine opens the WAL there; a missing
                // or unwritable directory must fail loudly at startup, never silently degrade to volatile.
                var storageDirectory = durability.ResolveStorageDirectory();
                Directory.CreateDirectory(storageDirectory);

                return new Fallen8(loggerFactory,
                    new WriteAheadLogOptions(durability.ResolveWalPath()),
                    new RecipeSubGraphCompiler());
            });

            // Own the load-on-start / save-on-stop lifecycle around the existing Save/Load transactions.
            builder.Services.AddHostedService<DurabilityLifecycleService>();

            builder.Services.AddControllers().AddJsonOptions(options =>
            {
                // Serve the REST DTOs through source-generated metadata instead of runtime
                // reflection. The context uses the same camelCase Web defaults as MVC, and is
                // inserted ahead of the default reflection-based resolver (which stays as a
                // fallback), so the emitted/accepted JSON is unchanged.
                options.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
            });

            var app = builder.Build();

            // Force the engine singleton to construct now (before the host starts) so an unanchored
            // WAL replays and the DurabilityLifecycleService's StartAsync can load over a live engine.
            // The recipe compiler is supplied to the constructor (see the factory above), so persisted
            // AND WAL-replayed subgraphs rehydrate.
            _ = app.Services.GetRequiredService<IFallen8>();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
                app.MapScalarApiReference();
            }

            app.UseHttpsRedirection();

            app.UseAuthorization();

            app.MapControllers();

            app.Run();
        }
    }
}
