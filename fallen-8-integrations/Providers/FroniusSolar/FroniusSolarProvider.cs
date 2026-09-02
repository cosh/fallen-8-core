// MIT License
//
// FroniusSolarProvider.cs
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
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Integrations.Contract;

namespace NoSQL.GraphDB.Integrations.Providers.FroniusSolar
{
    /// <summary>
    ///   The <c>fronius-solar</c> blueprint: a solar installation's inverters and the device that logs
    ///   them, read from the local Solar API.
    ///
    ///   <para><b>What it is for.</b> It proves that a source sharing NO STRONG IDENTIFIER WITH ANYTHING
    ///   ELSE still works, and that nothing in the contract forces a credential. The local Solar API
    ///   exposes no MAC address and no manufacturer serial anywhere, so its only overlap with another
    ///   view of the same box is the IP address, which is weak and resolves nothing; its identity across
    ///   its own runs comes entirely from instance-scoped native ids. There is therefore no credential
    ///   setting at all, which is the other half of the point.</para>
    ///
    ///   <para><b>Shape and coarse state, never time series.</b> It issues no realtime request. Power,
    ///   current, voltage and energy counters change between any two runs, so landing them would make
    ///   every run a write and make the zero-mutation invariant unobservable for the one provider whose
    ///   source is never unchanged. The one numeric it records, <c>fronius.pvPower</c>, is the configured
    ///   nameplate figure rather than a reading.</para>
    ///
    ///   <para>The requests live in <see cref="FroniusClient"/> and the vendor's own tables in
    ///   <see cref="FroniusStatusCodes"/> and <see cref="FroniusInverterStatus"/>; what lives here is what
    ///   the run ASSERTS: which entities exist, what identifies them, and which of them holds the address.</para>
    /// </summary>
    public sealed class FroniusSolarProvider : IIntegrationProvider, IObservableProvider
    {
        private const String ProviderIdValue = "fronius-solar";
        private const String BaseUrlSetting = "baseUrl";

        private const String InverterKind = "inverter";
        private const String DatamanagerKind = "datamanager";

        private const String UniqueIdClaim = "fronius-unique-id";
        private const String LoggerIdClaim = "fronius-logger-id";
        private const String Ipv4Claim = "ipv4";

        private const String LoggedByRelation = "loggedBy";

        /// <summary>
        ///   Data that is true before any run exists, so it is built once and shared. One setting, no
        ///   credential setting, and <c>canObserveCompleteState</c> true because one request returns every
        ///   inverter the device has seen in the last 24 hours: absence there means removal rather than a
        ///   sleeping inverter.
        /// </summary>
        private static readonly ProviderDescriptor DescriptorValue = new ProviderDescriptor
        {
            Id = ProviderIdValue,
            DisplayName = "Fronius Solar API (local)",
            Description =
                "Reads a Fronius inverter's or datamanager's local Solar API over unauthenticated HTTP " +
                "and describes the inverters it has seen in the last 24 hours plus the logging device " +
                "that fronts them. Shape and coarse state only: no power, current, voltage or energy " +
                "readings are read or recorded.",
            DocsUrl = ShippedDocs.IntegrationsPage,
            Settings = new[]
            {
                new ProviderSetting
                {
                    Key = BaseUrlSetting,
                    Label = "Device address",
                    Kind = SettingKind.Url,
                    Required = true,
                    Help =
                        "The device's address on your own network, with its scheme: " +
                        "http://192.168.1.50. Nothing else is needed, and there is deliberately no " +
                        "credential: the local Solar API is unauthenticated HTTP. If the device answers " +
                        "404, its Solar API is switched off - turn it on in the inverter's own web " +
                        "interface, under Communication and then Solar API.",
                },
            },
            EntityKinds = new[] { InverterKind, DatamanagerKind },
            ClaimTypes = new[] { UniqueIdClaim, LoggerIdClaim, Ipv4Claim },
            RelationTypes = new[] { LoggedByRelation },
            CanObserveCompleteState = true,
            ReadOnly = true,

            // The PROVIDER'S half of the embedding opt-in, declarative so no code of this provider's sits on the
            // path that produces embedding text. A job asks for the other half, and both default off. The status
            // word changes at dawn and dusk rather than every second, which is what keeps this embeddable at all.
            EntitySummaryTemplate = "{kind} {fronius.customName}, {fronius.status}",
        };

        /// <inheritdoc/>
        public ProviderDescriptor Descriptor => DescriptorValue;

        /// <summary>
        ///   The document the last <c>ObserveAsync</c> returned, for the conformance suite. It is the ONLY
        ///   mutable state on this provider, which is registered as a singleton and may be invoked
        ///   concurrently: a reference assignment is atomic, everything else a run needs is a local, and
        ///   nothing in the run reads this back.
        /// </summary>
        public SnapshotDocument? LastSnapshot { get; private set; }

