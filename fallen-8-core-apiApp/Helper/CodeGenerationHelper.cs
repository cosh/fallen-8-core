// MIT License
//
// CodeGenerationHelper.cs
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.Extensions.Caching.Memory;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core.Algorithms;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;

namespace NoSQL.GraphDB.Core.App.Helper
{

    public static class CodeGenerationHelper
    {
        #region Code generation stuff

        private const String PathDelegateNamespace = "NoSQL.GraphDB.Core.Algorithms.Path";
        private const String PathDelegateClassName = "PathDelegateProvider";

        private const String VertexFilterMethodName = "VertexFilter";
        private const String EdgeFilterMethodName = "EdgeFilter";
        private const String EdgePropertyFilterMethodName = "EdgePropertyFilter";
        private const String VertexCostMethodName = "VertexCost";
        private const String EdgeCostMethodName = "EdgeCost";

        /// <summary>
        ///   Diagnostic counter of ACTUAL path-traverser Roslyn compiles (feature codegen-cache-keying).
        ///   Incremented once per <c>compilation.Emit</c> in <see cref="GeneratePathTraverser"/>, so a
        ///   test can assert that bound-only-differing requests compile exactly once. Intended for tests
        ///   and diagnostics.
        /// </summary>
        private static int _pathCompileCount;

        /// <summary>The number of path-traverser compiles performed so far (tests/diagnostics).</summary>
        public static int PathCompileCount => System.Threading.Volatile.Read(ref _pathCompileCount);

        /// <summary>Resets <see cref="PathCompileCount"/> (tests/diagnostics).</summary>
        public static void ResetPathCompileCount() => System.Threading.Volatile.Write(ref _pathCompileCount, 0);

        /// <summary>
        ///   Compile bounds (feature dynamic-code-resource-limits R2). A single user filter/cost
        ///   fragment longer than <see cref="MaxFilterFragmentLength"/>, or a generated source longer
        ///   than <see cref="MaxGeneratedSourceLength"/>, is rejected BEFORE Roslyn is invoked - so a
        ///   large/pathological fragment cannot burn arbitrary compile-time CPU/memory. The length cap
        ///   is the load-bearing guard (Roslyn's own cancellation is only cooperative). Generous
        ///   defaults: they bound abuse, not real fragments.
        /// </summary>
        internal const int MaxFilterFragmentLength = 100_000;
        private const int MaxGeneratedSourceLength = 1_000_000;

        #endregion

        [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "This method dynamically generates and loads code at runtime, which is incompatible with trimming")]
        [UnconditionalSuppressMessage("Trimming", "IL2072:Target parameter argument does not satisfy 'DynamicallyAccessedMembersAttribute' in call to target method. The return value of the source method does not have matching annotations.", Justification = "Dynamic code generation requires runtime type creation")]
        public static String GeneratePathTraverser(out IPathTraverser traverser, PathSpecification definition)
        {
            // Compile bounds (feature dynamic-code-resource-limits R2): reject an oversize fragment /
            // generated source BEFORE invoking Roslyn. Returns the same human-readable message shape the
            // controllers already map to a failed compile (-> 400).
            var fragmentError = CheckPathFragmentLengths(definition);
            if (fragmentError != null)
            {
                traverser = null;
                return fragmentError;
            }

            var sourceCode = CreateSource(definition);

            if (sourceCode.Length > MaxGeneratedSourceLength)
            {
                traverser = null;
                return String.Format("The generated path-traverser source ({0} chars) exceeds the maximum of {1}.",
                    sourceCode.Length, MaxGeneratedSourceLength);
            }

            var tree = SyntaxFactory.ParseSyntaxTree(sourceCode, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));

            string fileName = System.IO.Path.GetRandomFileName();

