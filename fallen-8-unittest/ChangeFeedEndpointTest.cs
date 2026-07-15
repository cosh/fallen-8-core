// MIT License
//
// ChangeFeedEndpointTest.cs
//
// Copyright (c) 2025 Henning Rauch
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
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pipeline tests for the change feed SSE endpoint (feature change-feed Phase 2), through
    /// the real ASP.NET pipeline: mutate-to-event round trip with SSE framing, the filter/since
    /// 400 grammar, auth (401), the disabled/limit 503s, keep-alive comments, kill-switch-off
    /// functionality, and value-free payloads.
    /// </summary>
    [TestClass]
    public class ChangeFeedEndpointTest
    {
        private const string ApiKey = "changefeed-test-key";

        private sealed class FeedFactory : WebApplicationFactory<Program>
        {
            private readonly IReadOnlyDictionary<string, string> _settings;

            public FeedFactory(IReadOnlyDictionary<string, string> settings = null)
            {
                _settings = settings ?? new Dictionary<string, string>();
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
                foreach (var kv in _settings)
                {
                    builder.UseSetting(kv.Key, kv.Value);
                }
            }
        }

        private static StringContent Json(string body)
        {
            return new StringContent(body, Encoding.UTF8, "application/json");
        }

        private static async Task CreateVertex(HttpClient client, string label = "person")
        {
            using var response = await client.PutAsync("/vertex?waitForCompletion=true",
                Json("{\"label\":\"" + label + "\",\"creationDate\":1}"));
            response.EnsureSuccessStatusCode();
        }

        /// <summary>
        ///   Opens the SSE stream and returns a line reader over it. The caller cancels via the
        ///   token to close the connection.
        /// </summary>
        private static async Task<(HttpResponseMessage Response, StreamReader Reader)> OpenStream(
            HttpClient client, string query, CancellationToken cancellation)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/changefeed" + query);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation), Encoding.UTF8);
            return (response, reader);
        }

        /// <summary>Reads SSE lines until one starting with "data: " arrives; returns the frame's
        /// id/event/data lines.</summary>
        private static async Task<(string Id, string Event, string Data)> ReadEventFrame(
            StreamReader reader, CancellationToken cancellation)
        {
            string id = null, eventName = null;
            while (true)
            {
                var lineTask = reader.ReadLineAsync(cancellation).AsTask();
                var line = await lineTask;
                if (line == null)
                {
                    Assert.Fail("the SSE stream ended before an event frame arrived");
                }

                if (line.StartsWith("id: ", StringComparison.Ordinal))
                {
                    id = line.Substring(4);
                }
                else if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    eventName = line.Substring(7);
                }
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    return (id, eventName, line.Substring(6));
                }
                // keep-alive comments (": keepalive") and blank separators are skipped
            }
        }

        [TestMethod]
        public async Task MutateThenStream_DeliversTheEvent_WithSseFraming()
        {
            using var factory = new FeedFactory();
            using var client = factory.CreateClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var (response, reader) = await OpenStream(client, "", cts.Token);
            using (response)
            {
                await CreateVertex(client, "person");

                var frame = await ReadEventFrame(reader, cts.Token);

                Assert.AreEqual("vertexCreated", frame.Event);
                StringAssert.Contains(frame.Data, "\"kind\":\"vertexCreated\"");
                StringAssert.Contains(frame.Data, "\"element\":\"vertex\"");
                StringAssert.Contains(frame.Data, "\"label\":\"person\"");
                StringAssert.Contains(frame.Data, "\"seq\":");

                // id: <epoch-guid>:<seq>
                var separator = frame.Id.LastIndexOf(':');
                Assert.IsTrue(separator > 0, "the id carries <epoch>:<seq>");
                Assert.IsTrue(Guid.TryParse(frame.Id.Substring(0, separator), out _));
                Assert.IsTrue(long.TryParse(frame.Id.Substring(separator + 1), out var seq) && seq >= 1);

                cts.Cancel();
            }
        }

        [TestMethod]
        public async Task Filters_AreServerSide_AndKeepAliveCommentsFlow()
        {
            using var factory = new FeedFactory(new Dictionary<string, string>
            {
                ["Fallen8:ChangeFeed:KeepAliveSeconds"] = "1"
            });
            using var client = factory.CreateClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var (response, reader) = await OpenStream(client, "?labels=person&kinds=vertexCreated", cts.Token);
            using (response)
            {
                await CreateVertex(client, "robot");  // filtered out server-side
                await CreateVertex(client, "person"); // passes

                string dataLine = null;
                while (dataLine == null)
                {
                    var line = await reader.ReadLineAsync(cts.Token);
                    Assert.IsNotNull(line, "stream ended unexpectedly");
                    if (line.StartsWith("data: ", StringComparison.Ordinal))
                    {
                        dataLine = line;
                    }
                }

                StringAssert.Contains(dataLine, "\"label\":\"person\"",
                    "only the matching event reaches the client");
                Assert.IsFalse(dataLine.Contains("robot"), "the filtered event never leaves the server");

                // With a 1s heartbeat, an idle stream produces a keep-alive comment promptly.
                var sawKeepAlive = false;
                while (!sawKeepAlive)
                {
                    var line = await reader.ReadLineAsync(cts.Token);
                    Assert.IsNotNull(line, "stream ended unexpectedly");
                    sawKeepAlive = line.StartsWith(":", StringComparison.Ordinal);
                }
                Assert.IsTrue(sawKeepAlive);

                cts.Cancel();
            }
        }

        [TestMethod]
        public async Task InvalidFilterOrSince_Return400ProblemJson()
        {
            using var factory = new FeedFactory();
            using var client = factory.CreateClient();

            using var badKind = await client.GetAsync("/changefeed?kinds=vertexMangled");
            Assert.AreEqual(HttpStatusCode.BadRequest, badKind.StatusCode);
            Assert.AreEqual("application/problem+json", badKind.Content.Headers.ContentType?.MediaType);

            using var badElement = await client.GetAsync("/changefeed?elements=hyperedge");
            Assert.AreEqual(HttpStatusCode.BadRequest, badElement.StatusCode);

            using var badSince = await client.GetAsync("/changefeed?since=not-a-position");
            Assert.AreEqual(HttpStatusCode.BadRequest, badSince.StatusCode);

            using var badEpoch = await client.GetAsync("/changefeed?since=not-a-guid:42");
            Assert.AreEqual(HttpStatusCode.BadRequest, badEpoch.StatusCode);
        }

        [TestMethod]
        public async Task Disabled_Returns503_AndTheRestOfTheApiIsUntouched()
        {
            using var factory = new FeedFactory(new Dictionary<string, string>
            {
                ["Fallen8:ChangeFeed:Enabled"] = "false"
            });
            using var client = factory.CreateClient();

            using var stream = await client.GetAsync("/changefeed");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, stream.StatusCode);

            // Everything else behaves exactly as today.
            await CreateVertex(client);
            using var status = await client.GetAsync("/status");
            Assert.AreEqual(HttpStatusCode.OK, status.StatusCode);
        }

        [TestMethod]
        public async Task SubscriberLimit_Returns503()
        {
            using var factory = new FeedFactory(new Dictionary<string, string>
            {
                ["Fallen8:ChangeFeed:MaxSubscribers"] = "1"
            });
            using var client = factory.CreateClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var (first, _) = await OpenStream(client, "", cts.Token);
            using (first)
            {
                using var second = await client.GetAsync("/changefeed");
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, second.StatusCode);
                cts.Cancel();
            }
        }

        [TestMethod]
        public async Task WithApiKey_AnonymousIs401_AuthenticatedStreams()
        {
            using var factory = new FeedFactory(new Dictionary<string, string>
            {
                ["Fallen8:Security:ApiKey"] = ApiKey
            });

            using (var anonymous = factory.CreateClient())
            using (var response = await anonymous.GetAsync("/changefeed"))
            {
                Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode,
                    "the endpoint sits behind the fallback policy like every other endpoint");
            }

            using var authenticated = factory.CreateClient();
            authenticated.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var (stream, reader) = await OpenStream(authenticated, "", cts.Token);
            using (stream)
            {
                await CreateVertex(authenticated);
                var frame = await ReadEventFrame(reader, cts.Token);
                Assert.AreEqual("vertexCreated", frame.Event);
                cts.Cancel();
            }
        }

        [TestMethod]
        public async Task WorksFully_WithTheDynamicCodeKillSwitchOff_AndPayloadsCarryNoValues()
        {
            // The default host already runs with EnableDynamicCodeExecution=false - the feed's
            // declarative filters never need it.
            using var factory = new FeedFactory();
            using var client = factory.CreateClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var (response, reader) = await OpenStream(client, "?kinds=propertySet", cts.Token);
            using (response)
            {
                await CreateVertex(client);
                using (var setProperty = await client.PutAsync("/graphelement/0/secret?waitForCompletion=true",
                    Json("{\"propertyValue\":\"the-secret-value\",\"fullQualifiedTypeName\":\"System.String\"}")))
                {
                    setProperty.EnsureSuccessStatusCode();
                }

                var frame = await ReadEventFrame(reader, cts.Token);
                Assert.AreEqual("propertySet", frame.Event);
                StringAssert.Contains(frame.Data, "\"key\":\"secret\"");
                Assert.IsFalse(frame.Data.Contains("the-secret-value"),
                    "event payloads carry the property KEY, never the value");

                cts.Cancel();
            }
        }

        [TestMethod]
        public async Task Since_AcrossReconnect_ReplaysTheMissedEvents()
        {
            using var factory = new FeedFactory();
            using var client = factory.CreateClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            // First connection: observe the first event and remember its id.
            string lastId;
            var (first, firstReader) = await OpenStream(client, "", cts.Token);
            using (first)
            {
                await CreateVertex(client);
                var frame = await ReadEventFrame(firstReader, cts.Token);
                lastId = frame.Id;
            }

            // Disconnected: one event is missed.
            await CreateVertex(client, "missed-while-away");

            // Reconnect with since=<last id>: the missed event replays first.
            var (second, secondReader) = await OpenStream(client, "?since=" + Uri.EscapeDataString(lastId), cts.Token);
            using (second)
            {
                var replayed = await ReadEventFrame(secondReader, cts.Token);
                Assert.AreEqual("vertexCreated", replayed.Event);
                StringAssert.Contains(replayed.Data, "\"label\":\"missed-while-away\"");
                cts.Cancel();
            }
        }
    }
}
