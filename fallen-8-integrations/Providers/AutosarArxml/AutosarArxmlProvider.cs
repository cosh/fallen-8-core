// MIT License
//
// AutosarArxmlProvider.cs
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
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using NoSQL.GraphDB.Integrations.Contract;

namespace NoSQL.GraphDB.Integrations.Providers.AutosarArxml
{
    /// <summary>
    ///   THE STANDARDS BLUEPRINT: an AUTOSAR classic-platform system extract, which is how the
    ///   automotive industry exchanges the communication matrix of a vehicle network. It measures
    ///   something the other three shipped providers do not, and that is why it exists beside them:
    ///   a source that is a FILE with a published standard behind it, whose identity is defined by
    ///   the standard rather than invented by a vendor, and whose entities are overwhelmingly
    ///   RELATED to each other rather than merely listed.
    ///
    ///   <para>Everything hard about reading the format is in <see cref="ArxmlReader"/>. This type
    ///   only maps what the reader saw onto the snapshot contract, so the provider stays what every
    ///   provider is: a description of a source, with no say in identity, resolution or deletion.</para>
    ///
    ///   <para>Completeness is always <see cref="SnapshotCompleteness.Complete"/>, and that is the
    ///   whole reason this is an integration rather than a converter script: a system extract IS the
    ///   complete description of its network, so re-observing the next release withdraws exactly what
    ///   the release removed, and the change feed becomes the release diff for free.</para>
    ///
    ///   <para>THE SET OF FILES IS THE SOURCE, and that is where completeness bites. A job may carry one
    ///   extract per domain or per bus; they are read as one source, so the snapshot is complete over
    ///   their UNION and a later run given fewer of them withdraws - and then deletes - everything only
    ///   the missing file described. The <c>file</c> setting's help text says so, because whoever fills
    ///   in the form is the only person who can avoid it.</para>
    /// </summary>
    public sealed class AutosarArxmlProvider : IIntegrationProvider, IObservableProvider
    {
        /// <summary>The stable provider id. It is assigned once and never reused.</summary>
        public const String ProviderId = "autosar-arxml";

        /// <summary>The setting naming the files, each a NAME and never a path.</summary>
        public const String FileSetting = "file";

        /// <summary>The setting naming the vehicle these extracts describe.</summary>
        public const String VehicleSetting = "vehicle";

        /// <summary>
        ///   The one identifier type this provider claims. Its value is the VEHICLE followed by the
        ///   element's AUTOSAR reference path, because the path alone does not identify an element.
        ///   A short-name is unique among its siblings, so a reference path identifies an element
        ///   within ONE model; nothing in the standard coordinates package names across independently
        ///   authored models, and the standardised packages are common to all of them by construction.
        ///   A key built from the path alone therefore asserts that two different vehicles share those
        ///   elements. The ARXML form is specified by AUTOSAR's ARXML Serialization Rules
        ///   (AUTOSAR_TPS_ARXMLSerializationRules, R20-11).
        /// </summary>
        public const String VehiclePathClaimType = "arxml-vehicle-path";

        /// <summary>
        ///   The prefix every property this provider writes carries. It lives in ONE place: two
        ///   providers describing "the name" of something rarely mean the same thing, and an
        ///   unprefixed key means the value depends on which integration ran last.
        /// </summary>
        public const String PropertyPrefix = "arxml.";

        /// <summary>
        ///   What a vehicle name may be. Deliberately narrower than a free-text setting: the name
        ///   becomes the first segment of a claim value whose parts are split on the path's leading
        ///   slash, so a name carrying a slash would make the split ambiguous, and the vocabulary's
        ///   own accept pattern would refuse it anyway. Refusing it HERE means the operator gets a
        ///   sentence about the setting instead of a claim-key validation failure.
        /// </summary>
        private static readonly Regex VehiclePattern =
            new Regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

        /// <summary>
        ///   An element's identity: the vehicle, then its AUTOSAR reference path. The path already
        ///   begins with a slash, so nothing is inserted between them and the FIRST slash is what
        ///   splits the value back into its two parts. A null path yields null, so a missing path
        ///   stays missing rather than becoming a claim on the vehicle alone.
        /// </summary>
        private static String? VehicleKey(String vehicle, String? path)
            => path == null ? null : vehicle + path;

