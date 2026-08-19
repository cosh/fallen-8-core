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
using NoSQL.GraphDB.App.Chat;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Embedding;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.App.Namespaces;
using NoSQL.GraphDB.App.Security;
using NoSQL.GraphDB.App.Services;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Persistency;
using NoSQL.GraphDB.Core.Plugin;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.RateLimiting;

namespace NoSQL.GraphDB.App
{
    public class Program
    {
        /// <summary>
        ///   Name of the API-key security scheme in the OpenAPI document. A document-local
        ///   identifier (it names the "components.securitySchemes" entry and the generated clients'
        ///   auth setting), deliberately not the ASP.NET authentication scheme name
        ///   <see cref="Fallen8SecurityOptions.ApiKeyScheme"/>.
        /// </summary>
        private const String ApiKeySchemeName = "ApiKey";

        /// <summary>
        ///   True when the action behind <paramref name="description"/> opts out of authentication
        ///   with <c>[AllowAnonymous]</c>, so its operation must not claim the document-level
        ///   API-key requirement. Both sources are consulted: the endpoint metadata is what the
        ///   authorization middleware reads (and the only source for a non-controller endpoint),
        ///   the controller/action attributes are the declared view of an MVC action.
        /// </summary>
        private static Boolean AllowsAnonymousAccess(Microsoft.AspNetCore.Mvc.ApiExplorer.ApiDescription description)
        {
            if (description.ActionDescriptor.EndpointMetadata != null &&
                description.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
            {
                return true;
            }

            return description.ActionDescriptor is Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor action &&
                (action.MethodInfo.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any() ||
                 action.ControllerTypeInfo.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any());
        }

        /// <summary>
        ///   True when <paramref name="description"/> is the BARE path of a
        ///   <see cref="NamespaceRequiredAttribute"/> action, i.e. the one selector that can only ever
        ///   refuse. Derived from the attribute plus the matched path rather than a hand-kept route
        ///   list, so it cannot drift from the controller.
        /// </summary>
        private static Boolean RefusesEveryRequest(Microsoft.AspNetCore.Mvc.ApiExplorer.ApiDescription description)
        {
            return description.ActionDescriptor.EndpointMetadata != null
                && description.ActionDescriptor.EndpointMetadata.OfType<NamespaceRequiredAttribute>().Any()
                && !(description.RelativePath ?? String.Empty).StartsWith(
                       "ns/{" + NamespaceRouteConvention.RouteParameterName + "}/", StringComparison.Ordinal);
        }

        [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "MVC Controllers use reflection (AddControllers is RequiresUnreferencedCode) which is incompatible with trimming. Trimming is disabled for this application.")]
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // This instance's stored configuration overrides (feature writable-instance-config), added
            // FIRST so every eager Bind below sees them. Appended last as a source, which is what lets
            // it beat appsettings.json; it arbitrates per key so it can never beat the environment or
            // the command line. Reading the metadata directory here rather than through
            // Fallen8MetadataOptions.ResolveDirectory is deliberate: that helper falls back to a folder
            // under AppContext.BaseDirectory, which is the shared test output directory under the unit
            // suite, and an appended-last layer reading a file there would outrank what dozens of test
            // hosts inject. An instance that has not been told where its metadata lives keeps no
            // overrides, and needs none: it has no API key either, so it accepts no configuration write.
            var configOverrides = Fallen8ConfigOverridesSource.Resolve(
                builder.Configuration, builder.Configuration["Fallen8:Metadata:Directory"]);
            if (configOverrides != null)
            {
                ((IConfigurationBuilder)builder.Configuration).Add(configOverrides);
            }

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
                options.AddDocumentTransformer((document, context, _) =>
                {
                    // Without these the framework defaults stand: the title is the assembly name
                    // ("fallen-8-core-apiApp | v0.1") and the version is "1.0.0", contradicting the
                    // API version every route is served under. The version is taken off the document
                    // name so the version literal keeps ONE home: the AddOpenApi call above.
                    document.Info.Title = "Fallen-8 REST API";
                    document.Info.Version = context.DocumentName.TrimStart('v');

                    // The ONE home for the namespace URL scheme (feature graph-namespaces):
                    // explained here at the document level instead of on every operation.
                    document.Info.Description =
                        "A Fallen-8 hosts isolated graph namespaces. Every namespace-scoped path " +
                        "exists twice: bare, aliasing the reserved \"default\" namespace, and " +
                        "prefixed with /ns/{ns} to address a named namespace. A request naming an " +
                        "unknown namespace answers 404 application/problem+json with a " +
                        "\"namespace\" extension member. Fallen-8-level paths (the /ns management " +
                        "routes, save games, delegate validation) exist " +
                        "once and concern the whole collection of namespaces. Two scoped paths have " +
                        "no bare alias: /generate and /benchmark act on exactly one graph and never " +
                        "pick one for you, so their bare form answers 400 naming the /ns/{ns} URL.";

                    // Describe the credential (feature api-security-boundary) so Scalar offers an
                    // auth field and a generated client has somewhere to put the key. Declared
                    // ALWAYS, with a document-level requirement, because only ENFORCEMENT is a
                    // per-deployment setting (the handler demands the key solely when one is
                    // configured) while the credential's shape never changes - a conditional
                    // declaration would be absent from every published reference and from the pinned
                    // snapshot, both produced without a key. The [AllowAnonymous] operations override
                    // the requirement with an empty one (see the operation transformer below).
                    var apiKeyHeader = context.ApplicationServices
                        .GetRequiredService<IOptions<Fallen8SecurityOptions>>().Value.ApiKeyHeader;
                    document.Components ??= new Microsoft.OpenApi.OpenApiComponents();
                    document.Components.SecuritySchemes ??=
                        new Dictionary<String, Microsoft.OpenApi.IOpenApiSecurityScheme>();
                    document.Components.SecuritySchemes[ApiKeySchemeName] = new Microsoft.OpenApi.OpenApiSecurityScheme
                    {
                        Type = Microsoft.OpenApi.SecuritySchemeType.ApiKey,
                        In = Microsoft.OpenApi.ParameterLocation.Header,
                        // A blank configured header falls back exactly as the handler does - see
                        // ApiKeyAuthenticationHandler, which owns the credential's runtime contract.
                        Name = String.IsNullOrWhiteSpace(apiKeyHeader) ? "X-Api-Key" : apiKeyHeader,
                        Description =
                            "The Fallen-8 API key. An \"Authorization: Bearer <key>\" header is accepted " +
                            "as well. Required only on an instance that configures " +
                            "Fallen8:Security:ApiKey - an instance without a key runs unauthenticated."
                    };
                    document.Security = new List<Microsoft.OpenApi.OpenApiSecurityRequirement>
                    {
                        new Microsoft.OpenApi.OpenApiSecurityRequirement
                        {
                            [new Microsoft.OpenApi.OpenApiSecuritySchemeReference(ApiKeySchemeName, document)] =
                                new List<String>()
                        }
                    };

                    var sorted = new Microsoft.OpenApi.OpenApiPaths();
                    foreach (var path in document.Paths.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        sorted.Add(path.Key, path.Value);
                    }
                    document.Paths = sorted;
                    return System.Threading.Tasks.Task.CompletedTask;
                });

