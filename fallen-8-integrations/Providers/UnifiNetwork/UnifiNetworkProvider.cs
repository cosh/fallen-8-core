// MIT License
//
// UnifiNetworkProvider.cs
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
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NoSQL.GraphDB.Integrations.Contract;

namespace NoSQL.GraphDB.Integrations.Providers.UnifiNetwork
{
    /// <summary>
    ///   The many-entity blueprint: everything the CSV list does not have. A credential whose lifetime it
    ///   does not own, paged lists it must follow to the end, three entity kinds in one snapshot, and edges
    ///   addressed by claim to devices it has not emitted yet.
    ///
    ///   <para>The last of those is what it exists to prove: ENTITY ORDERING IS NOT A PROVIDER'S PROBLEM. A
    ///   device names its site and its uplink device by claim, and a client names the device it hangs off by
    ///   claim, so nothing here has to know whether the thing it points at has been emitted, resolved or
    ///   created. The entities come out in whatever order the console listed them.</para>
    ///
    ///   <para>It declares a COMPLETE snapshot, which is only honest because the paging loop refuses to
    ///   return a short list. Everything defensive in this provider follows from that one declaration:
    ///   absence in a complete snapshot is a withdrawal and, on the last claim, a deletion, so a list that
    ///   stops early does not miss devices, it removes them.</para>
    /// </summary>
    public sealed class UnifiNetworkProvider : IIntegrationProvider, IObservableProvider
    {
        /// <summary>The setting naming the full integration API base URL.</summary>
        public const String BaseUrlSetting = "baseUrl";

        /// <summary>The credential setting the API key arrives in.</summary>
        public const String ApiKeySetting = "apiKey";

        private const String KindSite = "site";
        private const String KindDevice = "device";
        private const String KindClient = "client";

        private const String ClaimMac = "mac";
        private const String ClaimIpv4 = "ipv4";
        private const String ClaimSiteId = "unifi-site-id";
        private const String ClaimDeviceId = "unifi-device-id";
        private const String ClaimClientId = "unifi-client-id";

        // The relation type happens to share the word "site" with the entity kind. They are different
        // vocabularies (one becomes an element label, the other an edge type), so they stay separate
        // constants rather than one that would have to mean both.
        private const String RelationSite = "site";
        private const String RelationUplink = "uplink";
        private const String RelationConnectedTo = "connectedTo";

        private const String PropertyName = "unifi.name";
        private const String PropertyModel = "unifi.model";
        private const String PropertyState = "unifi.state";
        private const String PropertyFirmwareVersion = "unifi.firmwareVersion";
        private const String PropertyIpAddress = "unifi.ipAddress";
        private const String PropertyFeatures = "unifi.features";
        private const String PropertyInterfaces = "unifi.interfaces";
        private const String PropertyClientType = "unifi.type";

