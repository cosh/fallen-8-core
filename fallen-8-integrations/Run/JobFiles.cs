// MIT License
//
// JobFiles.cs
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
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.Integrations.Contract;

namespace NoSQL.GraphDB.Integrations.Run
{
    /// <summary>
    ///   ONE FILE A JOB CARRIED, decoded. A file arrives with the job that needs it and is dropped when
    ///   the run ends, which is the credential rule applied to the other thing a run cannot fetch for
    ///   itself. The runtime opens nothing on disk, so this is the only way a file reaches a provider.
    /// </summary>
    public sealed class JobFilePayload
    {
        public JobFilePayload(String name, Byte[] content)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Content = content ?? throw new ArgumentNullException(nameof(content));
        }

        /// <summary>
        ///   The file's OWN name, as the caller sent it. It becomes the setting's effective value, so a
        ///   provider reads it with <c>context.Required(key)</c> and puts it in its messages and diagnostic
        ///   subjects ("devices.csv row 7"). It is a LABEL: nothing opens it, resolves it or joins it to a
        ///   path, which is why it needs no containment check - only the shape check that keeps a control
        ///   character out of a job report (<see cref="JobFiles.TryValidateName" />).
        /// </summary>
        public String Name { get; }

        /// <summary>
        ///   The file's BYTES. Bytes and not text, all the way from the browser: a vendor tool that writes
        ///   an AUTOSAR extract as UTF-16 is decoded correctly here by the same byte-order-mark detection
        ///   <c>File.ReadAllTextAsync</c> did when the file came off a mount, whereas a transport carrying
        ///   "the text" would have handed the provider mojibake with nothing on the report to explain it.
        /// </summary>
        public Byte[] Content { get; }
    }

    /// <summary>
    ///   Where a provider's FILE comes from: the files THIS RUN was given, by the setting key that asked
    ///   for one. There is no other source - the runtime opens nothing on disk - which is why a provider
    ///   needs no file system to be tested and the conformance suite can exercise the whole path offline.
    /// </summary>
    public interface IJobFiles
    {
        /// <summary>
        ///   Reads the file supplied for that setting, as text. For a setting given SEVERAL files this is
        ///   the first of them, which is what every single-file provider means and has always meant.
        /// </summary>
        Task<String> ReadAsync(String settingKey, CancellationToken cancellationToken);

        /// <summary>Whether a file was supplied for that setting at all, without reading it: null when one
        /// was, otherwise why not.</summary>
        Boolean TryResolve(String settingKey, out String? failure);

        /// <summary>
        ///   The names of every file supplied for that setting, in the order the job listed them; empty when
        ///   none was. Names rather than content, so a provider can loop over them and read one at a time.
        /// </summary>
        IReadOnlyList<String> NamesOf(String settingKey);

        /// <summary>
        ///   Reads the file at that position, as text.
        ///
        ///   <para>Positional rather than "give me all the texts", and the reason is size. This exists for
        ///   extracts of tens of megabytes each, and a call returning every text at once would hold all of
        ///   them decoded - UTF-16 in memory, on top of the bytes the job already holds - for the whole
        ///   parse. One at a time keeps the peak at the bytes plus ONE decoded file.</para>
        /// </summary>
        Task<String> ReadAtAsync(String settingKey, Int32 index, CancellationToken cancellationToken);
    }

    /// <summary>
    ///   The files one run holds, for exactly as long as that run lasts.
    ///
    ///   <para>Run scoped and disposed in the same <c>finally</c> as the credential lease, so a provider
    ///   that squirrelled the context away fails loudly instead of quietly reading caller data belonging
    ///   to a run that is over. What it deliberately does NOT copy from
    ///   <see cref="Credentials.CredentialLease" /> is the redaction hold: file content is the graph data
    ///   this run exists to write, so holding it as a secret would both scan every log line against a
    ///   multi-megabyte needle and redact the very text the run is supposed to store.</para>
    /// </summary>
    public sealed class JobFiles : IJobFiles, IDisposable
    {
        /// <summary>The longest a file's own name may be. It is display text on a report, not a path.</summary>
        internal const Int32 MaxNameLength = 260;

        private static readonly String[] NoNames = Array.Empty<String>();

        private readonly Dictionary<String, JobFileSet> _files;
        private readonly List<String> _requested = new List<String>();
        private Int32 _reads;
        private Boolean _ended;

        public JobFiles(IReadOnlyDictionary<String, JobFileSet>? filesBySettingKey)
        {
            _files = new Dictionary<String, JobFileSet>(StringComparer.OrdinalIgnoreCase);
            if (filesBySettingKey != null)
            {
                foreach (var pair in filesBySettingKey)
                {
                    _files[pair.Key] = pair.Value;
                }
            }
        }

        /// <summary>
        ///   Every setting key a run ASKED about, in order, whether or not a file came back. Asking is
        ///   what says which settings a provider believes it reads, so this is what the conformance
        ///   suite's declaration check judges - and a probe that found nothing is still an ask.
        /// </summary>
        public IReadOnlyList<String> Requested => _requested;

        /// <summary>
        ///   How many files were actually READ. Deliberately not the same as <see cref="Requested" />:
        ///   the offline check needs "this run got its data from a seam the suite provided", and a probe
        ///   for an optional file that nobody supplied provided nothing.
        /// </summary>
        public Int32 Reads => _reads;

        /// <summary>Whether the run has ended, after which every read refuses.</summary>
        public Boolean Ended => _ended;

        /// <inheritdoc />
        public Boolean TryResolve(String settingKey, out String? failure)
        {
            ThrowIfEnded();
            _requested.Add(settingKey ?? String.Empty);

            if (settingKey != null && _files.ContainsKey(settingKey))
            {
                failure = null;
                return true;
            }

            failure = String.Format("No file was supplied for setting '{0}'.", settingKey);
            return false;
        }

        /// <inheritdoc />
        public Task<String> ReadAsync(String settingKey, CancellationToken cancellationToken)
        {
            return ReadAtAsync(settingKey, 0, cancellationToken);
        }

        /// <inheritdoc />
        public IReadOnlyList<String> NamesOf(String settingKey)
        {
            ThrowIfEnded();
            _requested.Add(settingKey ?? String.Empty);

            if (settingKey == null || !_files.TryGetValue(settingKey, out var set))
            {
                return NoNames;
            }

            var names = new String[set.Files.Count];
            for (var i = 0; i < names.Length; i++)
            {
                names[i] = set.Files[i].Name;
            }

            return names;
        }

        /// <inheritdoc />
        public Task<String> ReadAtAsync(String settingKey, Int32 index, CancellationToken cancellationToken)
        {
            if (!TryResolve(settingKey, out var failure))
            {
                // CONFIGURATION and not source: the job is what is wrong, and both shipped providers
                // deliberately let a configuration failure through untouched rather than restating it as a
                // source failure, because "the file is unreadable" and "no file was sent" send an operator
                // to two different places.
                throw new ProviderConfigurationException(failure!);
            }

            var set = _files[settingKey];
            if (index < 0 || index >= set.Files.Count)
            {
                // A provider defect rather than a caller's, so it is not a job failure: reading past the
                // list means the provider counted its files wrongly, and answering with the first file
                // instead would silently parse one extract twice.
                throw new ArgumentOutOfRangeException(nameof(index), index, String.Format(
                    "Setting '{0}' was given {1} file(s), so there is none at that position.", settingKey,
                    set.Files.Count));
            }

            cancellationToken.ThrowIfCancellationRequested();
            _reads++;
            return Task.FromResult(Decode(set.Files[index].Content));
        }

        /// <summary>Drops what this run held: the bytes stop being readable.</summary>
        public void Dispose()
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
            _files.Clear();
        }

        /// <summary>
        ///   Bytes to text, with byte-order-mark detection, so the result is what
        ///   <c>File.ReadAllTextAsync</c> produced for the same bytes off a mount and a provider sees no
        ///   difference. UTF-8 without a mark is the fallback, as it was there.
        /// </summary>
        internal static String Decode(Byte[] content)
        {
            using var stream = new MemoryStream(content, writable: false);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        /// <summary>
        ///   The shape rule for a file's own name. It is not a containment check - nothing resolves this
        ///   name, so there is no directory to contain it in - but the name reaches every log sink, the
        ///   job report and a provider's diagnostic subjects, so a control character or an unbounded
        ///   string in it is a mess in the one place an operator goes to read what happened.
        /// </summary>
        internal static Boolean TryValidateName(String? name, String settingKey, out String? failure)
        {
            if (String.IsNullOrWhiteSpace(name))
            {
                failure = String.Format(
                    "The file supplied for setting '{0}' has no name. The name is what every message about " +
                    "this run calls the file, so a run without one cannot be read afterwards.", settingKey);
                return false;
            }

            if (name!.Length > MaxNameLength)
            {
                failure = String.Format(
                    "The file name supplied for setting '{0}' is {1} characters; at most {2} are allowed.",
                    settingKey, name.Length, MaxNameLength);
                return false;
            }

            foreach (var character in name)
            {
                if (Char.IsControl(character))
                {
                    failure = String.Format(
                        "The file name supplied for setting '{0}' contains a control character. The name is " +
                        "written into log lines and onto the job report, where one is invisible.", settingKey);
                    return false;
                }
            }

            failure = null;
            return true;
        }

        private void ThrowIfEnded()
        {
            if (_ended)
            {
                throw new InvalidOperationException(
                    "The files for this run have been dropped: a file may not be read past the run it " +
                    "arrived with, because it belongs to whoever submitted that job and to nothing else.");
            }
        }
    }

    /// <summary>
    ///   Turns the files a job carried into the run's <see cref="IJobFiles" />. A SEAM and not a
    ///   convenience, for the same reason <c>IProviderHttpFactory</c> and <c>IGraphTargetFactory</c> are
    ///   seams: the conformance suite substitutes it to observe what a candidate read, and an offline
    ///   check that cannot see the file half is the check its own comment calls worthless.
    /// </summary>
    public interface IJobFilesFactory
    {
        /// <summary>
        ///   The per-file ceiling on DECODED bytes, checked while the job is normalised so an unusable
        ///   payload is refused before the run starts. It sits on the seam that provides files because
        ///   that is the one place that knows where a file comes from and therefore how big one may be.
        ///   Zero or less means no ceiling.
        /// </summary>
        Int64 MaxFileBytes { get; }

        /// <summary>
        ///   The ceiling on the DECODED TOTAL across every file of one job. A second bound and not a
        ///   restatement of the per-file one: a job carrying a whole vehicle's extracts is many legal files
        ///   whose sum this process still has to hold at once. Zero or less means no ceiling.
        /// </summary>
        Int64 MaxJobFileBytes { get; }

        /// <summary>The files for one run.</summary>
        JobFiles Create(IReadOnlyDictionary<String, JobFileSet>? filesBySettingKey);
    }

    /// <summary>The real one: the run's files are the job's files, and there is nowhere else to get one.</summary>
    public sealed class JobFilesFactory : IJobFilesFactory
    {
        private readonly IOptions<Configuration.IntegrationsOptions> _options;

        public JobFilesFactory(IOptions<Configuration.IntegrationsOptions> options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <inheritdoc />
        public Int64 MaxFileBytes => _options.Value.MaxFileBytes;

        /// <inheritdoc />
        public Int64 MaxJobFileBytes => _options.Value.MaxJobFileBytes;

        /// <inheritdoc />
        public JobFiles Create(IReadOnlyDictionary<String, JobFileSet>? filesBySettingKey)
        {
            return new JobFiles(filesBySettingKey);
        }
    }
}