        private static readonly ProviderDescriptor DescriptorData = new ProviderDescriptor
        {
            Id = ProviderId,
            DisplayName = "AUTOSAR system extract (ARXML)",
            Description =
                "Reads the AUTOSAR classic-platform system extracts (ARXML, schema r4.0) the job carries " +
                "and describes the communication matrix they hold: each bus, its channels, its ECUs, " +
                "frames, PDUs, signals, system signals and scaling methods, with the send and receive " +
                "flow between them. CAN, FlexRay and ETHERNET buses are read. An Ethernet bus has no " +
                "frame layer, so its signals are reached through the PDU instead, each of its channels is " +
                "a VLAN an ECU is or is not on, and what addresses a PDU there is the socket layer: " +
                "network endpoints, sockets over UDP or TCP, and the connections between them, read onto " +
                "one set of kinds whichever AUTOSAR revision's spelling the extract uses. Above that it " +
                "reads the SOME/IP service instances each socket offers or consumes, and below it the " +
                "switch coupling ports and the links between them. Several extracts of one vehicle are " +
                "read as " +
                "one source, so a frame in one of them can carry a signal defined in another and an ECU " +
                "on two buses is one element attached to both; a value carried on several buses is one " +
                "system signal with a per-bus signal each, which is what joins the buses to each other. " +
                "The job names the VEHICLE, which becomes part of every element's identity, so two " +
                "vehicles can be imported under one identity without merging: an AUTOSAR reference path " +
                "identifies an element within one system description, not across several.",
            Settings = new[]
            {
                new ProviderSetting
                {
                    Key = VehicleSetting,
                    Label = "Vehicle",
                    Kind = SettingKind.Text,
                    Required = true,
                    Help =
                        "The vehicle these extracts describe, such as a programme or platform name. It " +
                        "becomes part of every element's identity, so two vehicles imported under one " +
                        "identity stay separate elements even where their AUTOSAR paths are identical. " +
                        "They routinely are: the standard makes a reference path unique within one " +
                        "system description and not across several, and the standardised platform " +
                        "packages appear in essentially every extract. REQUIRED, with no default, " +
                        "because a default would silently merge the second vehicle into the first. Use " +
                        "the SAME name for every job describing one vehicle, so its buses join up; use " +
                        "a different name for a different vehicle. Letters, digits, dot, dash and " +
                        "underscore, up to 64 characters, and no slash.",
                },
                new ProviderSetting
                {
                    Key = FileSetting,
                    Label = "System extracts",
                    Kind = SettingKind.File,
                    Required = true,
                    Accept = ".arxml,.xml",
                    Multiple = true,
                    Help =
                        "One or more extracts of ONE system, such as chassis.arxml and body.arxml, sent " +
                        "with the job. They are read in the order given and resolved as one source, so a " +
                        "reference from one extract into another resolves exactly like a reference inside " +
                        "one file; where two of them declare the same AUTOSAR path, the earlier one wins. " +
                        "THE SET OF FILES IS THE SOURCE: this integration describes it completely, so a " +
                        "later run given fewer files withdraws - and then deletes - everything only the " +
                        "missing file described. They travel with the run and are dropped when it ends: " +
                        "nothing is mounted, nothing is stored, and this provider never sees a path.",
                },
            },
            EntityKinds = new[]
            {
                ArxmlKinds.Network,
                ArxmlKinds.Channel,
                ArxmlKinds.Ecu,
                ArxmlKinds.Frame,
                ArxmlKinds.Pdu,
                ArxmlKinds.Signal,
                ArxmlKinds.SystemSignal,
                ArxmlKinds.CompuMethod,
                ArxmlKinds.Endpoint,
                ArxmlKinds.Socket,
                ArxmlKinds.Connection,
                ArxmlKinds.Service,
                ArxmlKinds.Coupling,
            },
            ClaimTypes = new[] { VehiclePathClaimType },
            RelationTypes = new[]
            {
                ArxmlRelations.AttachedTo,
                ArxmlRelations.PartOf,
                ArxmlRelations.Sends,
                ArxmlRelations.DeliversTo,
                ArxmlRelations.Contains,
                ArxmlRelations.Carries,
                ArxmlRelations.Secures,
                ArxmlRelations.Implements,
                ArxmlRelations.ScaledBy,
                ArxmlRelations.BoundTo,
                ArxmlRelations.ServerPort,
                ArxmlRelations.ClientPort,
                ArxmlRelations.ConnectedTo,
            },
            CanObserveCompleteState = true,
            ReadOnly = true,

            // The provider's half of the embedding opt-in. The holes are chosen for ONE query: a
            // signal's name is the identifier an engineer already knows, its two descriptions are the
            // only prose in the file and arrive in either language, and its unit is what connects an
            // odometer whose description says "accumulated distance" to somebody searching for
            // kilometers. Punctuation and nothing else between them, per the rule the descriptor's own
            // field states: an ECU, a frame and a PDU fill none of the last three holes.
            EntitySummaryTemplate =
                "{kind} {arxml.name}, {arxml.descEn}, {arxml.descDe}, {arxml.unit}",
        };

        /// <summary>What this provider is, as data.</summary>
        public ProviderDescriptor Descriptor => DescriptorData;

