// MIT License
//
// OllamaModelProbeTest.cs
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
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Chat;
using NoSQL.GraphDB.App.Helper;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The model-residency probe behind <c>GET /config</c> (features instance-config and
    ///   nahil-backend).
    ///
    ///   <para>The point of this class is the THREE-state answer. "Resident", "not resident" and
    ///   "unknown" are different, and which of the last two an answered-but-silent
    ///   <c>/api/ps</c> means depends on the backend: exhaustive on the local sidecar, not
    ///   exhaustive on Nahil. Getting that wrong is not a cosmetic bug - <c>false</c> outranks the
    ///   provider's own <c>loaded</c> flag in every consumer, so one wrong <c>false</c> told a
    ///   Studio operator that a provider they had just used was never loaded, permanently.</para>
    ///
    ///   <para>The Nahil bodies below are not invented: they are what <c>api.nahil.dev</c> answered
    ///   on 2026-08-31, captured verbatim (digest and key elided). That is the whole value of them -
    ///   a hand-waved fixture would have agreed with the bug.</para>
    /// </summary>
    [TestClass]
    public class OllamaModelProbeTest
    {
        private const String ChatModel = "phi4-f8-mini:latest";
        private const String EmbedModel = "bge-m3:latest";

        /// <summary>Nahil's <c>/api/ps</c> while the chat model is warm on a worker, verbatim. Note
        /// what is NOT here: <c>size_vram</c>. Nahil publishes no VRAM figure at all.</summary>
        private const String NahilPsChatWarm =
            "{\"models\":[{\"details\":{\"family\":\"\",\"parameter_size\":\"\"," +
            "\"quantization_level\":\"\"},\"digest\":\"a71921346716\"," +
            "\"expires_at\":\"2026-08-31T07:30:54.636658485+00:00\",\"model\":\"phi4-f8-mini:latest\"," +
            "\"nahil_class\":\"S1\",\"nahil_workers_warm\":1,\"name\":\"phi4-f8-mini:latest\"}]}";

        /// <summary>Nahil's <c>/api/ps</c> answer for an embedding model that is serving requests
        /// right now, verbatim. It is empty, which is the entire bug: <c>bge-m3</c> (class C2) never
        /// appears, during a request or after one, so its absence says nothing about it.</summary>
        private const String NahilPsEmpty = "{\"models\":[]}";

        private static String SidecarPs(String name, Int64 sizeVram)
        {
            return "{\"models\":[{\"name\":\"" + name + "\",\"model\":\"" + name + "\"," +
                "\"size\":5000000000,\"size_vram\":" + sizeVram.ToString() +
                ",\"expires_at\":\"2026-08-31T07:30:54Z\",\"details\":{\"family\":\"phi3\"}}]}";
        }

        private static OllamaConnection Nahil(String model)
        {
            return OllamaConnection.Nahil("Fallen8:Chat:Nahil", "https://api.nahil.dev", model, "nahil-secret-key");
        }

        private static OllamaConnection Sidecar(String model)
        {
            return OllamaConnection.Sidecar("Fallen8:Chat:Ollama", "http://localhost:11434", model);
        }

        /// <summary>Answers every request with one canned body, recording the paths asked for.</summary>
        private sealed class PsHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            private readonly String _body;
            private readonly Func<Task> _before;

            public PsHandler(String body, HttpStatusCode status = HttpStatusCode.OK, Func<Task> before = null)
            {
                _body = body;
                _status = status;
                _before = before;
            }

            public List<String> Paths { get; } = new List<String>();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Paths.Add(request.RequestUri.AbsolutePath);
                if (_before != null)
                {
                    await _before();
                }

                return new HttpResponseMessage(_status)
                {
                    Content = new StringContent(_body ?? String.Empty, System.Text.Encoding.UTF8, "application/json"),
                };
            }
        }

        // ---------------------------------------------------------------------------------------
        // The local sidecar: its /api/ps list is exhaustive, so all three answers are reachable and
        // NONE of this behaviour changed with the Nahil fix. That is what these cases are for.
        // ---------------------------------------------------------------------------------------

        [TestMethod]
        public async Task Sidecar_ModelListedWithVram_IsResidentOnGpu()
        {
            var state = await OllamaModelProbe.ProbeAsync(Sidecar(ChatModel), CancellationToken.None,
                new PsHandler(SidecarPs(ChatModel, 4_000_000_000)));

            Assert.IsNotNull(state, "an answered probe about a listed model is not 'unknown'");
            Assert.IsTrue(state.Resident);
            Assert.AreEqual(true, state.Gpu, "size_vram > 0 is the sidecar's GPU answer");
        }

        [TestMethod]
        public async Task Sidecar_ModelListedWithoutVram_IsResidentOnCpu()
        {
            var state = await OllamaModelProbe.ProbeAsync(Sidecar(ChatModel), CancellationToken.None,
                new PsHandler(SidecarPs(ChatModel, 0)));

            Assert.IsNotNull(state);
            Assert.IsTrue(state.Resident);
            Assert.AreEqual(false, state.Gpu,
                "the sidecar always publishes size_vram, so zero really does mean CPU here");
        }

        [TestMethod]
        public async Task Sidecar_EmptyList_IsDefinitivelyNotResident()
        {
            var state = await OllamaModelProbe.ProbeAsync(Sidecar(ChatModel), CancellationToken.None,
                new PsHandler("{\"models\":[]}"));

            Assert.IsNotNull(state, "the sidecar's empty list IS an answer, not 'unknown'");
            Assert.IsFalse(state.Resident);
            Assert.AreEqual(false, state.Gpu);
        }

        [TestMethod]
        public async Task Sidecar_ListWithoutTheConfiguredModel_IsDefinitivelyNotResident()
        {
            var state = await OllamaModelProbe.ProbeAsync(Sidecar(EmbedModel), CancellationToken.None,
                new PsHandler(SidecarPs(ChatModel, 4_000_000_000)));

            Assert.IsNotNull(state);
            Assert.IsFalse(state.Resident, "another model being loaded says nothing good about this one");
        }

        [TestMethod]
        public async Task Sidecar_TagDiffers_StillMatches()
        {
            // Configured untagged, reported with ":latest". Tolerating this is what keeps a
            // deployment that writes "phi4-f8-mini" from reading as permanently cold.
            var state = await OllamaModelProbe.ProbeAsync(Sidecar("phi4-f8-mini"), CancellationToken.None,
                new PsHandler(SidecarPs("phi4-f8-mini:latest", 1)));

            Assert.IsNotNull(state);
            Assert.IsTrue(state.Resident);
        }

        // ---------------------------------------------------------------------------------------
        // Nahil: the list reports only the classes it keeps warm, so "absent" is NOT an answer.
        // ---------------------------------------------------------------------------------------

        [TestMethod]
        public async Task Nahil_ModelListed_IsResidentWithUnknownDevice()
        {
            var state = await OllamaModelProbe.ProbeAsync(Nahil(ChatModel), CancellationToken.None,
                new PsHandler(NahilPsChatWarm));

            Assert.IsNotNull(state);
            Assert.IsTrue(state.Resident, "Nahil listed the model with a warm worker");
            Assert.IsNull(state.Gpu,
                "Nahil publishes no size_vram, and a missing figure must never read as 'on CPU': " +
                "the model runs on a remote worker whose device this host cannot see");
        }

        [TestMethod]
        public async Task Nahil_EmptyList_IsUnknownNotColdModel()
        {
            // THE REGRESSION. A bge-m3 embed that had just succeeded left /api/ps empty, and
            // reporting resident=false made GET /config say "not loaded (loads on first use)" about
            // a provider in active use - forever, because a definite answer outranks `loaded`.
            var state = await OllamaModelProbe.ProbeAsync(Nahil(EmbedModel), CancellationToken.None,
                new PsHandler(NahilPsEmpty));

            Assert.IsNull(state,
                "Nahil not listing a model is not a claim that the model is cold; it is no answer");
        }

        [TestMethod]
        public async Task Nahil_ListWithoutTheConfiguredModel_IsUnknown()
        {
            // The measured shape: the chat model IS listed while the embedding model is not, so the
            // list is demonstrably non-empty and still says nothing about bge-m3.
            var state = await OllamaModelProbe.ProbeAsync(Nahil(EmbedModel), CancellationToken.None,
                new PsHandler(NahilPsChatWarm));

            Assert.IsNull(state, "a non-empty list that omits the model is still not an answer on Nahil");
        }

        [TestMethod]
        public async Task Nahil_TagDiffers_StillMatches()
        {
            var state = await OllamaModelProbe.ProbeAsync(Nahil("phi4-f8-mini"), CancellationToken.None,
                new PsHandler(NahilPsChatWarm));

            Assert.IsNotNull(state, "the tag tolerance is not backend-specific");
            Assert.IsTrue(state.Resident);
        }

        // ---------------------------------------------------------------------------------------
        // The "can never stall or fail the config read" guarantee, on both backends.
        // ---------------------------------------------------------------------------------------

        [TestMethod]
        public async Task BackendError_IsUnknownOnBothBackends()
        {
            foreach (var connection in new[] { Sidecar(ChatModel), Nahil(ChatModel) })
            {
                var state = await OllamaModelProbe.ProbeAsync(connection, CancellationToken.None,
                    new PsHandler("nope", HttpStatusCode.InternalServerError));

                Assert.IsNull(state, "a 500 is unknown, never a residency claim (" + connection.SectionKey + ")");
            }
        }

        [TestMethod]
        public async Task UnparseableBody_IsUnknown()
        {
            var state = await OllamaModelProbe.ProbeAsync(Sidecar(ChatModel), CancellationToken.None,
                new PsHandler("<html>not json</html>"));

            Assert.IsNull(state);
        }

        [TestMethod]
        public async Task InvalidOrMissingConnection_ProbesNothing()
        {
            Assert.IsNull(await OllamaModelProbe.ProbeAsync(null, CancellationToken.None),
                "a backend with no Ollama-protocol target (OpenAI, Anthropic) has nothing to probe");

            var noModel = OllamaConnection.Sidecar("Fallen8:Chat:Ollama", "http://localhost:11434", "  ");
            var handler = new PsHandler(SidecarPs(ChatModel, 1));
            Assert.IsNull(await OllamaModelProbe.ProbeAsync(noModel, CancellationToken.None, handler));
            Assert.AreEqual(0, handler.Paths.Count, "an unusable connection must not be dialled at all");
        }

        [TestMethod]
        public async Task CallerCancellation_Propagates()
        {
            // The one failure the probe does NOT swallow: the caller went away, so its config read
            // is gone too and there is nobody to answer "unknown" to.
            using var cts = new CancellationTokenSource();
            var handler = new PsHandler(NahilPsChatWarm, HttpStatusCode.OK, () =>
            {
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

            // OperationCanceledException, not the TaskCanceledException a transport timeout raises:
            // the distinction is exactly what the probe's catch filter is for.
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(
                () => OllamaModelProbe.ProbeAsync(Nahil(ChatModel), cts.Token, handler));
        }

        [TestMethod]
        public async Task ProbeAsksApiPs_AndNothingElse()
        {
            var handler = new PsHandler(NahilPsChatWarm);
            await OllamaModelProbe.ProbeAsync(Nahil(ChatModel), CancellationToken.None, handler);

            CollectionAssert.AreEqual(new List<String> { "/api/ps" }, handler.Paths,
                "one metadata call, which is why this probe costs no tokens on Nahil");
        }
    }
}
