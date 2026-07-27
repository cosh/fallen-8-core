// MIT License
//
// McpTelemetryTest.cs
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
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Mcp.Configuration;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Per-tool MCP telemetry (feature fleet-observability §3.6): <see cref="ToolCatalog.CallAsync"/>
    ///   records the <c>fallen8.mcp.tool.calls</c> counter tagged with bounded identifiers only -
    ///   the tool's short name (or <c>unknown</c>), its tier, and ok/error - never a caller-supplied
    ///   string. Also verifies dispatch behaviour is preserved on the disabled-tier and unknown paths.
    /// </summary>
    [TestClass]
    public class McpTelemetryTest
    {
        private sealed record Recorded(long Value, Dictionary<String, Object> Tags);

        private static List<Recorded> Listen(MeterListener listener)
        {
            var recorded = new List<Recorded>();
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "NoSQL.GraphDB.Mcp" && instrument.Name == "fallen8.mcp.tool.calls")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
            {
                var captured = new Dictionary<String, Object>();
                foreach (var tag in tags)
                {
                    captured[tag.Key] = tag.Value!;
                }
                lock (recorded)
                {
                    recorded.Add(new Recorded(value, captured));
                }
            });
            listener.Start();
            return recorded;
        }

        [TestMethod]
        public async Task CallAsync_DisabledTierTool_RecordsResolvedToolAndTier_ResultError()
        {
            using var listener = new MeterListener();
            var recorded = Listen(listener);

            var bridge = McpTestSupport.Bridge(new McpTestSupport.LambdaHandler(
                _ => new HttpResponseMessage(HttpStatusCode.OK)));
            // Write tier OFF: f8_mutate resolves but is rejected (404), the resolved-but-disabled path.
            var catalog = McpTestSupport.Catalog(
                new McpToolsOptions { EnableWrite = false, EnableAdmin = false },
                McpTestSupport.AllTools(bridge));

            var result = await catalog.CallAsync("f8_mutate", McpTestSupport.Args("{}"), default);

            Assert.IsTrue(result.IsError == true, "a disabled tool is still rejected");
            listener.Dispose();
            lock (recorded)
            {
                Assert.IsTrue(recorded.Exists(r =>
                    r.Value == 1 &&
                    Equals(r.Tags.GetValueOrDefault("tool"), "mutate") &&
                    Equals(r.Tags.GetValueOrDefault("tier"), "write") &&
                    Equals(r.Tags.GetValueOrDefault("result"), "error")),
                    "the resolved-but-disabled call records tool=mutate tier=write result=error");
            }
        }

        [TestMethod]
        public async Task CallAsync_UnknownTool_RecordsUnknownTags_ResultError()
        {
            using var listener = new MeterListener();
            var recorded = Listen(listener);

            var bridge = McpTestSupport.Bridge(new McpTestSupport.LambdaHandler(
                _ => new HttpResponseMessage(HttpStatusCode.OK)));
            var catalog = McpTestSupport.Catalog(
                new McpToolsOptions { EnableWrite = true, EnableAdmin = true },
                McpTestSupport.AllTools(bridge));

            var result = await catalog.CallAsync("f8_does_not_exist", McpTestSupport.Args("{}"), default);

            Assert.IsTrue(result.IsError == true);
            listener.Dispose();
            lock (recorded)
            {
                Assert.IsTrue(recorded.Exists(r =>
                    Equals(r.Tags.GetValueOrDefault("tool"), "unknown") &&
                    Equals(r.Tags.GetValueOrDefault("tier"), "unknown") &&
                    Equals(r.Tags.GetValueOrDefault("result"), "error")),
                    "an unknown tool records tool=unknown tier=unknown result=error (never the raw caller string)");
            }
        }
    }
}