        /// <summary>
        ///   Built once from the same constants the run emits, so a typo cannot make the descriptor and the
        ///   snapshot disagree: the catalog validates these claim types against the vocabulary at STARTUP,
        ///   and a mismatch caught there costs a restart where the same mistake caught per run costs a
        ///   duplicate element on every run.
        /// </summary>
        private static readonly ProviderDescriptor Declared = new ProviderDescriptor
        {
            Id = "unifi-network",
            DisplayName = "UniFi Network",
            Description =
                "Reads a UniFi Network console over its integration API and describes every site, adopted " +
                "device and connected client it serves, with the topology between them.",
            DocsUrl = ShippedDocs.IntegrationsPage,
            Settings = new[]
            {
                // Exactly two settings, and NO SETTING NARROWS THE RUN: no site filter and no
                // includeClients flag. Completeness licenses withdrawal, so a setting that changes the size
                // of what was looked at changes the meaning of every absence - turning it off after a run
                // that saw everything withdraws and deletes whatever it stopped looking at, and turning it
                // back on does not undo the deletion. Warning about that in help text is worse than not
                // building the switch: a reader who understands the warning still cannot recover the
                // deleted elements, and one who skips it loses data.
                new ProviderSetting
                {
                    Key = BaseUrlSetting,
                    Label = "Integration API base URL",
                    Kind = SettingKind.Url,
                    Required = true,
                    Help =
                        "The FULL integration API base URL, not just the console's address. A local console " +
                        "is https://{consoleIP}/proxy/network/integration; the cloud connector is " +
                        "https://api.ui.com/v1/connector/consoles/{consoleId}/proxy/network/integration. A " +
                        "bare host is refused rather than repaired.",
                },
                new ProviderSetting
                {
                    Key = ApiKeySetting,
                    Label = "API key",
                    Kind = SettingKind.Credential,
                    Required = true,
                    Help =
                        "The API key for the front door the base URL above names, and the two take DIFFERENT " +
                        "keys: a local console's key is created in the Network application under Settings and " +
                        "then Integrations, while an api.ui.com base URL is the cloud connector and takes a " +
                        "Site Manager key created at unifi.ui.com under Settings and then API Keys.",
                },
            },
            EntityKinds = new[] { KindSite, KindDevice, KindClient },
            ClaimTypes = new[] { ClaimMac, ClaimSiteId, ClaimDeviceId, ClaimClientId, ClaimIpv4 },
            RelationTypes = new[] { RelationSite, RelationUplink, RelationConnectedTo },

            // A console serves its whole state through these lists, which is what licenses the complete
            // declaration in ObserveAsync - and what obliges the paging loop to refuse a short list.
            CanObserveCompleteState = true,

            // Two request shapes, both GET, asserted over a whole run by a test. The vendor's contract has
            // verbs that restart devices and rewrite firewall policy, and since the document declares no
            // security scheme there is no published way to scope a key, so read-only has to be a property
            // of this code rather than of the credential.
            ReadOnly = true,

            // The PROVIDER'S half of the embedding opt-in, declarative so no code of this provider's sits on the
            // path that produces embedding text. A job asks for the other half, and both default off. Only
            // shape and coarse state appear: a counter would change between any two runs and make every run a
            // write.
            EntitySummaryTemplate = "{kind} {unifi.name}, {unifi.model}, {unifi.state}, {unifi.ipAddress}",
        };

        /// <inheritdoc />
        public ProviderDescriptor Descriptor => Declared;

        /// <summary>
        ///   The document the last observation returned, for the conformance suite alone: the snapshot
        ///   checks need what the provider actually produced, and a provider that hides it is recorded as
        ///   unjudgeable AND failing. It is the ONLY mutable state on this class, which is what makes the
        ///   singleton safe to invoke concurrently; everything a run needs lives on its context and in
        ///   locals.
        /// </summary>
        public SnapshotDocument? LastSnapshot { get; private set; }

        /// <inheritdoc />
        public async Task<SnapshotDocument> ObserveAsync(ProviderContext context,
            CancellationToken cancellationToken)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            // The credential is read here and never kept: these keys belong to somebody else's console and
            // are rotated on their own timetable, so a stored copy is a wrong copy.
            var reader = new UnifiClient(
                context.Http,
                UnifiClient.RequireIntegrationBaseUrl(context.Required(BaseUrlSetting)),
                context.RequiredCredential(ApiKeySetting),
                context.Logger);

            var entities = new List<EntityDto>();
            var diagnostics = new List<DiagnosticDto>();
            var withoutHardwareIdentity = 0;

            var sites = await reader.ReadSitesAsync(cancellationToken).ConfigureAwait(false);
            if (sites.Count == 0)
            {
                // A console always has at least one site, so an empty list is an answer that cannot be
                // trusted rather than an empty console - and an empty complete snapshot withdraws
                // everything this identity ever claimed.
                throw new ProviderSourceException(
                    "The console listed no sites. A console always has at least one, so this is an answer " +
                    "that cannot be trusted, and a complete snapshot with nothing in it would withdraw " +
                    "every element this integration has ever claimed.");
            }

