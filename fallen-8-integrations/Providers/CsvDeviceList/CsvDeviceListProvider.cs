// MIT License
//
// CsvDeviceListProvider.cs
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
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Integrations.Contract;
using NoSQL.GraphDB.Integrations.Identity;

namespace NoSQL.GraphDB.Integrations.Providers.CsvDeviceList
{
    /// <summary>
    ///   THE FLOOR OF THE PROVIDER CONTRACT: a device list read out of a delimited text file. It exists to
    ///   prove that no credential, no paging, no rate limiting, no topology, no relation type and no second
    ///   entity kind is mandatory, so it measures whether the contract is the right SHAPE rather than merely
    ///   a working one. It is also genuinely useful: a spreadsheet is the most common inventory in
    ///   existence, and the cheapest way to give a graph the names, owners and notes no controller knows.
    ///
    ///   <para>Constructed by the container with NO arguments and held as a singleton, so everything one run
    ///   needs arrives on the <see cref="ProviderContext"/>. Its only mutable state is
    ///   <see cref="LastSnapshot"/>, which nothing in a run reads, so two runs may observe two files at
    ///   once without either seeing the other.</para>
    ///
    ///   <para>The whole of what it decides is what the FILE said. Canonicalising a MAC, resolving it to an
    ///   element, withdrawing a claim and deleting anything are all on the runtime's side of the boundary,
    ///   which is why the file's own failure modes are the only ones written out here.</para>
    /// </summary>
    public sealed class CsvDeviceListProvider : IIntegrationProvider, IObservableProvider
    {
        /// <summary>The stable provider id. It is assigned once and never reused.</summary>
        public const String ProviderId = "csv-device-list";

        /// <summary>The setting naming the file, which is a NAME and never a path.</summary>
        public const String FileSetting = "file";

        /// <summary>The setting naming the column separator.</summary>
        public const String DelimiterSetting = "delimiter";

        /// <summary>The setting renaming the rows this provider produces.</summary>
        public const String LabelSetting = "label";

        /// <summary>The entity kind a run produces unless <see cref="LabelSetting"/> renames it.</summary>
        public const String DefaultEntityKind = "device";

        /// <summary>The separator a run uses unless <see cref="DelimiterSetting"/> names another.</summary>
        public const String DefaultDelimiter = ",";

        private const String MacClaimType = "mac";
        private const String HostnameClaimType = "hostname";

        private const String MacColumn = "mac";
        private const String NameColumn = "name";
        private const String NoteColumn = "note";
        private const String HostnameColumn = "hostname";

        private const String NameProperty = "csv.name";
        private const String NoteProperty = "csv.note";
        private const String HostnameProperty = "csv.hostname";

        private const String CommaWord = "comma";
        private const String SemicolonWord = "semicolon";
        private const String TabWord = "tab";
        private const String PipeWord = "pipe";

        private const Int32 NoColumn = -1;

        /// <summary>
        ///   The fold that decides whether two rows name the SAME MAC address, taken from the vocabulary
        ///   rather than re-derived here: the trap is that two rows resolve to one element, and what "one
        ///   element" means is exactly the vocabulary's canonical form, so a plain string comparison would
        ///   let <c>44:D2:44:AA:BB:CC</c> and <c>44d244aabbcc</c> through as two rows and the second would
        ///   silently overwrite the first. The claim itself still carries the value AS THE FILE WROTE IT;
        ///   nothing here canonicalises a claim.
        /// </summary>
        private static readonly Func<String, String> MacFold = ResolveMacFold();

