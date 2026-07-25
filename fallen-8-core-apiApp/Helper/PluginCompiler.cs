// MIT License
//
// PluginCompiler.cs
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
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Analytics;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Plugins;

namespace NoSQL.GraphDB.App.Helper
{
    /// <summary>
    ///   Compiles a <see cref="PluginDefinition"/>'s WHOLE-type C# source into its plugin
    ///   <see cref="Type"/> and validates it against the category contract (feature
    ///   plugin-registration). The engine→API bridge implementation registered on each namespace
    ///   engine (the same pattern as <c>StoredQueryCompiler</c> / <c>RecipeSubGraphCompiler</c>).
    ///
    ///   <para>Unlike the path/subgraph fragment compilers (which wrap a caller's method BODY into a
    ///   fixed harness-generated class), this compiles a complete compilation unit the user authored -
    ///   a whole type implementing the contract interface - because a plugin is a full type, not a
    ///   fill-in-the-blank fragment (a DLL was a full type; its source replacement is too).</para>
    ///
    ///   <para>HONESTY: the compiled type runs IN-PROCESS WITH FULL TRUST when invoked - exactly as
    ///   dangerous as the uploaded DLL it replaces. The win is that the source is visible,
    ///   contract-validated, gated, logged, and per-namespace; it is NOT a sandbox.</para>
    /// </summary>
    public sealed class PluginCompiler : IPluginCompiler
    {
        /// <summary>
        ///   Upper bound on the plugin source accepted before Roslyn runs (the
        ///   dynamic-code-resource-limits discipline): a pathological source cannot burn arbitrary
        ///   compile-time CPU/memory. Generous - it bounds abuse, not real plugins.
        /// </summary>
        public const int MaxPluginSourceLength = 200_000;

        /// <summary>
        ///   The Roslyn metadata references, built ONCE per process: the full trusted-platform
        ///   assembly set (framework) plus the engine assembly, so an authored plugin can reference
        ///   anything the engine's public API exposes. Immutable and process-lifetime.
        /// </summary>
        private static readonly MetadataReference[] _references = BuildReferences();

        [UnconditionalSuppressMessage("SingleFile", "IL3000:Avoid accessing Assembly file path when publishing as a single file",
            Justification = "Runtime plugin compilation needs on-disk reference assemblies; the single-file case falls back to the TPA list which carries real paths.")]
        private static MetadataReference[] BuildReferences()
        {
            var refs = new List<MetadataReference>();
            var seen = new HashSet<String>(StringComparer.OrdinalIgnoreCase);

            var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as String ?? String.Empty;
            foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (seen.Add(path))
                {
                    try { refs.Add(MetadataReference.CreateFromFile(path)); }
                    catch { /* skip an unreadable reference rather than fail every compile */ }
                }
            }

            // Guarantee the engine assembly (the plugin contracts) is referenced even when it is not on
            // the TPA list (e.g. some hosting/test layouts).
            var engineLocation = typeof(IFallen8Read).Assembly.Location;
            if (!String.IsNullOrEmpty(engineLocation) && seen.Add(engineLocation))
            {
                try { refs.Add(MetadataReference.CreateFromFile(engineLocation)); } catch { }
            }

            return refs.ToArray();
        }

        /// <summary>
        ///   Maps a contract to the CLR interface the authored type must implement. Returns null for an
        ///   unrecognized contract (rejected as a compile error before Roslyn runs).
        /// </summary>
        private static Type ResolveContractType(PluginContract contract)
        {
            switch (contract)
            {
                case PluginContract.Path: return typeof(Core.Algorithms.Path.IShortestPathAlgorithm);
                case PluginContract.SubGraph: return typeof(ISubGraphAlgorithm);
                case PluginContract.Analytics: return typeof(IGraphAnalyticsAlgorithm);
                case PluginContract.GraphFunction: return typeof(IGraphFunction);
                default: return null;
            }
        }