            foreach (var site in sites)
            {
                var siteId = RequireId(site.Id, KindSite);
                entities.Add(BuildSite(siteId, site));

                foreach (var device in await reader.ReadDevicesAsync(siteId, cancellationToken)
                    .ConfigureAwait(false))
                {
                    var deviceId = RequireId(device.Id, KindDevice);

                    // One extra request per device, with NO setting to skip it: the uplink lives on the
                    // details resource and nowhere else, and skipping it would withdraw every uplink edge
                    // and recreate them when switched back, which reads as a topology change that never
                    // happened.
                    var details = await reader.ReadDeviceDetailsAsync(siteId, deviceId, cancellationToken)
                        .ConfigureAwait(false);

                    if (details == null)
                    {
                        // Listed, then gone: removed mid-run. Omitted ENTIRELY rather than emitted without
                        // its uplink, because a device emitted without the edge it has looks like a device
                        // whose uplink was removed.
                        diagnostics.Add(new DiagnosticDto(
                            DiagnosticCodes.DeviceRemovedDuringRun,
                            "The device was listed and then answered 404 when its details were read, so it " +
                            "was removed from the console during this run and is omitted from the snapshot.",
                            deviceId));
                        continue;
                    }

                    entities.Add(BuildDevice(siteId, deviceId, device, details.Uplink?.DeviceId));
                }

                foreach (var client in await reader.ReadClientsAsync(siteId, cancellationToken)
                    .ConfigureAwait(false))
                {
                    if (String.IsNullOrWhiteSpace(client.Id))
                    {
                        // The document requires an id, so this is a broken contract rather than a kind of
                        // client, and it is reported INDIVIDUALLY: one is worth looking at.
                        diagnostics.Add(new DiagnosticDto(
                            DiagnosticCodes.ClientWithoutId,
                            "A connected client arrived with no id, which the vendor's own contract requires. " +
                            "It is skipped: with no identity, every run would create another copy of it.",
                            client.Name));
                        continue;
                    }

                    if (String.IsNullOrWhiteSpace(client.MacAddress) &&
                        String.IsNullOrWhiteSpace(client.UplinkDeviceId))
                    {
                        // VPN and Teleport connections carry neither a MAC nor an uplink device, and both
                        // being ABSENT is how they are recognised: asking the discriminator instead is what
                        // makes a fifth client type throw, and the whole point of the flat type is that it
                        // finds two fields missing. They are COUNTED rather than emitted, because nothing
                        // about them would identify the same thing next run.
                        //
                        // Both rather than either, because either is the dangerous half of the test: a
                        // WIRED client whose console omitted the MAC it is required to report still has its
                        // client id and its uplink, and dropping it from a complete snapshot would withdraw
                        // and delete a client that is plainly still connected.
                        withoutHardwareIdentity++;
                        continue;
                    }

                    entities.Add(BuildClient(client));
                }
            }

            if (withoutHardwareIdentity > 0)
            {
                // One diagnostic carrying the count, not one per connection: a busy console has many, and
                // the fact worth reporting is that they exist at all.
                diagnostics.Add(new DiagnosticDto(
                    DiagnosticCodes.ClientsWithoutHardwareIdentity,
                    String.Format(
                        "{0} connected clients carry neither a MAC address nor an uplink device, which is " +
                        "what a VPN or Teleport connection looks like. They are counted rather than emitted, " +
                        "because nothing about them would identify the same thing on the next run.",
                        withoutHardwareIdentity)));
            }

            var snapshot = new SnapshotDocument
            {
                ProviderId = context.ProviderId,
                IntegrationInstanceId = context.InstanceId,
                Entities = entities,
                Diagnostics = diagnostics,

                // Honest only because every list above was followed to its end or the run was refused.
                Declares = SnapshotCompleteness.Complete,
            }.CapturedNow();

            LastSnapshot = snapshot;
            return snapshot;
        }

        // --- Describing what was read. Nothing below reaches the network, resolves anything or looks at
        // the graph: it turns one vendor document into one entity. -------------------------------------

        private static EntityDto BuildSite(String siteId, UnifiSite site)
        {
            var entity = new EntityDto { Kind = KindSite };
            entity.ClaimIfPresent(ClaimSiteId, siteId);
            entity.SetIfPresent(PropertyName, site.Name);
            return entity;
        }