        /// <summary>
        ///   Reads the device once and describes what it said.
        /// </summary>
        /// <exception cref="ProviderConfigurationException">The address setting is missing or unusable.</exception>
        /// <exception cref="ProviderSourceException">The device did not answer, answered a failure (which
        /// on this API arrives with HTTP 200), or named no inverter it could identify. The run then fails
        /// and withdraws nothing, because "I could not look" must never become "there is nothing there".</exception>
        public async Task<SnapshotDocument> ObserveAsync(ProviderContext context,
            CancellationToken cancellationToken)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var root = FroniusClient.Root(context.Required(BaseUrlSetting));
            var client = new FroniusClient(context.Http, context.Logger);

            var api = await client.ReadApiRootAsync(root, cancellationToken).ConfigureAwait(false);
            var inverters = await client.ReadInvertersAsync(api.Resources, cancellationToken)
                .ConfigureAwait(false);
            var loggerReading = await client.ReadLoggerAsync(api.Resources, cancellationToken)
                .ConfigureAwait(false);

            var snapshot = new SnapshotDocument
            {
                ProviderId = ProviderIdValue,
                IntegrationInstanceId = context.InstanceId,
                SourceVersion = api.ApiVersion == null
                    ? null
                    : "solar_api v" + api.ApiVersion.Value.ToString(CultureInfo.InvariantCulture),
            };

            // Complete: one request returns every inverter the device has seen in the last 24 hours, so an
            // absence is a removal rather than a sleeping inverter. Nothing narrows what this run looks at.
            snapshot.Declares = SnapshotCompleteness.Complete;
            snapshot.CapturedNow();

            // The logging device's id CONTAINS A DOT on the vendor's own example (240.107620), which is why
            // it claims under fronius-logger-id: the inverter type's accept pattern rejects the dot.
            var loggerId = loggerReading.Logger == null
                ? null
                : FroniusValues.Text(loggerReading.Logger.UniqueId);

            if (loggerReading.AbsentBecause != null)
            {
                snapshot.Diagnostics.Add(new DiagnosticDto(DiagnosticCodes.LoggerInfoUnavailable,
                    loggerReading.AbsentBecause, root.Host));
            }
            else if (loggerId == null)
            {
                snapshot.Diagnostics.Add(new DiagnosticDto(DiagnosticCodes.LoggerInfoUnavailable,
                    "GetLoggerInfo answered with a logging device that carries no UniqueID. Nothing could " +
                    "identify it across runs, so it is described as absent rather than created again on " +
                    "every run, and the address claim falls to the inverter under the same rule.",
                    root.Host));
            }

            var address = AddressOf(root, snapshot);

            // WHICH DEVICE HOLDS THE ADDRESS is derived from the contract rather than assumed. A
            // datamanager card fronts several inverters at one address, so giving each inverter that
            // address would advertise one overlap per inverter against the same switch port, all but one
            // wrong. If GetLoggerInfo answered, the address belongs to the logging device; if it failed the
            // documented way, it belongs to the single inverter; and with more than one inverter and no
            // logging device there is no honest holder, so no address claim is asserted at all.
            var addressOnLogger = address != null && loggerId != null;
            var addressOnInverter = address != null && loggerId == null && inverters.Count == 1;
            if (address != null && !addressOnLogger && !addressOnInverter)
            {
                context.Logger.LogInformation(
                    "The device reports {InverterCount} inverters and no logging device, so no device here " +
                    "holds {Address} unambiguously and no ipv4 claim is asserted: one claim per inverter " +
                    "would advertise overlaps against the same switch port, all but one of them wrong.",
                    inverters.Count, address);
            }

            if (loggerId != null)
            {
                snapshot.Entities.Add(Datamanager(loggerReading.Logger!, loggerId,
                    addressOnLogger ? address : null));
            }

            var described = 0;
            var unidentified = new List<String>();
            foreach (var entry in inverters)
            {
                var uniqueId = FroniusValues.Text(entry.Device.UniqueId);
                if (uniqueId == null)
                {
                    // Skipped and counted rather than invented: without a UniqueID nothing could resolve to
                    // it, so every run would create another copy of the same inverter.
                    unidentified.Add(entry.DeviceId);
                    snapshot.Diagnostics.Add(new DiagnosticDto(DiagnosticCodes.InverterWithoutUniqueId,
                        "This inverter carries no UniqueID, so nothing could ever resolve to it and every " +
                        "run would create another copy. It is skipped for this run.",
                        "device " + entry.DeviceId));
                    continue;
                }

                snapshot.Entities.Add(Inverter(entry.Device, uniqueId, loggerId,
                    addressOnInverter ? address : null));
                described++;
            }

            if (described == 0)
            {
                // The empty-list reasoning, applied to the case the list was not empty but nothing in it
                // could be identified: a complete snapshot describing no inverter withdraws and deletes
                // every inverter this identity ever claimed, and "I could not identify anything" is not
                // "there is nothing there".
                throw new ProviderSourceException(String.Format(
                    "The device reported {0} inverter(s) and none carried a UniqueID (device ids {1}). " +
                    "Declaring a complete snapshot with no inverter in it would withdraw and delete every " +
                    "inverter this identity ever claimed, so the run fails and withdraws nothing.",
                    inverters.Count.ToString(CultureInfo.InvariantCulture),
                    String.Join(", ", unidentified)));
            }

