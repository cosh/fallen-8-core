// MIT License
//
// AppJsonContext.cs
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

using System.Text.Json.Serialization;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core.App.Controllers.Model;

namespace NoSQL.GraphDB.App
{
    /// <summary>
    /// System.Text.Json source-generation context for the REST DTOs and the subgraph
    /// specification. It is inserted into the MVC JSON options' resolver chain so request/response
    /// (de)serialization uses generated metadata instead of runtime reflection, and it is used
    /// directly by the explicit <c>SubGraphSpecification</c> (de)serialization call sites.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The naming policy is set to camelCase to match the ASP.NET Core Web defaults that MVC uses,
    /// so the few DTOs without explicit <c>[JsonPropertyName]</c> attributes (for example
    /// <see cref="StatusREST"/> and <see cref="SampleStats"/>) emit identical property names. When
    /// the context is attached to MVC's own options via the resolver chain, MVC's options continue
    /// to drive naming, indentation and read behaviour; the generated metadata only provides the
    /// type shape. Output is therefore unchanged from the previous reflection-based behaviour.
    /// </para>
    /// <para>
    /// The abstract <c>AGraphElement</c> base is deliberately not registered: the only endpoint
    /// returning it (<c>GetGraphElement</c>) hands MVC a concrete <see cref="Vertex"/> or
    /// <see cref="Edge"/>, which the output formatter serializes by runtime type. Registering the
    /// concrete types (which include the inherited base properties) reproduces exactly that output
    /// without introducing a polymorphic <c>$type</c> discriminator or changing the OpenAPI schema.
    /// </para>
    /// </remarks>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(Graph))]
    [JsonSerializable(typeof(Vertex))]
    [JsonSerializable(typeof(Edge))]
    [JsonSerializable(typeof(VertexSpecification))]
    [JsonSerializable(typeof(EdgeSpecification))]
    [JsonSerializable(typeof(PropertySpecification))]
    [JsonSerializable(typeof(Property))]
    [JsonSerializable(typeof(StatusREST))]
    [JsonSerializable(typeof(SampleStats))]
    [JsonSerializable(typeof(SubGraphSpecification))]
    [JsonSerializable(typeof(SubGraphSummary))]
    [JsonSerializable(typeof(PatternSpecification))]
    [JsonSerializable(typeof(ScanSpecification))]
    [JsonSerializable(typeof(IndexScanSpecification))]
    [JsonSerializable(typeof(RangeIndexScanSpecification))]
    [JsonSerializable(typeof(FulltextIndexScanSpecification))]
    [JsonSerializable(typeof(FulltextSearchResultREST))]
    [JsonSerializable(typeof(FulltextSearchResultElementREST))]
    [JsonSerializable(typeof(SearchDistanceSpecification))]
    [JsonSerializable(typeof(PathSpecification))]
    [JsonSerializable(typeof(SemanticTraversalSpecification))]
    [JsonSerializable(typeof(EmbeddingWriteSpecification))]
    [JsonSerializable(typeof(ElementEmbeddingREST))]
    [JsonSerializable(typeof(PathREST))]
    [JsonSerializable(typeof(PathElementREST))]
    [JsonSerializable(typeof(PathFilterSpecification))]
    [JsonSerializable(typeof(PathCostSpecification))]
    [JsonSerializable(typeof(LiteralSpecification))]
    [JsonSerializable(typeof(PluginSpecification))]
    [JsonSerializable(typeof(IndexAddToSpecification))]
    [JsonSerializable(typeof(LoadSpecification))]
    [JsonSerializable(typeof(SaveSpecification))]
    [JsonSerializable(typeof(ResultTypeSpecification))]
    [JsonSerializable(typeof(ChangeEventREST))]
    [JsonSerializable(typeof(BulkImportResultREST))]
    [JsonSerializable(typeof(VectorIndexAddSpecification))]
    [JsonSerializable(typeof(VectorIndexScanSpecification))]
    [JsonSerializable(typeof(VectorSearchResultREST))]
    [JsonSerializable(typeof(VectorScoredElementREST))]
    [JsonSerializable(typeof(StoredQuerySpecification))]
    [JsonSerializable(typeof(StoredPathQueryBlock))]
    [JsonSerializable(typeof(StoredSubGraphQueryBlock))]
    [JsonSerializable(typeof(StoredQuerySummaryREST))]
    [JsonSerializable(typeof(StoredQueryDetailREST))]
    [JsonSerializable(typeof(ValidateDelegateSpecification))]
    [JsonSerializable(typeof(DelegateValidationREST))]
    [JsonSerializable(typeof(DelegateDiagnosticREST))]
    [JsonSerializable(typeof(BenchmarkResultREST))]
    [JsonSerializable(typeof(SaveGameREST))]
    [JsonSerializable(typeof(SaveGameKpisREST))]
    [JsonSerializable(typeof(IndexDescriptionREST))]
    [JsonSerializable(typeof(SaveGameRegistryDocument))]
    [JsonSerializable(typeof(System.Collections.Generic.Dictionary<string, string>))]
    [JsonSerializable(typeof(AnalyticsSpecification))]
    [JsonSerializable(typeof(AnalyticsResultREST))]
    [JsonSerializable(typeof(ScoredVertexREST))]
    [JsonSerializable(typeof(PartitionSummaryREST))]
    [JsonSerializable(typeof(PartitionMembersREST))]
    [JsonSerializable(typeof(WriteBackResultREST))]
    [JsonSerializable(typeof(GraphStatisticsREST))]
    [JsonSerializable(typeof(CardinalityStatsREST))]
    [JsonSerializable(typeof(NamedCountREST))]
    [JsonSerializable(typeof(DegreeStatsREST))]
    [JsonSerializable(typeof(IndexStatsREST))]
    [JsonSerializable(typeof(MemoryStatsREST))]
    public sealed partial class AppJsonContext : JsonSerializerContext
    {
    }
}