                // An [AllowAnonymous] operation answers without a credential even on a key-secured
                // instance, so it overrides the document-level requirement with an empty array - the
                // OpenAPI way of saying "no security here". Derived from the action's metadata rather
                // than a hand-kept path list, so a new anonymous route is described correctly.
                options.AddOperationTransformer((operation, context, _) =>
                {
                    if (AllowsAnonymousAccess(context.Description))
                    {
                        operation.Security = new List<Microsoft.OpenApi.OpenApiSecurityRequirement>();
                    }
                    return System.Threading.Tasks.Task.CompletedTask;
                });

                // The bare path of a [NamespaceRequired] action can only refuse, so the document must
                // not advertise its success response there: a client generated from this document
                // would otherwise expose a typed method that fails every single call. Marked
                // deprecated and stripped down to the refusal, from the action's own metadata.
                options.AddOperationTransformer((operation, context, _) =>
                {
                    if (RefusesEveryRequest(context.Description) && operation.Responses != null)
                    {
                        operation.Deprecated = true;
                        operation.Responses.Remove("200");
                        if (operation.Responses.TryGetValue("400", out var refusal)
                            && refusal is Microsoft.OpenApi.OpenApiResponse response)
                        {
                            response.Description =
                                "Always: this route names no namespace, and the operation acts on one " +
                                "graph. Call the /ns/{ns} form instead.";
                        }
                    }
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

            // Plugin registry configuration (feature plugin-registration).
            builder.Services.Configure<Fallen8PluginOptions>(
                builder.Configuration.GetSection(Fallen8PluginOptions.SectionName));

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

            // Fleet identity (feature fleet-observability): the tenant + instance this process
            // belongs to, stamped as OTel resource attributes on every metric, trace, and log so a
            // central consumer can separate the fleet. Resolved ONCE here (defaults auto-fill, the
            // auto instance id is stable for the process) and shared as a singleton with the
            // namespace-info gauge and the request-scoped namespace enrichment.
            builder.Services.Configure<Fallen8IdentityOptions>(
                builder.Configuration.GetSection(Fallen8IdentityOptions.SectionName));
            var identityOptions = new Fallen8IdentityOptions();
            builder.Configuration.GetSection(Fallen8IdentityOptions.SectionName).Bind(identityOptions);
            var identity = new Fallen8Identity(identityOptions);
            builder.Services.AddSingleton(identity);

            if (observability.AnyExporterEnabled)
            {
                // One resource for metrics, traces AND logs: service.name plus the fleet identity
                // resource attributes (feature fleet-observability), so every signal carries the
                // tenant + instance a consumer keys on.
                // serviceInstanceId is our stable instance id (not the SDK's random per-process
                // GUID), so the promoted service_instance_id label does not churn across restarts.
                Action<ResourceBuilder> configureResource = r =>
                    r.AddService("fallen8", serviceInstanceId: identity.InstanceId)
                        .AddAttributes(identity.ResourceAttributes());

                var otel = builder.Services.AddOpenTelemetry();
                otel.ConfigureResource(configureResource);
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

                    // Export the existing structured logs over OTLP with the SAME resource identity
                    // (feature fleet-observability). Console logging (configured above) is untouched;
                    // IncludeScopes carries the per-request namespace scope (see the enrichment
                    // middleware) onto every exported log record.
                    builder.Logging.AddOpenTelemetry(logging =>
                    {
                        var logResource = ResourceBuilder.CreateDefault();
                        configureResource(logResource);
                        logging.SetResourceBuilder(logResource);
                        logging.IncludeScopes = true;
                        logging.IncludeFormattedMessage = true;
                        logging.AddOtlpExporter(o => o.Endpoint = new Uri(observability.Otlp.Endpoint));
                    });
                }
            }

            // The namespace collection IS the Fallen-8 (feature graph-namespaces): one engine per
            // namespace, booted holding the reserved "default" namespace on the legacy storage
            // paths. Construction semantics (WAL replay at construction, compilers, ceilings) live
            // on Fallen8Namespaces.
            builder.Services.AddSingleton<Fallen8Namespaces>();

            // The fallen8_namespace_info gauge (feature fleet-observability §3.4): the per-namespace
            // id->name mapping a consumer joins engine metrics against. Constructed eagerly below
            // (only when an exporter is enabled) so its observable gauge registers on the app meter.
            builder.Services.AddSingleton<NoSQL.GraphDB.App.Diagnostics.NamespaceInfoMetrics>();

            // IFallen8 is the ADDRESSED namespace's engine: a non-disposable singleton dispatcher
            // that resolves per call from the ambient "ns" route value (see AddressedFallen8 for
            // why it must never be the raw engine - DI disposes what its factories return).
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton<IFallen8, AddressedFallen8>();

            // Save-game metadata registry (feature save-games): the persistent historical record of
            // checkpoints and the startup load authority.
            builder.Services.Configure<Fallen8MetadataOptions>(
                builder.Configuration.GetSection(Fallen8MetadataOptions.SectionName));

            // The configuration read model (feature writable-instance-config). Its constructor takes the
            // boot snapshot, so it is resolved deliberately after the namespace collection below rather
            // than lazily by the first request.
            builder.Services.AddSingleton(sp => new Fallen8ConfigOverrides(builder.Configuration, configOverrides,
                sp.GetService<ILoggerFactory>()?.CreateLogger("Fallen8.Configuration")));

            // Pushes live-tier settings into the running process on every configuration reload, whatever
            // caused it (feature writable-instance-config phase 4).
            builder.Services.AddSingleton(sp => new Fallen8LiveSettings(sp, builder.Configuration,
                sp.GetService<ILoggerFactory>()?.CreateLogger("Fallen8.Configuration")));
            builder.Services.AddSingleton<SaveGameRegistry>();

            // The one home for restoring a single namespace from the registry: shared by the boot
            // and by runtime activation, which differ only in what a failure means (feature
            // namespace-startup-load).
            builder.Services.AddSingleton<NamespaceLoader>();

            // Own the load-on-start / save-on-stop lifecycle around the existing Save/Load transactions.
            builder.Services.AddHostedService<DurabilityLifecycleService>();

            // Security configuration + trust boundary (feature api-security-boundary).
            builder.Services.Configure<Fallen8SecurityOptions>(
                builder.Configuration.GetSection(Fallen8SecurityOptions.SectionName));
            var security = new Fallen8SecurityOptions();
            builder.Configuration.GetSection(Fallen8SecurityOptions.SectionName).Bind(security);

            // Authentication: an API-key scheme. When no key is configured it authenticates nobody
            // (the server logs a warning below and runs unauthenticated; the only out-of-the-box
            // mitigation is the off-by-default code/plugin gates - the bind is whatever
            // ASPNETCORE_URLS/Kestrel is configured to, not loopback-only).
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

                // The plugin/embedding capability flags are the INDEPENDENT kill switch for their
                // opt-in surfaces, orthogonal to auth: the requirement is unmet when the flag is off
                // (-> denied) regardless of whether a key is set. Auth is layered on the SAME way as
                // every other endpoint - required only when a key is configured - so there is never a
                // stranded state where these endpoints reject a caller that every other endpoint
                // would accept. Dynamic code execution has NO capability flag: it is always on, so
                // the compile endpoints (/path, /subgraph, /delegates/validate, /storedquery) carry
                // only the standard fallback auth.
                o.AddPolicy(Fallen8SecurityOptions.DynamicPluginPolicy, p =>
                {
                    if (keyConfigured)
                    {
                        p.RequireAuthenticatedUser();
                    }
                    p.AddRequirements(new DynamicCapabilityRequirement(DynamicCapabilityRequirement.Capability.DynamicPluginLoading));
                });

                // The configuration-write gate (feature writable-instance-config): the capability half of
                // the two operator acts a write needs. The other half, that an API key must be
                // configured at all, is enforced in the action rather than here, and deliberately: a
                // policy that fails for an UNAUTHENTICATED caller produces a challenge, so a keyless
                // instance would answer 401 and invite a caller to authenticate with a key that does not
                // exist. The action answers 403 and says why.
                o.AddPolicy(Fallen8SecurityOptions.ConfigurationWritePolicy, p =>
                {
                    if (keyConfigured)
                    {
                        p.RequireAuthenticatedUser();
                    }
                    p.RequireAssertion(_ => security.EnableConfigurationWrite);
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

                // The chat gateway gate (feature instance-config): same shape as the embedding
                // capability - off by default, orthogonal to auth, 403 when off.
                o.AddPolicy(Fallen8ChatOptions.ChatPolicy, p =>
                {
                    if (keyConfigured)
                    {
                        p.RequireAuthenticatedUser();
                    }
                    p.AddRequirements(new DynamicCapabilityRequirement(DynamicCapabilityRequirement.Capability.Chat));
                });

                // The unstructured-ingestion gate (feature unstructured-ingestion): same shape -
                // off by default, orthogonal to auth, 403 when off.
                o.AddPolicy(Fallen8IngestionOptions.IngestionPolicy, p =>
                {
                    if (keyConfigured)
                    {
                        p.RequireAuthenticatedUser();
                    }
                    p.AddRequirements(new DynamicCapabilityRequirement(DynamicCapabilityRequirement.Capability.Ingestion));
                });

                // The integrations gate (feature integrations): same shape - off by default,
                // orthogonal to auth, 403 when off. That 403 IS the opt-out (F8_INTEGRATIONS=false)
                // and is what a client gates the feature on, so no /integrations action checks the
                // flag itself.
                o.AddPolicy(Fallen8IntegrationsOptions.IntegrationsPolicy, p =>
                {
                    if (keyConfigured)
                    {
                        p.RequireAuthenticatedUser();
                    }
                    p.AddRequirements(new DynamicCapabilityRequirement(DynamicCapabilityRequirement.Capability.Integrations));
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

            // Chat gateway (feature instance-config): same lazy shape as the embedding provider -
            // with the flag off (the default) nothing is constructed and no backend client opens.
            // Tests replace the IChatBackend registration with a deterministic fake.
            builder.Services.Configure<Fallen8ChatOptions>(
                builder.Configuration.GetSection(Fallen8ChatOptions.SectionName));
            builder.Services.AddSingleton<IChatBackend>(sp =>
                ChatBackendFactory.Create(sp.GetRequiredService<IOptions<Fallen8ChatOptions>>().Value));
            builder.Services.AddSingleton(sp => new Fallen8ChatProvider(
                sp.GetRequiredService<IOptions<Fallen8ChatOptions>>(),
                new Lazy<IChatBackend>(() => sp.GetRequiredService<IChatBackend>())));

            // Unstructured ingestion (feature unstructured-ingestion): the docling client is
            // inert until the first conversion - with the flag off (the default) the document
            // endpoints answer 403, and text formats never contact the sidecar even when on.
            // Tests replace the IDoclingConverter registration with a deterministic fake.
            builder.Services.Configure<Fallen8IngestionOptions>(
                builder.Configuration.GetSection(Fallen8IngestionOptions.SectionName));
            builder.Services.AddSingleton<NoSQL.GraphDB.App.Ingestion.IDoclingConverter>(sp =>
                new NoSQL.GraphDB.App.Ingestion.DoclingClient(
                    sp.GetRequiredService<IOptions<Fallen8IngestionOptions>>(),
                    sp.GetRequiredService<ILogger<NoSQL.GraphDB.App.Ingestion.DoclingClient>>()));
            builder.Services.AddSingleton<NoSQL.GraphDB.App.Ingestion.DocumentIngestionService>();
            builder.Services.AddSingleton<NoSQL.GraphDB.App.Ingestion.DocumentSearchService>();
            // The single global ingestion queue + its one background consumer (feature
            // semantic-layer): POST /document returns 202 and the worker drains jobs in arrival
            // order, resolving each job's namespace off the request thread.
            builder.Services.AddSingleton<NoSQL.GraphDB.App.Ingestion.IngestionJobQueue>();
            builder.Services.AddHostedService<NoSQL.GraphDB.App.Ingestion.IngestionWorker>();

            // Semantic-layer NLP enrichment (feature semantic-layer): the client is inert until
            // ingestion enriches a chunk - with the flag off (the default) nothing is contacted,
            // and enrichment is additive so ingestion still runs. Tests replace INlpClient.
            builder.Services.Configure<Fallen8NlpOptions>(
                builder.Configuration.GetSection(Fallen8NlpOptions.SectionName));
            builder.Services.AddSingleton<NoSQL.GraphDB.App.Ingestion.INlpClient>(sp =>
                new NoSQL.GraphDB.App.Ingestion.NlpClient(
                    sp.GetRequiredService<IOptions<Fallen8NlpOptions>>(),
                    sp.GetRequiredService<ILogger<NoSQL.GraphDB.App.Ingestion.NlpClient>>()));

            // The integration runtime proxy (feature integrations): the client is inert until a
            // /integrations route is called - with the flag off (the default) those routes answer 403
            // and nothing is contacted, and with no endpoint configured they answer 503 rather than
            // timing out. Tests replace IIntegrationsClient.
            builder.Services.Configure<Fallen8IntegrationsOptions>(
                builder.Configuration.GetSection(Fallen8IntegrationsOptions.SectionName));
            builder.Services.AddSingleton<NoSQL.GraphDB.App.Integrations.IIntegrationsClient>(sp =>
                new NoSQL.GraphDB.App.Integrations.IntegrationsClient(
                    sp.GetRequiredService<IOptions<Fallen8IntegrationsOptions>>(),
                    sp.GetRequiredService<ILogger<NoSQL.GraphDB.App.Integrations.IntegrationsClient>>()));

            // CORS: one named policy, default deny. Only the configured origins are allowed; never a
            // wildcard-with-credentials.
            builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
            {
                if (security.AllowedCorsOrigins != null && security.AllowedCorsOrigins.Length > 0)
                {
                    p.WithOrigins(security.AllowedCorsOrigins)
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        // Cache the preflight (OPTIONS) result so a cross-origin standalone UI
                        // (feature standalone-ui) does not re-preflight on every request - notably
                        // the change-feed SSE reconnect loop and bulk import.
                        .SetPreflightMaxAge(TimeSpan.FromSeconds(600));
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

            builder.Services.AddControllers(options =>
            {
                // Route twins (feature graph-namespaces): every namespace-scoped action also
                // answers under /ns/{ns}/... (a real second attribute route, no path rewriting);
                // the filter answers 404 problem+json for unknown namespaces before any action runs.
                options.Conventions.Add(new NamespaceRouteConvention());
                options.Filters.Add(typeof(NamespaceValidationFilter));
                options.Filters.Add(typeof(UnknownNamespaceExceptionFilter));
                // Its twin for a namespace that exists but is not loaded in this process (feature
                // namespace-startup-load): the net under every engine-dereference site the
                // pre-action filter does not cover, mapping to 503 rather than 404 because a 404
                // sends Studio to its "recreate empty" recover state over real data.
                options.Filters.Add(typeof(NamespaceNotLoadedExceptionFilter));
                // Restores application/problem+json on an error body that an action's
                // [Produces("application/json")] would otherwise downgrade; see the filter. The
                // order MUST be passed here: a filter added by type is described by a
                // TypeFilterAttribute whose own Order (0) is what MVC sorts on, so the type's
                // IOrderedFilter.Order is ignored and the action-scoped [Produces] would win the tie.
                options.Filters.Add(typeof(NoSQL.GraphDB.App.Helper.ProblemDetailsContentTypeFilter),
                    NoSQL.GraphDB.App.Helper.ProblemDetailsContentTypeFilter.FilterOrder);
            }).AddJsonOptions(options =>
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

            // Snapshot every catalogued key's effective value NOW (feature writable-instance-config):
            // the namespace collection has just latched six sections' worth of values into long-lived
            // state, so this is the moment the process committed to its configuration. A restart-tier
            // key is "pending" when its effective value later differs from this snapshot, which is why
            // the pending set needs no marker file and clears exactly when the process restarts.
            var overridesReadModel = app.Services.GetRequiredService<Fallen8ConfigOverrides>();

            // Register the namespace-info observable gauge now (feature fleet-observability): only
            // when an exporter is enabled, so a default configuration constructs no extra meter.
            if (observability.AnyExporterEnabled)
            {
                _ = app.Services.GetRequiredService<NoSQL.GraphDB.App.Diagnostics.NamespaceInfoMetrics>();
            }

            // Say out loud what the stored-overrides layer did: a value an operator saved that the
            // environment silently outranks is exactly the failure this feature exists to remove.
            overridesReadModel.LogState();

            // Only now that the host is built: a reload during startup would otherwise run an apply
            // delegate against services that do not exist yet.
            app.Services.GetRequiredService<Fallen8LiveSettings>().Start();

            var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Fallen8.Security");
            if (string.IsNullOrWhiteSpace(security.ApiKey))
            {
                startupLogger.LogWarning("Fallen-8 is running UNAUTHENTICATED (no Fallen8:Security:ApiKey configured). " +
                    "Configure an API key before exposing this server - the code endpoints run arbitrary in-process " +
                    "code unconditionally, so an unauthenticated server is an open code-execution surface.");
            }
            // Dynamic code execution is always on (compiled filter/cost fragments run in-process with
            // FULL TRUST). Always state the trust boundary; note plugin registration separately when enabled.
            startupLogger.LogWarning("Fallen-8 dynamic code execution is ALWAYS ENABLED: compiled filters/costs on " +
                "/path and /subgraph run in-process with FULL TRUST - anyone permitted to reach these endpoints is " +
                "trusted as the server process. This is a trust boundary, not a sandbox." +
                (security.EnableDynamicPluginLoading ? " Source plugin registration (/plugins/*) is also ENABLED." : string.Empty));

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
                // The bundled sample datasets (feature sample-graphs) ship under wwwroot/samples and
                // are served same-origin at /samples. .json is a known type; .jsonl is not, so map it
                // explicitly - otherwise the SPA fallback would answer .jsonl requests with index.html.
                // The wind-farm sample's documents/ dir (feature knowledge-demo) needs no addition:
                // .md, .pdf and .xlsx are all in FileExtensionContentTypeProvider's default map.
                var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
                contentTypes.Mappings[".jsonl"] = "application/x-ndjson";
                app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });
            }

            app.UseCors();
            app.UseRateLimiter();

            // Stamp the addressed namespace's id + name onto host-originated signals (feature
            // fleet-observability §3.5). Runs after routing (route value "ns" is populated) and only
            // when an exporter is enabled, so a default configuration runs zero extra middleware.
            if (observability.AnyExporterEnabled)
            {
                app.UseMiddleware<NoSQL.GraphDB.App.Namespaces.NamespaceEnrichmentMiddleware>();
            }

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
            // strings and /status already exposes counts+memory anonymously. Set
            // Prometheus:RequireApiKey=true (with an ApiKey) to require the key when /metrics is
            // reachable off-box.
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
                    "Fallen-8 observability: OTLP export is ENABLED to \"{Endpoint}\" (metrics + traces + logs, sampling ratio {Ratio}).",
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