        /// <inheritdoc/>
        [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
            Justification = "Runtime plugin compilation dynamically generates and loads code, which is incompatible with trimming.")]
        [UnconditionalSuppressMessage("Trimming", "IL2072:Target parameter argument does not satisfy 'DynamicallyAccessedMembersAttribute' in call to target method.",
            Justification = "The plugin type is created at runtime from compiled source; trimming is disabled for this application.")]
        [UnconditionalSuppressMessage("AOT", "IL3050:Calling members annotated with 'RequiresDynamicCodeAttribute' may break functionality when AOT compiling.",
            Justification = "Runtime plugin compilation is inherently dynamic; the app is not AOT-compiled.")]
        public bool TryCompile(PluginDefinition definition, out Type artifact, out String error)
        {
            artifact = null;
            error = null;

            if (definition == null)
            {
                error = "A plugin definition is required.";
                return false;
            }

            if (String.IsNullOrWhiteSpace(definition.SourceCode))
            {
                error = "Plugin source code is required.";
                return false;
            }

            if (definition.SourceCode.Length > MaxPluginSourceLength)
            {
                error = String.Format("The plugin source ({0} chars) exceeds the maximum of {1}.",
                    definition.SourceCode.Length, MaxPluginSourceLength);
                return false;
            }

            if (!PluginRegistry.IsValidName(definition.Name))
            {
                error = String.Format("'{0}' is not a valid plugin name.", definition.Name);
                return false;
            }

            var contractType = ResolveContractType(definition.Contract);
            if (contractType == null)
            {
                error = String.Format("Unknown plugin contract '{0}'.", definition.Contract);
                return false;
            }

            var tree = CSharpSyntaxTree.ParseText(definition.SourceCode,
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));

            var assemblyName = "plugin-" + Path.GetRandomFileName();
            var compilation = CSharpCompilation.Create(assemblyName)
                .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddReferences(_references)
                .AddSyntaxTrees(tree);

            using var ms = new MemoryStream();
            EmitResult emit = compilation.Emit(ms);
            if (!emit.Success)
            {
                error = FormatDiagnostics(emit.Diagnostics);
                return false;
            }

            ms.Seek(0, SeekOrigin.Begin);

            // Load into a collectible context so the assembly can be unloaded once the plugin is
            // deleted (success) or immediately (validation failure). On success the returned Type keeps
            // the context alive for the plugin's registered lifetime.
            var context = new AssemblyLoadContext(assemblyName, isCollectible: true);
            Assembly assembly;
            try
            {
                assembly = context.LoadFromStream(ms);
            }
            catch (Exception ex)
            {
                context.Unload();
                error = "The compiled plugin assembly could not be loaded: " + ex.Message;
                return false;
            }

            var validationError = ValidateContract(assembly, contractType, definition.Name, out var type);
            if (validationError != null)
            {
                context.Unload();
                error = validationError;
                return false;
            }

            artifact = type;
            return true;
        }

        /// <summary>
        ///   Enforces the contract: exactly one public, non-abstract class implementing
        ///   <paramref name="contractType"/> with a public parameterless constructor, activatable, whose
        ///   <c>PluginName</c> equals the registration name. Returns an error message, or null on success
        ///   (with <paramref name="type"/> set to the validated plugin type).
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
            Justification = "Reflecting over runtime-compiled types is inherent to plugin loading; trimming is disabled for this application.")]
        [UnconditionalSuppressMessage("Trimming", "IL2072:Target parameter argument does not satisfy 'DynamicallyAccessedMembersAttribute' in call to target method.",
            Justification = "The plugin type is created at runtime; trimming is disabled for this application.")]
        [UnconditionalSuppressMessage("Trimming", "IL2075:'this' argument does not satisfy 'DynamicallyAccessedMembersAttribute' in call to target method.",
            Justification = "The plugin type is reflected over at runtime (constructor lookup + activation); trimming is disabled for this application.")]
        private static String ValidateContract(Assembly assembly, Type contractType, String expectedName, out Type type)
        {
            type = null;

            Type[] exported;
            try
            {
                exported = assembly.GetExportedTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return "The plugin assembly exposed types that could not be loaded: " + ex.Message;
            }

            var implementors = exported
                .Where(t => t.IsClass && !t.IsAbstract && t.IsPublic && contractType.IsAssignableFrom(t))
                .ToList();

            if (implementors.Count == 0)
            {
                return String.Format(
                    "The plugin source must contain exactly one public class implementing {0}; it contains none.",
                    contractType.FullName);
            }

            if (implementors.Count > 1)
            {
                return String.Format(
                    "The plugin source must contain exactly one public class implementing {0}; it contains {1} ({2}).",
                    contractType.FullName, implementors.Count,
                    String.Join(", ", implementors.Select(t => t.FullName)));
            }

            var candidate = implementors[0];
            if (candidate.GetConstructor(Type.EmptyTypes) == null)
            {
                return String.Format("The plugin type '{0}' must have a public parameterless constructor.", candidate.FullName);
            }

            IPlugin instance;
            try
            {
                instance = Activator.CreateInstance(candidate) as IPlugin;
            }
            catch (Exception ex)
            {
                return String.Format("Instantiating the plugin type '{0}' threw: {1}", candidate.FullName, ex.Message);
            }

            if (instance == null)
            {
                return String.Format("The plugin type '{0}' is not an IPlugin.", candidate.FullName);
            }

            var pluginName = instance.PluginName;
            // IPlugin : IDisposable - release the validation probe instance immediately.
            try { instance.Dispose(); } catch { /* a plugin's Dispose is best-effort at validation time */ }

            if (String.IsNullOrEmpty(pluginName))
            {
                return String.Format("The plugin type '{0}' must expose a non-empty PluginName.", candidate.FullName);
            }

            if (!String.Equals(pluginName, expectedName, StringComparison.Ordinal))
            {
                return String.Format(
                    "The plugin's PluginName '{0}' must equal the registration name '{1}'.", pluginName, expectedName);
            }

            type = candidate;
            return null;
        }

        private static String FormatDiagnostics(IEnumerable<Diagnostic> diagnostics)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Failed to compile the plugin source:");
            foreach (var d in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                sb.AppendLine(String.Format("ID: {0}, Message: {1}, Location: {2}, Severity: {3}",
                    d.Id, d.GetMessage(), d.Location.GetLineSpan(), d.Severity));
            }
            return sb.ToString();
        }
    }
}
