// MIT License
//
// McpToolSurfaceTest.cs
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
using System.Linq;
using System.Net;
using System.Net.Http;
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
    ///   The token-frugal, hand-authored tool surface (feature mcp-server, Phase 0/§3.2): the
    ///   schema-shape proof (flat, enum-discriminated, NO oneOf/anyOf/$ref — the load-bearing
    ///   assumption the whole design rests on) and tier gating at both list and call.
    /// </summary>
    [TestClass]
    public class McpToolSurfaceTest
    {
        private static readonly IReadOnlyDictionary<String, JsonElement> NoArgs =
            new Dictionary<String, JsonElement>();

        /// <summary>A write-tier stand-in so tier gating can be proven before Phase 2 lands the
        /// real write tools.</summary>
        private sealed class StubWriteTool : IMcpTool
        {
            public String Name => "f8_stub_write";

            public ToolTier Tier => ToolTier.Write;

            public Tool Describe(McpToolsOptions tools) => new()
            {
                Name = Name,
                Description = "stub",
                InputSchema = SchemaBuilder.Empty(),
            };

            public Task<CallToolResult> InvokeAsync(
                IReadOnlyDictionary<String, JsonElement> arguments,
                McpToolsOptions tools,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(ToolResults.Ok("stub ok"));
            }
        }

        private static ToolCatalog Build(McpToolsOptions tools)
        {
            var bridge = McpTestSupport.Bridge(
                new McpTestSupport.LambdaHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
            return McpTestSupport.Catalog(tools, new IMcpTool[] { new OverviewTool(bridge), new StubWriteTool() });
        }

        [TestMethod]
        public void Overview_Schema_IsFlatEnumDiscriminated_NoComposition()
        {
            var overview = Build(new McpToolsOptions()).ListTools().Single(t => t.Name == "f8_overview");
            var schema = overview.InputSchema.GetRawText();

            StringAssert.Contains(schema, "\"type\":\"object\"", "the input schema is a JSON-Schema object");
            StringAssert.Contains(schema, "namespace", "the overview exposes the optional namespace parameter");

            // The load-bearing token/accuracy decision (spec §3.2): flat, no composition keywords.
            Assert.IsFalse(schema.Contains("oneOf", StringComparison.Ordinal), "no oneOf");
            Assert.IsFalse(schema.Contains("anyOf", StringComparison.Ordinal), "no anyOf");
            Assert.IsFalse(schema.Contains("allOf", StringComparison.Ordinal), "no allOf");
            Assert.IsFalse(schema.Contains("$ref", StringComparison.Ordinal), "no $ref");
        }

        [TestMethod]
        public void Overview_CarriesReadOnlyAndClosedWorldAnnotations()
        {
            var overview = Build(new McpToolsOptions()).ListTools().Single(t => t.Name == "f8_overview");

            Assert.IsNotNull(overview.Annotations);
            Assert.AreEqual(true, overview.Annotations!.ReadOnlyHint);
            Assert.AreEqual(false, overview.Annotations.OpenWorldHint);
            Assert.AreEqual(true, overview.Annotations.IdempotentHint);
        }

        [TestMethod]
        public void ListTools_DefaultTiers_ExposesOnlyReadTools()
        {
            var names = Build(new McpToolsOptions()).ListTools().Select(t => t.Name).ToList();

            CollectionAssert.Contains(names, "f8_overview");
            CollectionAssert.DoesNotContain(names, "f8_stub_write",
                "a write-tier tool is absent from tools/list when the write tier is off");
        }

        [TestMethod]
        public void ListTools_WriteEnabled_ExposesWriteTools()
        {
            var names = Build(new McpToolsOptions { EnableWrite = true }).ListTools().Select(t => t.Name).ToList();

            CollectionAssert.Contains(names, "f8_stub_write");
        }

        [TestMethod]
        public async Task CallTool_DisabledTier_IsRejectedEvenWhenNameIsKnown()
        {
            // The name is real; only the tier is off. Defends against a client replaying a cached list.
            var result = await Build(new McpToolsOptions()).CallAsync("f8_stub_write", NoArgs, CancellationToken.None);

            Assert.IsTrue(result.IsError, "calling a disabled-tier tool must be an error");
        }

        [TestMethod]
        public async Task CallTool_EnabledTier_Invokes()
        {
            var result = await Build(new McpToolsOptions { EnableWrite = true })
                .CallAsync("f8_stub_write", NoArgs, CancellationToken.None);

            Assert.IsFalse(result.IsError);
        }

        [TestMethod]
        public async Task CallTool_UnknownName_IsError()
        {
            var result = await Build(new McpToolsOptions()).CallAsync("f8_nope", NoArgs, CancellationToken.None);

            Assert.IsTrue(result.IsError);
        }

        [TestMethod]
        public void Search_Schema_DeclaresEveryModesRequiredInput()
        {
            // Regression guard: the schema (additionalProperties:false) must declare the operands
            // its modes require, or a schema-abiding client cannot reach index/property/vector.
            var bridge = McpTestSupport.Bridge(
                new McpTestSupport.LambdaHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
            var schema = new SearchTool(bridge).Describe(new McpToolsOptions()).InputSchema.GetRawText();

            StringAssert.Contains(schema, "\"value\"", "index/property modes need a declared 'value' operand");
            StringAssert.Contains(schema, "\"vector\"", "vector mode needs a declared 'vector' parameter");
        }

        /// <summary>The op enum of f8_storedquery follows the capabilities exactly as f8_plugins'
        /// does: a read-only agent sees list and get only (feature mcp-plugin-gaps, spec section 8).</summary>
        [TestMethod]
        public void StoredQuery_OpEnum_VariesByCapability()
        {
            static List<String> Ops(McpToolsOptions caps)
            {
                var tool = EveryTool().Single(t => t.Name == "f8_storedquery");
                return tool.Describe(caps).InputSchema.GetProperty("properties").GetProperty("op").GetProperty("enum")
                    .EnumerateArray().Select(e => e.GetString()!).ToList();
            }

            CollectionAssert.AreEqual(new[] { "list", "get" }, Ops(new McpToolsOptions()));
            CollectionAssert.AreEqual(new[] { "list", "get", "delete" }, Ops(new McpToolsOptions { EnableWrite = true }));
            CollectionAssert.AreEqual(new[] { "list", "get", "register" }, Ops(new McpToolsOptions { EnableCode = true }));
            CollectionAssert.AreEqual(new[] { "list", "get", "delete", "register" }, Ops(new McpToolsOptions { EnableWrite = true, EnableCode = true }));

            var readOnly = EveryTool().Single(t => t.Name == "f8_storedquery").Describe(new McpToolsOptions());
            Assert.AreEqual(true, readOnly.Annotations!.ReadOnlyHint, "with no write or code capability the tool is read-only");
            var widened = EveryTool().Single(t => t.Name == "f8_storedquery").Describe(new McpToolsOptions { EnableWrite = true });
            Assert.AreEqual(false, widened.Annotations!.ReadOnlyHint, "delete is advertised, so the tool is no longer read-only");
        }

        // --- every tool, every capability combination (feature mcp-plugin-gaps, spec section 3) ---

        private static IEnumerable<McpToolsOptions> EveryCapabilityCombination()
        {
            for (var bits = 0; bits < 8; bits++)
            {
                yield return new McpToolsOptions
                {
                    EnableWrite = (bits & 1) != 0,
                    EnableAdmin = (bits & 2) != 0,
                    EnableCode = (bits & 4) != 0,
                };
            }
        }

        private static IMcpTool[] EveryTool()
        {
            return McpTestSupport.AllTools(McpTestSupport.Bridge(
                new McpTestSupport.LambdaHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));
        }

        /// <summary>
        ///   The defect this pins: <c>f8_mutate</c> declared <c>properties</c> twice (an object map for
        ///   the single creates, an array for the batch) and the builder silently kept the last one.
        ///   A schema varies by capability, so every combination is described; the builder throws on
        ///   a repeat, and this test is what makes that throw a build failure rather than a surprise
        ///   on the first <c>tools/list</c>.
        /// </summary>
        [TestMethod]
        public void EveryTool_Describe_DeclaresNoDuplicateSchemaName()
        {
            foreach (var caps in EveryCapabilityCombination())
            {
                foreach (var tool in EveryTool())
                {
                    Tool described;
                    try
                    {
                        described = tool.Describe(caps);
                    }
                    catch (InvalidOperationException e)
                    {
                        Assert.Fail($"{tool.Name} (write={caps.EnableWrite} admin={caps.EnableAdmin} code={caps.EnableCode}): {e.Message}");
                        return;
                    }

                    var names = described.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
                    Assert.AreEqual(names.Count, names.Distinct(StringComparer.Ordinal).Count(), tool.Name + " declares a name twice");
                    if (described.InputSchema.TryGetProperty("required", out var required))
                    {
                        var requiredNames = required.EnumerateArray().Select(r => r.GetString()).ToList();
                        Assert.AreEqual(requiredNames.Count, requiredNames.Distinct().Count(), tool.Name + " requires a name twice");
                        foreach (var name in requiredNames)
                        {
                            CollectionAssert.Contains(names, name, tool.Name + " requires an undeclared name");
                        }
                    }
                }
            }
        }

        /// <summary>
        ///   One valid call per op (or per mode) of every tool, checked against the schema the tool
        ///   advertises with every capability on. A schema-abiding client validates arguments before
        ///   sending, so a call the runtime accepts but the schema rejects is a call that client can
        ///   never make; that was the state of <c>create_vertex</c> with an object <c>properties</c>
        ///   and of <c>remove_elements</c> with integer ids.
        /// </summary>
        [TestMethod]
        public void EveryTool_SampleCall_ValidatesAgainstItsAdvertisedSchema()
        {
            var samples = new Dictionary<String, String[]>(StringComparer.Ordinal)
            {
                ["f8_overview"] = new[] { "{}", "{\"namespace\":\"default\",\"detail\":\"statistics\"}" },
                ["f8_get"] = new[] { "{\"kind\":\"vertex\",\"id\":1,\"include\":[\"degree\"],\"fields\":[\"name\"]}" },
                ["f8_search"] = new[]
                {
                    "{\"mode\":\"index\",\"indexId\":\"names\",\"value\":\"Ada\",\"kind\":\"vertex\",\"limit\":5}",
                    "{\"mode\":\"property\",\"key\":\"name\",\"value\":42,\"cursor\":25}",
                    "{\"mode\":\"properties\",\"query\":\"Ada\",\"label\":\"person\"}",
                    "{\"mode\":\"fulltext\",\"indexId\":\"ft\",\"query\":\"ada\",\"fields\":[\"name\"]}",
                    "{\"mode\":\"vector\",\"indexId\":\"vec\",\"vector\":[0.1,0.2],\"limit\":3}",
                    "{\"mode\":\"semantic\",\"indexId\":\"vec\",\"query\":\"a sentence\"}",
                },
                ["f8_paths"] = new[]
                {
                    "{\"from\":1,\"to\":2,\"algorithm\":\"BLS\",\"maxDepth\":3,\"maxResults\":5}",
                    "{\"from\":1,\"to\":2,\"storedQuery\":\"q\"}",
                    "{\"from\":1,\"to\":2,\"vertexFilter\":\"return (v) => true;\",\"edgeCost\":\"return (e) => 1.0;\"}",
                    "{\"from\":1,\"to\":2,\"algorithm\":\"DIJKSTRA\",\"maxPathWeight\":2.5,\"timeBudgetSeconds\":3,\"semantic\":{\"queryVector\":[1,0,0],\"embeddingName\":\"default\",\"minScore\":0.5,\"costBySimilarity\":true}}",
                },
                ["f8_analytics"] = new[]
                {
                    "{}",
                    "{\"algorithm\":\"PAGERANK\",\"direction\":\"out\",\"maxResults\":10,\"maxIterations\":20,\"parameters\":{\"DampingFactor\":0.85}}",
                },
                ["f8_plugins"] = new[]
                {
                    "{\"op\":\"list\"}",
                    "{\"op\":\"get\",\"name\":\"p\"}",
                    "{\"op\":\"invoke\",\"name\":\"p\",\"parameters\":{\"k\":\"v\"}}",
                    "{\"op\":\"delete\",\"name\":\"p\"}",
                    "{\"op\":\"register_algorithm\",\"name\":\"p\",\"contract\":\"Path\",\"description\":\"d\",\"sourceCode\":\"class X {}\"}",
                    "{\"op\":\"register_function\",\"name\":\"f\",\"sourceCode\":\"class X {}\"}",
                },
                ["f8_storedquery"] = new[]
                {
                    "{\"op\":\"list\"}",
                    "{\"op\":\"get\",\"name\":\"q\"}",
                    "{\"op\":\"delete\",\"name\":\"q\"}",
                    "{\"op\":\"register\",\"name\":\"q\",\"kind\":\"Path\",\"description\":\"d\",\"path\":{\"filter\":{\"vertexFilter\":\"return (v) => true;\"}}}",
                    "{\"op\":\"register\",\"name\":\"t\",\"kind\":\"SubGraph\",\"subGraph\":{\"vertexFilter\":\"return (v) => true;\",\"patterns\":[]}}",
                },
                ["f8_documents"] = new[]
                {
                    "{\"op\":\"list\"}",
                    "{\"op\":\"get\",\"documentId\":3}",
                    "{\"op\":\"search\",\"query\":\"q\",\"mode\":\"fused\",\"k\":5,\"window\":1,\"groupByDocument\":true,\"queryVector\":[0.1]}",
                    "{\"op\":\"binding\"}",
                    "{\"op\":\"entities\",\"contains\":\"a\",\"limit\":10}",
                    "{\"op\":\"ingest_text\",\"name\":\"n\",\"text\":\"t\",\"format\":\"plain\",\"embed\":false,\"sourceUri\":\"s\",\"replaceDocumentId\":1,\"properties\":{\"k\":\"v\"},\"linkIndexIds\":[\"i\"],\"maxLinksPerChunk\":2}",
                    "{\"op\":\"delete\",\"documentId\":3}",
                    "{\"op\":\"bind\"}",
                },
                ["f8_mutate"] = new[]
                {
                    "{\"op\":\"create_vertex\",\"label\":\"person\",\"properties\":{\"name\":\"Ada\",\"age\":36}}",
                    "{\"op\":\"create_edge\",\"source\":1,\"target\":2,\"edgePropertyId\":\"knows\",\"label\":\"l\",\"properties\":{\"since\":2020}}",
                    "{\"op\":\"create_vertices\",\"vertices\":[{\"label\":\"person\",\"properties\":{\"name\":\"Ada\"}}]}",
                    "{\"op\":\"create_edges\",\"edges\":[{\"source\":1,\"target\":2,\"edgePropertyId\":\"knows\"}]}",
                    "{\"op\":\"set_property\",\"id\":1,\"key\":\"city\",\"value\":\"Berlin\"}",
                    "{\"op\":\"set_properties\",\"updates\":[{\"id\":1,\"key\":\"city\",\"value\":\"Berlin\"},{\"id\":1,\"key\":\"old\",\"remove\":true}]}",
                    "{\"op\":\"remove_property\",\"id\":1,\"key\":\"city\"}",
                    "{\"op\":\"remove_element\",\"id\":1}",
                    "{\"op\":\"remove_elements\",\"ids\":[1,2,3]}",
                    "{\"op\":\"set_embedding\",\"id\":1,\"name\":\"text\",\"vector\":[0.1,0.2]}",
                },
                ["f8_index"] = new[]
                {
                    "{\"op\":\"create\",\"indexId\":\"names\",\"pluginType\":\"DictionaryIndex\"}",
                    "{\"op\":\"create\",\"indexId\":\"vec\",\"pluginType\":\"VectorIndex\",\"options\":{\"dimension\":3,\"metric\":\"Cosine\",\"embeddingName\":\"text\"}}",
                    "{\"op\":\"add\",\"indexId\":\"names\",\"id\":1,\"key\":\"Ada\"}",
                    "{\"op\":\"add_many\",\"indexId\":\"names\",\"entries\":[{\"id\":1,\"key\":\"Ada\"},{\"id\":2,\"key\":42}]}",
                    "{\"op\":\"add_vector\",\"indexId\":\"vec\",\"id\":1,\"vector\":[1,0,0]}",
                    "{\"op\":\"add_vector\",\"indexId\":\"vec\",\"id\":1,\"propertyId\":\"embedding\"}",
                    "{\"op\":\"remove_element\",\"indexId\":\"names\",\"id\":1}",
                    "{\"op\":\"remove_key\",\"indexId\":\"names\",\"key\":\"Ada\"}",
                    "{\"op\":\"delete\",\"indexId\":\"names\"}",
                    "{\"op\":\"backfill\",\"indexId\":\"names\",\"propertyId\":\"name\",\"replace\":true,\"prefix\":false,\"label\":\"person\"}",
                },
                ["f8_subgraph"] = new[]
                {
                    "{\"name\":\"s\",\"algorithm\":\"BFS\"}",
                    "{\"name\":\"s\",\"storedQuery\":\"t\"}",
                    "{\"name\":\"s\",\"vertexFilter\":\"return (v) => true;\",\"edgeFilter\":\"return (e) => true;\"}",
                    "{\"name\":\"s\",\"semantic\":{\"queryVector\":[1,0,0],\"embeddingName\":\"default\",\"minScore\":0.5},\"patterns\":[{\"type\":\"Vertex\",\"semanticMinScore\":0.7},{\"type\":\"Edge\",\"direction\":\"OutgoingEdge\"},{\"type\":\"Vertex\"}]}",
                },
                ["f8_namespace"] = new[]
                {
                    "{\"op\":\"create\",\"name\":\"n\"}",
                    "{\"op\":\"rename\",\"name\":\"n\",\"newName\":\"m\"}",
                    "{\"op\":\"drop\",\"name\":\"n\"}",
                },
                ["f8_admin"] = new[]
                {
                    "{\"op\":\"save\",\"namespace\":\"default\",\"saveGameLocation\":\"p\",\"savePartitions\":2}",
                    "{\"op\":\"load\",\"id\":\"g\",\"restoreNamespace\":\"default\"}",
                    "{\"op\":\"list_savegames\"}",
                    "{\"op\":\"activate\",\"namespace\":\"n\"}",
                    "{\"op\":\"trim\",\"namespace\":\"default\"}",
                    "{\"op\":\"tabula_rasa\",\"namespace\":\"default\"}",
                    "{\"op\":\"get_settings\",\"writableOnly\":true}",
                    "{\"op\":\"set_settings\",\"settings\":{\"Fallen8:Chat:Model\":\"m\"}}",
                },
            };

            var allCaps = new McpToolsOptions { EnableWrite = true, EnableAdmin = true, EnableCode = true };
            var tools = EveryTool();
            CollectionAssert.AreEquivalent(samples.Keys.ToList(), tools.Select(t => t.Name).ToList(),
                "every registered tool has a sample row, and no row names a tool that does not exist");

            var failures = new List<String>();
            foreach (var tool in tools)
            {
                var schema = tool.Describe(allCaps).InputSchema;
                foreach (var sample in samples[tool.Name])
                {
                    var violations = FlatSchemaChecker.Violations(schema, JsonDocument.Parse(sample).RootElement);
                    failures.AddRange(violations.Select(v => $"{tool.Name} {sample}: {v}"));
                }
            }

            Assert.AreEqual(0, failures.Count, Environment.NewLine + String.Join(Environment.NewLine, failures));
        }
    }
}