        private static readonly ProviderDescriptor DescriptorData = new ProviderDescriptor
        {
            Id = ProviderId,
            DisplayName = "CSV device list",
            Description =
                "Reads a delimited text file of devices the job carries: one row per device, identified by " +
                "the MAC address in its 'mac' column, carrying the name, owner note and hostname a " +
                "controller does not know.",
            Settings = new[]
            {
                new ProviderSetting
                {
                    Key = FileSetting,
                    Label = "Device list",
                    Kind = SettingKind.File,
                    Required = true,
                    Accept = ".csv,.tsv,.txt",
                    Help =
                        "The file itself, such as devices.csv, sent with the job. It travels with the run " +
                        "and is dropped when the run ends: nothing is mounted, nothing is stored, and this " +
                        "provider never sees a path.",
                },
                new ProviderSetting
                {
                    Key = DelimiterSetting,
                    Label = "Delimiter",
                    Kind = SettingKind.Text,
                    Required = false,
                    Help =
                        "What separates the columns: one of the words tab, semicolon, pipe or comma, or a " +
                        "single literal character. The words exist because a literal tab does not survive a " +
                        "human editing a JSON field. Defaults to a comma.",
                    DefaultValue = DefaultDelimiter,
                },
                new ProviderSetting
                {
                    Key = LabelSetting,
                    Label = "Element label",
                    Kind = SettingKind.Text,
                    Required = false,
                    Help =
                        "What to call the rows this file describes, for a list that is not devices (say " +
                        "'printer'). It RENAMES what the run produces and never selects which rows are " +
                        "read, which is why a setting is allowed here: it cannot change what a complete " +
                        "snapshot covers. Defaults to device.",
                    DefaultValue = DefaultEntityKind,
                },
            },
            EntityKinds = new[] { DefaultEntityKind },
            ClaimTypes = new[] { MacClaimType, HostnameClaimType },
            RelationTypes = Array.Empty<String>(),
            CanObserveCompleteState = true,
            ReadOnly = true,

            // The PROVIDER'S half of the embedding opt-in, declarative so no code of this provider's sits on the
            // path that produces embedding text. A job asks for the other half, and both default off. A hole the
            // row cannot fill collapses, so a spreadsheet with only a name reads as "device Reception printer".
            EntitySummaryTemplate = "{kind} {csv.name}, {csv.hostname}, {csv.note}",
        };

        /// <summary>
        ///   What this provider is, as data. <see cref="ProviderDescriptor.EntityKinds"/> declares the
        ///   DEFAULT kind, because a descriptor is true before any run exists and the label a job passes is
        ///   not; that is honest here only because the setting renames rows rather than selecting them.
        /// </summary>
        public ProviderDescriptor Descriptor => DescriptorData;

        /// <summary>
        ///   The document the last observation returned, for the conformance suite alone: a provider that
        ///   does not expose one is recorded as unjudgeable AND failing, because a check that cannot fail is
        ///   not a check. Nothing in a run reads it, so two concurrent runs leave the later one's document
        ///   here and cost the suite nothing, which judges one run at a time.
        /// </summary>
        public SnapshotDocument? LastSnapshot { get; private set; }

        /// <summary>
        ///   Reads the file the <c>file</c> setting names and describes every row that carries a MAC
        ///   address. Completeness is always <see cref="SnapshotCompleteness.Complete"/>: a file read shows
        ///   the whole list every time, and no setting narrows what is read.
        /// </summary>
        /// <exception cref="ProviderConfigurationException">A setting is missing or unusable.</exception>
        /// <exception cref="ProviderSourceException">The file could not be read, has no header row, or has
        /// no <c>mac</c> column. Each fails the RUN and withdraws nothing, because reporting "the list is
        /// empty" would withdraw every device this identity ever claimed.</exception>
        public async Task<SnapshotDocument> ObserveAsync(ProviderContext context,
            CancellationToken cancellationToken)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var fileName = context.Required(FileSetting);
            var delimiter = ReadDelimiter(context);
            var label = context.Optional(LabelSetting, DefaultEntityKind)!.Trim();
            var text = await ReadAsync(context, fileName, cancellationToken).ConfigureAwait(false);

            if (!CsvTable.TryParse(text, delimiter, out var table, out var parseFailure))
            {
                throw new ProviderSourceException(String.Format(CultureInfo.InvariantCulture,
                    "The file '{0}', named by setting '{1}', could not be read as a table ({2}), so there is " +
                    "no column to read a MAC address out of. The run fails rather than reporting an empty " +
                    "list, because a complete snapshot with no devices withdraws every device this identity " +
                    "claimed.", fileName, FileSetting, parseFailure));
            }

