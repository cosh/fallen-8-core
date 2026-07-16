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

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Versioning;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.App.Security;
using NoSQL.GraphDB.App.Services;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Persistency;
using NoSQL.GraphDB.Core.Plugin;
using Scalar.AspNetCore;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading.RateLimiting;

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

            // Stored query library configuration (feature stored-query-library).
            builder.Services.Configure<Fallen8StoredQueryOptions>(
                builder.Configuration.GetSection(Fallen8StoredQueryOptions.SectionName));

            // Change feed configuration (feature change-feed): hosted default ON - a read-only
            // surface with a small idle cost, and what makes F8 Studio live out of the box.
            builder.Services.Configure<Fallen8ChangeFeedOptions>(
                builder.Configuration.GetSection(Fallen8ChangeFeedOptions.SectionName));

            // Bulk import/export configuration (feature bulk-import-export).
            builder.Services.Configure<Fallen8BulkIOOptions>(
                builder.Configuration.GetSection(Fallen8BulkIOOptions.SectionName));

            // Register the engine singleton through a factory so durable mode constructs the
            // WAL-enabling overload with the recipe compiler supplied AT CONSTRUCTION - an unanchored
            // WAL replays during construction, so only a compiler present then can recover its
            // subgraph entries. Volatile mode constructs the plain in-memory engine.
            builder.Services.AddSingleton<IFallen8>(sp =>
            {
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                var durability = sp.GetRequiredService<IOptions<Fallen8DurabilityOptions>>().Value;
                var storedQueryOptions = sp.GetRequiredService<IOptions<Fallen8StoredQueryOptions>>().Value;
                var changeFeedOptions = sp.GetRequiredService<IOptions<Fallen8ChangeFeedOptions>>().Value.ToEngineOptions();

                Fallen8 engine;
                if (durability.Volatile)
                {
                    engine = new Fallen8(loggerFactory, changeFeedOptions)
                    {
                        StoredQueryCompiler = new StoredQueryCompiler()
                    };
                }
                else
                {
                    // Ensure the storage directory exists BEFORE the engine opens the WAL there; a missing
                    // or unwritable directory must fail loudly at startup, never silently degrade to volatile.
                    var storageDirectory = durability.ResolveStorageDirectory();
                    Directory.CreateDirectory(storageDirectory);

                    // Both compilers are supplied AT CONSTRUCTION: an unanchored WAL replays during
                    // construction, so only compilers present then can recompile its CreateSubGraph /
                    // RegisterStoredQuery entries.
                    engine = new Fallen8(loggerFactory,
                        new WriteAheadLogOptions(durability.ResolveWalPath()),
                        new RecipeSubGraphCompiler(),
                        new StoredQueryCompiler(),
                        changeFeedOptions);
                }

                // Stored query library: apply the configured registration ceiling.
                engine.StoredQueries.MaxCount = storedQueryOptions.MaxCount;

                return engine;
            });

            // Save-game metadata registry (feature save-games): the persistent historical record of
            // checkpoints and the startup load authority.
            builder.Services.Configure<Fallen8MetadataOptions>(
                builder.Configuration.GetSection(Fallen8MetadataOptions.SectionName));
            builder.Services.AddSingleton<SaveGameRegistry>();

            // Own the load-on-start / save-on-stop lifecycle around the existing Save/Load transactions.
            builder.Services.AddHostedService<DurabilityLifecycleService>();

            // Security configuration + trust boundary (feature api-security-boundary).
            builder.Services.Configure<Fallen8SecurityOptions>(
                builder.Configuration.GetSection(Fallen8SecurityOptions.SectionName));
            var security = new Fallen8SecurityOptions();
            builder.Configuration.GetSection(Fallen8SecurityOptions.SectionName).Bind(security);

            // Authentication: an API-key scheme. When no key is configured it authenticates nobody
            // (the server logs a warning below and runs unauthenticated, mitigated by the
            // loopback-by-default bind and the off-by-default code/plugin gates).
            builder.Services.AddAuthentication(Fallen8SecurityOptions.ApiKeyScheme)
                .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                    Fallen8SecurityOptions.ApiKeyScheme, _ => { });

            builder.Services.AddSingleton<IAuthorizationHandler, DynamicCapabilityAuthorizationHandler>();
            builder.Services.AddAuthorization(o =>
            {
                // Authentication is all-or-nothing: when a key is configured EVERY endpoint requires it
                // (this fallback, unless the action opts out with [AllowAnonymous]); when no key is
                // configured the whole service is open - the same posture for reads, mutations, AND the
                // code/plugin endpoints (dev / trusted-network mode).
                var keyConfigured = !string.IsNullOrWhiteSpace(security.ApiKey);
                if (keyConfigured)
                {
                    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
                }

                // The dynamic code/plugin capability flags are the INDEPENDENT kill switch for the
                // RCE surface, orthogonal to auth: the requirement is unmet when the flag is off
                // (-> denied) regardless of whether a key is set. Auth is layered on the SAME way as
                // every other endpoint - required only when a key is configured - so there is never a
                // stranded state where the RCE endpoints reject a caller that every other endpoint
                // would accept.
                o.AddPolicy(Fallen8SecurityOptions.DynamicCodePolicy, p =>
                {
                    if (keyConfigured)
                    {
                        p.RequireAuthenticatedUser();
                    }
                    p.AddRequirements(new DynamicCapabilityRequirement(DynamicCapabilityRequirement.Capability.DynamicCodeExecution));
                });
                o.AddPolicy(Fallen8SecurityOptions.DynamicPluginPolicy, p =>
                {
                    if (keyConfigured)
                    {
                        p.RequireAuthenticatedUser();
                    }
                    p.AddRequirements(new DynamicCapabilityRequirement(DynamicCapabilityRequirement.Capability.DynamicPluginLoading));
                });
            });

            // CORS: one named policy, default deny. Only the configured origins are allowed; never a
            // wildcard-with-credentials.
            builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
            {
                if (security.AllowedCorsOrigins != null && security.AllowedCorsOrigins.Length > 0)
                {
                    p.WithOrigins(security.AllowedCorsOrigins).AllowAnyHeader().AllowAnyMethod();
                }
                // else: no origins configured -> the policy allows nothing cross-origin (deny).
            }));

            // Rate limiting: a stricter fixed-window partition on the expensive/dangerous endpoints;
            // a breach returns 429.
            builder.Services.AddRateLimiter(o =>
            {
                o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                o.AddFixedWindowLimiter(Fallen8SecurityOptions.SensitiveRateLimitPolicy, fw =>
                {
                    fw.PermitLimit = security.SensitiveRateLimitPermitPerWindow;
                    fw.Window = TimeSpan.FromSeconds(Math.Max(1, security.RateLimitWindowSeconds));
                    fw.QueueLimit = 0;
                });
            });

            // Register the isolated plugin directory as an additional plugin search directory so an
            // uploaded DLL (written there, never next to the app binaries) is still discoverable.
            PluginFactory.AddPluginSearchDirectory(security.ResolvePluginDirectory());

            builder.Services.AddControllers().AddJsonOptions(options =>
            {
                // Serve the REST DTOs through source-generated metadata instead of runtime
                // reflection. The context uses the same camelCase Web defaults as MVC, and is
                // inserted ahead of the default reflection-based resolver (which stays as a
                // fallback), so the emitted/accepted JSON is unchanged.
                options.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
            });

            // Global error envelope (feature api-error-contract E1): any unhandled fault, and any bare
            // status result, is rendered as an RFC 7807 application/problem+json response with the
            // correct status - instead of the framework's empty 500. [ApiController] model-binding
            // failures (a non-integer route id) also flow through this as a 400 ProblemDetails.
            builder.Services.AddProblemDetails();

            var app = builder.Build();

            // Force the engine singleton to construct now (before the host starts) so an unanchored
            // WAL replays and the DurabilityLifecycleService's StartAsync can load over a live engine.
            // The recipe compiler is supplied to the constructor (see the factory above), so persisted
            // AND WAL-replayed subgraphs rehydrate.
            _ = app.Services.GetRequiredService<IFallen8>();

            var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Fallen8.Security");
            if (string.IsNullOrWhiteSpace(security.ApiKey))
            {
                startupLogger.LogWarning("Fallen-8 is running UNAUTHENTICATED (no Fallen8:Security:ApiKey configured). " +
                    "Configure an API key before exposing this server. The code/plugin endpoints stay disabled " +
                    "unless explicitly enabled.");
            }
            if (security.EnableDynamicCodeExecution || security.EnableDynamicPluginLoading)
            {
                startupLogger.LogWarning("Fallen-8 dynamic code/plugin execution is ENABLED. Compiled filters and " +
                    "loaded plugins run in-process with FULL TRUST - anyone permitted to reach these endpoints is " +
                    "trusted as the server process. This is a trust boundary, not a sandbox.");
            }

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
                app.MapScalarApiReference();
                // Development keeps the rich developer exception page (enabled by default) so dev
                // diagnostics are not masked by the ProblemDetails handler below.
            }
            else
            {
                // Outside Development, an unhandled exception becomes an application/problem+json 500
                // with no stack leak (feature api-error-contract E1).
                app.UseExceptionHandler();
            }

            // Render bare status-code results (e.g. a 404 with no body) as a problem+json body too.
            app.UseStatusCodePages();

            app.UseHttpsRedirection();

            // G-1 (feature web-ui): when a built SPA is present under wwwroot, serve it. A pure-API
            // deployment (no wwwroot/index.html) is unchanged, including problem+json 404s for
            // unknown paths. Cross-origin calls to OTHER instances stay governed by the CORS
            // allow-list above (Fallen8:Security:AllowedCorsOrigins).
            // Note: "/" and client-side routes are handled by the MapFallbackToFile endpoint
            // below (routing runs before this middleware and endpoint-matched requests skip
            // static files); this serves the hashed assets and direct file requests.
            var spaIndexPresent = File.Exists(System.IO.Path.Combine(
                app.Environment.ContentRootPath, "wwwroot", "index.html"));
            if (spaIndexPresent)
            {
                app.UseStaticFiles();
            }

            app.UseCors();
            app.UseRateLimiter();

            // Correct order: authenticate the caller, THEN authorize (the missing UseAuthentication was
            // why UseAuthorization was a no-op gate before - feature api-security-boundary S1).
            app.UseAuthentication();
            app.UseAuthorization();

            app.MapControllers();

            if (spaIndexPresent)
            {
                // SPA fallback: any path no controller matched renders the app shell, so
                // client-side routes survive a full-page reload. The shell is public chrome
                // (AllowAnonymous) even when an API key is configured - every data endpoint
                // stays behind the fallback authorization policy.
                app.MapFallbackToFile("index.html").AllowAnonymous();
            }

            app.Run();
        }
    }
}
