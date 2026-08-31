// MIT License
//
// JobRequestReader.cs
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
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using NoSQL.GraphDB.Integrations.Run;

namespace NoSQL.GraphDB.Integrations.Hosting
{
    /// <summary>
    ///   HOW A JOB ARRIVES, AND WHY THERE ARE TWO WAYS.
    ///
    ///   <para>A job is one document plus, for a file-taking integration, its files. The original transport
    ///   put both in one JSON body with each file base64 in a string, and that is still accepted: it is what
    ///   a script with <c>curl</c> and <c>base64 -w0</c> writes, it is the whole contract for the integrations
    ///   that take no file at all, and every caller written against it keeps working.</para>
    ///
    ///   <para>It does not scale to a real extract, and the reason is not the 33% base64 adds. A browser
    ///   composing that body has to hold the file's bytes, its base64 string and the serialised request at
    ///   once, and a JavaScript string is capped at 512 MiB, so the encoder itself died at about 384 MiB of
    ///   input while the runtime was configured to accept more. A vehicle's AUTOSAR extract is several
    ///   gigabytes. The ceiling was in the ENCODER, which no amount of configuration reaches.</para>
    ///
    ///   <para>So a job may also arrive as <c>multipart/form-data</c>: the document in a <c>job</c> part, and
    ///   each file as its own part carrying raw bytes. Nothing expands, nothing is held twice, and the sender
    ///   streams from the file handle. Both transports deserialise into the same
    ///   <see cref="IntegrationJob" /> and go through the same <c>TryNormalize</c>, so one job submitted
    ///   either way produces an identical run and an identical report.</para>
    ///
    ///   <para>NOTHING HERE TOUCHES DISK. <c>HttpRequest.ReadFormAsync</c> and <c>IFormFile</c> would have
    ///   been a fraction of this code and are BANNED in this project (a convention test enforces it), because
    ///   the form reader spools any part over 64 KiB to a temp file. The runtime's published contract is that
    ///   it mounts no directory and opens nothing on disk, and a transport that quietly wrote a caller's
    ///   extract into the container's temp directory would make that false.</para>
    /// </summary>
    public static class JobRequestReader
    {
        /// <summary>The part that carries the job document, which must be the FIRST part.</summary>
        public const String JobPartName = "job";

        /// <summary>
        ///   The most the <c>job</c> part itself may carry. Generous by three orders of magnitude for a
        ///   document of settings and a credential; it exists so a caller who puts a file's bytes IN the
        ///   envelope is refused by a bound rather than by an out-of-memory.
        /// </summary>
        public const Int32 MaxJobPartBytes = 1_048_576;

        /// <summary>
        ///   How much is read from a part at a time. Rented from the pool and returned, so a job of many
        ///   files does not leave one buffer per file for the collector to deal with.
        /// </summary>
        private const Int32 SegmentBytes = 1_048_576;

        /// <summary>The web defaults, which is what the minimal-API body binding this replaced used.</summary>
        private static readonly JsonSerializerOptions JobJson =
            new JsonSerializerOptions(JsonSerializerDefaults.Web);

        /// <summary>
        ///   Reads the job off the request, whichever transport it came on.
        /// </summary>
        /// <param name="request">The request, whose body is read at most once.</param>
        /// <param name="maxFileBytes">The per-file ceiling on bytes; zero or less means no ceiling. A part
        /// over it stops being read at one byte past, and the job is refused by normalisation with the one
        /// per-file message there has ever been.</param>
        /// <param name="maxJobFiles">The ceiling on how many file parts one job may carry; zero or less
        /// means no ceiling. Refused at the part that breaks it rather than after accumulating the rest.</param>
        /// <param name="cancellationToken">Aborts the read.</param>
        public static async Task<JobRequest> ReadAsync(HttpRequest request, Int64 maxFileBytes,
            Int32 maxJobFiles, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (request.HasJsonContentType())
            {
                return await ReadJsonAsync(request, cancellationToken).ConfigureAwait(false);
            }

            if (TryGetMultipartBoundary(request.ContentType, out var boundary))
            {
                return await ReadMultipartAsync(request, boundary!, maxFileBytes, maxJobFiles,
                    cancellationToken).ConfigureAwait(false);
            }

            return JobRequest.Unsupported(String.Format(CultureInfo.InvariantCulture,
                "A job arrives as 'application/json' or as 'multipart/form-data', and this request declared " +
                "'{0}'. The multipart form carries each file as raw bytes in its own part, which is the only " +
                "shape that scales to a large extract; the JSON form carries them base64 in the document.",
                String.IsNullOrWhiteSpace(request.ContentType) ? "nothing" : request.ContentType));
        }