            // A single, immutable invocation to the compiler
            // to produce a library
            var compilation = CSharpCompilation.Create(fileName)
              .WithOptions(
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
              .AddReferences(_globalReferences)
              .AddSyntaxTrees(tree);

            IPathTraverser pathTraverserInstance = null;
            String resultMessage = null;

            using (var ms = new MemoryStream())
            {
                // Count every real compile (feature codegen-cache-keying): a cache hit never reaches
                // here, so this is the "compiled once" signal the acceptance test asserts.
                System.Threading.Interlocked.Increment(ref _pathCompileCount);

                // Feature observability: compile span + duration/failure metrics. A compile is a
                // cold, heavy operation - the unconditional timestamp is noise relative to Roslyn.
                using var compileSpan = NoSQL.GraphDB.App.Diagnostics.AppDiagnostics.Source.StartActivity("fallen8.codegen.compile");
                compileSpan?.SetTag("artifact", NoSQL.GraphDB.App.Diagnostics.AppDiagnostics.PathTraverserArtifact);
                var compileStart = Stopwatch.GetTimestamp();

                EmitResult compilationResult = compilation.Emit(ms);

                NoSQL.GraphDB.App.Diagnostics.AppDiagnostics.RecordCompile(
                    NoSQL.GraphDB.App.Diagnostics.AppDiagnostics.PathTraverserArtifact, compilationResult.Success,
                    Stopwatch.GetElapsedTime(compileStart).TotalSeconds);
                compileSpan?.SetTag("success", compilationResult.Success);

                if (compilationResult.Success)
                {
                    ms.Seek(0, SeekOrigin.Begin);

                    // Load into a collectible context so the generated assembly can be
                    // unloaded once the traverser is evicted from the cache and dropped.
                    var context = new AssemblyLoadContext("path-traverser-" + fileName, isCollectible: true);
                    Assembly assembly = context.LoadFromStream(ms);

                    var assemblyType = assembly.GetType(PathDelegateNamespace + "." + PathDelegateClassName);

                    pathTraverserInstance = (IPathTraverser)Activator.CreateInstance(assemblyType);
                }
                else
                {
                    StringBuilder sb = new StringBuilder();

                    foreach (Diagnostic codeIssue in compilationResult.Diagnostics)
                    {
                        string issue = $"ID: {codeIssue.Id}, Message: {codeIssue.GetMessage()}, Location: {codeIssue.Location.GetLineSpan()}, Severity: {codeIssue.Severity}";

                        sb.AppendLine(issue);
                    }

                    resultMessage = sb.ToString();
                }
            }

            traverser = pathTraverserInstance; ;

            return resultMessage;
        }

        /// <summary>
        ///   Rejects an oversize filter/cost fragment before compilation (feature
        ///   dynamic-code-resource-limits R2); returns a human-readable message, or <c>null</c> when all
        ///   fragments are within <see cref="MaxFilterFragmentLength"/>.
        /// </summary>
        private static string CheckPathFragmentLengths(PathSpecification definition)
        {
            if (definition.Filter != null)
            {
                var error = CheckFragment("filter.vertex", definition.Filter.Vertex)
                         ?? CheckFragment("filter.edge", definition.Filter.Edge)
                         ?? CheckFragment("filter.edgeProperty", definition.Filter.EdgeProperty);
                if (error != null)
                {
                    return error;
                }
            }

            if (definition.Cost != null)
            {
                var error = CheckFragment("cost.vertex", definition.Cost.Vertex)
                         ?? CheckFragment("cost.edge", definition.Cost.Edge);
                if (error != null)
                {
                    return error;
                }
            }

            return null;
        }

        private static string CheckFragment(string name, string fragment)
        {
            if (fragment != null && fragment.Length > MaxFilterFragmentLength)
            {
                return String.Format("The {0} fragment ({1} chars) exceeds the maximum of {2}.",
                    name, fragment.Length, MaxFilterFragmentLength);
            }
            return null;
        }

        /// <summary>
        /// Create the source for the code generation
        /// </summary>
        /// <param name="definition">The path specification</param>
        /// <returns>The source code</returns>
        private static string CreateSource(PathSpecification definition)
        {
            var @namespace = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(PathDelegateNamespace)).NormalizeWhitespace();

