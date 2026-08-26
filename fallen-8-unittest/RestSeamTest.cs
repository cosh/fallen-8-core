// MIT License
//
// RestSeamTest.cs
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
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Integrations.Graph;
using NoSQL.GraphDB.Mcp.Bridge;
using NoSQL.GraphDB.Rest;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The REST-client seam shared by the two deployables that may reach a Fallen-8 only over its public
    ///   HTTP contract. It exists because the same seam used to be written twice: the same absent-body
    ///   convention, the same timeout-versus-cancellation classification, and no mechanism forcing a fix in
    ///   one copy into the other.
    ///
    ///   <para>What is asserted here is the CLASSIFICATION, plus the fact that the two consumers still name
    ///   it in their own user-facing vocabulary rather than a flattened shared one.</para>
    /// </summary>
    [TestClass]
    public class RestSeamTest
    {
        /// <summary>A no-answer outcome named in a vocabulary of this test's own, so an assertion can read
        /// the classification back rather than parse a consumer's message.</summary>
        private sealed class SeamNoAnswer : Exception
        {
            public SeamNoAnswer(RestSendFailure failure, Exception cause)
                : base(failure.ToString(), cause)
            {
                Failure = failure;
            }

            public RestSendFailure Failure { get; }
        }

        private sealed class SeamRefusal : Exception
        {
            public SeamRefusal(Int32 status)
                : base("refused with " + status.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                Status = status;
            }

            public Int32 Status { get; }
        }

        private static Exception NoAnswer(RestSendFailure failure, Exception cause)
        {
            return new SeamNoAnswer(failure, cause);
        }

        private static Task<Exception> RefusedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            return Task.FromResult<Exception>(new SeamRefusal((Int32)response.StatusCode));
        }

        private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            return new HttpClient(new McpTestSupport.LambdaHandler(responder))
            {
                BaseAddress = new Uri("http://localhost/"),
            };
        }

        private static Task<String> BodyAsync(HttpClient client)
        {
            return RestSeam.SendForBodyAsync(client, HttpMethod.Get, "status", null, NoAnswer, RefusedAsync,
                CancellationToken.None);
        }

        // --- the absent-body convention ------------------------------------------------------------

        [TestMethod]
        public async Task A204_IsAnAbsentBody_NotAFailure()
        {
            using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

            Assert.IsNull(await BodyAsync(client),
                "every getter on this contract answers a missing element with 204, so a caller that treated " +
                "it as a failure could not ask whether a thing exists");
        }

        [DataTestMethod]
        [DataRow("null")]
        [DataRow("  null\n")]
        [DataRow("")]
        [DataRow("   ")]
        public async Task A200WhoseBodySaysNothing_IsAnAbsentBody(String body)
        {
            using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });

            Assert.IsNull(await BodyAsync(client),
                "a literal null document is the same 'no such thing' a 204 is, and a caller must not be able " +
                "to tell the two apart: '" + body + "'");
        }

        [DataTestMethod]
        [DataRow("\"null\"")]
        [DataRow("nullable")]
        [DataRow("{\"vertexCount\":0}")]
        public async Task A200CarryingAnActualDocument_IsNeverAbsent(String body)
        {
            using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });

            Assert.AreEqual(body, await BodyAsync(client),
                "the absent-body rule matches a bare null document, not any document that contains the four " +
                "letters: '" + body + "'");
        }

        // --- no answer at all, and which of the two it was -----------------------------------------

        [TestMethod]
        public async Task AClientSideDeadline_IsClassifiedAsTimedOut()
        {
            // HttpClient reports its OWN deadline as a TaskCanceledException, which IS an
            // OperationCanceledException. Classifying it as a cancellation presents "the target was too slow"
            // one frame up as "the caller walked away", and those two license opposite statements about
            // whether the request may have been applied.
            using var client = Client(_ => throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout", new TimeoutException()));

            var failure = await Assert.ThrowsExceptionAsync<SeamNoAnswer>(() => BodyAsync(client));

            Assert.AreEqual(RestSendFailure.TimedOut, failure.Failure);
        }

        [TestMethod]
        public async Task ATransportFailure_IsClassifiedAsUnreachable()
        {
            using var client = Client(_ => throw new HttpRequestException("the connection was reset"));

            var failure = await Assert.ThrowsExceptionAsync<SeamNoAnswer>(() => BodyAsync(client));

            Assert.AreEqual(RestSendFailure.Unreachable, failure.Failure,
                "unreachable says nothing was applied, while a deadline leaves that open, so the two must not " +
                "collapse into one classification");
            Assert.IsInstanceOfType(failure.InnerException, typeof(HttpRequestException),
                "and the cause is handed on, because it carries the only detail there is");
        }

        [TestMethod]
        public async Task ACancellationTheCallerAskedFor_IsNeverThisSeamsFailure()
        {
            // The other side of the same coin: the TOKEN decides, never the exception type. A caller that
            // walked away must not be told the target misbehaved.
            using var walkedAway = new CancellationTokenSource();
            var named = false;
            using var client = Client(_ =>
            {
                walkedAway.Cancel();
                throw new TaskCanceledException("the caller walked away", null, walkedAway.Token);
            });

            try
            {
                await RestSeam.SendForBodyAsync(client, HttpMethod.Get, "status", null,
                    (failure, cause) =>
                    {
                        named = true;
                        return new SeamNoAnswer(failure, cause);
                    },
                    RefusedAsync, walkedAway.Token);
                Assert.Fail("the seam answered a caller who had already asked to stop");
            }
            catch (SeamNoAnswer)
            {
                Assert.Fail("a cancellation the caller requested became this seam's own failure");
            }
            catch (OperationCanceledException)
            {
                // The one right answer: the caller's cancellation belongs to the caller.
            }

            Assert.IsFalse(named, "the caller's vocabulary is never even consulted for its own cancellation");
        }

        // --- a status to interpret -----------------------------------------------------------------

        [TestMethod]
        public async Task ANonSuccessStatus_GoesToTheRefusalNaming_NotTheNoAnswerNaming()
        {
            using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("the name is invalid", Encoding.UTF8, "text/plain"),
            });

            var refusal = await Assert.ThrowsExceptionAsync<SeamRefusal>(() => BodyAsync(client));

            Assert.AreEqual(400, refusal.Status,
                "a status the target answered with is a different fact from no answer at all, and only the " +
                "caller knows how to read it");
        }

        // --- the request the seam builds -----------------------------------------------------------

        [TestMethod]
        public async Task ARequestBody_GoesOutAsJsonUnderTheSharedWireConvention()
        {
            String contentType = null;
            String sent = null;
            using var client = Client(request =>
            {
                contentType = request.Content.Headers.ContentType.MediaType;
                sent = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("true", Encoding.UTF8, "application/json"),
                };
            });

            using var response = await RestSeam.SendAsync(client, HttpMethod.Put, "index/claims",
                new { GraphElementId = 5, PropertyValue = "mac:44d244aabbcc" }, NoAnswer, CancellationToken.None);

            Assert.AreEqual("application/json", contentType);
            Assert.AreEqual("{\"graphElementId\":5,\"propertyValue\":\"mac:44d244aabbcc\"}", sent,
                "both consumers speak the same camelCase wire contract, so the serialization cannot be a " +
                "per-consumer choice");
            Assert.AreEqual("true", await response.Content.ReadAsStringAsync(),
                "SendAsync hands the response back readable, which is what the chunked embedding write needs " +
                "in order to read a status itself");
        }

        [TestMethod]
        public async Task TheSharedWireOptions_AreSealedOnceTheFirstRequestHasUsedThem()
        {
            using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
            await RestSeam.SendForBodyAsync(client, HttpMethod.Post, "index", new { Name = "claims" }, NoAnswer,
                RefusedAsync, CancellationToken.None);

            Assert.IsTrue(RestSeam.JsonOptions.IsReadOnly);
            Assert.ThrowsException<InvalidOperationException>(
                () => RestSeam.JsonOptions.PropertyNamingPolicy = null,
                "one consumer retuning the shared options would silently change the other's wire format");
        }

        // --- the vocabularies stay separate --------------------------------------------------------

        [TestMethod]
        public async Task TheBridgeNamesANoAnswerInItsOwnVocabulary()
        {
            var timedOut = await Assert.ThrowsExceptionAsync<BridgeError>(() => McpTestSupport
                .Bridge(new McpTestSupport.LambdaHandler(_ => throw new TaskCanceledException(
                    "The request was canceled due to the configured HttpClient.Timeout", new TimeoutException())))
                .GetStatusAsync("default", CancellationToken.None));

            Assert.AreEqual(504, timedOut.Status, "an agent reads a status, so a timeout is a gateway timeout");
            Assert.IsTrue(timedOut.Retryable, "and waiting longer may well work");

            var unreachable = await Assert.ThrowsExceptionAsync<BridgeError>(() => McpTestSupport
                .Bridge(new McpTestSupport.LambdaHandler(_ => throw new HttpRequestException("no route to host")))
                .GetStatusAsync("default", CancellationToken.None));

            Assert.AreEqual(503, unreachable.Status);
            StringAssert.Contains(unreachable.Detail, "no route to host",
                "the transport's own words are the only detail there is: " + unreachable.Detail);
        }

        [TestMethod]
        public async Task TheTwoConsumersNameTheSameClassificationDifferently()
        {
            // Deliberate, and both halves are user-facing: an agent reads a bridge status and a run report
            // names a graph failure. Collapsing them into one shared exception type would put one
            // deployable's words in the other's mouth.
            using var forTheTarget = Client(_ => throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout", new TimeoutException()));
            using var target = new Fallen8RestTarget(forTheTarget, "default");

            var graph = await Assert.ThrowsExceptionAsync<GraphTargetTimeoutException>(
                () => target.CreateVerticesAsync(
                    new[] { new VertexWrite("device", Array.Empty<GraphProperty>()) }, CancellationToken.None));

            var bridge = await Assert.ThrowsExceptionAsync<BridgeError>(() => McpTestSupport
                .Bridge(new McpTestSupport.LambdaHandler(_ => throw new TaskCanceledException(
                    "The request was canceled due to the configured HttpClient.Timeout", new TimeoutException())))
                .GetStatusAsync("default", CancellationToken.None));

            StringAssert.Contains(graph.Message, "Fallen8Target:TimeoutSeconds",
                "the run report names the knob an operator would change: " + graph.Message);
            Assert.AreEqual("Fallen-8 timeout", bridge.Title,
                "and the bridge says it in the words a tool result carries");
        }

        // --- the namespace the target addresses ----------------------------------------------------

        [DataTestMethod]
        [DataRow("a/b")]
        [DataRow("a\\b")]
        [DataRow(".")]
        [DataRow("..")]
        [DataRow(" leading")]
        [DataRow("   ")]
        public void ANamespaceFallen8WouldRefuse_FailsBeforeARunWritesAnything(String name)
        {
            // The target used to percent-encode without validating, so a name the platform rejects reached
            // the wire and failed part way through a run as a 404 on some route.
            var refusal = Assert.ThrowsException<GraphTargetException>(() =>
            {
                using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK));
                using var target = new Fallen8RestTarget(client, name);
            });

            StringAssert.Contains(refusal.Message, "cannot address a graph", refusal.Message);
        }

        [TestMethod]
        public void ANamespaceDifferingOnlyInCase_IsNotTheReservedDefault()
        {
            // Fallen-8 compares namespace names ordinally, so "DEFAULT" is a DIFFERENT graph. Matching the
            // bare-route alias case-insensitively sent its writes to the default one instead.
            String path = null;
            using var recording = Client(request =>
            {
                path = request.RequestUri.AbsolutePath;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[1]", Encoding.UTF8, "application/json"),
                };
            });

            using var target = new Fallen8RestTarget(recording, "DEFAULT");
            target.CreateVerticesAsync(new[] { new VertexWrite("device", Array.Empty<GraphProperty>()) },
                CancellationToken.None).GetAwaiter().GetResult();

            StringAssert.StartsWith(path, "/ns/DEFAULT/",
                "a bare route would have written into the reserved default graph instead: " + path);
        }
    }
}
