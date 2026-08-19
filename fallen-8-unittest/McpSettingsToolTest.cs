// MIT License
//
// McpSettingsToolTest.cs
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
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Protocol;
using NoSQL.GraphDB.Mcp.Configuration;
using NoSQL.GraphDB.Mcp.Tools;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// The f8_admin settings operations (feature writable-instance-config), invoked against a canned
    /// REST bridge. These exist because the ops shipped once with a JsonNode re-parenting bug that
    /// threw on EVERY get_settings call, and nothing pinned the invocation path: the coverage gates
    /// only assert which routes are bridged, not that an op survives its own response handling.
    /// </summary>
    [TestClass]
    public class McpSettingsToolTest
    {
        /// <summary>Captures the bridged request and answers a canned 200 (the shape
        /// AuditDefectMcpAlgorithmTest uses; private per file, following that convention).</summary>
        private sealed class CapturingHandler : HttpMessageHandler
        {
            private readonly String _responseBody;

            public CapturingHandler(String responseBody)
            {
                _responseBody = responseBody;
            }

            public HttpMethod Method
            {
                get; private set;
            }

            public String RequestPath
            {
                get; private set;
            }

            public String Body
            {
                get; private set;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Method = request.Method;
                RequestPath = request.RequestUri == null ? null : request.RequestUri.AbsolutePath;
                Body = request.Content == null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_responseBody, Encoding.UTF8, "application/json"),
                };
            }
        }

        /// <summary>The tool's human summary line, which is what a model reads first.</summary>
        private static String Text(CallToolResult result)
        {
            return result.Content.Count > 0 && result.Content[0] is TextContentBlock text
                ? text.Text
                : "(no content)";
        }

        private const String ConfigBody =
            "{\"semantic\":{},\"observability\":{},\"apiKeyRequired\":true,\"configWriteEnabled\":true,"
            + "\"settings\":["
            + "{\"key\":\"Fallen8:Plugins:MaxCount\",\"kind\":\"int\",\"tier\":\"live\","
            + "\"applyMode\":\"liveForNewWork\",\"value\":\"64\",\"source\":\"default\",\"restartPending\":false},"
            + "{\"key\":\"Fallen8:Security:ApiKey\",\"kind\":\"string\",\"tier\":\"notWritable\","
            + "\"applyMode\":\"never\",\"valueWithheld\":true,\"source\":\"environment\",\"restartPending\":false}"
            + "],\"pendingRestart\":[]}";

        [TestMethod]
        public async Task GetSettings_ReturnsTheInventory()
        {
            var tool = new AdminTool(McpTestSupport.Bridge(new CapturingHandler(ConfigBody)));

            var result = await tool.InvokeAsync(
                McpTestSupport.Args("{\"op\":\"get_settings\"}"),
                new McpToolsOptions { EnableAdmin = true },
                CancellationToken.None);

            Assert.IsFalse(result.IsError, Text(result));
            var payload = McpTestSupport.Structured(result);
            Assert.AreEqual(2, payload.GetProperty("settings").GetArrayLength(),
                "both settings come back, including the never-writable one with its reason");
            Assert.AreEqual(0, payload.GetProperty("pendingRestart").GetArrayLength());
        }

        [TestMethod]
        public async Task GetSettings_WritableOnly_DropsTheNeverWritableRows()
        {
            var tool = new AdminTool(McpTestSupport.Bridge(new CapturingHandler(ConfigBody)));

            var result = await tool.InvokeAsync(
                McpTestSupport.Args("{\"op\":\"get_settings\",\"writableOnly\":true}"),
                new McpToolsOptions { EnableAdmin = true },
                CancellationToken.None);

            Assert.IsFalse(result.IsError, Text(result));
            var settings = McpTestSupport.Structured(result).GetProperty("settings");
            Assert.AreEqual(1, settings.GetArrayLength());
            Assert.AreEqual("Fallen8:Plugins:MaxCount", settings[0].GetProperty("key").GetString());
        }

        [TestMethod]
        public async Task SetSettings_PatchesConfig_AndSummarisesFromThePerKeyResults()
        {
            // One result per shape the summary must distinguish: applied-for-new-work, waiting for a
            // restart, and a live apply that failed. A batch-level count alone would read the failed
            // one as "in effect", which is the lie the summary exists to avoid.
            var handler = new CapturingHandler(
                "{\"results\":["
                + "{\"key\":\"Fallen8:ChangeFeed:MaxSubscribers\",\"value\":\"64\",\"coerced\":false,"
                + "\"cleared\":false,\"applyMode\":\"liveForNewWork\",\"restartPending\":false},"
                + "{\"key\":\"Fallen8:Ingestion:MaxPages\",\"value\":\"250\",\"coerced\":false,"
                + "\"cleared\":false,\"applyMode\":\"restart\",\"restartPending\":true},"
                + "{\"key\":\"Fallen8:Plugins:MaxCount\",\"value\":\"128\",\"coerced\":false,"
                + "\"cleared\":false,\"applyMode\":\"restart\",\"restartPending\":true,"
                + "\"applyFailure\":\"the delegate threw\"}"
                + "],\"pendingRestart\":[{\"key\":\"Fallen8:Ingestion:MaxPages\","
                + "\"runningValue\":\"500\",\"pendingValue\":\"250\"}]}");
            var tool = new AdminTool(McpTestSupport.Bridge(handler));

            var result = await tool.InvokeAsync(
                McpTestSupport.Args("{\"op\":\"set_settings\",\"settings\":{"
                    + "\"Fallen8:ChangeFeed:MaxSubscribers\":\"64\","
                    + "\"Fallen8:Ingestion:MaxPages\":\"250\","
                    + "\"Fallen8:Plugins:MaxCount\":\"128\"}}"),
                new McpToolsOptions { EnableAdmin = true },
                CancellationToken.None);

            Assert.IsFalse(result.IsError, Text(result));
            Assert.AreEqual(HttpMethod.Patch, handler.Method);
            Assert.AreEqual("/config", handler.RequestPath);
            var sent = JsonDocument.Parse(handler.Body).RootElement.GetProperty("settings");
            Assert.AreEqual("64", sent.GetProperty("Fallen8:ChangeFeed:MaxSubscribers").GetString());

            var summary = Text(result);
            StringAssert.Contains(summary, "could NOT be applied",
                "the failed live apply is named, not folded into a success");
            StringAssert.Contains(summary, "restarts the server");
            StringAssert.Contains(summary, "NEW work only");
        }

        [TestMethod]
        public async Task SetSettings_WithoutASettingsObject_IsA400()
        {
            var tool = new AdminTool(McpTestSupport.Bridge(new CapturingHandler("{}")));

            var result = await tool.InvokeAsync(
                McpTestSupport.Args("{\"op\":\"set_settings\"}"),
                new McpToolsOptions { EnableAdmin = true },
                CancellationToken.None);

            Assert.IsTrue(result.IsError);
        }
    }
}

