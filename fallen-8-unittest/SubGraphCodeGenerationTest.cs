// MIT License
//
// SubGraphCodeGenerationTest.cs
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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using NoSQL.GraphDB.Core.App.Helper;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Tests for translating a REST <see cref="SubGraphSpecification"/> into an engine
    /// <see cref="SubGraphDefinition"/>, including the runtime compilation of the filter
    /// code fragments into working delegates.
    /// </summary>
    [TestClass]
    public class SubGraphCodeGenerationTest
    {
        private static (Fallen8 graph, VertexModel person, VertexModel company, EdgeModel knows) BuildGraph()
        {
            var fallen8 = new Fallen8(TestLoggerFactory.Create());
            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());

            var verticesTx = new CreateVerticesTransaction();
            verticesTx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", "Alice" } });
            verticesTx.AddVertex(creationDate, "company", new Dictionary<string, object>() { { "name", "TechCorp" } });
            fallen8.EnqueueTransaction(verticesTx).WaitUntilFinished();
            var v = verticesTx.GetCreatedVertices();

            var edgesTx = new CreateEdgesTransaction();
            edgesTx.AddEdge(v[0].Id, "knows", v[1].Id, creationDate, "knows");
            fallen8.EnqueueTransaction(edgesTx).WaitUntilFinished();

            fallen8.TryGetEdge(out var edge, fallen8.GetAllEdges().First().Id);
            return (fallen8, v[0], v[1], edge);
        }

        [TestMethod]
        public void TryGenerate_NullSpecification_ReturnsError()
        {
            var error = CodeGenerationHelper.TryGenerateSubGraphDefinition(null, out var definition);

            Assert.IsNotNull(error, "A null specification must produce an error");
            Assert.IsNull(definition, "No definition should be produced");
        }

        [TestMethod]
        public void TryGenerate_MissingName_ReturnsError()
        {
            var spec = new SubGraphSpecification { Name = "  " };

            var error = CodeGenerationHelper.TryGenerateSubGraphDefinition(spec, out var definition);

            Assert.IsNotNull(error, "A missing name must produce an error");
            Assert.IsNull(definition);
        }

        [TestMethod]
        public void TryGenerate_EmptyFilters_ProduceMatchAllNullDelegates()
        {
            // A vertex pattern with no fragments must compile to a pattern whose delegates
            // are null (the algorithm treats null as "match everything").
            var spec = new SubGraphSpecification
            {
                Name = "all",
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification { Type = "Vertex", PatternName = "any" }
                }
            };

            var error = CodeGenerationHelper.TryGenerateSubGraphDefinition(spec, out var definition);

            Assert.IsNull(error, "Empty fragments are valid");
            Assert.IsNotNull(definition);
            Assert.IsNull(definition.VertexFilter, "No pre-filter was supplied");
            Assert.AreEqual(1, definition.Pattern.Count);
            var vp = (VertexPattern)definition.Pattern[0];
            Assert.IsNull(vp.GraphElement, "Empty graphElementFilter must leave the delegate null");
            Assert.IsNull(vp.Vertex, "Empty vertexFilter must leave the delegate null");
        }

        [TestMethod]
        public void TryGenerate_ValidFilters_CompileToWorkingDelegates()
        {
            var (graph, person, company, knows) = BuildGraph();

            var spec = new SubGraphSpecification
            {
                Name = "people-who-know",
                VertexFilter = "return (ge) => ge.Label == \"person\";",
                EdgeFilter = "return (ge) => ge.Label == \"knows\";",
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification
                    {
                        Type = "Vertex",
                        PatternName = "p",
                        GraphElementFilter = "return (ge) => ge.Label == \"person\";",
                        VertexFilter = "return (v) => v.Label == \"person\";"
                    },
                    new PatternSpecification
                    {
                        Type = "Edge",
                        PatternName = "rel",
                        Direction = "OutgoingEdge",
                        EdgePropertyFilter = "return (p) => p == \"knows\";",
                        EdgeFilter = "return (e) => e.Label == \"knows\";"
                    },
                    new PatternSpecification { Type = "Vertex", PatternName = "c" }
                }
            };

            var error = CodeGenerationHelper.TryGenerateSubGraphDefinition(spec, out var definition);

            Assert.IsNull(error, "Valid fragments should compile: " + error);
            Assert.IsNotNull(definition);

            // Pre-filters
            Assert.IsNotNull(definition.VertexFilter?.GraphElement);
            Assert.IsTrue(definition.VertexFilter.GraphElement(person), "person matches the vertex pre-filter");
            Assert.IsFalse(definition.VertexFilter.GraphElement(company), "company does not match the vertex pre-filter");
            Assert.IsTrue(definition.EdgeFilter.GraphElement(knows), "knows edge matches the edge pre-filter");

            // Pattern 0 (vertex)
            var vp = (VertexPattern)definition.Pattern[0];
            Assert.IsTrue(vp.GraphElement(person));
            Assert.IsTrue(vp.Vertex(person));

            // Pattern 1 (edge)
            var ep = (EdgePattern)definition.Pattern[1];
            Assert.AreEqual(Direction.OutgoingEdge, ep.Direction);
            Assert.IsTrue(ep.EdgeProperty("knows"), "edge property filter accepts 'knows'");
            Assert.IsFalse(ep.EdgeProperty("works_at"), "edge property filter rejects 'works_at'");
            Assert.IsTrue(ep.Edge(knows));
        }

        [TestMethod]
        public void TryGenerate_DirectionParsing_IsCaseInsensitiveAndSupportsAllValues()
        {
            var spec = new SubGraphSpecification
            {
                Name = "dirs",
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification { Type = "Vertex" },
                    new PatternSpecification { Type = "Edge", Direction = "incomingedge" },
                    new PatternSpecification { Type = "Vertex" },
                    new PatternSpecification { Type = "Edge", Direction = "Undirected" },
                    new PatternSpecification { Type = "Vertex" }
                }
            };

            var error = CodeGenerationHelper.TryGenerateSubGraphDefinition(spec, out var definition);

            Assert.IsNull(error);
            Assert.AreEqual(Direction.IncomingEdge, ((EdgePattern)definition.Pattern[1]).Direction);
            Assert.AreEqual(Direction.UndirectedEdge, ((EdgePattern)definition.Pattern[3]).Direction);
        }

        [TestMethod]
        public void TryGenerate_VariableLengthEdge_MapsLengthsAndType()
        {
            var spec = new SubGraphSpecification
            {
                Name = "var-len",
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification { Type = "Vertex" },
                    new PatternSpecification { Type = "VariableLengthEdge", Direction = "OutgoingEdge", MinLength = 1, MaxLength = 3 },
                    new PatternSpecification { Type = "Vertex" }
                }
            };

            var error = CodeGenerationHelper.TryGenerateSubGraphDefinition(spec, out var definition);

            Assert.IsNull(error);
            var vep = definition.Pattern[1] as VariableLengthEdgePattern;
            Assert.IsNotNull(vep, "Pattern must be a VariableLengthEdgePattern");
            Assert.AreEqual((ushort)1, vep.MinLength);
            Assert.AreEqual((ushort)3, vep.MaxLength);
            Assert.AreEqual(PatternType.VariableLengthEdge, vep.Type);
        }

        [TestMethod]
        public void TryGenerate_VariableLength_MinGreaterThanMax_ReturnsError()
        {
            var spec = new SubGraphSpecification
            {
                Name = "bad-lengths",
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification { Type = "Vertex" },
                    new PatternSpecification { Type = "VariableLengthEdge", MinLength = 5, MaxLength = 2 },
                    new PatternSpecification { Type = "Vertex" }
                }
            };

            var error = CodeGenerationHelper.TryGenerateSubGraphDefinition(spec, out var definition);

            Assert.IsNotNull(error, "minLength > maxLength must be rejected");
            Assert.IsNull(definition);
        }

        [TestMethod]
        public void TryGenerate_VariableLength_MaxLengthExceedingCap_ReturnsError()
        {
            var spec = new SubGraphSpecification
            {
                Name = "too-long",
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification { Type = "Vertex" },
                    new PatternSpecification { Type = "VariableLengthEdge", MinLength = 1, MaxLength = 5000 },
                    new PatternSpecification { Type = "Vertex" }
                }
            };

            var error = CodeGenerationHelper.TryGenerateSubGraphDefinition(spec, out var definition);

            Assert.IsNotNull(error, "An excessive maxLength must be rejected to prevent pathological expansion");
            Assert.IsNull(definition);
        }

        [TestMethod]
        public void TryGenerate_UnknownDirection_ReturnsError()
        {
            var spec = new SubGraphSpecification
            {
                Name = "bad-direction",
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification { Type = "Vertex" },
                    new PatternSpecification { Type = "Edge", Direction = "sideways" },
                    new PatternSpecification { Type = "Vertex" }
                }
            };

            var error = CodeGenerationHelper.TryGenerateSubGraphDefinition(spec, out var definition);

            Assert.IsNotNull(error, "An unknown direction must be rejected, not silently defaulted");
            StringAssert.Contains(error, "sideways");
            Assert.IsNull(definition);
        }

        [TestMethod]
        public void TryGenerate_OutgoingDirectionVariants_AllParse()
        {
            foreach (var value in new[] { "Out", "out", "OutgoingEdge", "outgoing" })
            {
                var spec = new SubGraphSpecification
                {
                    Name = "dir",
                    Patterns = new List<PatternSpecification>
                    {
                        new PatternSpecification { Type = "Vertex" },
                        new PatternSpecification { Type = "Edge", Direction = value },
                        new PatternSpecification { Type = "Vertex" }
                    }
                };

                var error = CodeGenerationHelper.TryGenerateSubGraphDefinition(spec, out var definition);

                Assert.IsNull(error, $"'{value}' should be a valid outgoing direction");
                Assert.AreEqual(Direction.OutgoingEdge, ((EdgePattern)definition.Pattern[1]).Direction);
            }
        }

        [TestMethod]
        public void TryGenerate_UnknownPatternType_ReturnsError()
        {
            var spec = new SubGraphSpecification
            {
                Name = "bad-type",
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification { Type = "Triangle" }
                }
            };

            var error = CodeGenerationHelper.TryGenerateSubGraphDefinition(spec, out var definition);

            Assert.IsNotNull(error, "An unknown pattern type must be rejected");
            StringAssert.Contains(error, "Triangle");
            Assert.IsNull(definition);
        }

        [TestMethod]
        public void TryGenerate_InvalidCodeFragment_ReturnsCompilerDiagnostics()
        {
            var spec = new SubGraphSpecification
            {
                Name = "bad-code",
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification
                    {
                        Type = "Vertex",
                        // Not valid C#: references an unknown symbol.
                        GraphElementFilter = "return (ge) => ge.ThisPropertyDoesNotExist == 42;"
                    }
                }
            };

            var error = CodeGenerationHelper.TryGenerateSubGraphDefinition(spec, out var definition);

            Assert.IsNotNull(error, "Invalid C# must produce a compiler error message");
            Assert.IsNull(definition, "No definition should be produced when compilation fails");
        }
    }
}
