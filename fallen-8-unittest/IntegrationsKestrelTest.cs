// MIT License
//
// IntegrationsKestrelTest.cs
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
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Integrations.Hosting;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The integrations runtime on a REAL SOCKET (feature integration-file-transport).
    ///
    ///   <para>Everything else about the job route is tested over <c>TestServer</c>, which is faster and
    ///   enough for a contract about statuses and bodies. What TestServer cannot express is a request whose
    ///   BODY is still being sent, because its transport is an in-memory pipe with no separate send and
    ///   receive: a refusal issued mid-upload is delivered there whether or not it would be over TCP. That
    ///   is precisely the case this feature depends on - a multi-gigabyte form is refused at part three, and
    ///   whether the caller learns why or just sees a broken pipe is a real question about a real socket.</para>
    ///
    ///   <para>So these run against Kestrel on 127.0.0.1 with an ephemeral port. There are deliberately only
    ///   as many as need the socket; a test that does not need one belongs in the fast file.</para>
    /// </summary>
    [TestClass]
    public class IntegrationsKestrelTest
    {
        private const String Boundary = "----f8-kestrel-boundary";
        private const String CsvProviderId = "csv-device-list";
        private const String ArxmlProviderId = "autosar-arxml";

        /// <summary>
        ///   A REFUSAL MID-UPLOAD REACHES THE CALLER. The form's third part breaks the ordinal rule, so the
        ///   runtime answers 400 with several megabytes of body still to come, and the assertion is that the
        ///   client reads that 400 rather than a transport failure.
        ///
        ///   <para>If this ever fails, the fallback is a stated bounded drain allowance rather than a hope:
        ///   without it, every refusal on a large form would reach the person uploading as a dropped
        ///   connection, and the one thing they need - which part was wrong - would be in a log they cannot
        ///   see.</para>
        /// </summary>
        [TestMethod]
        public async Task ARefusalWhileTheBodyIsStillUploadingStillReachesTheCaller()
        {
            await using var host = await RuntimeOnASocket.StartAsync();
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            // 8 MiB after the offending part, sent in slow chunks, so the refusal is certainly issued while
            // the client is still writing rather than after it has finished.
            var trailing = new Byte[8 * 1024 * 1024];
            var body = new SlowStream(BodyWithBadThirdPart(trailing), 64 * 1024);
            using var content = new StreamContent(body);
            content.Headers.ContentType =
                MediaTypeHeaderValue.Parse("multipart/form-data; boundary=" + Boundary);

            HttpResponseMessage answer;
            try
            {
                answer = await client.PostAsync(host.JobUrl, content);
            }
            catch (HttpRequestException failure)
            {
                Assert.Fail(
                    "the refusal did not reach the caller: the upload failed at the transport instead, so " +
                    "somebody uploading a large form learns only that the connection broke and the one " +
                    "thing they need - which part was wrong - is in a log they cannot see. " +
                    failure.Message);
                throw;
            }

            using (answer)
            {
                var text = await answer.Content.ReadAsStringAsync();
                Assert.AreEqual(400, (Int32)answer.StatusCode,
                    "a form whose parts are misnumbered was not refused: " + text);
                StringAssert.Contains(text, "numbered from 0", text);
            }

            // MEASURED, and worth stating because it is not what one would guess: the refusal arrives, and
            // the whole remaining body is transmitted anyway. Kestrel DRAINS an unread request body when the
            // response completes so the connection stays usable, and the client therefore finishes sending
            // before it reads the answer.
            //
            // The consequence is a product fact, not a curiosity: refusing server-side does not spare the
            // upload. Somebody submitting a bad form of several gigabytes waits out the whole thing to be
            // told which part was wrong. That is exactly why the form checks what it can BEFORE sending, and
            // why those client-side checks are not merely a nicety.
            Assert.AreEqual(body.Length, body.Delivered,
                "the client stopped sending early, which means Kestrel no longer drains an unread body. " +
                "That is an IMPROVEMENT, not a regression - but the finding recorded for this feature, and " +
                "the reasoning that rests on it, both need updating: " +
                body.Delivered.ToString(CultureInfo.InvariantCulture) + " of " +
                body.Length.ToString(CultureInfo.InvariantCulture) + " bytes went out");
        }

        /// <summary>
        ///   The ceiling refusal reaches the caller the same way, which is the case an operator actually
        ///   meets: a file bigger than the runtime accepts, refused while the rest of it is still in flight.
        /// </summary>
        [TestMethod]
        public async Task AFileOverTheCeilingIsRefusedWhileItIsStillArriving()
        {
            await using var host = await RuntimeOnASocket.StartAsync(maxFileBytes: 64 * 1024);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            var document = Encoding.UTF8.GetBytes(
                "{\"providerId\":\"" + CsvProviderId + "\",\"integrationInstanceId\":\"too-big\"}");
            var oversized = new Byte[8 * 1024 * 1024];
            var body = Form(
                (ValuePart("job"), document),
                (FilePart("files[file]", "devices.csv"), oversized));

            using var content = new StreamContent(new SlowStream(body, 64 * 1024));
            content.Headers.ContentType =
                MediaTypeHeaderValue.Parse("multipart/form-data; boundary=" + Boundary);

            using var answer = await client.PostAsync(host.JobUrl, content);
            var text = await answer.Content.ReadAsStringAsync();

            Assert.AreEqual(400, (Int32)answer.StatusCode, text);
            StringAssert.Contains(text, "more than 65536 bytes", text);
            StringAssert.Contains(text, "stopped reading at the ceiling", text);
        }

        /// <summary>
        ///   A legal multipart job over a real socket, so the accepted path is proven on the same transport
        ///   as the refusals. Its run fails at the graph target, which is the only phase this host has no
        ///   way to complete.
        /// </summary>
        [TestMethod]
        public async Task ALegalMultipartJobIsAcceptedOverARealSocket()
        {
            await using var host = await RuntimeOnASocket.StartAsync();
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            using var form = new MultipartFormDataContent(Boundary);
            form.Add(new StringContent(
                "{\"providerId\":\"" + CsvProviderId + "\",\"integrationInstanceId\":\"socket-office\"}",
                Encoding.UTF8, "application/json"), "job");
            form.Add(new ByteArrayContent(
                    Encoding.UTF8.GetBytes("mac,name\n44:D2:44:AA:BB:CC,Reception AP\n")),
                "files[file]", "devices.csv");

            using var answer = await client.PostAsync(host.JobUrl + "?wait=true", form);
            var text = await answer.Content.ReadAsStringAsync();

            Assert.AreEqual(200, (Int32)answer.StatusCode,
                "a legal multipart job was refused over a real socket: " + text);
            StringAssert.Contains(text, "\"errorKind\":\"graph\"",
                "the run failed somewhere other than at its graph target, which is the only phase this " +
                "host cannot complete: " + text);
        }

        #region the body

        /// <summary>A form whose third part breaks the ordinal rule, with a lot of body after it.</summary>
        private static Byte[] BodyWithBadThirdPart(Byte[] trailing)
        {
            var document = Encoding.UTF8.GetBytes(
                "{\"providerId\":\"" + ArxmlProviderId + "\",\"integrationInstanceId\":\"mid-body\"}");

            return Form(
                (ValuePart("job"), document),
                (FilePart("files[file][0]", "a.arxml"), Encoding.UTF8.GetBytes("<A/>")),
                // 7, where 1 was due. Everything after this part is body the runtime never reads.
                (FilePart("files[file][7]", "b.arxml"), trailing));
        }

        private static String ValuePart(String name)
        {
            return "form-data; name=\"" + name + "\"";
        }

        private static String FilePart(String name, String fileName)
        {
            return "form-data; name=\"" + name + "\"; filename=\"" + fileName + "\"";
        }

        private static Byte[] Form(params (String Disposition, Byte[] Content)[] parts)
        {
            var crlf = new String(new[] { (Char)13, (Char)10 });
            var buffer = new MemoryStream();
            foreach (var part in parts)
            {
                var header = Encoding.UTF8.GetBytes(
                    "--" + Boundary + crlf + "Content-Disposition: " + part.Disposition + crlf + crlf);
                buffer.Write(header, 0, header.Length);
                buffer.Write(part.Content, 0, part.Content.Length);
                var terminator = Encoding.UTF8.GetBytes(crlf);
                buffer.Write(terminator, 0, terminator.Length);
            }

            var closing = Encoding.UTF8.GetBytes("--" + Boundary + "--" + crlf);
            buffer.Write(closing, 0, closing.Length);
            return buffer.ToArray();
        }

        /// <summary>
        ///   Hands out the body in small pieces so the request is demonstrably still being SENT when the
        ///   answer arrives. Without this the client may have written everything before the server got round
        ///   to refusing, and the test would be about a completed upload.
        /// </summary>
        private sealed class SlowStream : Stream
        {
            private readonly Byte[] _body;
            private readonly Int32 _chunk;
            private Int32 _position;

            public SlowStream(Byte[] body, Int32 chunk)
            {
                _body = body;
                _chunk = chunk;
            }

            public override Boolean CanRead => true;

            public override Boolean CanSeek => false;

            public override Boolean CanWrite => false;

            public override Int64 Length => _body.Length;

            public override Int64 Position
            {
                get => _position;
                set => throw new NotSupportedException();
            }

            /// <summary>How much was actually handed to the client, so a test can prove it stopped early.</summary>
            public Int64 Delivered => _position;

            public override async ValueTask<Int32> ReadAsync(Memory<Byte> buffer,
                CancellationToken cancellationToken = default)
            {
                if (_position >= _body.Length)
                {
                    return 0;
                }

                // A small pause per chunk, which is what keeps the send in progress. 8 MiB at 64 KiB a
                // millisecond is about an eighth of a second of sending, and the refusal is decided in the
                // first few kilobytes.
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);

                var count = Math.Min(Math.Min(_chunk, buffer.Length), _body.Length - _position);
                _body.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            public override Int32 Read(Byte[] buffer, Int32 offset, Int32 count)
            {
                return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
            }

            public override void Flush()
            {
            }

            public override Int64 Seek(Int64 offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(Int64 value)
            {
                throw new NotSupportedException();
            }

            public override void Write(Byte[] buffer, Int32 offset, Int32 count)
            {
                throw new NotSupportedException();
            }
        }

        #endregion

        #region the host

        /// <summary>
        ///   The runtime, built the way its own entry point builds it, but listening on an ephemeral
        ///   loopback port. The graph target is a name that does not resolve, so a run gets as far as it can
        ///   and then fails somewhere this test can recognise.
        /// </summary>
        private sealed class RuntimeOnASocket : IAsyncDisposable
        {
            private readonly WebApplication _app;

            private RuntimeOnASocket(WebApplication app, String jobUrl)
            {
                _app = app;
                JobUrl = jobUrl;
            }

            public String JobUrl { get; }

            public static async Task<RuntimeOnASocket> StartAsync(Int64 maxFileBytes = 0)
            {
                var builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                builder.WebHost.UseUrls("http://127.0.0.1:0");

                var settings = new Dictionary<String, String>
                {
                    ["Fallen8Target:BaseUrl"] = "http://graph.does-not-resolve.invalid:19999/",
                };
                if (maxFileBytes > 0)
                {
                    settings["Integrations:MaxFileBytes"] =
                        maxFileBytes.ToString(CultureInfo.InvariantCulture);
                }

                builder.Configuration.AddInMemoryCollection(settings);
                IntegrationsHost.AddFallen8Integrations(builder.Services, builder.Configuration);

                var app = builder.Build();
                IntegrationEndpoints.Map(app);
                await app.StartAsync().ConfigureAwait(false);

                var address = app.Services.GetRequiredService<IServer>().Features
                    .Get<IServerAddressesFeature>()!.Addresses.First();
                return new RuntimeOnASocket(app, address.TrimEnd('/') + "/integration/job");
            }

            public async ValueTask DisposeAsync()
            {
                await _app.StopAsync().ConfigureAwait(false);
                await _app.DisposeAsync().ConfigureAwait(false);
            }
        }

        #endregion
    }
}
