// MIT License
//
// AnalyticsToolKnobsTest.cs
//
// Copyright (c) 2026 Henning Rauch
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
using NoSQL.GraphDB.Mcp.Configuration;
using NoSQL.GraphDB.Mcp.Tools;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pins that f8_analytics forwards the per-run knobs (maxIterations, the numeric parameters map
    /// e.g. DampingFactor) to the REST analytics endpoint. These DTO fields exist and the engine/REST
    /// support them, but the tool previously never populated them, so agents could not tune a run.
    /// Captures the outbound request body via a stub bridge (no convergence dependency).
    /// </summary>
    [TestClass]
    public class AnalyticsToolKnobsTest
    {
        [TestMethod]
        public async Task Analytics_ForwardsMaxIterationsAndParameters_ToTheEndpoint()
        {
            String capturedBody = null;
            var handler = new McpTestSupport.LambdaHandler(req =>
            {
                capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                const String canned =
                    "{\"algorithm\":\"PAGERANK\",\"converged\":true,\"iterationsRun\":7," +
                    "\"elapsedMs\":1.0,\"budgetExhausted\":false,\"vertexCount\":0,\"results\":[]}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(canned, Encoding.UTF8, "application/json"),
                };
            });

            var tool = new AnalyticsTool(McpTestSupport.Bridge(handler));
            await tool.InvokeAsync(
                McpTestSupport.Args("{\"algorithm\":\"PAGERANK\",\"maxIterations\":7,\"parameters\":{\"DampingFactor\":0.5}}"),
                new McpToolsOptions(),
                CancellationToken.None);

            Assert.IsNotNull(capturedBody, "The tool must POST a request body to the analytics endpoint.");
            using var doc = JsonDocument.Parse(capturedBody);
            var root = doc.RootElement;
            Assert.AreEqual(7, root.GetProperty("maxIterations").GetInt32(),
                "maxIterations must reach the analytics endpoint (it was previously never sent).");
            Assert.IsTrue(root.TryGetProperty("parameters", out var parameters) &&
                          parameters.ValueKind == JsonValueKind.Object,
                "A numeric parameters map must be sent.");
            Assert.AreEqual(0.5, parameters.GetProperty("DampingFactor").GetDouble(), 1e-9,
                "The DampingFactor knob must be forwarded in the parameters map.");
        }
    }
}
