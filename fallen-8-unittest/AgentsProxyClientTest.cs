// MIT License
//
// AgentsProxyClientTest.cs
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
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Agents;
using NoSQL.GraphDB.App.Configuration;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The SHIPPED <see cref="AgentsClient" /> (feature agent-host): the apiApp's proxy client for
    ///   the agent host, exercised directly rather than substituted.
    ///
    ///   <para>
    ///     <b>Why this file exists.</b> Every proxy test in <c>AgentEndpointTest</c> replaces
    ///     <c>IAgentsClient</c> with a fake, which is right for testing the controller and useless
    ///     for testing the client. So the shipped client ran in no test at all: not its per-call
    ///     budget, not its unreachable arm, not its caller-cancellation arm, and not the streaming
    ///     arm's headers deadline or its mid-stream failure. Those are precisely the paths a
    ///     misbehaving sidecar exercises in production.
    ///   </para>
    ///   <para>
    ///     The seam used is the one the class already offers for this: an optional
    ///     <see cref="HttpMessageHandler" />, the same seam the other two sidecar clients take.
    ///   </para>
    /// </summary>
    [TestClass]
    public class AgentsProxyClientTest
    {
        #region the buffered arm

        [TestMethod]
        public async Task TheHostsStatusBodyAndContentTypeAllComeBackUntouched()
        {
            // The proxy decides nothing from a body, so all three have to survive the hop: a 409 the
            // host chose must not become this client's idea of a failure.
            using var client = Client(Answer(HttpStatusCode.Conflict,
                "{\"detail\":\"the host said so\"}", "application/problem+json"));

            var response = await client.ForwardAsync(HttpMethod.Post, "agent", "{\"task\":\"x\"}",
                CancellationToken.None);

            Assert.AreEqual(409, response.Status);
            Assert.AreEqual("{\"detail\":\"the host said so\"}", response.Body);
            StringAssert.Contains(response.ContentType, "application/problem+json");
        }

        [TestMethod]
        public async Task TheRequestGoesToTheHostsOwnPathWithTheBodyAsSent()
        {
            var handler = Answer(HttpStatusCode.OK, "{}");
            using var client = Client(handler);

            await client.ForwardAsync(HttpMethod.Post, "agent", "{\"task\":\"count\"}",
                CancellationToken.None);

            Assert.AreEqual(1, handler.Requests.Count);
            Assert.AreEqual("/agent", handler.Requests[0].Path);
            Assert.AreEqual("{\"task\":\"count\"}", handler.Requests[0].Body);
            Assert.AreEqual("application/json", handler.Requests[0].ContentType);
        }

        [TestMethod]
        public async Task AGetCarriesNoBodyRatherThanAnEmptyOne()
        {
            var handler = Answer(HttpStatusCode.OK, "[]");
            using var client = Client(handler);

            await client.ForwardAsync(HttpMethod.Get, "agent", null, CancellationToken.None);

            Assert.IsNull(handler.Requests[0].Body,
                "a listing sent a body, which a host is entitled to refuse");
        }

        [TestMethod]
        public async Task AnUnconfiguredEndpointIsRefusedBeforeAnythingIsContacted()
        {
            var handler = Answer(HttpStatusCode.OK, "{}");
            using var client = Client(handler, endpoint: String.Empty);

            var failure = await Assert.ThrowsExceptionAsync<AgentsUnavailableException>(
                () => client.ForwardAsync(HttpMethod.Get, "agent", null, CancellationToken.None));

            StringAssert.Contains(failure.Message, "Fallen8:Agents:Endpoint");
            Assert.AreEqual(0, handler.Requests.Count,
                "an unconfigured proxy still opened a connection");
            Assert.IsFalse(client.Configured);
        }

        [TestMethod]
        public async Task AnUnreachableHostBecomesTheOneStatusThisProxyInvents()
        {
            using var client = Client(Throwing(new HttpRequestException("connection refused")));

            var failure = await Assert.ThrowsExceptionAsync<AgentsUnavailableException>(
                () => client.ForwardAsync(HttpMethod.Get, "agent", null, CancellationToken.None));

            StringAssert.Contains(failure.Message, "did not answer");
            StringAssert.Contains(failure.Message, "connection refused",
                "the transport's own reason is what an operator needs");
        }

        [TestMethod]
        public async Task ThePerCallBudgetAppliesEvenThoughHttpClientTimeoutIsDisarmed()
        {
            // HttpClient.Timeout is disarmed so the streaming arm can stay open for hours, which
            // means the SMALL arm has to apply its own budget or it inherits no deadline at all.
            // That is the trade the constructor makes, and this is the half of it that could
            // silently go missing.
            using var client = Client(Stalling(), timeoutSeconds: 1);

            // The caller's own bound is far longer than the budget under test and exists only so a
            // regression ENDS: without the per-call budget there is no deadline at all, and an
            // unbounded wait would wedge the suite rather than failing it. When it fires, the
            // exception type is wrong and the assertion below says so.
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var started = System.Diagnostics.Stopwatch.StartNew();
            var failure = await Assert.ThrowsExceptionAsync<AgentsUnavailableException>(
                () => client.ForwardAsync(HttpMethod.Get, "agent", null, bound.Token));
            started.Stop();

            StringAssert.Contains(failure.Message, "did not answer");
            Assert.IsFalse(bound.IsCancellationRequested,
                "the small arm inherited no deadline; the test's own bound had to end it");
            Assert.IsTrue(started.Elapsed < TimeSpan.FromSeconds(15),
                "the small arm took " + started.Elapsed + " against a one second budget");
        }

        [TestMethod]
        public async Task ACallerWhoGoesAwayIsNotReportedAsAnUnavailableHost()
        {
            // A caller closing a tab is not a broken sidecar. Reporting it as one would put a false
            // 503 in the log every time somebody navigated away.
            using var client = Client(Stalling(), timeoutSeconds: 300);
            using var caller = new CancellationTokenSource();

            var pending = client.ForwardAsync(HttpMethod.Get, "agent", null, caller.Token);
            caller.CancelAfter(TimeSpan.FromMilliseconds(50));

            try
            {
                await pending;
                Assert.Fail("the caller's cancellation was swallowed");
            }
            catch (AgentsUnavailableException)
            {
                Assert.Fail("a caller who went away was reported as an unavailable host");
            }
            catch (OperationCanceledException)
            {
            }
        }

        #endregion

        #region the streaming arm

        [TestMethod]
        public async Task TheStatusAndContentTypeArriveBeforeAnyBodyFlows()
        {
            // The whole reason onHeaders exists: a refusal has to reach the caller AS a refusal. If
            // the status arrived after the body had started, a 400 naming a bad filter would be sent
            // as a 200 with an error somewhere inside the stream.
            using var client = Client(Answer(HttpStatusCode.BadRequest,
                "{\"detail\":\"Unknown feed event kind\"}", "application/problem+json"));

            var order = new List<String>();
            var destination = new RecordingStream(order);

            await client.StreamAsync("agent/feed", (status, contentType) =>
            {
                order.Add("headers:" + status + ":" + contentType);
                return Task.CompletedTask;
            }, destination, CancellationToken.None);

            Assert.IsTrue(order.Count >= 2, "nothing was written: " + String.Join(" | ", order));
            StringAssert.StartsWith(order[0], "headers:400:");
            StringAssert.Contains(order[0], "application/problem+json");
            Assert.IsTrue(order[1].StartsWith("write", StringComparison.Ordinal),
                "the body flowed before the headers were reported");

            // And a refusal's body is copied through, not swallowed by a forward that assumed
            // success.
            StringAssert.Contains(destination.Text, "Unknown feed event kind");
        }

        [TestMethod]
        public async Task EachChunkIsFlushedAsItArrivesRatherThanBufferedToTheEnd()
        {
            // A stream forwarded in one lump passes every assertion about content and still makes a
            // live feed useless. The flush count is the only observable difference.
            using var client = Client(Streaming(
                "id: 1\nevent: agentSpawned\ndata: {}\n\n",
                "id: 2\nevent: toolCalled\ndata: {}\n\n",
                "id: 3\nevent: agentCompleted\ndata: {}\n\n"));

            var order = new List<String>();
            var destination = new RecordingStream(order);

            await client.StreamAsync("agent/feed", (_, __) => Task.CompletedTask, destination,
                CancellationToken.None);

            Assert.IsTrue(destination.Flushes >= 3,
                "the forward flushed " + destination.Flushes + " times for three chunks, so a "
                + "subscriber is served in bursts");
            StringAssert.Contains(destination.Text, "event: agentSpawned");
            StringAssert.Contains(destination.Text, "event: agentCompleted");
        }

        [TestMethod]
        public async Task AHostThatNeverSendsHeadersIsGivenUpOnRatherThanHeldForever()
        {
            // The defect this arm had: no deadline on the headers phase. A host that accepted the
            // connection and never answered held the caller's request open indefinitely with no 503
            // ever reported, because the body deliberately has no deadline and nothing covered the
            // phase before it.
            using var client = Client(Stalling(), timeoutSeconds: 1);

            // Bounded for the same reason as the buffered arm's budget test: with no deadline on the
            // headers phase this waits forever, and a wedged suite hides the defect it is meant to
            // report.
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var started = System.Diagnostics.Stopwatch.StartNew();
            var failure = await Assert.ThrowsExceptionAsync<AgentsUnavailableException>(
                () => client.StreamAsync("agent/feed", (_, __) => Task.CompletedTask,
                    new MemoryStream(), bound.Token));
            started.Stop();

            StringAssert.Contains(failure.Message, "did not answer");
            Assert.IsFalse(bound.IsCancellationRequested,
                "the headers phase had no deadline; the test's own bound had to end it");
            Assert.IsTrue(started.Elapsed < TimeSpan.FromSeconds(15),
                "the headers phase took " + started.Elapsed + " against a one second budget");
        }

        [TestMethod]
        public async Task AHostThatDiesMidStreamIsReportedAsUnavailableRatherThanEscaping()
        {
            // IOException is what a connection dropping mid-copy throws, and it is the likeliest
            // failure of a stream that stays open for hours. Unnamed by the catches it escaped this
            // method as itself and reached the controller after the response had started, where
            // there is no status left to send.
            using var client = Client(FailingMidStream());

            var failure = await Assert.ThrowsExceptionAsync<AgentsUnavailableException>(
                () => client.StreamAsync("agent/feed", (_, __) => Task.CompletedTask,
                    new MemoryStream(), CancellationToken.None));

            StringAssert.Contains(failure.Message, "did not answer");
        }

        [TestMethod]
        public async Task ASubscriberWhoDisconnectsPropagatesRatherThanLookingLikeADeadHost()
        {
            using var client = Client(Streaming(": keep-alive\n\n"), timeoutSeconds: 300);
            using var caller = new CancellationTokenSource();

            // Cancelled while the body is being copied, which is how a feed normally ends.
            var destination = new BlockingStream(caller);

            try
            {
                await client.StreamAsync("agent/feed", (_, __) => Task.CompletedTask, destination,
                    caller.Token);
                Assert.Fail("the subscriber's disconnect was swallowed");
            }
            catch (AgentsUnavailableException)
            {
                Assert.Fail("a subscriber who disconnected was reported as an unavailable host");
            }
            catch (OperationCanceledException)
            {
            }
        }

        [TestMethod]
        public async Task AnUnconfiguredEndpointRefusesTheStreamToo()
        {
            using var client = Client(Answer(HttpStatusCode.OK, ""), endpoint: String.Empty);

            var failure = await Assert.ThrowsExceptionAsync<AgentsUnavailableException>(
                () => client.StreamAsync("agent/feed", (_, __) => Task.CompletedTask,
                    new MemoryStream(), CancellationToken.None));

            StringAssert.Contains(failure.Message, "Fallen8:Agents:Endpoint");
        }

        #endregion

        #region the health probe, which the disarmed timeout could have broken

        [TestMethod]
        public async Task TheHealthProbeStillWorksWithHttpClientTimeoutDisarmed()
        {
            // The constructor disarms HttpClient.Timeout, and the base's cached /health probe uses
            // the same client. It survives because it links its OWN five-second budget, which is a
            // claim worth pinning rather than trusting.
            var handler = Answer(HttpStatusCode.OK, "{\"status\":\"ok\"}");
            using var client = Client(handler);

            Assert.IsTrue(await client.IsReachableAsync(CancellationToken.None));
            Assert.AreEqual("/health", handler.Requests[0].Path);
        }

        [TestMethod]
        public async Task AStalledHealthProbeGivesUpOnItsOwnBudgetRatherThanHanging()
        {
            using var client = Client(Stalling(), timeoutSeconds: 600);

            var started = System.Diagnostics.Stopwatch.StartNew();
            Assert.IsFalse(await client.IsReachableAsync(CancellationToken.None));
            started.Stop();

            Assert.IsTrue(started.Elapsed < TimeSpan.FromSeconds(20),
                "the probe inherited the disarmed timeout and waited " + started.Elapsed);
        }

        [TestMethod]
        public async Task AnUnconfiguredProxyIsNotReachableAndContactsNothing()
        {
            var handler = Answer(HttpStatusCode.OK, "{}");
            using var client = Client(handler, endpoint: String.Empty);

            Assert.IsFalse(await client.IsReachableAsync(CancellationToken.None));
            Assert.AreEqual(0, handler.Requests.Count);
        }

        #endregion

        #region harness

        private static AgentsClient Client(HttpMessageHandler handler,
            String endpoint = "http://agent-host.invalid", Int32 timeoutSeconds = 30)
        {
            var options = new Fallen8AgentsOptions
            {
                Enabled = true,
                Endpoint = endpoint,
                TimeoutSeconds = timeoutSeconds,
            };

            return new AgentsClient(Options.Create(options),
                TestLoggerFactory.Create().CreateLogger<AgentsClient>(), handler);
        }

        private static RecordingHandler Answer(HttpStatusCode status, String body,
            String contentType = "application/json")
        {
            return new RecordingHandler(status, new[] { body }, contentType, null, false);
        }

        private static RecordingHandler Streaming(params String[] chunks)
        {
            return new RecordingHandler(HttpStatusCode.OK, chunks, "text/event-stream", null, false);
        }

        /// <summary>A host that accepts the request and never answers.</summary>
        private static RecordingHandler Stalling()
        {
            return new RecordingHandler(HttpStatusCode.OK, Array.Empty<String>(), "application/json",
                null, false, stall: true);
        }

        private static RecordingHandler Throwing(Exception failure)
        {
            return new RecordingHandler(HttpStatusCode.OK, Array.Empty<String>(), "application/json",
                failure, false);
        }

        /// <summary>A host whose body throws part way through, as a dropped connection does.</summary>
        private static RecordingHandler FailingMidStream()
        {
            return new RecordingHandler(HttpStatusCode.OK, new[] { "id: 1\n" }, "text/event-stream",
                null, true);
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            private readonly String[] _chunks;
            private readonly String _contentType;
            private readonly Exception _failure;
            private readonly Boolean _failMidStream;
            private readonly Boolean _stall;

            public RecordingHandler(HttpStatusCode status, String[] chunks, String contentType,
                Exception failure, Boolean failMidStream, Boolean stall = false)
            {
                _status = status;
                _chunks = chunks;
                _contentType = contentType;
                _failure = failure;
                _failMidStream = failMidStream;
                _stall = stall;
            }

            public List<(String Path, String Body, String ContentType)> Requests { get; }
                = new List<(String, String, String)>();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Requests.Add((
                    request.RequestUri!.AbsolutePath,
                    request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                    request.Content?.Headers.ContentType?.MediaType));

                if (_failure != null)
                {
                    throw _failure;
                }

                if (_stall)
                {
                    // Never answers, and only the caller's token ends the wait, which is what a
                    // wedged sidecar looks like.
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                var content = _failMidStream
                    ? (HttpContent)new StreamContent(new HalfBrokenStream(_chunks.Length == 0
                        ? Array.Empty<Byte>()
                        : Encoding.UTF8.GetBytes(_chunks[0])))
                    : new StreamContent(new ChunkedStream(_chunks));

                content.Headers.Remove("Content-Type");
                content.Headers.TryAddWithoutValidation("Content-Type", _contentType);

                return new HttpResponseMessage(_status) { Content = content };
            }
        }

        /// <summary>Hands out one chunk per read, so a forward that buffers is distinguishable from
        /// one that copies as it goes.</summary>
        private sealed class ChunkedStream : Stream
        {
            private readonly Queue<Byte[]> _chunks;

            public ChunkedStream(IEnumerable<String> chunks)
            {
                _chunks = new Queue<Byte[]>();
                foreach (var chunk in chunks)
                {
                    _chunks.Enqueue(Encoding.UTF8.GetBytes(chunk));
                }
            }

            public override Boolean CanRead => true;

            public override Boolean CanSeek => false;

            public override Boolean CanWrite => false;

            public override Int64 Length => throw new NotSupportedException();

            public override Int64 Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override Int32 Read(Byte[] buffer, Int32 offset, Int32 count)
            {
                if (_chunks.Count == 0)
                {
                    return 0;
                }

                var chunk = _chunks.Dequeue();
                var taken = Math.Min(count, chunk.Length);
                Array.Copy(chunk, 0, buffer, offset, taken);
                return taken;
            }

            public override void Flush()
            {
            }

            public override Int64 Seek(Int64 offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(Int64 value) => throw new NotSupportedException();

            public override void Write(Byte[] buffer, Int32 offset, Int32 count)
                => throw new NotSupportedException();
        }

        /// <summary>Delivers one chunk and then fails the way a dropped connection does.</summary>
        private sealed class HalfBrokenStream : Stream
        {
            private readonly Byte[] _first;
            private Boolean _delivered;

            public HalfBrokenStream(Byte[] first)
            {
                _first = first;
            }

            public override Boolean CanRead => true;

            public override Boolean CanSeek => false;

            public override Boolean CanWrite => false;

            public override Int64 Length => throw new NotSupportedException();

            public override Int64 Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override Int32 Read(Byte[] buffer, Int32 offset, Int32 count)
            {
                if (_delivered || _first.Length == 0)
                {
                    throw new IOException("the connection was closed by the remote host");
                }

                _delivered = true;
                var taken = Math.Min(count, _first.Length);
                Array.Copy(_first, 0, buffer, offset, taken);
                return taken;
            }

            public override void Flush()
            {
            }

            public override Int64 Seek(Int64 offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(Int64 value) => throw new NotSupportedException();

            public override void Write(Byte[] buffer, Int32 offset, Int32 count)
                => throw new NotSupportedException();
        }

        /// <summary>Records the order of headers and writes, and counts flushes.</summary>
        private sealed class RecordingStream : Stream
        {
            private readonly List<String> _order;
            private readonly MemoryStream _written = new MemoryStream();

            public RecordingStream(List<String> order)
            {
                _order = order;
            }

            public Int32 Flushes
            {
                get; private set;
            }

            public String Text => Encoding.UTF8.GetString(_written.ToArray());

            public override Boolean CanRead => false;

            public override Boolean CanSeek => false;

            public override Boolean CanWrite => true;

            public override Int64 Length => _written.Length;

            public override Int64 Position
            {
                get => _written.Position;
                set => throw new NotSupportedException();
            }

            public override void Write(Byte[] buffer, Int32 offset, Int32 count)
            {
                _order.Add("write:" + count);
                _written.Write(buffer, offset, count);
            }

            public override Task WriteAsync(Byte[] buffer, Int32 offset, Int32 count,
                CancellationToken cancellationToken)
            {
                Write(buffer, offset, count);
                return Task.CompletedTask;
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<Byte> buffer,
                CancellationToken cancellationToken = default)
            {
                _order.Add("write:" + buffer.Length);
                _written.Write(buffer.Span);
                return ValueTask.CompletedTask;
            }

            public override void Flush()
            {
                Flushes++;
            }

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                Flushes++;
                return Task.CompletedTask;
            }

            public override Int32 Read(Byte[] buffer, Int32 offset, Int32 count)
                => throw new NotSupportedException();

            public override Int64 Seek(Int64 offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(Int64 value) => throw new NotSupportedException();
        }

        /// <summary>Cancels the caller's token on the first write, so the copy is interrupted the way
        /// a disconnecting subscriber interrupts it.</summary>
        private sealed class BlockingStream : Stream
        {
            private readonly CancellationTokenSource _caller;

            public BlockingStream(CancellationTokenSource caller)
            {
                _caller = caller;
            }

            public override Boolean CanRead => false;

            public override Boolean CanSeek => false;

            public override Boolean CanWrite => true;

            public override Int64 Length => throw new NotSupportedException();

            public override Int64 Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Write(Byte[] buffer, Int32 offset, Int32 count)
            {
                _caller.Cancel();
                throw new OperationCanceledException(_caller.Token);
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<Byte> buffer,
                CancellationToken cancellationToken = default)
            {
                _caller.Cancel();
                throw new OperationCanceledException(_caller.Token);
            }

            public override void Flush()
            {
            }

            public override Int32 Read(Byte[] buffer, Int32 offset, Int32 count)
                => throw new NotSupportedException();

            public override Int64 Seek(Int64 offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(Int64 value) => throw new NotSupportedException();
        }

        #endregion
    }
}