            @namespace = @namespace.AddUsings(
                SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System")),
                SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Linq")),
                SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("NoSQL.GraphDB.Core.Model")),
                // Fragments may score embeddings via the traversal context (feature
                // element-embeddings): VectorMath/VectorDistanceMetric come from Index.Vector;
                // TraversalContext resolves through the Algorithms namespace ancestry.
                SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("NoSQL.GraphDB.Core.Index.Vector"))
                );

            var classDeclaration = SyntaxFactory.ClassDeclaration(PathDelegateClassName);

            // Add the public modifier: (public class Order)
            classDeclaration = classDeclaration.AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.SealedKeyword)
                );

            // Inherit BaseEntity<T> and implement IHaveIdentity: (public class Order : BaseEntity<T>, IHaveIdentity)
            classDeclaration = classDeclaration.AddBaseListTypes(
                SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName("IPathTraverser")));

            var methods = new List<MethodDeclarationSyntax>();

            if (definition.Filter != null)
            {
                methods.Add(GenerateMethodSyntax("Delegates.VertexFilter", VertexFilterMethodName, definition.Filter.Vertex));
                methods.Add(GenerateMethodSyntax("Delegates.EdgeFilter", EdgeFilterMethodName, definition.Filter.Edge));
                methods.Add(GenerateMethodSyntax("Delegates.EdgePropertyFilter", EdgePropertyFilterMethodName, definition.Filter.EdgeProperty));
            }
            else
            {
                methods.Add(GenerateMethodSyntax("Delegates.VertexFilter", VertexFilterMethodName, null));
                methods.Add(GenerateMethodSyntax("Delegates.EdgeFilter", EdgeFilterMethodName, null));
                methods.Add(GenerateMethodSyntax("Delegates.EdgePropertyFilter", EdgePropertyFilterMethodName, null));
            }

            if (definition.Cost != null)
            {
                methods.Add(GenerateMethodSyntax("Delegates.VertexCost", VertexCostMethodName, definition.Cost.Vertex));
                methods.Add(GenerateMethodSyntax("Delegates.EdgeCost", EdgeCostMethodName, definition.Cost.Edge));
            }
            else
            {
                methods.Add(GenerateMethodSyntax("Delegates.VertexCost", VertexCostMethodName, null));
                methods.Add(GenerateMethodSyntax("Delegates.EdgeCost", EdgeCostMethodName, null));
            }

            // Add the field, the property and method to the class.
            classDeclaration = classDeclaration.AddMembers(methods.ToArray());

            // Add the class to the namespace.
            @namespace = @namespace.AddMembers(classDeclaration);

            // Normalize and get code as string.
            var code = @namespace
                .NormalizeWhitespace()
                .ToFullString();

            return code;
        }

        private static MethodDeclarationSyntax GenerateMethodSyntax(string returnType, string methodname, string code)
        {
            String codeToCompile = "return null;";

            if (!String.IsNullOrWhiteSpace(code))
            {
                codeToCompile = code;
            }

            // Create a stament with the body of a method.
            var syntax = SyntaxFactory.ParseStatement(codeToCompile);

            // Create a method. Every factory method takes the request's TraversalContext
            // (feature element-embeddings) so a fragment can close over the query vector;
            // fragments that ignore it compile unchanged, and the cache key (the fragments)
            // is untouched because the context is a parameter, not part of the source.
            return SyntaxFactory.MethodDeclaration(SyntaxFactory.ParseTypeName(returnType), methodname)
               .AddModifiers(
                   SyntaxFactory.Token(SyntaxKind.PublicKeyword)
                   )
               .AddParameterListParameters(
                   SyntaxFactory.Parameter(SyntaxFactory.Identifier("context"))
                       .WithType(SyntaxFactory.ParseTypeName("TraversalContext")))
               .WithBody(SyntaxFactory.Block(syntax));
        }

        /// <summary>
        ///   The Roslyn metadata references, built ONCE per process (feature codegen-cache-keying).
        ///   Framework/engine reference assemblies never change for the life of the process, so reading
        ///   them from disk on every compile paid hundreds of ms per cold compile for nothing. Shared by
        ///   the path and subgraph compile paths. <see cref="MetadataReference"/> instances are immutable
        ///   and process-lifetime and reference no collectible context, so this is unload-safe.
        /// </summary>
        private static readonly MetadataReference[] _globalReferences = BuildGlobalReferences().ToArray();

        /// <summary>
        ///   The shared metadata references, for the side-effect-free validation compile in
        ///   <see cref="DelegateValidationHelper"/> (feature web-ui, gap G-2).
        /// </summary>
        internal static MetadataReference[] GlobalReferences => _globalReferences;

        [UnconditionalSuppressMessage("SingleFile", "IL3000:Avoid accessing Assembly file path when publishing as a single file", Justification = "This method handles the single-file case by checking for empty Location and uses AppContext.BaseDirectory as fallback")]
        private static IEnumerable<MetadataReference> BuildGlobalReferences()
        {
            var assemblies = new[]
                {
                typeof(System.Linq.Enumerable).Assembly,
                typeof(IFallen8Read).Assembly,
                typeof(object).Assembly
            };

            var returnList = new List<MetadataReference>();

            // For single-file apps, assembly.Location may be empty, so we need to handle this carefully
            foreach (var assembly in assemblies)
            {
                var location = assembly.Location;
                if (!string.IsNullOrEmpty(location))
                {
                    returnList.Add(MetadataReference.CreateFromFile(location));
                }
            }

            //The location of the .NET assemblies
            // Use the location of the core runtime assembly to find the runtime directory
            var runtimeAssembly = typeof(object).Assembly;
            var assemblyPath = string.IsNullOrEmpty(runtimeAssembly.Location)
                ? AppContext.BaseDirectory
                : System.IO.Path.GetDirectoryName(runtimeAssembly.Location);

            /*
            * Adding some necessary .NET assemblies
            * These assemblies couldn't be loaded correctly via the same construction as above,
            * in specific the System.Runtime.
            */
            returnList.Add(MetadataReference.CreateFromFile(System.IO.Path.Combine(assemblyPath, "mscorlib.dll")));
            returnList.Add(MetadataReference.CreateFromFile(System.IO.Path.Combine(assemblyPath, "System.dll")));
            returnList.Add(MetadataReference.CreateFromFile(System.IO.Path.Combine(assemblyPath, "System.Core.dll")));
            returnList.Add(MetadataReference.CreateFromFile(System.IO.Path.Combine(assemblyPath, "System.Runtime.dll")));

            return returnList;
        }

        #region Subgraph code generation

        private const String SubGraphProviderNamespace = "NoSQL.GraphDB.Core.Algorithms.SubGraph.Generated";
        private const String SubGraphProviderClassName = "SubGraphDelegateProvider";

        /// <summary>
        /// Upper bound on a variable-length edge pattern's MaxLength accepted from the REST
        /// API. Guards against pathological, combinatorially expensive path expansion driven
        /// by untrusted input.
        /// </summary>
        private const int MaxVariableEdgeLength = 100;

        /// <summary>
        /// Caches compiled delegate providers by their generated source. Identical filter
        /// sets reuse the same compiled assembly instead of invoking Roslyn on every subgraph
        /// creation. Entries expire so the collectible load context holding each compiled
        /// assembly can be unloaded once no live subgraph still references its delegates.
        /// </summary>
        private static readonly MemoryCache _subGraphProviderCache =
            new MemoryCache(new MemoryCacheOptions { SizeLimit = 256, ExpirationScanFrequency = TimeSpan.FromMinutes(1) });

        private static readonly MemoryCacheEntryOptions _subGraphProviderCacheOptions =
            new MemoryCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromSeconds(60)).SetSize(1);

        /// <summary>
        /// Removes all cached compiled subgraph filter providers, allowing their collectible
        /// load contexts to be unloaded once no delegates remain referenced. Intended for
        /// tests and diagnostics.
        /// </summary>
        public static void ClearSubGraphProviderCache()
        {
            _subGraphProviderCache.Clear();
        }

        /// <summary>
        /// A single generated delegate: the method that produces it and the action that
        /// assigns the compiled delegate back onto the pattern it belongs to.
        /// </summary>
        private sealed class GeneratedDelegateSlot
        {
            public String MethodName
            {
                get; set;
            }
            public String ReturnType
            {
                get; set;
            }
            public String Code
            {
                get; set;
            }
            public Action<Object> Assign
            {
                get; set;
            }
        }

        /// <summary>
        /// Translates a <see cref="SubGraphSpecification"/> into an engine
        /// <see cref="SubGraphDefinition"/>, compiling every non-empty filter code
        /// fragment into its delegate in a single generated assembly.
        /// </summary>
        /// <param name="specification">The REST specification.</param>
        /// <param name="definition">The resulting definition, or null on failure.</param>
        /// <returns>
        /// <c>null</c> on success; otherwise a human-readable error message (invalid
        /// specification or compiler diagnostics).
        /// </returns>
        /// <remarks>
        /// The specification's semantic block (feature element-embeddings) is handled here, so
        /// every producer of a definition - the /subgraph endpoint, the persisted-recipe
        /// compiler, the stored-query compiler - binds it identically: the traversal context is
        /// built ONCE and closed over by the compiled delegates at registration time
        /// (recalculation and WAL replay never embed anything), and a declarative
        /// <c>minScore</c> becomes the vertex pre-filter.
        /// </remarks>
        public static String TryGenerateSubGraphDefinition(SubGraphSpecification specification, out SubGraphDefinition definition)
        {
            definition = null;

            if (specification == null)
            {
                return "Subgraph specification is null.";
            }

            if (String.IsNullOrWhiteSpace(specification.Name))
            {
                return "Subgraph specification requires a name.";
            }

            var semanticError = SemanticTraversalHelper.TryBuild(specification.Semantic, allowCost: false, out var semantic);
            if (semanticError != null)
            {
                return semanticError;
            }

            if (semantic.VertexFilter != null && !String.IsNullOrWhiteSpace(specification.VertexFilter))
            {
                return "semantic.minScore and a vertexFilter fragment own the same pre-filter slot; use one.";
            }

            var slots = new List<GeneratedDelegateSlot>();

            var def = new SubGraphDefinition
            {
                Name = specification.Name,
                AdditionalInformation = specification.AdditionalInformation
            };

            // Optional vertex pre-filter.
            RegisterSlot(slots, "Delegates.VertexFilter", specification.VertexFilter,
                d => def.VertexFilter = (Delegates.VertexFilter)d);

            // Optional edge pre-filter.
            RegisterSlot(slots, "Delegates.EdgeFilter", specification.EdgeFilter,
                d => def.EdgeFilter = (Delegates.EdgeFilter)d);

            // Pattern sequence.
            if (specification.Patterns != null && specification.Patterns.Count > 0)
            {
                def.Pattern = new List<APattern>();

                foreach (var patternSpec in specification.Patterns)
                {
                    var error = BuildPattern(patternSpec, def.Pattern, slots);
                    if (error != null)
                    {
                        return error;
                    }
                }
            }

            var compileError = CompileDelegates(slots, semantic.Context);
            if (compileError != null)
            {
                return compileError;
            }

            // The declarative pre-filter fills the (verified-empty) vertex pre-filter slot.
            if (semantic.VertexFilter != null)
            {
                def.VertexFilter = semantic.VertexFilter;
            }

            definition = def;
            return null;
        }

        private static String BuildPattern(PatternSpecification patternSpec, List<APattern> patterns, List<GeneratedDelegateSlot> slots)
        {
            if (patternSpec == null)
            {
                return "A pattern element is null.";
            }

            var type = (patternSpec.Type ?? String.Empty).Trim().ToLowerInvariant();

            switch (type)
            {
                case "vertex":
                    {
                        var vertexPattern = new VertexPattern { PatternName = patternSpec.PatternName };
                        RegisterSlot(slots, "Delegates.VertexFilter", patternSpec.VertexFilter,
                            d => vertexPattern.Vertex = (Delegates.VertexFilter)d);
                        patterns.Add(vertexPattern);
                        return null;
                    }

                case "edge":
                    {
                        if (!TryParseDirection(patternSpec.Direction, out var edgeDirection))
                        {
                            return String.Format("Unknown direction '{0}'. Expected 'OutgoingEdge', 'IncomingEdge' or 'UndirectedEdge'.", patternSpec.Direction);
                        }

                        var edgePattern = new EdgePattern
                        {
                            PatternName = patternSpec.PatternName,
                            Direction = edgeDirection
                        };
                        RegisterEdgeSlots(edgePattern, patternSpec, slots);
                        patterns.Add(edgePattern);
                        return null;
                    }

                case "variablelengthedge":
                    {
                        if (patternSpec.MinLength > patternSpec.MaxLength)
                        {
                            return String.Format(
                                "Variable-length edge pattern '{0}' has minLength ({1}) greater than maxLength ({2}).",
                                patternSpec.PatternName, patternSpec.MinLength, patternSpec.MaxLength);
                        }

                        if (patternSpec.MaxLength > MaxVariableEdgeLength)
                        {
                            return String.Format(
                                "Variable-length edge pattern '{0}' has maxLength ({1}) exceeding the allowed maximum of {2}.",
                                patternSpec.PatternName, patternSpec.MaxLength, MaxVariableEdgeLength);
                        }

                        if (!TryParseDirection(patternSpec.Direction, out var variableDirection))
                        {
                            return String.Format("Unknown direction '{0}'. Expected 'OutgoingEdge', 'IncomingEdge' or 'UndirectedEdge'.", patternSpec.Direction);
                        }

                        var variablePattern = new VariableLengthEdgePattern
                        {
                            PatternName = patternSpec.PatternName,
                            Direction = variableDirection,
                            MinLength = patternSpec.MinLength,
                            MaxLength = patternSpec.MaxLength
                        };
                        RegisterEdgeSlots(variablePattern, patternSpec, slots);
                        patterns.Add(variablePattern);
                        return null;
                    }

                default:
                    return String.Format(
                        "Unknown pattern type '{0}'. Expected 'Vertex', 'Edge' or 'VariableLengthEdge'.",
                        patternSpec.Type);
            }
        }

        private static void RegisterEdgeSlots(EdgePattern edgePattern, PatternSpecification patternSpec, List<GeneratedDelegateSlot> slots)
        {
            RegisterSlot(slots, "Delegates.EdgePropertyFilter", patternSpec.EdgePropertyFilter,
                d => edgePattern.EdgeProperty = (Delegates.EdgePropertyFilter)d);
            RegisterSlot(slots, "Delegates.EdgeFilter", patternSpec.EdgeFilter,
                d => edgePattern.Edge = (Delegates.EdgeFilter)d);
        }

        private static void RegisterSlot(List<GeneratedDelegateSlot> slots, String returnType, String code, Action<Object> assign)
        {
            // A null/empty fragment means "match everything" - leave the delegate null.
            if (String.IsNullOrWhiteSpace(code))
            {
                return;
            }

            slots.Add(new GeneratedDelegateSlot
            {
                MethodName = "M" + slots.Count,
                ReturnType = returnType,
                Code = code,
                Assign = assign
            });
        }

        private static bool TryParseDirection(String value, out Direction direction)
        {
            // A missing direction defaults to outgoing; an unrecognized one is an error
            // rather than a silent fallback that would traverse the wrong way.
            if (String.IsNullOrWhiteSpace(value))
            {
                direction = Direction.OutgoingEdge;
                return true;
            }

            switch (value.Trim().ToLowerInvariant())
            {
                case "out":
                case "outgoing":
                case "outgoingedge":
                    direction = Direction.OutgoingEdge;
                    return true;
                case "in":
                case "incoming":
                case "incomingedge":
                    direction = Direction.IncomingEdge;
                    return true;
                case "undirected":
                case "undirectededge":
                    direction = Direction.UndirectedEdge;
                    return true;
                default:
                    direction = Direction.OutgoingEdge;
                    return false;
            }
        }

        [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "This method dynamically generates and loads code at runtime, which is incompatible with trimming")]
        [UnconditionalSuppressMessage("Trimming", "IL2072:Target parameter argument does not satisfy 'DynamicallyAccessedMembersAttribute' in call to target method. The return value of the source method does not have matching annotations.", Justification = "Dynamic code generation requires runtime type creation")]
        [UnconditionalSuppressMessage("Trimming", "IL2075:'this' argument does not satisfy 'DynamicallyAccessedMembersAttribute' in call to target method. The return value of the source method does not have matching annotations.", Justification = "Generated provider type is created at runtime and its methods are invoked by name")]
        [UnconditionalSuppressMessage("Trimming", "IL2080:'this' argument does not satisfy 'DynamicallyAccessedMembersAttribute' in call to target method.", Justification = "Cached generated provider type is invoked by name at runtime; trimming is disabled for this application")]
        private static String CompileDelegates(List<GeneratedDelegateSlot> slots, TraversalContext context)
        {
            if (slots.Count == 0)
            {
                // Nothing to compile - every filter was "match everything".
                return null;
            }

            var sourceCode = BuildProviderSource(slots);

            // Reuse a previously compiled provider for an identical filter set: avoids
            // re-running Roslyn for the same code.
            if (!_subGraphProviderCache.TryGetValue(sourceCode, out (Object Instance, Type Type) provider))
            {
                NoSQL.GraphDB.App.Diagnostics.AppDiagnostics.RecordCacheMiss(NoSQL.GraphDB.App.Diagnostics.AppDiagnostics.SubGraphArtifact);

                var compileError = CompileProvider(sourceCode, out provider);
                if (compileError != null)
                {
                    return compileError;
                }

                _subGraphProviderCache.Set(sourceCode, provider, _subGraphProviderCacheOptions);
            }
            else
            {
                NoSQL.GraphDB.App.Diagnostics.AppDiagnostics.RecordCacheHit(NoSQL.GraphDB.App.Diagnostics.AppDiagnostics.SubGraphArtifact);
            }

            foreach (var slot in slots)
            {
                var method = provider.Type.GetMethod(slot.MethodName);
                // The factory methods take the traversal context (feature element-embeddings);
                // the materialized delegates close over it, so a cached provider (keyed on
                // source) still binds a fresh context per registration.
                var compiledDelegate = method.Invoke(provider.Instance, new Object[] { context });
                slot.Assign(compiledDelegate);
            }

            return null;
        }

        [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "This method dynamically generates and loads code at runtime, which is incompatible with trimming")]
        [UnconditionalSuppressMessage("Trimming", "IL2072:Target parameter argument does not satisfy 'DynamicallyAccessedMembersAttribute' in call to target method. The return value of the source method does not have matching annotations.", Justification = "Dynamic code generation requires runtime type creation")]
        private static String CompileProvider(String sourceCode, out (Object Instance, Type Type) provider)
        {
            provider = default;

            var tree = SyntaxFactory.ParseSyntaxTree(sourceCode, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));

            string fileName = System.IO.Path.GetRandomFileName();

            var compilation = CSharpCompilation.Create(fileName)
              .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
              .AddReferences(_globalReferences)
              .AddSyntaxTrees(tree);

            using (var ms = new MemoryStream())
            {
                // Feature observability: compile span + duration/failure metrics (cold path).
                using var compileSpan = NoSQL.GraphDB.App.Diagnostics.AppDiagnostics.Source.StartActivity("fallen8.codegen.compile");
                compileSpan?.SetTag("artifact", NoSQL.GraphDB.App.Diagnostics.AppDiagnostics.SubGraphArtifact);
                var compileStart = Stopwatch.GetTimestamp();

                EmitResult compilationResult = compilation.Emit(ms);

                NoSQL.GraphDB.App.Diagnostics.AppDiagnostics.RecordCompile(
                    NoSQL.GraphDB.App.Diagnostics.AppDiagnostics.SubGraphArtifact, compilationResult.Success,
                    Stopwatch.GetElapsedTime(compileStart).TotalSeconds);
                compileSpan?.SetTag("success", compilationResult.Success);

                if (!compilationResult.Success)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("Failed to compile subgraph filter expression(s):");

                    foreach (Diagnostic codeIssue in compilationResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                    {
                        sb.AppendLine($"ID: {codeIssue.Id}, Message: {codeIssue.GetMessage()}, Location: {codeIssue.Location.GetLineSpan()}, Severity: {codeIssue.Severity}");
                    }

                    return sb.ToString();
                }

                ms.Seek(0, SeekOrigin.Begin);

                // Load into a collectible context so the generated assembly can be unloaded
                // once its cache entry expires and no live subgraph references its delegates.
                var context = new AssemblyLoadContext("subgraph-provider-" + fileName, isCollectible: true);
                Assembly assembly = context.LoadFromStream(ms);

                var providerType = assembly.GetType(SubGraphProviderNamespace + "." + SubGraphProviderClassName);
                var providerInstance = Activator.CreateInstance(providerType);

                provider = (providerInstance, providerType);
            }

            return null;
        }

        private static String BuildProviderSource(List<GeneratedDelegateSlot> slots)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Linq;");
            sb.AppendLine("using NoSQL.GraphDB.Core.Model;");
            sb.AppendLine("using NoSQL.GraphDB.Core.Algorithms;");
            sb.AppendLine("using NoSQL.GraphDB.Core.Index.Vector;");
            sb.AppendLine("namespace " + SubGraphProviderNamespace);
            sb.AppendLine("{");
            sb.AppendLine("    public sealed class " + SubGraphProviderClassName);
            sb.AppendLine("    {");

            foreach (var slot in slots)
            {
                sb.AppendLine("        public " + slot.ReturnType + " " + slot.MethodName + "(TraversalContext context)");
                sb.AppendLine("        {");
                sb.AppendLine("            " + slot.Code);
                sb.AppendLine("        }");
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        #endregion
    }
}
