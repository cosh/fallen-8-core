// MIT License
//
// IntegrationsHost.cs
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.Integrations.Configuration;
using NoSQL.GraphDB.Integrations.Contract;
using NoSQL.GraphDB.Integrations.Credentials;
using NoSQL.GraphDB.Integrations.Identity;
using NoSQL.GraphDB.Integrations.Providers.AutosarArxml;
using NoSQL.GraphDB.Integrations.Providers.CsvDeviceList;
using NoSQL.GraphDB.Integrations.Providers.FroniusSolar;
using NoSQL.GraphDB.Integrations.Providers.UnifiNetwork;
using NoSQL.GraphDB.Integrations.Run;
using NoSQL.GraphDB.Integrations.Validation;

namespace NoSQL.GraphDB.Integrations.Hosting
{
    /// <summary>
    ///   The runtime's service graph, in one place so the entry point stays a bind-and-run and a test can
    ///   build the same graph without Kestrel.
    /// </summary>
    public static class IntegrationsHost
    {
        /// <summary>Registers everything the runtime needs.</summary>
        public static IServiceCollection AddFallen8Integrations(IServiceCollection services,
            IConfiguration configuration)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            services.Configure<IntegrationsOptions>(configuration.GetSection(IntegrationsOptions.SectionName));
            services.Configure<Fallen8TargetOptions>(configuration.GetSection(Fallen8TargetOptions.SectionName));
            services.Configure<IntegrationsIdentityOptions>(
                configuration.GetSection(IntegrationsIdentityOptions.SectionName));
            services.Configure<IntegrationsObservabilityOptions>(
                configuration.GetSection(IntegrationsObservabilityOptions.SectionName));

            // The vocabulary is loaded once and throws on a malformed file, so a runtime with a
            // half-understood identity model does not start.
            services.AddSingleton(IdentifierVocabulary.Shipped);
            services.AddSingleton<SnapshotValidator>();

            // Process-wide, and deliberately not per run: two instances can be configured against the same
            // credential, so per-run counting would switch the other run's redaction off on the first run's
            // completion.
            services.AddSingleton<ActiveCredentials>();
            services.AddSingleton<IProviderFileStore, DirectoryFileStore>();
            services.AddSingleton<CredentialResolver>();
            services.AddSingleton<IProviderHttpFactory, ProviderHttpFactory>();
            services.AddSingleton<RunGate>();

            // Pure, and therefore reviewable and testable with nothing in the way.
            services.AddSingleton<IdentityResolver>();
            services.AddSingleton<SnapshotApplier>();

            // The shipped blueprints, each measuring something the others do not: one with no
            // credential, no paging and one entity kind; one with many entity kinds, paging and
            // topology; one with no strong identifier overlap at all; and one whose source is a
            // published STANDARD, so its identity is defined by the standard rather than invented by a
            // vendor and its entities are overwhelmingly related rather than merely listed.
            //
            // The ORDER is part of the pinned descriptor snapshot, so a new provider is appended.
            services.AddSingleton<IIntegrationProvider, CsvDeviceListProvider>();
            services.AddSingleton<IIntegrationProvider, UnifiNetworkProvider>();
            services.AddSingleton<IIntegrationProvider, FroniusSolarProvider>();
            services.AddSingleton<IIntegrationProvider, AutosarArxmlProvider>();
            services.AddSingleton(provider => new ProviderCatalog(
                provider.GetServices<IIntegrationProvider>(),
                provider.GetRequiredService<IdentifierVocabulary>()));

            services.AddSingleton<IGraphTargetFactory, GraphTargetFactory>();
            services.AddSingleton<JobRunner>();

            IntegrationsObservability.Add(services, configuration);

            // LAST, and the ordering is the point: the OTLP log exporter registers a provider, so a
            // redaction wrap installed before it would let the collector receive exactly what the console
            // was spared. Rewriting the existing registrations rather than clearing them is what makes the
            // filter cover the sinks an operator configured rather than only the one this code knows about.
            RedactingLoggerProvider.WrapRegisteredProviders(services);

            return services;
        }

        /// <summary>
        ///   Says out loud, once, what this process will and will not do: which graph it writes into,
        ///   whether a credentialed run is restricted to named hosts, whose certificate it will not validate,
        ///   and that it issues no save and no trim.
        /// </summary>
        public static void LogStartupPosture(ILogger logger, IntegrationsOptions options,
            Fallen8TargetOptions target)
        {
            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            logger.LogInformation(
                "Integration job runner listening on {BindAddress}:{Port}, writing into {BaseUrl} " +
                "(namespace default {Namespace}, api key {KeyState}).",
                options.BindAddress, options.Port, target.BaseUrl, target.DefaultNamespace,
                String.IsNullOrEmpty(target.ApiKey) ? "not set" : "set");

            logger.LogInformation(
                "Credentials arrive with the job that needs them and are dropped when the run ends: this " +
                "runtime has no credential store and nothing to rotate. Provider files are read from " +
                "{FilesDirectory}.",
                options.FilesDirectory);

            var allowedHosts = options.Credentials.AllowedHostSet();
            if (allowedHosts.Count == 0)
            {
                logger.LogWarning(
                    "Integrations:Credentials:AllowedHosts is empty, so a run holding a credential may " +
                    "contact ANY host. A source address arrives in a job's settings from whoever can reach " +
                    "the API, so set this to the hosts your own controllers live on.");
            }
            else
            {
                logger.LogInformation(
                    "A run holding a credential may contact only {AllowedHosts}, enforced on the way out.",
                    String.Join(", ", allowedHosts));
            }

            var selfSigned = options.SelfSignedHostSet();
            if (selfSigned.Count > 0)
            {
                logger.LogWarning(
                    "TLS certificates are NOT validated for {SelfSignedHosts}: the only place this runtime " +
                    "reduces trust, and not pinning - a named host is trusted for whatever certificate it " +
                    "presents.",
                    String.Join(", ", selfSigned));
            }

            logger.LogInformation(
                "This runtime issues no save and no trim: nothing here bounds the target's write-ahead log, " +
                "because a checkpoint is a whole-graph durability decision belonging to whoever owns the graph.");
        }

        /// <summary>
        ///   The provider ids a running catalog offers, for the startup line and for a test that wants to
        ///   assert the shipped set without reaching into DI twice.
        /// </summary>
        public static IEnumerable<String> ProviderIds(ProviderCatalog catalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException(nameof(catalog));
            }

            foreach (var descriptor in catalog.Descriptors)
            {
                yield return descriptor.Id;
            }
        }
    }
}