        /// <summary>The document the last observation returned, for the conformance suite alone.</summary>
        public SnapshotDocument? LastSnapshot { get; private set; }

        /// <summary>
        ///   Reads every extract the <c>file</c> setting carries and describes the one communication
        ///   matrix they hold between them.
        /// </summary>
        /// <exception cref="ProviderConfigurationException">The setting is missing.</exception>
        /// <exception cref="ProviderSourceException">A file could not be read, is not an AUTOSAR
        /// r4.0 extract, or no file in the set carries a bus this version reads. Each fails the RUN and
        /// withdraws nothing: describing an unreadable file as an empty network would withdraw every
        /// element this identity ever claimed, and "I could not look" must never become "there is
        /// nothing there".</exception>
        public async Task<SnapshotDocument> ObserveAsync(ProviderContext context,
            CancellationToken cancellationToken)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            // The SETTING's value is every file's name, joined, which is what a message about the set as a
            // whole says; the per-file name a refusal needs comes from the list itself, and the reader
            // carries it into its own messages.
            var settingValue = context.Required(FileSetting);
            var fileNames = context.FileNames(FileSetting);

            // Validated before a single byte is read: the vehicle is part of every claim this run
            // composes, so a bad one is a refusal about the setting rather than tens of thousands of
            // invalid claim keys discovered at validation time.
            var vehicle = context.Required(VehicleSetting).Trim();
            if (!VehiclePattern.IsMatch(vehicle))
            {
                throw new ProviderSourceException(String.Format(CultureInfo.InvariantCulture,
                    "The setting '{0}' is not a usable vehicle name. It becomes part of every " +
                    "element's identity, so it may hold only letters, digits, dot, dash and " +
                    "underscore, must start with a letter or digit, and may be at most 64 " +
                    "characters. It must not contain a slash, because the slash is what separates " +
                    "the vehicle from the AUTOSAR path in an element's identity.", VehicleSetting));
            }

            var reader = new ArxmlReader();
            ArxmlNetwork network;
            try
            {
                // One file at a time, in JOB ORDER, into one reader. The order is what decides which extract
                // owns a path two of them declare, and reading them one by one rather than gathering them
                // first is what keeps a set of tens-of-megabytes extracts from being held all at once.
                //
                // As BYTES rather than text: the reader drives an XmlReader over them, so asking for the
                // text would decode a whole extract to UTF-16 for a parser that never wanted a string, and
                // would read a document declaring a non-UTF-8 encoding without a mark as mojibake.
                for (var index = 0; index < fileNames.Count; index++)
                {
                    using var bytes = await context
                        .RequireFileStreamAtAsync(FileSetting, index, cancellationToken)
                        .ConfigureAwait(false);
                    reader.Add(fileNames[index], bytes);
                }

                // Resolved ONCE, over the union: a frame in one extract carrying a signal defined in another
                // is the whole reason a job may carry several, and per-file resolution would drop that edge.
                network = reader.Complete();
            }
            catch (ArxmlFormatException failure)
            {
                // The set, then the reader's own sentence about the one file that failed. Both halves are
                // needed once a job carries several: the set says what was submitted, and only the reader
                // knows which of them an operator has to go and open.
                throw new ProviderSourceException(String.Format(CultureInfo.InvariantCulture,
                    "Setting '{0}' was given '{1}'. {2} The run fails rather than reporting an empty " +
                    "network, because a complete snapshot with nothing in it withdraws every element this " +
                    "identity claimed.", FileSetting, settingValue, failure.Message), failure);
            }

            var describesABus = false;
            foreach (var element in network.Elements)
            {
                if (element.Kind == ArxmlKinds.Network)
                {
                    describesABus = true;
                    break;
                }
            }

            if (!describesABus)
            {
                // A communication matrix with no bus in it has not been OBSERVED, it has failed to be
                // observed: the files are readable AUTOSAR but describe something else (a software
                // component package, a diagnostic extract, or a bus of a kind this version does not
                // read). Reporting it as an empty complete snapshot would delete the whole network a
                // previous run described.
                //
                // Judged over the SET and never per file: a body-domain extract with no bus in it is
                // perfectly ordinary beside a chassis extract that has one, and failing per file would
                // refuse exactly the jobs this provider now exists to accept.
                //
                // Narrowed rather than removed as protocols arrived. It still has to fail, for the reason
                // above; what changed is that "no cluster of the one kind we read" is no longer the same
                // statement as "no bus", so the message names what WAS found when the set turns out to
                // carry a bus of a kind this version skips - which is the difference between an operator
                // upgrading and an operator hunting for a corrupt file.
                var unread = DescribeUnread(network.UnreadClusters);
                throw new ProviderSourceException(String.Format(CultureInfo.InvariantCulture,
                    "Nothing in '{0}', the extract set named by setting '{1}', carries a bus this version " +
                    "reads, though every file in it read as AUTOSAR, so there is no communication matrix " +
                    "in the set to describe. This version reads CAN, FlexRay and Ethernet clusters.{2} The " +
                    "run fails " +
                    "rather than reporting an empty network, because a complete snapshot with nothing in " +
                    "it withdraws every element this identity claimed.",
                    settingValue, FileSetting, unread));
            }