            context.Logger.LogInformation(
                "Fronius Solar API at {Host} described {InverterCount} inverter(s) and {LoggerCount} " +
                "logging device(s), with no realtime request issued.",
                root.Host, described, loggerId == null ? 0 : 1);

            LastSnapshot = snapshot;
            return snapshot;
        }

        /// <summary>
        ///   The address claim's value, or null with the diagnostic that says why there is none. A HOST
        ///   NAME rather than an IPv4 literal asserts NO address claim: this claim is the only overlap this
        ///   provider has with another view of the same box, so its silent absence would be invisible, and
        ///   recording a name under <c>ipv4</c> would put a value in the claim space that never
        ///   canonicalises to an address.
        /// </summary>
        private static String? AddressOf(Uri root, SnapshotDocument snapshot)
        {
            if (root.HostNameType == UriHostNameType.IPv4)
            {
                return root.Host;
            }

            snapshot.Diagnostics.Add(new DiagnosticDto(DiagnosticCodes.AddressIsNotAnIpv4Literal,
                "The device address names a host rather than an IPv4 literal, so no ipv4 claim is " +
                "asserted. That claim is this integration's ONLY overlap with another view of the same " +
                "box, because the Solar API exposes no MAC address and no manufacturer serial anywhere, " +
                "so its absence is reported rather than left invisible. Configure the address as an IPv4 " +
                "literal to get the overlap back.",
                root.Host));
            return null;
        }

        /// <summary>
        ///   One inverter, as this run asserts it: its instance-scoped native id, optionally the address,
        ///   the properties the source answered, and the edge to whatever logs it.
        /// </summary>
        private static EntityDto Inverter(FroniusInverterDto device, String uniqueId, String? loggerId,
            String? address)
        {
            var entity = new EntityDto { Kind = InverterKind };
            entity.ClaimIfPresent(UniqueIdClaim, uniqueId);
            entity.ClaimIfPresent(Ipv4Claim, address);

            // Decoded once, for both platforms: CustomName arrives as HTML entities on a Datamanager or a
            // Symo Hybrid (the vendor's own example being the run &#80;&#114;&#105;) and as plain text on a
            // GEN24, and decoding is idempotent on plain text, which is what makes one code path correct
            // for both rather than a platform switch nothing could keep true.
            entity.SetIfPresent("fronius.customName", Trimmed(WebUtility.HtmlDecode(device.CustomName)));

            // The NUMBER, never a model name: the document's type table has more than 250 entries, exists
            // only in a PDF, and is wrong anyway on the newest platforms, which it says always report 1.
            entity.SetIfPresent("fronius.deviceType", device.DeviceType);

            entity.SetIfPresent("fronius.status", FroniusInverterStatus.Describe(device.StatusCode));

            // Recorded, NOT obeyed: "do not display this in visualizations" is a dashboard preference, and
            // dropping the inverter would withdraw it from the graph the moment somebody set it.
            entity.SetIfPresent("fronius.show", FroniusValues.Flag(device.Show));

            if (device.ErrorCode != null && device.ErrorCode.Value != FroniusInverterStatus.AbsentErrorCode)
            {
                // -1 is ABSENCE per the document, not an error numbered minus one, so it is not recorded as
                // a number somebody will read as a fault.
                entity.SetIfPresent("fronius.errorCode", device.ErrorCode.Value);
            }

            entity.SetIfPresent("fronius.pvPower", device.PvPower);
            entity.RelateIfPresent(LoggedByRelation, LoggerIdClaim, loggerId);

            return entity;
        }

        /// <summary>The logging device that fronts the Solar API: a datamanager card, a hybridmanager, or
        /// whatever else serves it.</summary>
        private static EntityDto Datamanager(FroniusLoggerDto logger, String loggerId, String? address)
        {
            var entity = new EntityDto { Kind = DatamanagerKind };
            entity.ClaimIfPresent(LoggerIdClaim, loggerId);
            entity.ClaimIfPresent(Ipv4Claim, address);

            entity.SetIfPresent("fronius.productId", Trimmed(logger.ProductId));
            entity.SetIfPresent("fronius.platformId", Trimmed(logger.PlatformId));
            entity.SetIfPresent("fronius.hwVersion", Trimmed(logger.HardwareVersion));
            entity.SetIfPresent("fronius.swVersion", Trimmed(logger.SoftwareVersion));
            entity.SetIfPresent("fronius.timezoneLocation", Trimmed(logger.TimezoneLocation));

            return entity;
        }

        /// <summary>Trimmed text, or null for text the source left blank, which is the same statement as
        /// not answering at all.</summary>
        private static String? Trimmed(String? text)
        {
            return String.IsNullOrWhiteSpace(text) ? null : text!.Trim();
        }
    }
}