        private static async Task<JobRequest> ReadJsonAsync(HttpRequest request,
            CancellationToken cancellationToken)
        {
            IntegrationJob? job;
            try
            {
                job = await JsonSerializer.DeserializeAsync<IntegrationJob>(request.Body, JobJson,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                // Named rather than swallowed into "a job definition is required": a malformed body and an
                // absent one are different mistakes, and this arm used to be the framework's own 400.
                return JobRequest.Rejected(String.Format(CultureInfo.InvariantCulture,
                    "The job document is not valid JSON: {0}", ex.Message));
            }

            return job == null
                ? JobRequest.Rejected("A job definition is required.")
                : JobRequest.Accepted(job);
        }

        /// <summary>
        ///   Reads the multipart form: the <c>job</c> part first, then a part per file.
        ///
        ///   <para>Every part name is CHECKED, and an unknown one is refused rather than ignored. Ignoring
        ///   is the dangerous choice here: a client that misspells a file part would submit a job the runtime
        ///   reads as a complete snapshot that does not mention whatever that file described, and a complete
        ///   snapshot withdraws what it does not mention.</para>
        /// </summary>
        private static async Task<JobRequest> ReadMultipartAsync(HttpRequest request, String boundary,
            Int64 maxFileBytes, Int32 maxJobFiles, CancellationToken cancellationToken)
        {
            var reader = new MultipartReader(boundary, request.Body);
            IntegrationJob? job = null;
            var groups = new Dictionary<String, FileSettingParts>(StringComparer.Ordinal);
            var order = new List<String>();
            var parts = 0;
            var fileParts = 0;

            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken).ConfigureAwait(false))
                   != null)
            {
                parts++;
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                {
                    return JobRequest.Rejected(String.Format(CultureInfo.InvariantCulture,
                        "Part {0} of the form declares no usable Content-Disposition, so there is no way to " +
                        "tell what it is. Every part of a job form is named.", parts));
                }

                var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? String.Empty;
                var fileName = FileNameOf(disposition);

                if (String.Equals(name, JobPartName, StringComparison.Ordinal))
                {
                    if (parts != 1)
                    {
                        return JobRequest.Rejected(String.Format(CultureInfo.InvariantCulture,
                            "The 'job' part is part {0} of the form and has to be the first. The files are " +
                            "read as they stream past, so the document that says which settings they belong " +
                            "to cannot arrive after them.", parts));
                    }

                    if (fileName != null)
                    {
                        // A Blob appended to a FormData declares a filename, and the envelope is a value.
                        // Refused rather than tolerated because the same mistake made with a file part is
                        // what the whole grammar is here to catch.
                        return JobRequest.Rejected(
                            "The 'job' part declares a filename, so it was sent as a file rather than as a " +
                            "value. The job document is a value part; only the file parts are files.");
                    }

                    var read = await ReadPartAsync(section.Body, MaxJobPartBytes, cancellationToken)
                        .ConfigureAwait(false);
                    if (read.Truncated)
                    {
                        return JobRequest.Rejected(String.Format(CultureInfo.InvariantCulture,
                            "The 'job' part is more than {0} bytes. It carries the job DOCUMENT; a file's " +
                            "bytes belong in a 'files[...]' part of their own, where nothing has to hold " +
                            "them all at once.", MaxJobPartBytes));
                    }

                    var parsed = ParseJob(read.Content);
                    if (parsed.Job == null)
                    {
                        return parsed;
                    }

                    job = parsed.Job;
                    continue;
                }

                if (job == null)
                {
                    return JobRequest.Rejected(String.Format(CultureInfo.InvariantCulture,
                        "The first part of the form is '{0}' rather than 'job'. The job document comes " +
                        "first, because a file part is only meaningful once the setting it belongs to is " +
                        "known.", name));
                }

                if (!TryParseFilePartName(name, out var settingKey, out var ordinal, out var nameFailure))
                {
                    return JobRequest.Rejected(nameFailure!);
                }

                if (fileName == null)
                {
                    return JobRequest.Rejected(String.Format(CultureInfo.InvariantCulture,
                        "The part '{0}' declares no filename. A file's own name is what every message about " +
                        "the run calls it, and the runtime has nothing else to call it: it opens nothing on " +
                        "disk, so there is no path to fall back to.", name));
                }

                if (maxJobFiles > 0 && fileParts >= maxJobFiles)
                {
                    // Refused AT the part rather than after reading it, which is the point of counting here
                    // as well as in normalisation: the byte ceilings cannot bound a thousand one-byte files,
                    // and reading them to find that out defeats the bound.
                    return JobRequest.Rejected(String.Format(CultureInfo.InvariantCulture,
                        "This job carries more than {0} files (Integrations:MaxJobFiles), counted across " +
                        "every file setting on it, and the rest of the form was not read. A file of one byte " +
                        "is legal, so a set can satisfy both byte ceilings and still ask this runtime for an " +
                        "unreasonable number of entries.", maxJobFiles));
                }

                if (!groups.TryGetValue(settingKey!, out var group))
                {
                    group = new FileSettingParts(ordinal.HasValue);
                    groups[settingKey!] = group;
                    order.Add(settingKey!);
                }

                if (!group.TryAccept(settingKey!, ordinal, out var orderFailure))
                {
                    return JobRequest.Rejected(orderFailure!);
                }

                var content = await ReadPartAsync(section.Body, maxFileBytes, cancellationToken)
                    .ConfigureAwait(false);

                group.Files.Add(new JobFile
                {
                    Name = fileName,
                    Content = content.Content,
                    Truncated = content.Truncated,
                });
                fileParts++;

                if (content.Truncated)
                {
                    // Stop reading the request entirely. The job is going to be refused by normalisation,
                    // which owns the per-file message, and reading the remaining parts of a multi-gigabyte
                    // body to reach a refusal already decided would be the cost the ceiling exists to avoid.
                    break;
                }
            }

