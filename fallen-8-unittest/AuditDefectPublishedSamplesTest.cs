// MIT License
//
// AuditDefectPublishedSamplesTest.cs
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
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core.Expression;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// A request sample that ships in the API reference must be a body the very endpoint that
    /// publishes it accepts. This pins audit defects B05 (the <c>PUT /vertex</c> sample sent
    /// <c>properties</c> as an object map and <c>creationDate</c> as an ISO string, while the DTO
    /// takes an array of property specifications and a Unix-seconds number) and B06 (every scan
    /// sample and example said <c>"operator": "Equal"</c>, which is neither the wire form - an
    /// integer code - nor a member of <see cref="BinaryOperator"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The samples live in controller XML <c>&lt;remarks&gt;</c>, and C# cannot interpolate a
    /// constant into a doc comment, so there is no way to share one literal between the doc and a
    /// test. The test therefore reads the sample TEXT back out of the served OpenAPI document and
    /// deserializes it into the action's request type: the assertion is made against the published
    /// bytes, so a sample cannot drift away from its DTO again.
    /// </para>
    /// <para>
    /// Binding uses MVC's own request settings (<see cref="JsonSerializerDefaults.Web"/>) plus
    /// <see cref="JsonUnmappedMemberHandling.Disallow"/>: succeeding while silently dropping a
    /// misspelled field would not prove a copy-paste works.
    /// </para>
    /// <para>
    /// Only the SERVED document is read, never the pinned snapshot: the snapshot is regenerated
    /// (pwsh scripts/update-openapi-snapshot.ps1) as a separate step, and this test must be true
    /// about the code the moment the code changes.
    /// </para>
    /// </remarks>
    [TestClass]
    public class AuditDefectPublishedSamplesTest
    {
        private const String DocumentPath = "/openapi/v0.1.json";
        private const String SampleMarker = "Sample request:";

        /// <summary>
        /// Exactly what a pasted body meets on the wire: camelCase, case-insensitive and
        /// numbers-from-strings (the Web defaults MVC uses), and no tolerance for a field the DTO
        /// does not declare.
        /// </summary>
        private static readonly JsonSerializerOptions WireOptions =
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

        /// <summary>
        /// Every operation whose <c>&lt;remarks&gt;</c> publishes a request body sample in
        /// GraphController.Vertex.cs / GraphController.Scan.cs, with the CLR type MVC binds that
        /// body to. Each is checked under its bare path AND its /ns/{ns} route twin, because the
        /// document carries the description twice (feature graph-namespaces).
        /// </summary>
        private static readonly (String Method, String Path, Type Dto)[] PublishedSamples =
        {
            ("put", "/vertex", typeof(VertexSpecification)),
            ("post", "/scan/graph/property/{propertyId}", typeof(ScanSpecification)),
            ("post", "/scan/graph/properties", typeof(PropertySearchSpecification)),
            ("post", "/scan/index/all", typeof(IndexScanSpecification)),
            ("post", "/scan/index/range", typeof(RangeIndexScanSpecification)),
            ("post", "/scan/index/fulltext", typeof(FulltextIndexScanSpecification)),
            ("post", "/scan/index/vector", typeof(VectorIndexScanSpecification)),
            ("post", "/scan/index/spatial", typeof(SearchDistanceSpecification))
        };

        /// <summary>
        /// Boots the real app in Development (only there are /openapi and Scalar mapped) with a
        /// volatile engine, so generating the document writes no checkpoint or WAL.
        /// </summary>
        private sealed class DevelopmentApiFactory : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
            }
        }

        private static async Task<JsonDocument> ServedDocument(DevelopmentApiFactory factory)
        {
            using var client = factory.CreateClient();
            using var response = await client.GetAsync(DocumentPath);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "The framework must serve the OpenAPI document at " + DocumentPath + " in Development.");
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }

        #region the samples must bind

        [TestMethod]
        public async Task EveryPublishedSample_BindsToTheRequestTypeOfItsOperation()
        {
            using var factory = new DevelopmentApiFactory();
            using var document = await ServedDocument(factory);

            var failures = new List<String>();
            foreach (var sample in PublishedSamples)
            {
                foreach (var path in new[] { sample.Path, "/ns/{ns}" + sample.Path })
                {
                    var body = SampleBody(document, sample.Method, path);
                    try
                    {
                        var bound = JsonSerializer.Deserialize(body, sample.Dto, WireOptions);
                        Assert.IsNotNull(bound,
                            "The sample for " + sample.Method.ToUpperInvariant() + " " + path +
                            " deserialized to null.");
                    }
                    catch (JsonException ex)
                    {
                        failures.Add(sample.Method.ToUpperInvariant() + " " + path + " -> " +
                            sample.Dto.Name + ": " + ex.Message);
                    }
                }
            }

            Assert.AreEqual(0, failures.Count,
                "A published request sample must be a body its own endpoint accepts (audit defects " +
                "B05, B06). Rejected:\n" + String.Join("\n", failures));
        }

        [TestMethod]
        public async Task AddVertexSample_BindsWithAPropertyArrayAndANumericCreationDate()
        {
            // B05: the sample used to send an object MAP of properties and an ISO creationDate, so
            // both fields failed binding. Deserializing is not enough here - assert the sample
            // still carries the fields it is meant to demonstrate, so "fixing" it by deleting them
            // would fail too.
            using var factory = new DevelopmentApiFactory();
            using var document = await ServedDocument(factory);

            var body = SampleBody(document, "put", "/vertex");
            using (var parsed = JsonDocument.Parse(body))
            {
                Assert.AreEqual(JsonValueKind.Array, parsed.RootElement.GetProperty("properties").ValueKind,
                    "properties is a List<PropertySpecification>, so the sample must show a JSON ARRAY.");
                Assert.AreEqual(JsonValueKind.Number, parsed.RootElement.GetProperty("creationDate").ValueKind,
                    "creationDate is a UInt32 Unix timestamp, so the sample must show a NUMBER.");
            }

            var definition = JsonSerializer.Deserialize<VertexSpecification>(body, WireOptions);
            Assert.IsNotNull(definition, "The vertex sample must bind.");
            Assert.AreEqual("person", definition.Label, "The sample's label must survive binding.");
            Assert.IsTrue(definition.CreationDate > 0u,
                "The sample must demonstrate a real Unix-seconds creationDate, not the default 0.");
            Assert.IsNotNull(definition.Properties, "The sample must demonstrate at least one property.");
            Assert.IsTrue(definition.Properties.Count > 0, "The sample must demonstrate at least one property.");

            foreach (var property in definition.Properties)
            {
                // Each entry carries its OWN propertyId - that is the difference from the old map
                // shape, where the key was the property name and propertyId was absent.
                Assert.IsFalse(String.IsNullOrWhiteSpace(property.PropertyId),
                    "Every sampled property must name its propertyId.");
                Assert.IsFalse(String.IsNullOrWhiteSpace(property.FullQualifiedTypeName),
                    "Every sampled property must name its fullQualifiedTypeName.");
                Assert.IsNotNull(Type.GetType(property.FullQualifiedTypeName),
                    "The sampled type name must resolve: " + property.FullQualifiedTypeName);
                Assert.IsNotNull(property.PropertyValue,
                    "propertyValue is the value's TEXT form, so the sample must show a JSON string.");
            }
        }

        [TestMethod]
        public async Task ScanSamples_CarryAnOperatorCodeThatBindsToARealMember()
        {
            // B06: both scan samples said "operator": "Equal" - a string where an integer travels,
            // and not even a member name (the member is Equals).
            using var factory = new DevelopmentApiFactory();
            using var document = await ServedDocument(factory);

            var graphScan = JsonSerializer.Deserialize<ScanSpecification>(
                SampleBody(document, "post", "/scan/graph/property/{propertyId}"), WireOptions);
            Assert.IsNotNull(graphScan, "The graph-scan sample must bind.");
            Assert.AreEqual(BinaryOperator.Equals, graphScan.Operator,
                "The graph-scan sample demonstrates an equality scan, so it must send code 0.");
            Assert.IsNotNull(graphScan.Literal, "The graph-scan sample must carry its literal.");
            Assert.AreEqual("John Doe", graphScan.Literal.Value, "The sample's literal must survive binding.");
            Assert.AreEqual(ResultTypeSpecification.Vertices, graphScan.ResultType,
                "resultType, unlike operator, DOES travel as its name - the sample must keep the string.");

            var indexScan = JsonSerializer.Deserialize<IndexScanSpecification>(
                SampleBody(document, "post", "/scan/index/all"), WireOptions);
            Assert.IsNotNull(indexScan, "The index-scan sample must bind.");
            Assert.AreEqual(BinaryOperator.Equals, indexScan.Operator,
                "The index-scan sample demonstrates an equality scan, so it must send code 0.");
            Assert.IsFalse(String.IsNullOrWhiteSpace(indexScan.IndexId),
                "The index-scan sample must name an index.");
        }

        [TestMethod]
        public async Task ScanSchemas_PublishAnOperatorExampleAClientCanSend()
        {
            // The schema for BinaryOperator is a bare {"type":"integer"} with no member names, so
            // the property example is the only place in the document a client can learn the shape.
            using var factory = new DevelopmentApiFactory();
            using var document = await ServedDocument(factory);

            var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

            // The property example has ONE source (ScanSpecification.Operator) and is inherited by
            // the derived index-scan schema, so both published copies are checked.
            foreach (var schemaName in new[] { "ScanSpecification", "IndexScanSpecification" })
            {
                Assert.IsTrue(schemas.TryGetProperty(schemaName, out var schema),
                    "The document must describe " + schemaName + ".");
                var operatorSchema = schema.GetProperty("properties").GetProperty("operator");

                AssertSendableOperator(SingleExample(operatorSchema),
                    schemaName + ".operator's published example");

                // The mapping has to be reachable from the document itself, since the referenced
                // enum schema carries no names.
                var description = operatorSchema.GetProperty("description").GetString();
                foreach (var name in Enum.GetNames<BinaryOperator>())
                {
                    StringAssert.Contains(description, name,
                        "The operator description is the one home for the code mapping, so it must " +
                        "name every member (missing: " + name + ").");
                }
            }

            // The whole-body example on ScanSpecification is a second published sample of the same
            // field. (IndexScanSpecification declares its own body example in
            // Controllers/Model/IndexScanSpecification.cs, outside this change's scope.)
            var bodyExample = schemas.GetProperty("ScanSpecification").GetProperty("example");
            AssertSendableOperator(bodyExample.GetProperty("operator"),
                "ScanSpecification's published body example");
        }

        #endregion

        #region why the samples say 0

        [TestMethod]
        public void BinaryOperator_TravelsAsItsIntegerCode_ExactlyAsDocumented()
        {
            // Pins the mapping the operator description now spells out. Reordering the enum members
            // would silently repoint every documented code, so the codes are asserted, not the names.
            Assert.AreEqual(0, (Int32)BinaryOperator.Equals, "0 must stay Equals.");
            Assert.AreEqual(1, (Int32)BinaryOperator.Greater, "1 must stay Greater.");
            Assert.AreEqual(2, (Int32)BinaryOperator.GreaterOrEquals, "2 must stay GreaterOrEquals.");
            Assert.AreEqual(3, (Int32)BinaryOperator.Lower, "3 must stay Lower.");
            Assert.AreEqual(4, (Int32)BinaryOperator.LowerOrEquals, "4 must stay LowerOrEquals.");
            Assert.AreEqual(5, (Int32)BinaryOperator.NotEquals, "5 must stay NotEquals.");
            Assert.AreEqual(6, Enum.GetValues<BinaryOperator>().Length,
                "A new member must be added to the documented mapping too.");

            Assert.AreEqual(BinaryOperator.Equals,
                JsonSerializer.Deserialize<BinaryOperator>("0", WireOptions),
                "The integer code is the wire form.");
            Assert.AreEqual(BinaryOperator.NotEquals,
                JsonSerializer.Deserialize<BinaryOperator>("5", WireOptions),
                "The integer code is the wire form.");

            // The value that used to be published. It is not a member name and not a number, so it
            // fails under every serializer configuration - which is exactly why it 400d.
            Assert.ThrowsException<JsonException>(
                () => JsonSerializer.Deserialize<BinaryOperator>("\"Equal\"", WireOptions),
                "\"Equal\" is not a BinaryOperator, so it must never be published as an example.");
        }

        #endregion

        /// <summary>
        /// Pulls the sample request body out of an operation's published description: the first
        /// brace-balanced object after the "Sample request:" marker. Anchoring on the marker matters,
        /// because a description's prose can mention a route template such as
        /// <c>/scan/graph/property/{propertyId}</c> before the sample starts.
        /// </summary>
        private static String SampleBody(JsonDocument document, String method, String path)
        {
            Assert.IsTrue(document.RootElement.GetProperty("paths").TryGetProperty(path, out var pathItem),
                "The document must describe the path " + path + ".");
            Assert.IsTrue(pathItem.TryGetProperty(method, out var operation),
                "The document must describe " + method.ToUpperInvariant() + " " + path + ".");
            Assert.IsTrue(operation.TryGetProperty("description", out var descriptionElement) &&
                descriptionElement.ValueKind == JsonValueKind.String,
                method.ToUpperInvariant() + " " + path + " must publish its <remarks> as a description.");

            var description = descriptionElement.GetString();
            var marker = description.IndexOf(SampleMarker, StringComparison.Ordinal);
            Assert.IsTrue(marker >= 0,
                method.ToUpperInvariant() + " " + path + " must publish a '" + SampleMarker + "' block.");

            var open = description.IndexOf('{', marker);
            Assert.IsTrue(open >= 0,
                method.ToUpperInvariant() + " " + path + "'s sample block must contain a JSON object.");

            var close = MatchingBrace(description, open);
            Assert.IsTrue(close > open,
                method.ToUpperInvariant() + " " + path + "'s sample object is not brace-balanced.");

            // The indentation inside the block is insignificant whitespace between JSON tokens.
            return description.Substring(open, close - open + 1);
        }

        /// <summary>
        /// Index of the brace closing the object that opens at <paramref name="open"/>, or -1.
        /// String contents are skipped so a brace inside a value cannot unbalance the scan.
        /// </summary>
        private static Int32 MatchingBrace(String text, Int32 open)
        {
            var depth = 0;
            var inString = false;
            var escaped = false;

            for (var i = open; i < text.Length; i++)
            {
                var c = text[i];

                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        /// <summary>
        /// The generator writes a schema example either as a scalar <c>example</c> or as a
        /// single-entry <c>examples</c> array; this accepts both, so the assertions pin OUR contract
        /// rather than the library's spelling of it.
        /// </summary>
        private static JsonElement SingleExample(JsonElement schema)
        {
            if (schema.TryGetProperty("example", out var example))
            {
                return example;
            }

            Assert.IsTrue(schema.TryGetProperty("examples", out var examples) &&
                examples.ValueKind == JsonValueKind.Array && examples.GetArrayLength() > 0,
                "The schema must publish an example for a field whose enum schema shows only 'integer'.");
            return examples[0];
        }

        /// <summary>
        /// Asserts a published <c>operator</c> example is the integer code a client can actually
        /// send. A number and the generator's quoted rendering of the same digits both pass, because
        /// which of the two it writes is the library's choice; what must never come back is a member
        /// NAME, which no client can send and which is what B06 published.
        /// </summary>
        private static void AssertSendableOperator(JsonElement example, String what)
        {
            var raw = example.GetRawText();
            var text = example.ValueKind == JsonValueKind.String ? example.GetString() : raw;

            Assert.IsTrue(Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code),
                what + " must be the operator's integer code - BinaryOperator carries no string-enum " +
                "converter, so a member name cannot be sent - but is: " + raw);
            Assert.IsTrue(Enum.IsDefined((BinaryOperator)code),
                what + " must be one of the codes 0..5, but is: " + raw);
        }
    }
}
