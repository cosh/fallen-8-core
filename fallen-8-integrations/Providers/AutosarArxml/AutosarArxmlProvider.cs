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
    /// </summary>
    public sealed class AutosarArxmlProvider : IIntegrationProvider, IObservableProvider
    {
        /// <summary>The stable provider id. It is assigned once and never reused.</summary>
        public const String ProviderId = "autosar-arxml";

        /// <summary>The setting naming the file, which is a NAME and never a path.</summary>
        public const String FileSetting = "file";

        /// <summary>The one identifier type this provider claims.</summary>
        public const String PathClaimType = "arxml-path";

        /// <summary>
        ///   The prefix every property this provider writes carries. It lives in ONE place: two
        ///   providers describing "the name" of something rarely mean the same thing, and an
        ///   unprefixed key means the value depends on which integration ran last.
        /// </summary>
        public const String PropertyPrefix = "arxml.";

        private static readonly ProviderDescriptor DescriptorData = new ProviderDescriptor
        {
            Id = ProviderId,
            DisplayName = "AUTOSAR system extract (ARXML)",
            Description =
                "Reads an AUTOSAR classic-platform system extract (ARXML, schema r4.0) the job carries " +
                "and describes the FlexRay communication matrix it holds: the network, its ECUs, frames, " +
                "PDUs, signals, system signals and scaling methods, with the send and receive flow " +
                "between them.",
            Settings = new[]
            {
                new ProviderSetting
                {
                    Key = FileSetting,
                    Label = "System extract",
                    Kind = SettingKind.File,
                    Required = true,
                    Accept = ".arxml,.xml",
                    Help =
                        "The extract itself, such as network.arxml, sent with the job. It travels with the " +
                        "run and is dropped when the run ends: nothing is mounted, nothing is stored, and " +
                        "this provider never sees a path.",
                },
            },
            EntityKinds = new[]
            {
                ArxmlKinds.Network,
                ArxmlKinds.Ecu,
                ArxmlKinds.Frame,
                ArxmlKinds.Pdu,
                ArxmlKinds.Signal,
                ArxmlKinds.SystemSignal,
                ArxmlKinds.CompuMethod,
            },
            ClaimTypes = new[] { PathClaimType },
            RelationTypes = new[]
            {
                ArxmlRelations.AttachedTo,
                ArxmlRelations.Sends,
                ArxmlRelations.DeliversTo,
                ArxmlRelations.Contains,
                ArxmlRelations.Carries,
                ArxmlRelations.Secures,
                ArxmlRelations.Implements,
                ArxmlRelations.ScaledBy,
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
        ///   Reads the extract the <c>file</c> setting names and describes the whole communication
        ///   matrix in it.
        /// </summary>
        /// <exception cref="ProviderConfigurationException">The setting is missing.</exception>
        /// <exception cref="ProviderSourceException">The file could not be read, is not an AUTOSAR
        /// r4.0 extract, or carries no FlexRay cluster. Each fails the RUN and withdraws nothing:
        /// describing an unreadable file as an empty network would withdraw every element this
        /// identity ever claimed, and "I could not look" must never become "there is nothing
        /// there".</exception>
        public async Task<SnapshotDocument> ObserveAsync(ProviderContext context,
            CancellationToken cancellationToken)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var fileName = context.Required(FileSetting);
            var text = await context.RequireFileTextAsync(FileSetting, cancellationToken)
                .ConfigureAwait(false);

            ArxmlNetwork network;
            try
            {
                network = ArxmlReader.Read(text);
            }
            catch (ArxmlFormatException failure)
            {
                throw new ProviderSourceException(String.Format(CultureInfo.InvariantCulture,
                    "The file '{0}', named by setting '{1}', is not an AUTOSAR system extract this " +
                    "runtime can read: {2} The run fails rather than reporting an empty network, because " +
                    "a complete snapshot with nothing in it withdraws every element this identity claimed.",
                    fileName, FileSetting, failure.Message), failure);
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
                // observed: the file is readable AUTOSAR but describes something else (a software
                // component package, a diagnostic extract, a CAN-only network this version does not
                // read). Reporting it as an empty complete snapshot would delete the whole network a
                // previous run described.
                throw new ProviderSourceException(String.Format(CultureInfo.InvariantCulture,
                    "The file '{0}', named by setting '{1}', is a readable AUTOSAR extract but carries no " +
                    "FlexRay cluster, so there is no communication matrix in it to describe. This version " +
                    "reads FlexRay clusters only. The run fails rather than reporting an empty network, " +
                    "because a complete snapshot with nothing in it withdraws every element this identity " +
                    "claimed.", fileName, FileSetting));
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
                entity.ClaimIfPresent(PathClaimType, element.Path);

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
                    owner.RelateIfPresent(relation.Type, PathClaimType, relation.ToPath);
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
        ///   The wire code of a reader diagnostic. The mapping lives here rather than in the reader so
        ///   the reader carries no dependency on the snapshot contract, and an unmapped kind is a
        ///   compile-time hole rather than a silent "unknown".
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
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind,
                        "Every reader diagnostic kind needs a wire code, or a report would carry one " +
                        "nobody can group by.");
            }
        }
    }
}
