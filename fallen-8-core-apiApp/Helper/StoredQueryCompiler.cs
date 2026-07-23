// MIT License
//
// StoredQueryCompiler.cs
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
using System.Text.Json;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core.App.Helper;
using NoSQL.GraphDB.Core.StoredQueries;

namespace NoSQL.GraphDB.App.Helper
{
    /// <summary>
    ///   Compiles a <see cref="StoredQueryDefinition"/>'s stored specification block into its
    ///   executable artifact through the SAME Roslyn paths the inline endpoints use
    ///   (<see cref="CodeGenerationHelper.GeneratePathTraverser"/> /
    ///   <see cref="CodeGenerationHelper.TryGenerateSubGraphDefinition"/>), including the
    ///   dynamic-code-resource-limits compile bounds. Used by the registration endpoint
    ///   (validate-then-store) and registered on the graph via
    ///   <c>IFallen8.StoredQueryCompiler</c> for load-time rehydration.
    /// </summary>
    public sealed class StoredQueryCompiler : IStoredQueryCompiler
    {
        public bool TryCompile(StoredQueryDefinition definition, out Object artifact, out String error)
        {
            artifact = null;
            error = null;

            if (definition == null)
            {
                error = "Stored query definition is null.";
                return false;
            }

            if (String.IsNullOrWhiteSpace(definition.SpecificationJson))
            {
                error = String.Format("Stored query '{0}' has no specification.", definition.Name);
                return false;
            }

            switch (definition.Kind)
            {
                case StoredQueryKind.Path:
                    return TryCompilePath(definition, ref artifact, ref error);

                case StoredQueryKind.SubGraph:
                    return TryCompileSubGraph(definition, ref artifact, ref error);

                default:
                    error = String.Format("Stored query '{0}' has an unknown kind.", definition.Name);
                    return false;
            }
        }

        private static bool TryCompilePath(StoredQueryDefinition definition, ref Object artifact, ref String error)
        {
            StoredPathQueryBlock block;
            try
            {
                block = JsonSerializer.Deserialize(definition.SpecificationJson, AppJsonContext.Default.StoredPathQueryBlock);
            }
            catch (Exception ex)
            {
                error = String.Format("Could not deserialize the specification for stored query '{0}': {1}",
                    definition.Name, ex.Message);
                return false;
            }

            if (block == null)
            {
                error = String.Format("The specification for stored query '{0}' deserialized to null.", definition.Name);
                return false;
            }

            // The stored block carries only what is stored (filter + cost); the numeric bounds
            // and algorithm name stay per-request and do not influence compilation (the compile
            // cache keys on (Filter, Cost) for the same reason - feature codegen-cache-keying).
            var pathSpecification = new PathSpecification
            {
                Filter = block.Filter,
                Cost = block.Cost
            };

            var compileError = CodeGenerationHelper.GeneratePathTraverser(out var traverser, pathSpecification);
            if (traverser == null)
            {
                error = compileError ?? String.Format("Compiling stored query '{0}' produced no traverser.", definition.Name);
                return false;
            }

            artifact = traverser;
            return true;
        }

        private static bool TryCompileSubGraph(StoredQueryDefinition definition, ref Object artifact, ref String error)
        {
            StoredSubGraphQueryBlock block;
            try
            {
                block = JsonSerializer.Deserialize(definition.SpecificationJson, AppJsonContext.Default.StoredSubGraphQueryBlock);
            }
            catch (Exception ex)
            {
                error = String.Format("Could not deserialize the specification for stored query '{0}': {1}",
                    definition.Name, ex.Message);
                return false;
            }

            if (block == null)
            {
                error = String.Format("The specification for stored query '{0}' deserialized to null.", definition.Name);
                return false;
            }

            // Pattern-level semantic thresholds cannot ride a template (feature
            // subgraph-semantic-thresholds): the template's delegates bind at ITS registration,
            // where no semantic query exists, so a threshold would close over the empty context
            // and match nothing - rejected loudly, mirroring the semantic-on-invocation 400.
            if (block.Patterns != null)
            {
                foreach (var pattern in block.Patterns)
                {
                    if (pattern != null && pattern.SemanticMinScore.HasValue)
                    {
                        error = String.Format(
                            "Stored query '{0}': 'semanticMinScore' is not available in a stored SubGraph template; inline the filters instead.",
                            definition.Name);
                        return false;
                    }
                }
            }

            // The template compiles under the stored query's own name as a placeholder; an
            // invocation instantiates it under the per-request subgraph instance name.
            var subGraphSpecification = new SubGraphSpecification
            {
                Name = definition.Name,
                VertexFilter = block.VertexFilter,
                EdgeFilter = block.EdgeFilter,
                Patterns = block.Patterns
            };

            var compileError = CodeGenerationHelper.TryGenerateSubGraphDefinition(subGraphSpecification, out var subGraphDefinition);
            if (compileError != null)
            {
                error = compileError;
                return false;
            }

            artifact = subGraphDefinition;
            return true;
        }
    }
}
