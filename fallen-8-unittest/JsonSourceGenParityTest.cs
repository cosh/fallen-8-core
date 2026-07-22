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
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.App.Controllers.Model;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Serializer;
using NoSQL.GraphDB.Core.SubGraph;
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
                Indices = new List<IndexDescriptionREST>
                {
                    new IndexDescriptionREST { IndexId = "nameIndex", PluginType = "DictionaryIndex" }
                },
                AvailableIndexPlugins = new List<string> { "DictionaryIndex", "SpatialIndex" },
                AvailablePathPlugins = new List<string> { "Dijkstra" },
                AvailableAnalyticsPlugins = new List<string> { "PAGERANK", "WCC" },
                AvailableServicePlugins = new List<string>(),
                Embedding = new EmbeddingProviderStatsREST
                {
                    Enabled = true,
                    Backend = "Ollama",
                    ModelName = "bge-m3",
                    ModelVersion = "",
                    Dimension = 1024,
                    IntendedMetric = "Cosine",
                    Loaded = false
                }
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

        /// <summary>
        /// Data-driven sweep: every DTO registered in <see cref="AppJsonContext"/> must serialize
        /// identically under source generation and reflection. A durability guard then asserts that
        /// every registered <c>[JsonSerializable]</c> type is actually covered, so adding a new DTO
        /// to the context without a parity assertion fails this test.
        /// </summary>
        [TestMethod]
        public void AllRegisteredAppDtos_SourceGen_MatchReflection()
        {
            var representatives = RepresentativeAppDtos();

            // 1) Parity for each representative instance (serialized by runtime type, as MVC does).
            foreach (var (value, name) in representatives)
            {
                AssertSameJson(value, name);
            }

            // 2) Durability guard. Enumerate the [JsonSerializable(typeof(T))] declarations straight
            //    from metadata (ConstructorArguments, so it does not depend on the attribute exposing
            //    the type at runtime) and require every one to be covered somewhere.
            var registered = typeof(AppJsonContext)
                .GetCustomAttributesData()
                .Where(a => a.AttributeType == typeof(JsonSerializableAttribute))
                .Select(a => (Type)a.ConstructorArguments[0].Value)
                .ToHashSet();

            var covered = new HashSet<Type>(representatives.Select(r => r.Value.GetType()));
            // Graph/Vertex/Edge need live engine models -> GraphResponseDtos_SourceGen_MatchReflection.
            covered.Add(typeof(Graph));
            covered.Add(typeof(Vertex));
            covered.Add(typeof(Edge));
            // PathREST/PathElementREST need a computed Path -> PathDtos_SourceGen_MatchReflection.
            covered.Add(typeof(PathREST));
            covered.Add(typeof(PathElementREST));

            var uncovered = registered.Where(t => !covered.Contains(t)).OrderBy(t => t.Name).ToList();
            Assert.AreEqual(0, uncovered.Count,
                "AppJsonContext registers types without JSON source-gen parity coverage: " +
                string.Join(", ", uncovered.Select(t => t.Name)) +
                ". Add a representative instance to RepresentativeAppDtos (or cover it in a dedicated test).");

            // Keep the covered set honest: everything claimed must still be registered.
            var stale = covered.Where(t => !registered.Contains(t)).OrderBy(t => t.Name).ToList();
            Assert.AreEqual(0, stale.Count,
                "Parity coverage claims types no longer registered in AppJsonContext: " +
                string.Join(", ", stale.Select(t => t.Name)));
        }

        /// <summary>
        /// The path response DTOs carry only transfer constructors, so build them from a real
        /// engine-computed <see cref="Path"/> and prove source-gen parity for both.
        /// </summary>
        [TestMethod]
        public async Task PathDtos_SourceGen_MatchReflection()
        {
            await _controller.AddVertex(NewVertex("person", "name", "A"), true);
            await _controller.AddVertex(NewVertex("person", "name", "B"), true);

            var graph = _controller.GetGraph();
            var sourceId = graph.Vertices[0].Id;
            var targetId = graph.Vertices[1].Id;

            await _controller.AddEdge(new EdgeSpecification
            {
                CreationDate = 1713862800u,
                SourceVertex = sourceId,
                TargetVertex = targetId,
                EdgePropertyId = "knows",
                Label = "friendship"
            }, true);

            var definition = new ShortestPathDefinition
            {
                SourceVertexId = sourceId,
                DestinationVertexId = targetId,
                MaxDepth = 7,
                MaxResults = 1
            };
            var found = _fallen8.TryCalculateShortestPath(out var paths, "BLS", definition);
            Assert.IsTrue(found, "BLS should find a path from A to B.");
            Assert.IsTrue(paths != null && paths.Count > 0, "The BLS result must contain a path.");

            var pathRest = new PathREST(paths[0]);
            Assert.IsTrue(pathRest.PathElements.Count > 0, "The path must contain at least one element.");

            AssertSameJson(pathRest, "PathREST (BLS A->B)");
            AssertSameJson(pathRest.PathElements[0], "PathElementREST");
        }

        /// <summary>
        /// Engine (<c>CoreJsonContext</c>) parity for <see cref="SubGraphRecipe"/>: the source-gen
        /// serialization used by <c>PersistencyFactory</c> must equal the reflection baseline.
        /// </summary>
        [TestMethod]
        public void SubGraphRecipe_SourceGen_MatchesReflection()
        {
            var coreContext = ResolveCoreJsonContext();
            var recipe = new SubGraphRecipe
            {
                Name = "friends-of-alice",
                SubGraphId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                AlgorithmPluginName = "RecipeSubGraphAlgorithm",
                SourceFallen8Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                SpecificationJson = "{\"name\":\"friends-of-alice\"}"
            };

            // Source-gen: PersistencyFactory's path is JsonSerializer.Serialize(recipe,
            // CoreJsonContext.Default.SubGraphRecipe); the context's options carry the same (default)
            // settings and the generated metadata.
            var sourceGen = JsonSerializer.Serialize(recipe, typeof(SubGraphRecipe), coreContext.Options);
            // Reflection baseline: CoreJsonContext declares no options, so it carries default option
            // values; the baseline is therefore default options with a pure reflection resolver.
            var reflectionOptions = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
            var reflection = JsonSerializer.Serialize(recipe, typeof(SubGraphRecipe), reflectionOptions);

            Assert.AreEqual(reflection, sourceGen, "SubGraphRecipe source-gen vs reflection");
        }

        /// <summary>
        /// Engine (<c>CoreJsonContext</c>) parity for the internal <c>DelegateDescriptor</c>: the JSON
        /// that <see cref="DelegateJson"/> actually emits (source-gen resolver) must equal the
        /// reflection baseline for the same descriptor, both for a static method with a signature
        /// hash and for an instance method carrying a target spec.
        /// </summary>
        [TestMethod]
        public void DelegateDescriptor_SourceGen_MatchesReflection()
        {
            var coreContext = ResolveCoreJsonContext();
            var descriptorType = typeof(SubGraphRecipe).Assembly
                .GetType("NoSQL.GraphDB.Core.Serializer.DelegateDescriptor", throwOnError: true);

            var sourceGenOptions = BuildDelegateJsonOptions(coreContext);
            // Same option values as DelegateJson, but a pure reflection resolver for the baseline.
            var reflectionOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                TypeInfoResolver = new DefaultJsonTypeInfoResolver()
            };

            // Static method + signature hash: Target/Notes stay null, exercising WhenWritingNull.
            var staticJson = DelegateJson.Serialize((Func<int, int>)SampleStaticDelegateTarget, includeSignatureHash: true);
            AssertDescriptorParity(descriptorType, staticJson, sourceGenOptions, reflectionOptions, "static+hash");

            // Instance method + target spec: the nested DelegateTargetSpec object is present.
            var targetSpec = new DelegateTargetSpec
            {
                FactoryTypeAssemblyQualifiedName = typeof(string).AssemblyQualifiedName,
                JsonArgs = "{\"k\":1}"
            };
            var instanceJson = DelegateJson.Serialize((Func<string>)SampleInstanceDelegateTarget, targetSpec);
            AssertDescriptorParity(descriptorType, instanceJson, sourceGenOptions, reflectionOptions, "instance+target");
        }

        private static void AssertDescriptorParity(Type descriptorType, string producedJson,
            JsonSerializerOptions sourceGenOptions, JsonSerializerOptions reflectionOptions, string because)
        {
            var descriptor = JsonSerializer.Deserialize(producedJson, descriptorType, sourceGenOptions);
            Assert.IsNotNull(descriptor, "descriptor round-trip (" + because + ")");

            var sourceGen = JsonSerializer.Serialize(descriptor, descriptorType, sourceGenOptions);
            var reflection = JsonSerializer.Serialize(descriptor, descriptorType, reflectionOptions);

            Assert.AreEqual(reflection, sourceGen, "DelegateDescriptor source-gen vs reflection (" + because + ")");
            // The production DelegateJson output itself must equal the reflection baseline.
            Assert.AreEqual(reflection, producedJson, "DelegateJson output vs reflection baseline (" + because + ")");
        }

        /// <summary>
        /// Reaches the engine's <c>CoreJsonContext</c> singleton by reflection. The engine keeps the
        /// context (and <c>DelegateDescriptor</c>) internal by design and declares no
        /// <c>InternalsVisibleTo</c>, so the test reflects rather than widening that visibility.
        /// </summary>
        private static JsonSerializerContext ResolveCoreJsonContext()
        {
            var contextType = typeof(SubGraphRecipe).Assembly
                .GetType("NoSQL.GraphDB.Core.Serializer.CoreJsonContext", throwOnError: true);
            var defaultProperty = contextType.GetProperty("Default",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(defaultProperty, "CoreJsonContext.Default was not found (renamed?).");
            return (JsonSerializerContext)defaultProperty.GetValue(null);
        }

        /// <summary>
        /// Mirrors <c>DelegateJson.CreateJsonOptions</c> (camelCase, indented, ignore nulls) with the
        /// engine's source-gen context as the resolver.
        /// </summary>
        private static JsonSerializerOptions BuildDelegateJsonOptions(JsonSerializerContext coreContext)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            options.TypeInfoResolverChain.Insert(0, coreContext);
            return options;
        }

        private static int SampleStaticDelegateTarget(int x) => x + 1;

        private string SampleInstanceDelegateTarget() => "instance-" + GetType().Name;

        /// <summary>
        /// One representative, fully-populated instance for each directly-constructible DTO
        /// registered in <see cref="AppJsonContext"/>. Graph/Vertex/Edge (need engine models) and
        /// PathREST/PathElementREST (need a computed path) are covered by dedicated tests instead.
        /// </summary>
        private static List<(object Value, string Name)> RepresentativeAppDtos()
        {
            return new List<(object, string)>
            {
                (new VertexSpecification
                {
                    Label = "person",
                    CreationDate = 1713862800u,
                    Properties = new List<PropertySpecification>
                    {
                        new PropertySpecification { PropertyId = "name", PropertyValue = "John Doe", FullQualifiedTypeName = "System.String" }
                    }
                }, "VertexSpecification"),
                (new EdgeSpecification
                {
                    CreationDate = 1713862800u,
                    SourceVertex = 1,
                    TargetVertex = 2,
                    EdgePropertyId = "knows",
                    Label = "friendship",
                    Properties = new List<PropertySpecification>
                    {
                        new PropertySpecification { PropertyId = "since", PropertyValue = "2024", FullQualifiedTypeName = "System.String" }
                    }
                }, "EdgeSpecification"),
                (new PropertySpecification { PropertyId = "age", PropertyValue = "42", FullQualifiedTypeName = "System.Int32" }, "PropertySpecification"),
                (new Property { PropertyId = "name", PropertyValue = "John Doe" }, "Property"),
                (new StatusREST
                {
                    UsedMemory = 1073741824L,
                    VertexCount = 10000,
                    EdgeCount = 25000,
                    Indices = new List<IndexDescriptionREST>
                    {
                        new IndexDescriptionREST { IndexId = "nameIndex", PluginType = "DictionaryIndex" }
                    },
                    AvailableIndexPlugins = new List<string> { "DictionaryIndex", "SpatialIndex" },
                    AvailablePathPlugins = new List<string> { "BLS", "DIJKSTRA" },
                    AvailableAnalyticsPlugins = new List<string> { "PAGERANK", "WCC" },
                    AvailableServicePlugins = new List<string>()
                }, "StatusREST"),
                (new SampleStats { VertexCount = 3, EdgeCount = 7 }, "SampleStats"),
                (SampleSpecification(), "SubGraphSpecification"),
                (new SubGraphSummary
                {
                    Name = "friends-of-alice",
                    VertexCount = 2,
                    EdgeCount = 1,
                    AlgorithmPluginName = "RecipeSubGraphAlgorithm",
                    SourceFallen8Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    CanRecalculate = true,
                    AdditionalInformation = new Dictionary<string, string> { { "owner", "alice" } }
                }, "SubGraphSummary"),
                (new PatternSpecification
                {
                    Type = "Edge",
                    PatternName = "rel",
                    Direction = "OutgoingEdge",
                    EdgePropertyFilter = "return (p) => p == \"knows\";",
                    MinLength = 1,
                    MaxLength = 3
                }, "PatternSpecification"),
                (new ScanSpecification
                {
                    Operator = BinaryOperator.Equals,
                    Literal = new LiteralSpecification { Value = "John Doe", FullQualifiedTypeName = "System.String" },
                    Label = "person",
                    ResultType = ResultTypeSpecification.Both
                }, "ScanSpecification"),
                (new IndexScanSpecification
                {
                    IndexId = "idx-name",
                    Operator = BinaryOperator.Greater,
                    Literal = new LiteralSpecification { Value = "10", FullQualifiedTypeName = "System.Int32" },
                    Label = "age",
                    ResultType = ResultTypeSpecification.Vertices
                }, "IndexScanSpecification"),
                (new RangeIndexScanSpecification
                {
                    IndexId = "idx-age",
                    LeftLimit = "1",
                    RightLimit = "100",
                    FullQualifiedTypeName = "System.Int32",
                    IncludeLeft = true,
                    IncludeRight = false,
                    ResultType = ResultTypeSpecification.Edges
                }, "RangeIndexScanSpecification"),
                (new FulltextIndexScanSpecification { IndexId = "ft-idx", RequestString = "graph database" }, "FulltextIndexScanSpecification"),
                (BuildFulltextSearchResult(), "FulltextSearchResultREST"),
                (BuildFulltextSearchResultElement(), "FulltextSearchResultElementREST"),
                (new SearchDistanceSpecification { IndexId = "spatial", GraphElementId = 5, Distance = 2.5f }, "SearchDistanceSpecification"),
                (new PathSpecification
                {
                    PathAlgorithmName = "DIJKSTRA",
                    MaxDepth = 5,
                    MaxResults = 10,
                    MaxPathWeight = 100.0,
                    Filter = new PathFilterSpecification(),
                    Cost = new PathCostSpecification(),
                    Semantic = new SemanticTraversalSpecification
                    {
                        QueryVector = new[] { 0.1f, -0.5f },
                        EmbeddingName = "default",
                        Metric = "Cosine",
                        MinScore = 0.7,
                        CostBySimilarity = true
                    }
                }, "PathSpecification"),
                (new PathFilterSpecification(), "PathFilterSpecification"),
                (new PathCostSpecification(), "PathCostSpecification"),
                (new SemanticTraversalSpecification
                {
                    QueryVector = new[] { 0.25f, 0.75f, -1f },
                    EmbeddingName = "title",
                    Metric = "L2",
                    MinScore = null, // exercises null-writing
                    CostBySimilarity = false
                }, "SemanticTraversalSpecification"),
                (new EmbeddingWriteSpecification
                {
                    Vector = new[] { 0.12f, -0.5f, 0.33f }
                }, "EmbeddingWriteSpecification"),
                (new ElementEmbeddingREST
                {
                    Name = "default",
                    Vector = new[] { 0.12f, -0.5f },
                    Model = "bge-micro-v2#384#Cosine"
                }, "ElementEmbeddingREST"),
                (new EmbedElementSpecification
                {
                    GraphElementId = 42,
                    Text = "a red bicycle",
                    Name = "default"
                }, "EmbedElementSpecification"),
                (new EmbedElementItem
                {
                    GraphElementId = 7,
                    Text = "one"
                }, "EmbedElementItem"),
                (new EmbedElementsSpecification
                {
                    Name = "default",
                    Items = new List<EmbedElementItem>
                    {
                        new EmbedElementItem { GraphElementId = 1, Text = "one" }
                    }
                }, "EmbedElementsSpecification"),
                (new EmbeddingSearchSpecification
                {
                    IndexId = "embeddings",
                    Text = "red bicycles",
                    K = 10,
                    Kind = "vertex",
                    Label = null // exercises null-writing
                }, "EmbeddingSearchSpecification"),
                (new EmbedTextSpecification
                {
                    Texts = new List<string> { "one", "two" }
                }, "EmbedTextSpecification"),
                (new EmbeddingVectorsREST
                {
                    Model = "bge-micro-v2#384#Cosine",
                    Dimension = 2,
                    Vectors = new List<float[]> { new[] { 0.1f, 0.2f } }
                }, "EmbeddingVectorsREST"),
                (new EmbeddingProviderStatsREST
                {
                    Enabled = true,
                    Backend = "Onnx",
                    ModelName = "bge-micro-v2",
                    ModelVersion = "",
                    Dimension = 384,
                    IntendedMetric = "Cosine",
                    Loaded = false
                }, "EmbeddingProviderStatsREST"),
                (new VectorIndexAddSpecification
                {
                    GraphElementId = 42,
                    Vector = new[] { 0.12f, -0.5f, 0.33f }
                }, "VectorIndexAddSpecification (explicit mode)"),
                (new VectorIndexAddSpecification
                {
                    GraphElementId = 42,
                    PropertyId = "embedding"
                }, "VectorIndexAddSpecification (property mode)"),
                (new VectorIndexScanSpecification
                {
                    IndexId = "myEmbeddings",
                    Query = new[] { 0.1f, 0.2f, 0.3f },
                    K = 10,
                    Kind = "vertex",
                    Label = "person"
                }, "VectorIndexScanSpecification"),
                (new VectorSearchResultREST
                {
                    Metric = "Cosine",
                    HigherIsBetter = true,
                    Results = new List<VectorScoredElementREST>
                    {
                        new VectorScoredElementREST { GraphElementId = 7, Score = 0.93f }
                    }
                }, "VectorSearchResultREST"),
                (new VectorScoredElementREST { GraphElementId = 7, Score = 0.93f }, "VectorScoredElementREST"),
                (new BulkImportResultREST
                {
                    VerticesCreated = 10000,
                    EdgesCreated = 25000,
                    LinesRead = 35001
                }, "BulkImportResultREST"),
                (new ChangeEventREST
                {
                    Seq = 4712,
                    Ts = new DateTime(2026, 7, 15, 12, 34, 56, 789, DateTimeKind.Utc),
                    Kind = "propertySet",
                    Element = "vertex",
                    Id = 42,
                    Label = "person",
                    Key = "name"
                }, "ChangeEventREST"),
                (new ChangeEventREST
                {
                    Seq = 4713,
                    Ts = new DateTime(2026, 7, 15, 12, 34, 56, 789, DateTimeKind.Utc),
                    Kind = "resync",
                    Reason = "trim"
                }, "ChangeEventREST (resync, null-omitting fields)"),
                (new StoredQuerySpecification
                {
                    Name = "adults-shortest",
                    Kind = "Path",
                    Description = "age>30 vertices",
                    Path = new StoredPathQueryBlock
                    {
                        Filter = new PathFilterSpecification(),
                        Cost = new PathCostSpecification()
                    }
                }, "StoredQuerySpecification"),
                (new StoredPathQueryBlock
                {
                    Filter = new PathFilterSpecification(),
                    Cost = new PathCostSpecification()
                }, "StoredPathQueryBlock"),
                (new StoredSubGraphQueryBlock
                {
                    VertexFilter = "return (v) => v.Label == \"person\";",
                    EdgeFilter = "return (e) => e.Label == \"knows\";",
                    Patterns = new List<PatternSpecification>
                    {
                        new PatternSpecification { Type = "Vertex", PatternName = "start" }
                    }
                }, "StoredSubGraphQueryBlock"),
                (new StoredQuerySummaryREST
                {
                    Name = "adults-shortest",
                    Kind = "Path",
                    Description = "age>30 vertices",
                    CreatedAt = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc),
                    CompileState = "Compiled"
                }, "StoredQuerySummaryREST"),
                (new StoredQueryDetailREST
                {
                    Name = "person-net",
                    Kind = "SubGraph",
                    CreatedAt = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc),
                    CompileState = "Failed",
                    SpecificationJson = "{\"vertexFilter\":\"return (ge) => true;\"}",
                    CompileDiagnostics = "ID: CS1002, Message: ; expected"
                }, "StoredQueryDetailREST"),
                (new LiteralSpecification { Value = "John Doe", FullQualifiedTypeName = "System.String" }, "LiteralSpecification"),
                (new PluginSpecification
                {
                    UniqueId = "indexService1",
                    PluginType = "DictionaryIndex",
                    PluginOptions = new Dictionary<string, PropertySpecification>
                    {
                        { "cacheSize", new PropertySpecification { PropertyId = "cacheSize", PropertyValue = "1000", FullQualifiedTypeName = "System.Int32" } }
                    }
                }, "PluginSpecification"),
                (new IndexAddToSpecification
                {
                    GraphElementId = 7,
                    Key = new PropertySpecification { PropertyId = "name", PropertyValue = "John", FullQualifiedTypeName = "System.String" }
                }, "IndexAddToSpecification"),
                (new LoadSpecification { StartServices = true, SaveGameLocation = "C:/Fallen8/database.f8s" }, "LoadSpecification"),
                (new SaveSpecification { SaveGameLocation = "C:/Fallen8/database.f8s", SavePartitions = 8 }, "SaveSpecification"),
                (ResultTypeSpecification.Both, "ResultTypeSpecification"),
                (new ValidateDelegateSpecification
                {
                    DelegateKind = "VertexFilter",
                    Fragment = "return (v) => v.Label == \"person\";"
                }, "ValidateDelegateSpecification"),
                (new DelegateValidationREST
                {
                    Valid = false,
                    Diagnostics = new List<DelegateDiagnosticREST>
                    {
                        new DelegateDiagnosticREST
                        {
                            Line = 1,
                            Column = 17,
                            EndLine = 1,
                            EndColumn = 21,
                            Id = "CS1061",
                            Message = "no such member",
                            Severity = "error"
                        }
                    }
                }, "DelegateValidationREST"),
                (new DelegateDiagnosticREST
                {
                    Line = 2,
                    Column = 26,
                    EndLine = 2,
                    EndColumn = 29,
                    Id = "CS0103",
                    Message = "The name 'zzz' does not exist in the current context",
                    Severity = "error"
                }, "DelegateDiagnosticREST"),
                (new BenchmarkResultREST
                {
                    Iterations = 10,
                    EdgesTraversed = 10_001_000,
                    AverageTps = 592134058.33,
                    MedianTps = 601225777.44,
                    StandardDeviationTps = 60311324.76
                }, "BenchmarkResultREST"),
                (new IndexDescriptionREST
                {
                    IndexId = "embeddings",
                    PluginType = "VectorIndex",
                    EmbeddingName = "default",
                    Model = "bge-micro-v2#384#Cosine"
                }, "IndexDescriptionREST"),
                (new SaveGameKpisREST
                {
                    VertexCount = 1000,
                    EdgeCount = 10000,
                    UsedMemoryBytes = 282016350,
                    Indices = new List<IndexDescriptionREST>
                    {
                        new IndexDescriptionREST { IndexId = "myIndex", PluginType = "DictionaryIndex" }
                    },
                    AvailableIndexPlugins = new List<string> { "DictionaryIndex" },
                    AvailablePathPlugins = new List<string> { "BLS", "DIJKSTRA" },
                    AvailableServicePlugins = new List<string>(),
                    SubGraphs = new List<string> { "friends-of-trent" }
                }, "SaveGameKpisREST"),
                (new SaveGameREST
                {
                    Id = "sg-20260715-093012-4f2a",
                    SavedAt = "2026-07-15T09:30:12.412Z",
                    Trigger = "api",
                    Location = "C:/Fallen8/database.f8s",
                    FileCount = 9,
                    TotalBytes = 73400320,
                    EngineVersion = "0.9.3.0",
                    Kpis = new SaveGameKpisREST { VertexCount = 1000, EdgeCount = 10000 }
                }, "SaveGameREST"),
                (new SaveGameRegistryDocument
                {
                    SchemaVersion = 1,
                    SaveGames = new List<SaveGameREST>
                    {
                        new SaveGameREST
                        {
                            Id = "sg-20260715-093012-4f2a",
                            SavedAt = "2026-07-15T09:30:12.412Z",
                            Trigger = "api",
                            Location = "C:/Fallen8/database.f8s",
                            FileCount = 9,
                            TotalBytes = 73400320,
                            EngineVersion = "0.9.3.0"
                        }
                    }
                }, "SaveGameRegistryDocument"),
                (new Dictionary<string, string>
                {
                    { "PAGERANK", "PageRank via power iteration" },
                    { "WCC", "Weakly connected components" }
                }, "Dictionary<String,String> (analytics algorithms listing)"),
                (new AnalyticsSpecification
                {
                    VertexLabel = "person",
                    EdgePropertyId = "knows",
                    Direction = "out",
                    MaxIterations = 100,
                    Epsilon = 1e-6,
                    TimeBudgetSeconds = 30,
                    Parameters = new Dictionary<string, double> { { "DampingFactor", 0.85 } },
                    MaxResults = 10,
                    WriteBack = true,
                    WriteBackPropertyKey = "analytics.pagerank"
                }, "AnalyticsSpecification"),
                (new AnalyticsResultREST
                {
                    Algorithm = "PAGERANK",
                    Converged = true,
                    IterationsRun = 23,
                    ElapsedMs = 184.2,
                    BudgetExhausted = false,
                    VertexCount = 4,
                    Statistics = new Dictionary<string, double> { { "ComponentCount", 2d } },
                    Results = new List<ScoredVertexREST>
                    {
                        new ScoredVertexREST { GraphElementId = 7, Score = 0.25 }
                    },
                    Partitions = new List<PartitionSummaryREST>
                    {
                        new PartitionSummaryREST { PartitionId = 0, Size = 42 }
                    },
                    WriteBack = new WriteBackResultREST
                    {
                        PropertyKey = "analytics.pagerank",
                        VerticesWritten = 4,
                        Chunks = 1
                    }
                }, "AnalyticsResultREST"),
                (new ScoredVertexREST { GraphElementId = 7, Score = 0.25 }, "ScoredVertexREST"),
                (new PartitionSummaryREST { PartitionId = 0, Size = 42 }, "PartitionSummaryREST"),
                (new PartitionMembersREST
                {
                    PartitionId = 0,
                    Size = 42,
                    Offset = 10,
                    Members = new List<int> { 10, 11, 12 }
                }, "PartitionMembersREST"),
                (new WriteBackResultREST
                {
                    PropertyKey = "analytics.wcc",
                    VerticesWritten = 42,
                    Chunks = 1
                }, "WriteBackResultREST"),
                (new NamedCountREST { Name = "person", Count = 1200 }, "NamedCountREST"),
                (new CardinalityStatsREST
                {
                    DistinctTotal = 17,
                    Top = new List<NamedCountREST> { new NamedCountREST { Name = "person", Count = 1200 } }
                }, "CardinalityStatsREST"),
                (new DegreeStatsREST { Min = 0, Max = 420, Mean = 3.7, P50 = 2, P90 = 9, P99 = 40 }, "DegreeStatsREST"),
                (new IndexStatsREST { Name = "byName", Type = "DictionaryIndex", Keys = 1000, Values = 1200 }, "IndexStatsREST"),
                (new MemoryStatsREST
                {
                    ProcessWorkingSetBytes = 1073741824L,
                    GcHeapBytes = 805306368L,
                    GcLastHeapSizeBytes = 805306368L,
                    GcFragmentedBytes = 52428800L
                }, "MemoryStatsREST"),
                (new GraphStatisticsREST
                {
                    VertexCount = 4,
                    EdgeCount = 2,
                    VertexLabels = new CardinalityStatsREST
                    {
                        DistinctTotal = 2,
                        Top = new List<NamedCountREST> { new NamedCountREST { Name = "person", Count = 3 } }
                    },
                    EdgeLabels = new CardinalityStatsREST { DistinctTotal = 1, Top = new List<NamedCountREST>() },
                    InDegree = new DegreeStatsREST { Max = 1, Mean = 0.5 },
                    OutDegree = new DegreeStatsREST { Max = 2, Mean = 0.5 },
                    TotalDegree = new DegreeStatsREST { Max = 2, Mean = 1 },
                    PropertyKeys = new CardinalityStatsREST { DistinctTotal = 1, Top = new List<NamedCountREST>() },
                    Indices = new List<IndexStatsREST>
                    {
                        new IndexStatsREST { Name = "byName", Type = "DictionaryIndex", Keys = 1, Values = 1 }
                    },
                    Memory = new MemoryStatsREST { ProcessWorkingSetBytes = 1073741824L, GcHeapBytes = 805306368L },
                    ComputedInMs = 1.4,
                    Sampled = false,
                    SampleStride = 1
                }, "GraphStatisticsREST")
            };
        }

        private static FulltextSearchResultREST BuildFulltextSearchResult()
        {
            // The transfer constructor tolerates a null engine result; populate via the public setters.
            return new FulltextSearchResultREST(null)
            {
                MaximumScore = 0.87,
                Elements = new List<FulltextSearchResultElementREST> { BuildFulltextSearchResultElement() }
            };
        }

        private static FulltextSearchResultElementREST BuildFulltextSearchResultElement()
        {
            return new FulltextSearchResultElementREST(null)
            {
                GraphElementId = 123,
                Score = 0.87,
                Highlights = new List<string> { "a <em>graph</em> <em>database</em> doc" }
            };
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
                VertexFilter = "return (v) => v.Label == \"person\";",
                EdgeFilter = null, // exercises default null-writing
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification { Type = "Vertex", PatternName = "start", VertexFilter = "return (v) => true;" },
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
