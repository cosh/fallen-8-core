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
using Microsoft.Extensions.Hosting;
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
            services.AddSingleton<CredentialResolver>();

            // The files a run may read are the files its job carried, and there is no other source: this
            // container mounts nothing and opens nothing on disk.
            services.AddSingleton<IJobFilesFactory, JobFilesFactory>();
            services.AddSingleton<IProviderHttpFactory, ProviderHttpFactory>();
            services.AddSingleton<RunGate>();
            // Singleton because it IS the process's memory of what is running. One slot per identity, dropped
            // on restart - not a run log (see RunTracker).
            services.AddSingleton<RunTracker>();

            // The ONE thing that outlives the process, and only while a run is in flight. Off unless an
            // operator mounts somewhere for it; RunSpool states exactly what may be written there and what
            // may not.
            services.AddSingleton(provider => new RunSpool(
                provider.GetRequiredService<IOptions<IntegrationsOptions>>().Value.SpoolDirectory,
                provider.GetRequiredService<ILogger<RunSpool>>()));

            // "This process is going away", as one injectable fact rather than a hosting dependency inside
            // the run machinery. Tolerant of there being no host at all, which is what a service-collection
            // test gets.
            services.AddSingleton(provider =>
            {
                var lifetime = provider.GetService<IHostApplicationLifetime>();
                return lifetime == null ? RunShutdown.Never : new RunShutdown(lifetime.ApplicationStopping);
            });

            // Picks up what a stopped process left in flight. It is not a scheduler: it reads the entries
            // that exist, resumes each once, and never runs anything again.
            services.AddHostedService<RunResumeService>();

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
                "Credentials and files arrive with the job that needs them and are dropped when the run " +
                "ends: this runtime has no credential store, no files mount and nothing to rotate.");

            // Said as two lines rather than one interpolation, because a non-positive ceiling means NO
            // ceiling and "a file may be up to 0 bytes" would be the opposite of what is enforced.
            if (options.MaxFileBytes > 0)
            {
                logger.LogInformation(
                    "A file a job carries may be up to {MaxFileBytes} bytes, decoded " +
                    "(Integrations:MaxFileBytes).", options.MaxFileBytes);
            }
            else
            {
                logger.LogWarning(
                    "Integrations:MaxFileBytes is {MaxFileBytes}, which switches the per-file ceiling OFF: " +
                    "a job may carry any file the request body bound admits, and how much memory a run " +
                    "spends is then whoever submits it to decide.", options.MaxFileBytes);
            }

            // The second ceiling, said separately for the same reason: a setting a provider declares
            // multiple takes a whole vehicle's extracts at once, so the number that decides whether this
            // container survives a job is their SUM rather than any one file's size.
            if (options.MaxJobFileBytes > 0)
            {
                logger.LogInformation(
                    "The files on one job may come to {MaxJobFileBytes} bytes in total, decoded " +
                    "(Integrations:MaxJobFileBytes).", options.MaxJobFileBytes);
            }
            else
            {
                logger.LogWarning(
                    "Integrations:MaxJobFileBytes is {MaxJobFileBytes}, which switches the job-total ceiling " +
                    "OFF: a job may carry as many legal files as the request body bound admits, and this " +
                    "process holds all of them at once.", options.MaxJobFileBytes);
            }

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

            if (String.IsNullOrWhiteSpace(options.SpoolDirectory))
            {
                logger.LogInformation(
                    "Nothing is written to disk: no Integrations:SpoolDirectory is configured, so a run in " +
                    "flight when this process stops is LOST rather than resumed, and a long embedding phase " +
                    "starts again from nothing.");
            }
            else
            {
                logger.LogInformation(
                    "Runs IN FLIGHT are spooled to {SpoolDirectory} so a restart continues them: the job's " +
                    "envelope, the snapshot and the embedding journal, never a credential and never a " +
                    "file's bytes. An entry is deleted on every ending a run has, so this is not a run " +
                    "history.", options.SpoolDirectory);
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
