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
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Embedding;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.App.Namespaces;
using NoSQL.GraphDB.App.Security;
using NoSQL.GraphDB.App.Services;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Persistency;
using NoSQL.GraphDB.Core.Plugin;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
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
            builder.Services.AddOpenApi("v0.1", options =>
            {
                // The generator emits 'paths' in action-discovery order (controller file order x
                // declaration order), so any regrouping of controller code would reorder the
                // pinned snapshot. Sorting makes the snapshot byte-stable across refactors
                // (feature structural-decomposition, target 0).
                options.AddDocumentTransformer((document, _, _) =>
                {
                    var sorted = new Microsoft.OpenApi.OpenApiPaths();
                    foreach (var path in document.Paths.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        sorted.Add(path.Key, path.Value);
                    }
                    document.Paths = sorted;
                    return System.Threading.Tasks.Task.CompletedTask;
                });
            });

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

            // Namespace collection configuration (feature graph-namespaces).
            builder.Services.Configure<Fallen8NamespacesOptions>(
                builder.Configuration.GetSection(Fallen8NamespacesOptions.SectionName));

            // Change feed configuration (feature change-feed): hosted default ON - a read-only
            // surface with a small idle cost, and what makes F8 Studio live out of the box.
            builder.Services.Configure<Fallen8ChangeFeedOptions>(
                builder.Configuration.GetSection(Fallen8ChangeFeedOptions.SectionName));

            // Bulk import/export configuration (feature bulk-import-export).
            builder.Services.Configure<Fallen8BulkIOOptions>(
                builder.Configuration.GetSection(Fallen8BulkIOOptions.SectionName));

            // Graph analytics configuration + the concurrent-run gate (feature graph-analytics).
            builder.Services.Configure<Fallen8AnalyticsOptions>(
                builder.Configuration.GetSection(Fallen8AnalyticsOptions.SectionName));
            builder.Services.AddSingleton<AnalyticsRunGate>();

            // Observability (feature observability): options + the readiness flag + health checks.
            // OpenTelemetry itself is registered further below ONLY when an exporter is enabled -
            // a fully default configuration runs zero OTel code paths.
            builder.Services.Configure<Fallen8ObservabilityOptions>(
                builder.Configuration.GetSection(Fallen8ObservabilityOptions.SectionName));
            var observability = new Fallen8ObservabilityOptions();
            builder.Configuration.GetSection(Fallen8ObservabilityOptions.SectionName).Bind(observability);
            builder.Services.AddSingleton<StartupState>();
            builder.Services.AddHealthChecks()
                .AddCheck<StartupReadinessCheck>("startup-load", tags: new[] { "ready" });

            if (observability.AnyExporterEnabled)
            {
                var otel = builder.Services.AddOpenTelemetry();
                otel.ConfigureResource(r => r.AddService("fallen8"));
                otel.WithMetrics(metrics =>
                {
                    // The engine + app meters, plus the BUILT-IN HTTP/Kestrel/runtime meters -
                    // native in .NET 10, no instrumentation packages.
                    metrics.AddMeter(
                        NoSQL.GraphDB.Core.Diagnostics.Fallen8Diagnostics.SourceName,
                        NoSQL.GraphDB.App.Diagnostics.AppDiagnostics.SourceName,
                        "Microsoft.AspNetCore.Hosting",
                        "Microsoft.AspNetCore.Server.Kestrel",
                        "System.Runtime");
                    if (observability.Prometheus.Enabled)
                    {
                        metrics.AddPrometheusExporter();
                    }
                    if (!string.IsNullOrWhiteSpace(observability.Otlp.Endpoint))
                    {
                        metrics.AddOtlpExporter(o => o.Endpoint = new Uri(observability.Otlp.Endpoint));
                    }
                });

                // Trace EXPORT exists only via OTLP (Prometheus is metrics-only): without an
                // endpoint no sampler listens and StartActivity returns null - spans cost nothing.
                if (!string.IsNullOrWhiteSpace(observability.Otlp.Endpoint))
                {
                    otel.WithTracing(tracing =>
                    {
                        tracing.AddSource(
                            NoSQL.GraphDB.Core.Diagnostics.Fallen8Diagnostics.SourceName,
                            NoSQL.GraphDB.App.Diagnostics.AppDiagnostics.SourceName,
                            "Microsoft.AspNetCore");
                        tracing.SetSampler(new ParentBasedSampler(
                            new TraceIdRatioBasedSampler(observability.TracingSamplingRatio)));
                        tracing.AddOtlpExporter(o => o.Endpoint = new Uri(observability.Otlp.Endpoint));
                    });
                }
            }

            // The namespace collection IS the Fallen-8 (feature graph-namespaces): one engine per
            // namespace, booted holding the reserved "default" namespace on the legacy storage
            // paths. Construction semantics (WAL replay at construction, compilers, ceilings) live
            // on Fallen8Namespaces.
            builder.Services.AddSingleton<Fallen8Namespaces>();

            // IFallen8 is the ADDRESSED namespace's engine: a non-disposable singleton dispatcher
            // that resolves per call from the ambient "ns" route value (see AddressedFallen8 for
            // why it must never be the raw engine - DI disposes what its factories return).
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton<IFallen8, AddressedFallen8>();

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

                // The embedding provider gate (feature embedding-provider): same shape as the
                // code/plugin capabilities - off by default, orthogonal to auth, 403 when off.
                o.AddPolicy(Fallen8EmbeddingOptions.EmbeddingPolicy, p =>
                {
                    if (keyConfigured)
                    {
                        p.RequireAuthenticatedUser();
                    }
                    p.AddRequirements(new DynamicCapabilityRequirement(DynamicCapabilityRequirement.Capability.EmbeddingProvider));
                });
            });

            // Embedding provider (feature embedding-provider). The backend generator resolves
            // LAZILY on first use - with the flag off (the default) nothing is ever constructed
            // and no model loads, so the default deployment stays model-free. Tests replace the
            // IEmbeddingGenerator registration with a deterministic fake.
            builder.Services.Configure<Fallen8EmbeddingOptions>(
                builder.Configuration.GetSection(Fallen8EmbeddingOptions.SectionName));
            builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
                EmbeddingBackendFactory.Create(sp.GetRequiredService<IOptions<Fallen8EmbeddingOptions>>().Value));
            builder.Services.AddSingleton(sp => new Fallen8EmbeddingProvider(
                sp.GetRequiredService<IOptions<Fallen8EmbeddingOptions>>(),
                new Lazy<IEmbeddingGenerator<string, Embedding<float>>>(
                    () => sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>())));

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

            // Force the namespace collection to construct now (before the host starts) so unanchored
            // WALs replay and the DurabilityLifecycleService's StartAsync can load over live engines.
            // The recipe compiler is supplied at engine construction (see Fallen8Namespaces), so
            // persisted AND WAL-replayed subgraphs rehydrate.
            _ = app.Services.GetRequiredService<Fallen8Namespaces>();

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

            // Health endpoints (feature observability): liveness (no checks - up once Kestrel
            // answers) and readiness (the startup-load flag). Anonymous, status-only - the same
            // posture as /status.
            app.MapHealthChecks("/healthz", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
            {
                Predicate = _ => false
            }).AllowAnonymous();
            app.MapHealthChecks("/readyz", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
            {
                Predicate = check => check.Tags.Contains("ready")
            }).AllowAnonymous();

            // The Prometheus scrape endpoint (feature observability). Anonymous BY DEFAULT - a
            // deliberate, documented call (spec §3.7): the inventory carries zero user-supplied
            // strings, /status already exposes counts+memory anonymously, and the server binds
            // loopback by default. Prometheus:RequireApiKey=true drops the exemption.
            if (observability.Prometheus.Enabled)
            {
                var metricsEndpoint = app.MapPrometheusScrapingEndpoint("/metrics");
                if (!observability.Prometheus.RequireApiKey)
                {
                    metricsEndpoint.AllowAnonymous();
                }

                // Honest auth-mode line: RequireApiKey only bites when a key is actually
                // configured (the fallback policy is installed only then) - say so.
                var keyConfigured = !string.IsNullOrWhiteSpace(security.ApiKey);
                var authMode = !observability.Prometheus.RequireApiKey
                    ? "anonymous (Prometheus:RequireApiKey=false)"
                    : keyConfigured
                        ? "API key required"
                        : "RequireApiKey=true but NO API key is configured - effectively anonymous (configure Fallen8:Security:ApiKey)";
                startupLogger.LogWarning(
                    "Fallen-8 observability: GET /metrics is ENABLED (Prometheus exposition), auth mode: {AuthMode}. " +
                    "The metric inventory carries aggregate operational numbers only (no user-supplied strings).",
                    authMode);
            }
            if (!string.IsNullOrWhiteSpace(observability.Otlp.Endpoint))
            {
                startupLogger.LogWarning(
                    "Fallen-8 observability: OTLP export is ENABLED to \"{Endpoint}\" (metrics + traces, sampling ratio {Ratio}).",
                    observability.Otlp.Endpoint, observability.TracingSamplingRatio);
            }
            if (!observability.AnyExporterEnabled)
            {
                startupLogger.LogInformation(
                    "Fallen-8 observability: no exporters enabled (Fallen8:Observability) - zero OpenTelemetry code paths run; /statistics and the health endpoints are always available.");
            }

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