            if (!table!.TryGetColumn(MacColumn, out var macColumn))
            {
                throw new ProviderSourceException(String.Format(CultureInfo.InvariantCulture,
                    "The file '{0}', named by setting '{1}', has no '{2}' column, so no row could ever be " +
                    "identified. The columns found were: {3}. They are named rather than counted because " +
                    "the usual cause is the wrong '{4}': a separator the file does not use leaves the whole " +
                    "header row as one column. The run fails rather than reporting an empty list, because a " +
                    "complete snapshot with no devices withdraws every device this identity claimed.",
                    fileName, FileSetting, MacColumn, Describe(table.Header), DelimiterSetting));
            }

            var nameColumn = ColumnOf(table, NameColumn);
            var noteColumn = ColumnOf(table, NoteColumn);
            var hostnameColumn = ColumnOf(table, HostnameColumn);

            var snapshot = new SnapshotDocument
            {
                ProviderId = context.ProviderId,
                IntegrationInstanceId = context.InstanceId,
                Declares = SnapshotCompleteness.Complete,
            }.CapturedNow();

            // Keyed by the fold, valued by the line the MAC was first seen on, so the diagnostic can name
            // the row that WAS used rather than only the one that was not.
            var firstSeen = new Dictionary<String, Int32>(StringComparer.Ordinal);

            foreach (var row in table.Rows)
            {
                var subject = String.Format(CultureInfo.InvariantCulture, "{0} row {1}", fileName,
                    row.LineNumber);

                if (row.UnterminatedQuote)
                {
                    // A newline inside a quoted field is UNSUPPORTED, and this is where that is said out
                    // loud: the row was read as the row it LOOKS like, which is the physical line, rather
                    // than joined with the next one and silently mis-parsing everything after it. It is a
                    // log line rather than a snapshot diagnostic only because no diagnostic code covers it
                    // (see the feature report); the row's own consequences are reported below under a code
                    // that is true.
                    context.Logger.LogWarning(
                        "A quoted field in {File} {Subject} does not close on the line, so it probably " +
                        "contains a newline. That is unsupported: the row was read as the row it looks " +
                        "like, and what came of it is reported on the job.", fileName, subject);
                }

                var mac = row.Cell(macColumn);
                if (mac == null)
                {
                    snapshot.Diagnostics.Add(new DiagnosticDto(DiagnosticCodes.RowWithoutMac,
                        "This row has no value in the '" + MacColumn + "' column, so nothing could ever " +
                        "resolve to it. It was skipped rather than failing the run: losing a whole run to " +
                        "one typo leaves every later row unobserved, and the run then withdraws every " +
                        "device this identity claimed." + (row.UnterminatedQuote
                            ? " Its quoting does not close on the line, so a quoted field probably contains " +
                              "a newline, which is unsupported and is the likely cause."
                            : String.Empty),
                        subject));
                    continue;
                }

                // A cell that folds to nothing (somebody typed "none") is not compared at all: every such
                // row folds to the same empty string, so the second would be reported as a duplicate of the
                // first. The row is still emitted, and the runtime's validator is what names the value it
                // cannot use, because judging an identifier value is not this side of the boundary's job.
                var fold = MacFold(mac);
                if (fold.Length > 0)
                {
                    if (firstSeen.TryGetValue(fold, out var firstLine))
                    {
                        snapshot.Diagnostics.Add(new DiagnosticDto(DiagnosticCodes.DuplicateMacInFile,
                            String.Format(CultureInfo.InvariantCulture,
                                "The MAC '{0}' is already on row {1}, so only that first row was used. Two " +
                                "rows carrying one MAC resolve to one element and would overwrite each other " +
                                "by file order, which makes the graph depend on the order somebody typed the " +
                                "file in.", mac, firstLine),
                            subject));
                        continue;
                    }

                    firstSeen.Add(fold, row.LineNumber);
                }

                var entity = new EntityDto { Kind = label };

                // The value goes out as the file wrote it: the runtime canonicalises it, and a provider that
                // canonicalised first would be the second home of a rule that only works if there is one.
                entity.Claims.Add(new IdentityClaimDto { Type = MacClaimType, Value = mac });

                var hostname = row.Cell(hostnameColumn);
                if (hostname != null)
                {
                    // Weak, and the vocabulary is what says so. No strength is declared here: a provider
                    // able to call its own weak identifier strong makes a hostname resolve, and the run then
                    // attaches this file's data to whichever element last held the name.
                    entity.Claims.Add(new IdentityClaimDto { Type = HostnameClaimType, Value = hostname });
                }

                Record(entity, NameProperty, row.Cell(nameColumn));
                Record(entity, NoteProperty, row.Cell(noteColumn));
                Record(entity, HostnameProperty, hostname);

                snapshot.Entities.Add(entity);
            }

