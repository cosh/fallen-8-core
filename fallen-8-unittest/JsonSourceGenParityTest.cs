// MIT License
//
// JsonSourceGenParityTest.cs
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
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.App.Controllers.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Proves that the N1 System.Text.Json source-generation change does not alter the emitted
    /// JSON for the REST DTOs or the subgraph specification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>_reflectionWeb</c> mirrors MVC's pre-change serializer (Web defaults, reflection-based
    /// metadata). <c>_sourceGenWeb</c> mirrors MVC's post-change serializer: the same Web defaults
    /// with <see cref="AppJsonContext"/> inserted at the front of the resolver chain and the
    /// reflection resolver kept as a fallback, exactly as configured in <c>Program.cs</c>. When the
    /// two produce identical output for a value, the switch to source generation is provably a
    /// no-op for that value.
    /// </para>
    /// </remarks>
    [TestClass]
    public class JsonSourceGenParityTest
    {
        private ILoggerFactory _loggerFactory;
        private Fallen8 _fallen8;
        private GraphController _controller;

        private JsonSerializerOptions _reflectionWeb;
        private JsonSerializerOptions _sourceGenWeb;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
            _fallen8 = new Fallen8(_loggerFactory);
            _controller = new GraphController(_loggerFactory.CreateLogger<GraphController>(), _fallen8);
            _fallen8.EnqueueTransaction(new TabulaRasaTransaction()).WaitUntilFinished();

            // Pre-change MVC serializer: Web defaults, pure reflection metadata.
            _reflectionWeb = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                TypeInfoResolver = new DefaultJsonTypeInfoResolver()
            };

            // Post-change MVC serializer: Web defaults with the source-gen context in front and
            // reflection kept as the fallback (identical wiring to Program.cs).
            _sourceGenWeb = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            _sourceGenWeb.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
            _sourceGenWeb.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());
        }

        /// <summary>
        /// The vertex, edge and graph response DTOs (which carry inherited base-class properties)
        /// and the polymorphic <c>GetGraphElement</c> return must serialize identically.
        /// </summary>
        [TestMethod]
        public async Task GraphResponseDtos_SourceGen_MatchReflection()
        {
            await _controller.AddVertex(NewVertex("person", "name", "John Doe"), true);
            await _controller.AddVertex(NewVertex("person", "name", "Jane Smith"), true);

            var initial = _controller.GetGraph();
            var sourceId = initial.Vertices[0].Id;
            var targetId = initial.Vertices[1].Id;

            await _controller.AddEdge(new EdgeSpecification
            {
                CreationDate = 1713862800u,
                SourceVertex = sourceId,
                TargetVertex = targetId,
                EdgePropertyId = "knows",
                Label = "friendship",
                Properties = new List<PropertySpecification>
                {
                    new PropertySpecification { PropertyId = "since", PropertyValue = "2024", FullQualifiedTypeName = "System.String" }
                }
            }, true);

            var graph = _controller.GetGraph();
            var edgeId = graph.Edges[0].Id;

            // Concrete response DTOs (runtime type == declared type).
            AssertSameJson(_controller.GetVertex(sourceId), "Vertex (with outgoing edge + properties)");
            AssertSameJson(_controller.GetEdge(edgeId), "Edge (with properties)");
            AssertSameJson(graph, "Graph (vertices + edges)");

            // Polymorphic endpoint: declared AGraphElement, runtime Vertex/Edge. MVC's output
            // formatter serializes by runtime type, which is what AssertSameJson exercises.
            var elementVertex = _controller.GetGraphElement(sourceId);
            var elementEdge = _controller.GetGraphElement(edgeId);
            Assert.IsInstanceOfType(elementVertex, typeof(Vertex));
            Assert.IsInstanceOfType(elementEdge, typeof(Edge));
            AssertSameJson(elementVertex, "GetGraphElement -> runtime Vertex");
            AssertSameJson(elementEdge, "GetGraphElement -> runtime Edge");
        }

        /// <summary>
        /// DTOs without explicit <c>[JsonPropertyName]</c> rely on the camelCase naming policy;
        /// the source-gen context must reproduce those names exactly.
        /// </summary>
        [TestMethod]
        public void NamingPolicyDtos_SourceGen_MatchReflection()
        {
            var status = new StatusREST
            {
                UsedMemory = 1073741824L,
                VertexCount = 10000,
                EdgeCount = 25000,
                AvailableIndexPlugins = new List<string> { "DictionaryIndex", "SpatialIndex" },
                AvailablePathPlugins = new List<string> { "Dijkstra" },
                AvailableServicePlugins = new List<string>()
            };
            AssertSameJson(status, "StatusREST (no [JsonPropertyName] -> camelCase policy)");

            var stats = new SampleStats { VertexCount = 3, EdgeCount = 7 };
            AssertSameJson(stats, "SampleStats (no [JsonPropertyName] -> camelCase policy)");

            // A null-bearing StatusREST also exercises the default null-writing behaviour.
            AssertSameJson(new StatusREST(), "StatusREST (all defaults / nulls)");
        }

        /// <summary>
        /// Request/spec DTOs, including one carrying the string enum, must serialize identically.
        /// </summary>
        [TestMethod]
        public void SpecificationDtos_SourceGen_MatchReflection()
        {
            AssertSameJson(SampleSpecification(), "SubGraphSpecification (with patterns)");

            var scan = new ScanSpecification { ResultType = ResultTypeSpecification.Both };
            AssertSameJson(scan, "ScanSpecification (string enum)");

            var range = new RangeIndexScanSpecification { ResultType = ResultTypeSpecification.Edges };
            AssertSameJson(range, "RangeIndexScanSpecification (string enum)");
        }

        /// <summary>
        /// The explicit SubGraphController/RecipeSubGraphCompiler call sites now use the context's
        /// JsonTypeInfo directly. The serialized string must equal the previous parameterless
        /// (default-options) reflection output, and the value must round-trip unchanged.
        /// </summary>
        [TestMethod]
        public void SubGraphSpecification_ExplicitCallSite_MatchesReflectionDefault()
        {
            var spec = SampleSpecification();

            // Pre-change: SubGraphController used JsonSerializer.Serialize(spec) (default options).
            var reflectionDefault = JsonSerializer.Serialize(spec);
            var sourceGen = JsonSerializer.Serialize(spec, AppJsonContext.Default.SubGraphSpecification);
            Assert.AreEqual(reflectionDefault, sourceGen, "SubGraphController serialize parity");

            // Pre-change: RecipeSubGraphCompiler used JsonSerializer.Deserialize<SubGraphSpecification>(json).
            var roundTripped = JsonSerializer.Deserialize(sourceGen, AppJsonContext.Default.SubGraphSpecification);
            Assert.IsNotNull(roundTripped);
            Assert.AreEqual(reflectionDefault, JsonSerializer.Serialize(roundTripped, AppJsonContext.Default.SubGraphSpecification),
                "SubGraphSpecification round-trip parity");
        }

        private static VertexSpecification NewVertex(string label, string propId, string propValue)
        {
            return new VertexSpecification
            {
                Label = label,
                CreationDate = 1713862800u,
                Properties = new List<PropertySpecification>
                {
                    new PropertySpecification { PropertyId = propId, PropertyValue = propValue, FullQualifiedTypeName = "System.String" }
                }
            };
        }

        private static SubGraphSpecification SampleSpecification()
        {
            return new SubGraphSpecification
            {
                Name = "friends-of-alice",
                AdditionalInformation = new Dictionary<string, string> { { "owner", "alice" } },
                VertexFilter = "return (ge) => ge.Label == \"person\";",
                EdgeFilter = null, // exercises default null-writing
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification { Type = "Vertex", PatternName = "start", GraphElementFilter = "return (ge) => true;" },
                    new PatternSpecification { Type = "Edge", PatternName = "rel", Direction = "OutgoingEdge", EdgePropertyFilter = "return (p) => p == \"knows\";" }
                }
            };
        }

        private void AssertSameJson(object value, string because)
        {
            // Serialize by runtime type, exactly as the ASP.NET Core output formatter does.
            var type = value.GetType();
            var reflection = JsonSerializer.Serialize(value, type, _reflectionWeb);
            var sourceGen = JsonSerializer.Serialize(value, type, _sourceGenWeb);
            Assert.AreEqual(reflection, sourceGen, because);
        }
    }
}