            if (job == null)
            {
                return JobRequest.Rejected(
                    "The form carries no 'job' part. A job is a document plus its files, and the document " +
                    "is what says which integration to run and under which identity.");
            }

            if (job.Files != null && job.Files.Count > 0)
            {
                return JobRequest.Rejected(
                    "The 'job' part carries a 'files' map of its own. On a multipart form the files ARE the " +
                    "file parts; a second list in the document would be a second answer to which files this " +
                    "run reads.");
            }

            // A document that sent "files": null leaves the map null rather than empty, and the parts have
            // to land somewhere.
            job.Files ??= new Dictionary<String, JobFileGroup>(StringComparer.Ordinal);

            foreach (var key in order)
            {
                job.Files[key] = groups[key].ToGroup();
            }

            return JobRequest.Accepted(job);
        }

        private static JobRequest ParseJob(Byte[] document)
        {
            if (document.Length == 0)
            {
                return JobRequest.Rejected("The 'job' part is empty. A job definition is required.");
            }

            try
            {
                var job = JsonSerializer.Deserialize<IntegrationJob>(document, JobJson);
                return job == null
                    ? JobRequest.Rejected("A job definition is required.")
                    : JobRequest.Accepted(job);
            }
            catch (JsonException ex)
            {
                return JobRequest.Rejected(String.Format(CultureInfo.InvariantCulture,
                    "The 'job' part is not valid JSON: {0}", ex.Message));
            }
        }

        /// <summary>
        ///   <c>filename</c>, honouring <c>filename*</c> when the client sent one, which is how a name with
        ///   a non-ASCII character survives the header. Null when the part declared neither, which is what
        ///   distinguishes a file part from a value part.
        /// </summary>
        private static String? FileNameOf(ContentDispositionHeaderValue disposition)
        {
            var starred = HeaderUtilities.RemoveQuotes(disposition.FileNameStar).Value;
            if (!String.IsNullOrEmpty(starred))
            {
                return starred;
            }

            var plain = HeaderUtilities.RemoveQuotes(disposition.FileName).Value;
            return String.IsNullOrEmpty(plain) ? null : plain;
        }

        /// <summary>
        ///   <c>files[key]</c> or <c>files[key][n]</c>, and nothing else.
        ///
        ///   <para>The ordinal is EXPLICIT rather than implied by the order the parts arrive, because the
        ///   difference between one file and a list of one file is load-bearing: a setting the descriptor
        ///   does not declare <c>multiple</c> refuses the list form, and repeated same-named parts cannot
        ///   express a list of one. So the grammar has to be able to say it.</para>
        /// </summary>
        private static Boolean TryParseFilePartName(String name, out String? settingKey, out Int32? ordinal,
            out String? failure)
        {
            settingKey = null;
            ordinal = null;

            const String prefix = "files[";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
                !name.EndsWith("]", StringComparison.Ordinal))
            {
                failure = String.Format(CultureInfo.InvariantCulture,
                    "The part '{0}' is not a part name this route understands. A job form carries 'job' and " +
                    "then 'files[<settingKey>]' for one file, or 'files[<settingKey>][<n>]' for a setting " +
                    "given several. An unknown part is refused rather than ignored: a misspelled file part " +
                    "would submit a snapshot that does not mention what that file described, and a snapshot " +
                    "withdraws what it does not mention.", name);
                return false;
            }

            var rest = name.Substring(prefix.Length);
            var close = rest.IndexOf(']', StringComparison.Ordinal);
            settingKey = close < 0 ? String.Empty : rest.Substring(0, close);

            if (settingKey.Length == 0 || !IsSettingKey(settingKey))
            {
                failure = String.Format(CultureInfo.InvariantCulture,
                    "The part '{0}' names the setting key '{1}', which is not a shape a setting key takes: " +
                    "letters, digits, dot, dash and underscore. The key is what joins the file to the " +
                    "setting the provider declared, so the part name has to be readable as one.",
                    name, settingKey);
                settingKey = null;
                return false;
            }

            var tail = rest.Substring(close + 1);
            if (tail.Length == 0)
            {
                failure = null;
                return true;
            }

            if (tail.Length > 2 && tail[0] == '[' && tail[tail.Length - 1] == ']' &&
                Int32.TryParse(tail.Substring(1, tail.Length - 2), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var parsed))
            {
                ordinal = parsed;
                failure = null;
                return true;
            }

            failure = String.Format(CultureInfo.InvariantCulture,
                "The part '{0}' has '{1}' after the setting key, which is neither nothing nor a decimal " +
                "'[<n>]'. A setting given several files numbers them from 0.", name, tail);
            settingKey = null;
            return false;
        }

        private static Boolean IsSettingKey(String value)
        {
            foreach (var character in value)
            {
                var legal = (character >= 'A' && character <= 'Z') ||
                            (character >= 'a' && character <= 'z') ||
                            (character >= '0' && character <= '9') ||
                            character == '_' || character == '.' || character == '-';
                if (!legal)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        ///   Reads one part into ONE array, rented in segments so a 128 MiB file is not grown by doubling,
        ///   and stops one byte past the ceiling.
        ///
        ///   <para>One byte past is enough to know the ceiling was broken, and it is all that is kept: the
        ///   bytes of a file that is going to be refused are released and the caller is told "more than N".
        ///   A multipart part declares no length, so there is no size to report instead - and reading the
        ///   whole thing to produce one would be paying the cost the ceiling exists to avoid.</para>
        /// </summary>
        private static async Task<PartContent> ReadPartAsync(Stream body, Int64 ceiling,
            CancellationToken cancellationToken)
        {
            var limit = ceiling > 0 ? ceiling + 1 : Int64.MaxValue;
            var segments = new List<ArraySegment<Byte>>();
            var total = 0L;
            var eof = false;

            try
            {
                while (!eof && total < limit)
                {
                    var buffer = ArrayPool<Byte>.Shared.Rent(SegmentBytes);
                    var used = 0;
                    while (used < buffer.Length && total + used < limit)
                    {
                        var room = (Int32)Math.Min(buffer.Length - used, limit - total - used);
                        var read = await body.ReadAsync(buffer.AsMemory(used, room), cancellationToken)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            eof = true;
                            break;
                        }

                        used += read;
                    }

                    segments.Add(new ArraySegment<Byte>(buffer, 0, used));
                    total += used;
                }

                if (ceiling > 0 && total > ceiling)
                {
                    return new PartContent(Array.Empty<Byte>(), truncated: true);
                }

                // Allocated ONCE, at the size measured, rather than grown: a MemoryStream reaching 128 MiB
                // has doubled its way through a quarter of a gigabyte of copies to get there.
                var content = new Byte[total];
                var offset = 0;
                foreach (var segment in segments)
                {
                    Buffer.BlockCopy(segment.Array!, 0, content, offset, segment.Count);
                    offset += segment.Count;
                }

                return new PartContent(content, truncated: false);
            }
            finally
            {
                foreach (var segment in segments)
                {
                    ArrayPool<Byte>.Shared.Return(segment.Array!);
                }
            }
        }

        private static Boolean TryGetMultipartBoundary(String? contentType, out String? boundary)
        {
            boundary = null;
            if (String.IsNullOrWhiteSpace(contentType) ||
                !MediaTypeHeaderValue.TryParse(contentType, out var mediaType) ||
                !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var value = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
            if (String.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            boundary = value;
            return true;
        }

        private readonly struct PartContent
        {
            public PartContent(Byte[] content, Boolean truncated)
            {
                Content = content;
                Truncated = truncated;
            }

            public Byte[] Content { get; }

            public Boolean Truncated { get; }
        }

        /// <summary>
        ///   The parts seen so far for ONE file setting: which form they used, and how far the ordinals have
        ///   got. Kept per setting so the ordinal rules can be enforced as the parts stream past instead of
        ///   buffering the form to sort it.
        /// </summary>
        private sealed class FileSettingParts
        {
            private readonly Boolean _asList;
            private Int32 _next;

            public FileSettingParts(Boolean asList)
            {
                _asList = asList;
            }

            public List<JobFile> Files { get; } = new List<JobFile>();

            public Boolean TryAccept(String settingKey, Int32? ordinal, out String? failure)
            {
                if (ordinal.HasValue != _asList)
                {
                    failure = String.Format(CultureInfo.InvariantCulture,
                        "Setting '{0}' was given files in both forms, 'files[{0}]' and 'files[{0}][<n>]'. " +
                        "One file or a numbered list, not both: the two forms mean different things to a " +
                        "setting that takes exactly one file.", settingKey);
                    return false;
                }

                if (!_asList)
                {
                    if (Files.Count > 0)
                    {
                        failure = String.Format(CultureInfo.InvariantCulture,
                            "Setting '{0}' was given the part 'files[{0}]' more than once. A setting given " +
                            "several files numbers them: 'files[{0}][0]', 'files[{0}][1]'.", settingKey);
                        return false;
                    }

                    failure = null;
                    return true;
                }

                if (ordinal!.Value != _next)
                {
                    // Ascending and contiguous, checked as they arrive. Any other order would mean holding
                    // parts back to sort them, which is holding a whole extract in the reader.
                    failure = _next == 0
                        ? String.Format(CultureInfo.InvariantCulture,
                            "The first file part for setting '{0}' is numbered {1}. A setting's files are " +
                            "numbered from 0, in ascending order, with no gaps.", settingKey, ordinal.Value)
                        : String.Format(CultureInfo.InvariantCulture,
                            "The file parts for setting '{0}' jump from {1} to {2}. They are numbered from " +
                            "0, in ascending order, with no gaps: a gap would be a file this run silently " +
                            "did not read, and a run that read fewer files withdraws whatever only the " +
                            "missing ones described.", settingKey, _next - 1, ordinal.Value);
                    return false;
                }

                _next++;
                failure = null;
                return true;
            }

            public JobFileGroup ToGroup()
            {
                return _asList ? JobFileGroup.FromList(Files) : (JobFileGroup)Files[0];
            }
        }
    }

    /// <summary>
    ///   What reading the request produced: a job, or the refusal and the status to answer with.
    /// </summary>
    public sealed class JobRequest
    {
        private JobRequest(IntegrationJob? job, Int32 status, String? failure)
        {
            Job = job;
            Status = status;
            Failure = failure;
        }

        /// <summary>The job, or null when the request could not be read as one.</summary>
        public IntegrationJob? Job { get; }

        /// <summary>The status to answer with when <see cref="Job" /> is null; 0 otherwise.</summary>
        public Int32 Status { get; }

        /// <summary>Why the request is not a job, or null when it is.</summary>
        public String? Failure { get; }

        internal static JobRequest Accepted(IntegrationJob job)
        {
            return new JobRequest(job, 0, null);
        }

        internal static JobRequest Rejected(String failure)
        {
            return new JobRequest(null, StatusCodes.Status400BadRequest, failure);
        }

        internal static JobRequest Unsupported(String failure)
        {
            return new JobRequest(null, StatusCodes.Status415UnsupportedMediaType, failure);
        }
    }
}