            LastSnapshot = snapshot;
            return snapshot;
        }

        /// <summary>
        ///   Reads the file the setting names, turning anything that went wrong into a SOURCE failure that
        ///   names the setting. A configuration failure and a cancellation pass through untouched, because
        ///   both already name the right system and calling a bad file name a source failure would send an
        ///   operator to look at the file rather than at the job.
        /// </summary>
        private static async Task<String> ReadAsync(ProviderContext context, String fileName,
            CancellationToken cancellationToken)
        {
            try
            {
                return await context.ReadFileAsync(FileSetting, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException
                                            && failure is not ProviderConfigurationException
                                            && failure is not ProviderSourceException)
            {
                throw new ProviderSourceException(String.Format(CultureInfo.InvariantCulture,
                    "The file '{0}', named by setting '{1}', could not be read: {2}. The run fails and " +
                    "withdraws nothing: reporting an empty list would withdraw every device this identity " +
                    "claimed, because \"I could not look\" must never become \"there is nothing there\".",
                    fileName, FileSetting, failure.Message), failure);
            }
        }

        /// <summary>
        ///   Reads the delimiter: one of the four words, or a single literal character. The words exist
        ///   because a literal tab does not survive a human editing a JSON field, and they are the only way
        ///   to ask for one here at all, since a whitespace-only setting reads as absent.
        /// </summary>
        private static Char ReadDelimiter(ProviderContext context)
        {
            var word = context.Optional(DelimiterSetting, DefaultDelimiter)!.Trim();
            switch (word.ToLowerInvariant())
            {
                case TabWord:
                    return '\t';
                case SemicolonWord:
                    return ';';
                case PipeWord:
                    return '|';
                case CommaWord:
                    return ',';
            }

            // A quote is refused rather than accepted as a separator: it is the other half of the grammar
            // the reader implements, so a file quoted and separated by the same character has no reading.
            if (word.Length == 1 && word[0] != CsvTable.QuoteCharacter)
            {
                return word[0];
            }

            throw new ProviderConfigurationException(String.Format(CultureInfo.InvariantCulture,
                "Setting '{0}' is '{1}', which is neither one of the words {2}, {3}, {4}, {5} nor a single " +
                "literal character other than a quote.", DelimiterSetting, word, TabWord, SemicolonWord,
                PipeWord, CommaWord));
        }

        /// <summary>The index of an optional column, or <see cref="NoColumn"/> when the file has none.</summary>
        private static Int32 ColumnOf(CsvTable table, String name)
        {
            return table.TryGetColumn(name, out var index) ? index : NoColumn;
        }

        /// <summary>
        ///   Writes a property only when the file answered it. An ABSENT value is absent: writing an empty
        ///   string for something the source did not say makes the property exist and overwrites what
        ///   another integration knows about the same device.
        /// </summary>
        private static void Record(EntityDto entity, String key, String? value)
        {
            if (value != null)
            {
                entity.Properties[key] = value;
            }
        }

        /// <summary>
        ///   The header cells as a readable list for a refusal, each quoted so a blank column is visible.
        ///   Never empty: a non-blank header line always splits into at least one cell.
        /// </summary>
        private static String Describe(IReadOnlyList<String> header)
        {
            var quoted = new List<String>(header.Count);
            foreach (var name in header)
            {
                quoted.Add("'" + name + "'");
            }

            return String.Join(", ", quoted);
        }

        /// <summary>
        ///   Resolves <see cref="MacFold"/> from the shipped vocabulary. The fallback is unreachable in this
        ///   deployable, because the catalog refuses to start when a declared claim type is missing, and it
        ///   is a trim-and-lower fold rather than a throw so a vocabulary edit can never turn reading a
        ///   spreadsheet into a crash.
        /// </summary>
        private static Func<String, String> ResolveMacFold()
        {
            if (IdentifierVocabulary.Shipped.TryGet(MacClaimType, out var identifier) && identifier != null)
            {
                return identifier.Canonicalise;
            }

            return value => value.Trim().ToLowerInvariant();
        }
    }
}
