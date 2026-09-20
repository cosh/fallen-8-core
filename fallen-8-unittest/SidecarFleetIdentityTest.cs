// MIT License
//
// SidecarFleetIdentityTest.cs
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
using System.Linq;
using System.Text.RegularExpressions;
using System.Reflection;
using System.IO;
using System.Threading;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Agents.Configuration;
using NoSQL.GraphDB.Integrations.Configuration;
using NoSQL.GraphDB.Mcp.Configuration;
using NoSQL.GraphDB.Rest.Configuration;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The fleet identity the three sidecars declare (feature sidecar-shared-options). It used to be
    ///   three copies of one class differing in a twelve-character prefix, and each deployable's OTel
    ///   wiring then recovered the instance id by SEARCHING the attribute list it had just built - a
    ///   third copy of a third line.
    ///
    ///   <para>
    ///     Two things are pinned here, and the second is the one that was broken. The defaults are the
    ///     fleet's, identically for all three. And the resolved <see cref="FleetIdentity.InstanceId" />
    ///     agrees with the attribute of the same name, because that property is what a consumer passes
    ///     as OTel's <c>service.instance.id</c>: the integrations runtime passed nothing, so the SDK
    ///     minted a random per-process GUID and the promoted label churned on every restart while the
    ///     other two were stable.
    ///   </para>
    /// </summary>
    [TestClass]
    public class SidecarFleetIdentityTest
    {
        /// <summary>
        ///   One case per deployable, so a new sidecar that forgets its prefix shows up here. The
        ///   prefix is the ONLY thing that may differ between them, which is why it is the only
        ///   thing this table carries.
        /// </summary>
        private static IEnumerable<(String Deployable, AFleetIdentityOptions Options, String Prefix, String Section)> All()
        {
            yield return ("mcp", new McpIdentityOptions(), "f8-mcp-", McpIdentityOptions.SectionName);
            yield return ("integrations", new IntegrationsIdentityOptions(), "f8-integrations-",
                IntegrationsIdentityOptions.SectionName);
            yield return ("agents", new AgentsIdentityOptions(), "f8-agents-", AgentsIdentityOptions.SectionName);
        }

        /// <summary>
        ///   The dashboard's own stream selector, as a pattern to pull out of its JSON. One home,
        ///   because CodeQualityTest reads the same thing for the source literals and these two
        ///   readers must not drift.
        /// </summary>
        private const String SelectorPattern =
            @"service_name\s*=~\s*\\?""(?<pattern>[^""\\]+)\\?""";

        /// <summary>
        ///   What each sidecar's OTel wiring actually declares about itself, read off the BUILT
        ///   resource rather than inferred from the options. This is the pin for the two defects a
        ///   review found unpinned: the integrations runtime passed no serviceInstanceId, so
        ///   service.instance.id was the SDK's random per-process GUID, and its service.name was a
        ///   spelling the shipped dashboard's selector cannot match.
        /// </summary>
        private static IReadOnlyDictionary<String, Object> ResourceOf(
            String observabilitySection, String identitySection, String instanceId,
            Action<IServiceCollection, IConfiguration> wire)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<String, String>
            {
                [observabilitySection + ":Otlp:Endpoint"] = "http://collector.invalid:4317",
                [identitySection + ":Instance:Id"] = instanceId,
                [identitySection + ":Instance:Name"] = "Pinned instance",
                [identitySection + ":Tenant:Id"] = "acme",
            }).Build();

            var services = new ServiceCollection();
            services.AddLogging();
            wire(services, config);

            using var provider = services.BuildServiceProvider();
            var meterProvider = provider.GetService(typeof(OpenTelemetry.Metrics.MeterProvider));
            Assert.IsNotNull(meterProvider,
                observabilitySection + " configured an OTLP endpoint but registered no MeterProvider, "
                + "so nothing was wired and this test would pass vacuously");

            var resource = meterProvider.GetType()
                .GetProperty("Resource", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(meterProvider) as OpenTelemetry.Resources.Resource;
            Assert.IsNotNull(resource,
                "MeterProviderSdk no longer exposes Resource, so this pin cannot read what the "
                + "process declares. Find the new way rather than deleting the test.");

            return resource.Attributes.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        }

        /// <summary>The three wirings, so both pins below cover all of them.</summary>
        private static IEnumerable<(String Name, String Observability, String Identity, String Expected,
            Action<IServiceCollection, IConfiguration> Wire)> Wirings()
        {
            yield return ("mcp", "Mcp:Observability", "Mcp:Identity", "fallen8-mcp",
                (s, c) => NoSQL.GraphDB.Mcp.Hosting.McpObservability.AddMcpObservability(s, c));
            yield return ("integrations", "Integrations:Observability", "Integrations:Identity", "fallen8-integrations",
                (s, c) => NoSQL.GraphDB.Integrations.Hosting.IntegrationsObservability.Add(s, c));
            yield return ("agents", "Agents:Observability", "Agents:Identity", "fallen8-agents",
                (s, c) => NoSQL.GraphDB.Agents.Hosting.AgentsObservability.AddAgentsObservability(s, c));
        }

        [TestMethod]
        public void EverySidecarDeclaresTheResolvedInstanceIdAsServiceInstanceId()
        {
            // DEFECT 2. The other two sidecars always passed the resolved id into
            // AddService(serviceInstanceId:); the integrations runtime passed nothing, so the SDK
            // minted a random GUID per process and the promoted label moved on every restart.
            // Nothing pinned it, which is why it survived a feature and a review.
            foreach (var (name, observability, identity, expected, wire) in Wirings())
            {
                var attributes = ResourceOf(observability, identity, "pinned-" + name, wire);

                Assert.IsTrue(attributes.TryGetValue("service.instance.id", out var serviceInstanceId),
                    name + " declares no service.instance.id, so the SDK supplies a random one that "
                    + "churns on every restart");
                Assert.AreEqual("pinned-" + name, serviceInstanceId,
                    name + " reports '" + serviceInstanceId + "' as service.instance.id instead of the "
                    + "configured instance id, so the promoted label does not survive a restart");
                Assert.AreEqual(serviceInstanceId, attributes[FleetIdentity.InstanceIdKey],
                    name + ": service.instance.id and fallen8.instance.id must be the same value, or a "
                    + "dashboard joining one to the other matches nothing");

                Assert.AreEqual(expected, attributes["service.name"], name + " service.name");
                Assert.AreEqual("acme", attributes[FleetIdentity.TenantIdKey], name + " tenant");
                Assert.AreEqual("Pinned instance", attributes[FleetIdentity.InstanceNameKey],
                    name + " instance name");
            }
        }

        [TestMethod]
        public void EverySidecarsOwnResourceMatchesTheShippedDashboardsSelector()
        {
            // DEFECT 3, pinned at the RESOURCE rather than at the source literal. CodeQualityTest
            // holds the literals against the dashboard; this holds what the process actually
            // declares, so a wiring that computed its name instead of writing it is covered too.
            var dashboard = Path.Combine(TestRepo.Root(), "observability", "grafana", "dashboards",
                "per-tenant.json");
            var selectors = Regex.Matches(File.ReadAllText(dashboard), SelectorPattern)
                .Select(m => m.Groups["pattern"].Value)
                .Where(p => !p.Contains('$', StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            Assert.IsTrue(selectors.Count > 0,
                "no literal service_name selector in the shipped dashboard, so this pin has nothing "
                + "to check. The dashboard is the source of the convention.");

            foreach (var (name, observability, identity, _, wire) in Wirings())
            {
                var declared = (String)ResourceOf(observability, identity, "pinned-" + name, wire)["service.name"];
                foreach (var pattern in selectors)
                {
                    Assert.IsTrue(
                        Regex.IsMatch(declared, "^(?:" + pattern + ")$", RegexOptions.CultureInvariant),
                        name + " declares service.name '" + declared + "', which the shipped dashboard's "
                        + "selector /" + pattern + "/ does not match, so its logs are absent from that panel");
                }
            }
        }

        [TestMethod]
        public void TheClampIsSafeForEveryApiItsConsumersArmWithIt()
        {
            // OptionBounds' summary states the ceiling of three APIs and calls MaxSeconds the
            // tightest of them. Those are measurable facts, so they are measured here rather than
            // asserted in prose: an earlier version of that comment claimed all three shared the
            // Int32.MaxValue-ms limit, which is false for both timers, and nothing caught it.
            var clamped = OptionBounds.Seconds(Int32.MaxValue);

            using (var client = new HttpClient())
            {
                client.Timeout = clamped;   // throws if the clamp is looser than this API allows
                Assert.AreEqual(clamped, client.Timeout);
            }

            using (var source = new CancellationTokenSource())
            {
                source.CancelAfter(clamped);
            }

            using (var timer = new PeriodicTimer(clamped))
            {
                Assert.IsNotNull(timer);
            }

            // And the tightest of the three is the one that binds, so the clamp is not merely safe
            // but as generous as it can be: one second more would break HttpClient.
            Assert.ThrowsException<ArgumentOutOfRangeException>(
                () => new HttpClient { Timeout = TimeSpan.FromSeconds(OptionBounds.MaxSeconds + 1) },
                "MaxSeconds is meant to be HttpClient.Timeout's own limit; if this stops throwing, "
                + "the platform moved and the type's summary needs re-measuring");
        }

        [TestMethod]
        public void EachSidecarsAutoFilledInstanceId_CarriesItsOwnPrefix()
        {
            foreach (var (deployable, options, prefix, _) in All())
            {
                var resolved = options.Resolve();

                Assert.IsTrue(resolved.InstanceId.StartsWith(prefix, StringComparison.Ordinal),
                    deployable + " minted '" + resolved.InstanceId + "', which does not say which deployable it came from");
                Assert.AreEqual(prefix.Length + 12, resolved.InstanceId.Length,
                    "the generated half is 12 hex characters of a GUID");
            }
        }

        [TestMethod]
        public void TheThreeSectionNames_AreTheOneThingAnOperatorWritesAndStayDistinct()
        {
            var sections = All().Select(x => x.Section).ToList();

            CollectionAssert.AreEquivalent(
                new[] { "Mcp:Identity", "Integrations:Identity", "Agents:Identity" }, sections,
                "sharing the base must not have moved the section an operator configures");
            Assert.AreEqual(sections.Count, sections.Distinct(StringComparer.Ordinal).Count());
        }

        [TestMethod]
        public void TheDefaultsAreTheFleetsAndAreTheSameForAllThree()
        {
            foreach (var (deployable, options, _, _) in All())
            {
                var resolved = options.Resolve();

                Assert.AreEqual("default", resolved.TenantId, deployable + " tenant id");
                Assert.AreEqual(resolved.TenantId, resolved.TenantName,
                    deployable + ": an unset tenant name falls back to its id");
                Assert.AreEqual(resolved.InstanceId, resolved.InstanceName,
                    deployable + ": an unset instance name falls back to its id");
            }
        }

        [TestMethod]
        public void AnOperatorsOwnValuesWin_AndAreNotPrefixed()
        {
            foreach (var (deployable, options, _, _) in All())
            {
                options.Tenant = new IdentityLevel { Id = "acme", Name = "ACME GmbH" };
                options.Instance = new IdentityLevel { Id = "f8-prod-eu", Name = "Production EU" };

                var resolved = options.Resolve();

                Assert.AreEqual("acme", resolved.TenantId, deployable);
                Assert.AreEqual("ACME GmbH", resolved.TenantName, deployable);
                Assert.AreEqual("f8-prod-eu", resolved.InstanceId,
                    deployable + ": a configured id is used verbatim, so it can MATCH the apiApp instance it fronts");
                Assert.AreEqual("Production EU", resolved.InstanceName, deployable);
            }
        }

        [TestMethod]
        public void BlankAndWhitespaceCountAsUnset_RatherThanAsAnEmptyIdentity()
        {
            foreach (var (deployable, options, prefix, _) in All())
            {
                options.Tenant = new IdentityLevel { Id = "   ", Name = "" };
                options.Instance = new IdentityLevel { Id = "", Name = "\t" };

                var resolved = options.Resolve();

                Assert.AreEqual("default", resolved.TenantId, deployable + ": whitespace is not a tenant");
                Assert.AreEqual("default", resolved.TenantName, deployable);
                Assert.IsTrue(resolved.InstanceId.StartsWith(prefix, StringComparison.Ordinal),
                    deployable + ": an empty instance id auto-fills");
                Assert.AreEqual(resolved.InstanceId, resolved.InstanceName, deployable);
            }
        }

        [TestMethod]
        public void ANulledOutLevel_DoesNotThrow()
        {
            // Configuration binding can leave a level null if a section is present but empty, and
            // resolving identity is startup code: throwing here would take the process down over a
            // blank line in appsettings.json.
            foreach (var (deployable, options, prefix, _) in All())
            {
                options.Tenant = null!;
                options.Instance = null!;

                var resolved = options.Resolve();

                Assert.AreEqual("default", resolved.TenantId, deployable);
                Assert.IsTrue(resolved.InstanceId.StartsWith(prefix, StringComparison.Ordinal), deployable);
            }
        }

        [TestMethod]
        public void TheResolvedInstanceId_IsTheSameValueTheAttributeCarries()
        {
            // THE defect this class exists to prevent. The wirings pass InstanceId as OTel's
            // service.instance.id and the attribute list as the fleet identity; if those two ever
            // disagreed, a dashboard joining one to the other would silently match nothing.
            foreach (var (deployable, options, _, _) in All())
            {
                var resolved = options.Resolve();
                var attributes = resolved.Attributes().ToDictionary(kv => kv.Key, kv => kv.Value);

                Assert.AreEqual(resolved.InstanceId, attributes[FleetIdentity.InstanceIdKey], deployable);
                Assert.AreEqual(resolved.InstanceName, attributes[FleetIdentity.InstanceNameKey], deployable);
                Assert.AreEqual(resolved.TenantId, attributes[FleetIdentity.TenantIdKey], deployable);
                Assert.AreEqual(resolved.TenantName, attributes[FleetIdentity.TenantNameKey], deployable);
            }
        }

        [TestMethod]
        public void TheAttributeKeys_AreTheFourTheFleetDashboardsJoinOn()
        {
            var keys = new McpIdentityOptions().Resolve().Attributes().Select(kv => kv.Key).ToList();

            CollectionAssert.AreEqual(
                new[]
                {
                    "fallen8.tenant.id", "fallen8.tenant.name", "fallen8.instance.id", "fallen8.instance.name",
                },
                keys,
                "these are wire names: the collector's spanmetrics promotion and the Grafana panels use them, "
                + "so renaming one is a dashboard change and not a refactor");
        }

        [TestMethod]
        public void RepeatedAttributeCalls_AreStableOnOneResolvedIdentity()
        {
            // Resolve() mints; the result does not. The wirings resolve once and then read the
            // property and the list off the same object, which is what makes that safe.
            var resolved = new AgentsIdentityOptions().Resolve();

            var first = resolved.Attributes().Select(kv => kv.Value?.ToString()).ToList();
            var second = resolved.Attributes().Select(kv => kv.Value?.ToString()).ToList();

            CollectionAssert.AreEqual(first, second);
            Assert.AreEqual(resolved.InstanceId, resolved.InstanceId);
        }

        [TestMethod]
        public void ResolvingTwiceWithNoConfiguredId_MintsTwoIdentities_WhichIsWhyItIsCalledOnce()
        {
            // Documented on Resolve() and worth a test rather than only a comment: this is the
            // reason each wiring resolves once into a local and never calls it again.
            var options = new IntegrationsIdentityOptions();

            Assert.AreNotEqual(options.Resolve().InstanceId, options.Resolve().InstanceId,
                "if these ever matched, the call-once rule would have quietly stopped mattering");

            options.Instance = new IdentityLevel { Id = "pinned" };
            Assert.AreEqual(options.Resolve().InstanceId, options.Resolve().InstanceId,
                "a configured id is stable across calls, which is the point of configuring one");
        }
    }
}
