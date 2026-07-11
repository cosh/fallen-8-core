// MIT License
//
// SubGraphCodeGenUnloadTest.cs
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
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using NoSQL.GraphDB.Core.App.Helper;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Verifies that runtime-compiled subgraph filter assemblies are loaded into a
    /// collectible context and can be unloaded, so distinct filter sets do not leak
    /// process memory for the lifetime of the application.
    /// </summary>
    [TestClass]
    public class SubGraphCodeGenUnloadTest
    {
        // Compiles a provider for a unique filter and returns a weak reference to a type from
        // the generated (collectible) assembly, holding no strong reference to it. NoInlining
        // ensures no compiled artifact is rooted by this method's frame after it returns.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CompileAndWeaklyReferenceGeneratedType(string uniqueLabel)
        {
            var spec = new SubGraphSpecification
            {
                Name = "unload-probe-" + uniqueLabel,
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification
                    {
                        Type = "Vertex",
                        PatternName = "p",
                        // The unique label makes the generated source (and thus the compiled
                        // assembly) distinct from any other test's.
                        GraphElementFilter = "return (ge) => ge.Label == \"" + uniqueLabel + "\";"
                    }
                }
            };

            var error = CodeGenerationHelper.TryGenerateSubGraphDefinition(spec, out var definition);
            Assert.IsNull(error, "the probe filter should compile: " + error);

            var vertexPattern = (VertexPattern)definition.Pattern[0];
            Delegate compiledFilter = vertexPattern.GraphElement;
            Assert.IsNotNull(compiledFilter, "the graph-element filter should have been compiled");

            // The delegate's method lives in the generated, collectible assembly.
            var generatedType = compiledFilter.Method.DeclaringType;
            return new WeakReference(generatedType);
        }

        [TestMethod]
        public void CompiledSubGraphProvider_IsUnloadedAfterCacheCleared()
        {
            var generatedTypeRef = CompileAndWeaklyReferenceGeneratedType("UnloadProbeLabel");

            Assert.IsTrue(generatedTypeRef.IsAlive, "the generated type is alive while cached");

            // Drop the cache's strong reference, then force collection.
            CodeGenerationHelper.ClearSubGraphProviderCache();

            for (int i = 0; i < 15 && generatedTypeRef.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            Assert.IsFalse(generatedTypeRef.IsAlive,
                "the compiled provider type (and its collectible load context) should unload once the cache is cleared and no references remain");
        }
    }
}