        private static EntityDto BuildDevice(String siteId, String deviceId, UnifiDevice device,
            String? uplinkDeviceId)
        {
            var entity = new EntityDto { Kind = KindDevice };
            entity.ClaimIfPresent(ClaimDeviceId, deviceId);
            entity.ClaimIfPresent(ClaimMac, device.MacAddress);
            ClaimAddress(entity, device.IpAddress);
            entity.SetIfPresent(PropertyName, device.Name);
            entity.SetIfPresent(PropertyModel, device.Model);
            entity.SetIfPresent(PropertyState, device.State);
            entity.SetIfPresent(PropertyFirmwareVersion, device.FirmwareVersion);
            entity.SetIfPresent(PropertyIpAddress, device.IpAddress);
            entity.SetIfPresent(PropertyFeatures, MemberNames(device.Features, false));
            entity.SetIfPresent(PropertyInterfaces, MemberNames(device.Interfaces, true));
            entity.RelateIfPresent(RelationSite, ClaimSiteId, siteId);
            entity.RelateIfPresent(RelationUplink, ClaimDeviceId, uplinkDeviceId);
            return entity;
        }

        private static EntityDto BuildClient(UnifiConnectedClient client)
        {
            var entity = new EntityDto { Kind = KindClient };
            entity.ClaimIfPresent(ClaimClientId, client.Id);
            entity.ClaimIfPresent(ClaimMac, client.MacAddress);
            ClaimAddress(entity, client.IpAddress);
            entity.SetIfPresent(PropertyName, client.Name);
            entity.SetIfPresent(PropertyClientType, client.Type);
            entity.SetIfPresent(PropertyIpAddress, client.IpAddress);
            entity.RelateIfPresent(RelationConnectedTo, ClaimDeviceId, client.UplinkDeviceId);
            return entity;
        }

        /// <summary>
        ///   An id the vendor's contract requires and this provider cannot work without: a site's id is
        ///   what every device and client request hangs off, and a device's id is what its details request
        ///   needs. A missing one is therefore "the source answered unusably" and fails the run, which
        ///   withdraws nothing, rather than an omission that would delete elements.
        /// </summary>
        private static String RequireId(String? id, String kind)
        {
            if (String.IsNullOrWhiteSpace(id))
            {
                throw new ProviderSourceException(String.Format(
                    "The console served a {0} with no id, which its own published contract requires. Nothing " +
                    "further can be asked about it, so the run fails rather than describing a console it " +
                    "could only read part of.",
                    kind));
            }

            return id;
        }

        /// <summary>
        ///   The address claim, passed through exactly as the console reported it.
        ///
        ///   <para>This provider deliberately does NOT ask whether the value would survive its type's accept
        ///   pattern. Value validation is runtime work: the validator drops a claim whose value does not
        ///   canonicalise, reports <c>invalidIdentifierValue</c>, and KEEPS the entity, so a client that picked
        ///   up an IPv6-only lease costs one weak claim rather than the element. A copy of that judgement here
        ///   would be a second place where a value is judged, and the boundary exists precisely so a provider
        ///   never needs one.</para>
        /// </summary>
        private static void ClaimAddress(EntityDto entity, String? address)
        {
            entity.ClaimIfPresent(ClaimIpv4, address);
        }

        /// <summary>
        ///   The member names of one of the vendor's set-shaped objects, SORTED and then joined, or null
        ///   when the object named nothing. Sorting before joining is what stops two runs over one
        ///   unchanged console from differing, which would make every run a write.
        /// </summary>
        /// <param name="source">The raw <c>features</c> or <c>interfaces</c> object.</param>
        /// <param name="nonEmptyArraysOnly">True for <c>interfaces</c>, whose members are arrays and whose
        /// empty ones say the device has none of that interface; false for <c>features</c>, where a member
        /// being present at all is the fact.</param>
        private static String? MemberNames(JsonElement? source, Boolean nonEmptyArraysOnly)
        {
            if (source == null || source.Value.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var names = new List<String>();
            foreach (var member in source.Value.EnumerateObject())
            {
                if (member.Value.ValueKind == JsonValueKind.Null ||
                    (nonEmptyArraysOnly &&
                     (member.Value.ValueKind != JsonValueKind.Array || member.Value.GetArrayLength() == 0)))
                {
                    continue;
                }

                names.Add(member.Name);
            }

            if (names.Count == 0)
            {
                return null;
            }

            names.Sort(StringComparer.Ordinal);
            return String.Join(",", names);
        }
    }
}