            var snapshot = new SnapshotDocument
            {
                ProviderId = context.ProviderId,
                IntegrationInstanceId = context.InstanceId,
                Declares = SnapshotCompleteness.Complete,
            }.CapturedNow();

            var entityByPath = new Dictionary<String, EntityDto>(StringComparer.Ordinal);
            foreach (var element in network.Elements)
            {
                var entity = new EntityDto { Kind = element.Kind };
                entity.ClaimIfPresent(VehiclePathClaimType, VehicleKey(vehicle, element.Path));

                foreach (var property in element.Properties)
                {
                    entity.SetIfPresent(PropertyPrefix + property.Key, property.Value);
                }

                entityByPath[element.Path] = entity;
                snapshot.Entities.Add(entity);
            }

            foreach (var relation in network.Relations)
            {
                // The reader already dropped every relation whose ends it could not resolve, so a miss
                // here would be a reader defect rather than a file's problem. It is still checked,
                // because silently attaching an edge to the wrong entity is the one failure that a
                // report cannot show.
                if (entityByPath.TryGetValue(relation.FromPath, out var owner))
                {
                    owner.RelateIfPresent(relation.Type, VehiclePathClaimType,
                        VehicleKey(vehicle, relation.ToPath));
                }
            }

            foreach (var diagnostic in network.Diagnostics)
            {
                snapshot.Diagnostics.Add(new DiagnosticDto(CodeOf(diagnostic.Kind), diagnostic.Message,
                    diagnostic.Subject));
            }

            LastSnapshot = snapshot;
            return snapshot;
        }

        /// <summary>
        ///   Names the buses a set carried that this version does not read, for the refusal above. Empty
        ///   when there were none, so the sentence reads normally in the ordinary case: a set with no bus
        ///   at all is a different problem from a set full of a bus we skip, and one message serves both
        ///   only if it says which it is.
        /// </summary>
        private static String DescribeUnread(IReadOnlyList<UnreadCluster> unread)
        {
            if (unread.Count == 0)
            {
                return String.Empty;
            }

            var names = new List<String>(unread.Count);
            foreach (var cluster in unread)
            {
                names.Add(String.Format(CultureInfo.InvariantCulture, "{0} (in {1} file(s))",
                    cluster.Element, cluster.Files));
            }

            return String.Format(CultureInfo.InvariantCulture,
                " The set does carry {0}, which a later version may read.", String.Join(", ", names));
        }

        /// <summary>
        ///   The wire code of a reader diagnostic. The mapping lives here rather than in the reader so the
        ///   reader carries no dependency on the snapshot contract.
        ///
        ///   <para>An unmapped kind throws AT RUNTIME, in the middle of observing, rather than failing the
        ///   build: this is a switch over an enum, and C# does not require it to be exhaustive. The doc here
        ///   claimed a compile-time hole for a while and there was never one, so adding a kind means adding
        ///   an arm in the same change, and <c>EveryDiagnosticKindHasAWireCode</c> is what actually enforces
        ///   it.</para>
        /// </summary>
        private static String CodeOf(ArxmlDiagnosticKind kind)
        {
            switch (kind)
            {
                case ArxmlDiagnosticKind.UnresolvedReference:
                    return DiagnosticCodes.ArxmlUnresolvedReference;
                case ArxmlDiagnosticKind.DuplicatePath:
                    return DiagnosticCodes.ArxmlDuplicatePath;
                case ArxmlDiagnosticKind.UndecidablePortDirection:
                    return DiagnosticCodes.ArxmlUndecidablePortDirection;
                case ArxmlDiagnosticKind.RedeclaredPaths:
                    return DiagnosticCodes.ArxmlRedeclaredPaths;
                case ArxmlDiagnosticKind.RedeclaredCluster:
                    return DiagnosticCodes.ArxmlRedeclaredCluster;
                case ArxmlDiagnosticKind.UnreadCluster:
                    return DiagnosticCodes.ArxmlUnreadCluster;
                case ArxmlDiagnosticKind.SocketLayerUnrecognised:
                    return DiagnosticCodes.ArxmlSocketLayerUnrecognised;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind,
                        "Every reader diagnostic kind needs a wire code, or a report would carry one " +
                        "nobody can group by.");
            }
        }
    }
}
