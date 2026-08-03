// MIT License
//
// CodegenEnvironmentParityTest.cs
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

using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.App.Helper;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Parity guard for the single-homed dynamic-code compile environment (consolidation-audit
    ///   CA-4). A user filter/cost fragment is compiled in three places that MUST inject the same
    ///   ambient environment (using set, wrapper namespace, <c>(TraversalContext context)</c>
    ///   signature): the real <c>/path</c> generator (<see cref="CodeGenerationHelper.GeneratePathTraverser"/>),
    ///   the real <c>/subgraph</c> generator (<see cref="CodeGenerationHelper.TryGenerateSubGraphDefinition"/>),
    ///   and the side-effect-free validator behind <c>POST /delegates/validate</c>
    ///   (<see cref="DelegateValidationHelper.TryValidate"/>). Before the environment was
    ///   single-homed the validator hand-mirrored the two generators, so adding a using to the
    ///   generators (element-embeddings added <c>Index.Vector</c>) and forgetting the validator
    ///   would have made the editor reject a fragment the endpoints compile - silently.
    ///
    ///   <para>These tests drive a fragment that references a symbol from EVERY supported namespace
    ///   (System, System.Linq, Core.Model, Core.Index.Vector, and the <c>context</c> of
    ///   Core.Algorithms) and assert the validator's verdict AGREES with the real generator's
    ///   outcome - accepting valid fragments and, crucially, rejecting a symbol outside the shared
    ///   using set. If the shared environment loses a namespace, all consumers break together and
    ///   these tests fail.</para>
    /// </summary>
    [TestClass]
    public class CodegenEnvironmentParityTest
    {
        // A VertexFilter body (v : VertexModel) touching every supported namespace:
        //   System            -> Math.Abs
        //   System.Linq       -> Enumerable.Any
        //   Core.Model        -> v.Label, AGraphElementModel.DefaultEmbeddingName
        //   Core.Index.Vector -> VectorMath.TryScore, VectorDistanceMetric.Cosine
        //   Core.Algorithms   -> the `context` (TraversalContext) parameter and its members
        private const string AllNamespacesVertexFilter =
            "return (v) => new[] { v.Label, AGraphElementModel.DefaultEmbeddingName }.Any(l => l != null) " +
            "&& (!context.HasQueryVector || (VectorMath.TryScore(out var s, context.QueryVector, context.QueryVector, VectorDistanceMetric.Cosine) && Math.Abs(s) >= 0f));";

        // The AGraphElementModel-typed equivalent (ge : AGraphElementModel), which is the only shape
        // that reaches the validator's SUBGRAPH environment (the GraphElementFilter kind). It also
        // touches every supported namespace via context.TrySimilarity / context.Metric.
        private const string AllNamespacesGraphElementFilter =
            "return (ge) => context.TrySimilarity(ge, out var s) && Math.Abs(s) >= 0f " +
            "&& new[] { ge.Label }.Any(l => l != AGraphElementModel.DefaultEmbeddingName) && context.Metric != VectorDistanceMetric.L2;";

        // References StringBuilder UNQUALIFIED. Its namespace (System.Text) is deliberately NOT in
        // either compile environment, so the simple name cannot bind and both the generator and the
        // validator must reject it (CS0246). Fully qualifying it would resolve without a using and
        // defeat the test, so the simple name is the point.
        private const string OutOfEnvironmentFragment =
            "return (v) => new StringBuilder().Length == v.Label.Length ? true : false;";

        [TestMethod]
        public void PathEnvironment_ValidatorAndGeneratorAgree_OnAFragmentTouchingEverySupportedNamespace()
        {
            var spec = new PathSpecification
            {
                Filter = new PathFilterSpecification { Vertex = AllNamespacesVertexFilter }
            };

            var generatorError = CodeGenerationHelper.GeneratePathTraverser(out IPathTraverser traverser, spec);

            Assert.IsNull(generatorError,
                "The /path generator must compile a fragment that uses every supported namespace. Got: " + generatorError);
            Assert.IsNotNull(traverser, "A successful compile must produce a traverser.");

            Assert.IsTrue(DelegateValidationHelper.TryValidate("VertexFilter", AllNamespacesVertexFilter, out var validation),
                "VertexFilter is a known kind.");
            Assert.IsTrue(validation.Valid,
                "The validator must accept what the /path generator compiles - the environments must match.");
            Assert.AreEqual(0, validation.Diagnostics.Count,
                "A fragment the generator accepts must produce no validation diagnostics (no errors, no warnings).");
        }

        [TestMethod]
        public void SubGraphEnvironment_ValidatorAndGeneratorAgree_OnAFragmentTouchingEverySupportedNamespace()
        {
            var spec = new SubGraphSpecification
            {
                Name = "codegen-env-parity",
                VertexFilter = AllNamespacesVertexFilter
            };

            var generatorError = CodeGenerationHelper.TryGenerateSubGraphDefinition(spec, out var definition);

            Assert.IsNull(generatorError,
                "The /subgraph generator must compile a fragment that uses every supported namespace. Got: " + generatorError);
            Assert.IsNotNull(definition, "A successful compile must produce a definition.");
            Assert.IsNotNull(definition.VertexFilter, "The compiled vertex pre-filter delegate must be bound.");

            // The validator's SUBGRAPH environment is reachable only through the GraphElementFilter
            // kind (AGraphElementModel-typed); a VertexModel is one, so this exercises the same
            // subgraph using set the generator used above.
            Assert.IsTrue(DelegateValidationHelper.TryValidate("GraphElementFilter", AllNamespacesGraphElementFilter, out var validation),
                "GraphElementFilter is a known kind.");
            Assert.IsTrue(validation.Valid,
                "The validator's subgraph environment must accept what the /subgraph generator compiles.");
        }

        [TestMethod]
        public void PathEnvironment_ValidatorAndGeneratorAgree_RejectingASymbolOutsideTheSharedUsingSet()
        {
            var spec = new PathSpecification
            {
                Filter = new PathFilterSpecification { Vertex = OutOfEnvironmentFragment }
            };

            var generatorError = CodeGenerationHelper.GeneratePathTraverser(out IPathTraverser traverser, spec);

            Assert.IsNotNull(generatorError,
                "The generator must reject a type whose namespace is not in the shared using set.");
            Assert.IsNull(traverser, "A failed compile must not produce a traverser.");

            Assert.IsTrue(DelegateValidationHelper.TryValidate("VertexFilter", OutOfEnvironmentFragment, out var validation),
                "VertexFilter is a known kind.");
            Assert.IsFalse(validation.Valid,
                "The validator must reject the SAME out-of-environment symbol the generator rejects; if it accepted, "
                + "the validator's using set would be a superset of the generator's.");
        }
    }
}
