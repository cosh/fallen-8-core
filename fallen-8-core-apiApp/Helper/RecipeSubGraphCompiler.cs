// MIT License
//
// RecipeSubGraphCompiler.cs
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
using NoSQL.GraphDB.App;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using NoSQL.GraphDB.Core.App.Helper;
using NoSQL.GraphDB.Core.SubGraph;

namespace NoSQL.GraphDB.App.Helper
{
    /// <summary>
    ///   Rebuilds a subgraph definition from a persisted recipe by deserializing the stored
    ///   <see cref="SubGraphSpecification"/> and recompiling its filter fragments.
    /// </summary>
    /// <remarks>
    ///   Registered on the graph via <c>IFallen8.SubGraphRecipeCompiler</c> so persisted
    ///   subgraphs can be recomputed on load.
    /// </remarks>
    public sealed class RecipeSubGraphCompiler : ISubGraphRecipeCompiler
    {
        public bool TryCompile(SubGraphRecipe recipe, out SubGraphDefinition definition, out String error)
        {
            definition = null;
            error = null;

            if (recipe == null)
            {
                error = "Recipe is null.";
                return false;
            }

            if (String.IsNullOrWhiteSpace(recipe.SpecificationJson))
            {
                error = String.Format("Recipe for subgraph '{0}' has no specification.", recipe.Name);
                return false;
            }

            SubGraphSpecification specification;
            try
            {
                specification = JsonSerializer.Deserialize(recipe.SpecificationJson, AppJsonContext.Default.SubGraphSpecification);
            }
            catch (Exception ex)
            {
                error = String.Format("Could not deserialize the specification for subgraph '{0}': {1}", recipe.Name, ex.Message);
                return false;
            }

            if (specification == null)
            {
                error = String.Format("The specification for subgraph '{0}' deserialized to null.", recipe.Name);
                return false;
            }

            error = CodeGenerationHelper.TryGenerateSubGraphDefinition(specification, out definition);
            return error == null;
        }
    }
}
