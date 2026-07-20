// MIT License
//
// DelegateValidationHelper.cs
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
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using NoSQL.GraphDB.App.Controllers.Model;

namespace NoSQL.GraphDB.Core.App.Helper
{
    /// <summary>
    ///   Compile-checks a single delegate fragment without executing it (feature web-ui, gap G-2).
    ///
    ///   <para>The fragment is wrapped in a provider class shaped like the ones the path and
    ///   subgraph endpoints generate (same usings, same namespace ancestry, same delegate return
    ///   types), then compiled with the shared Roslyn references. Only
    ///   <c>Compilation.GetDiagnostics()</c> runs - nothing is emitted, loaded, or executed, so
    ///   validation has no side effects (FR-23).</para>
    ///
    ///   <para>The wrapper places the fragment verbatim at a known line with zero added
    ///   indentation, so diagnostic positions map back to fragment coordinates by a constant line
    ///   offset with columns unchanged (FR-24). Positions outside the fragment (e.g. an unbalanced
    ///   brace reported at the wrapper's trailer) are clamped to the nearest fragment position.</para>
    /// </summary>
    public static class DelegateValidationHelper
    {
        private const String ValidationClassName = "DelegateValidationProvider";

        /// <summary>
        ///   delegateKind -> (delegate return type, compiled-in-subgraph-context). The five path
        ///   kinds validate in the path wrapper's environment, GraphElementFilter in the subgraph
        ///   wrapper's (see CodeGenerationHelper.CreateSource / BuildProviderSource).
        /// </summary>
        private static readonly Dictionary<String, (String ReturnType, Boolean SubGraph)> _kinds =
            new Dictionary<String, (String, Boolean)>(StringComparer.OrdinalIgnoreCase)
            {
                ["VertexFilter"] = ("Delegates.VertexFilter", false),
                ["EdgeFilter"] = ("Delegates.EdgeFilter", false),
                ["EdgePropertyFilter"] = ("Delegates.EdgePropertyFilter", false),
                ["VertexCost"] = ("Delegates.VertexCost", false),
                ["EdgeCost"] = ("Delegates.EdgeCost", false),
                ["GraphElementFilter"] = ("Delegates.GraphElementFilter", true),
            };

        /// <summary>The accepted delegateKind values, for error messages.</summary>
        public static String KnownKindsList => String.Join(", ", _kinds.Keys);

        /// <summary>
        ///   The canonical (case-normalized, bounded) name for a known kind, so a metric tag
        ///   carries "VertexFilter" rather than whatever casing the caller sent (feature
        ///   nl-assist-feedback-loop, FL-1 tag hygiene). False for an unknown kind.
        /// </summary>
        public static Boolean TryCanonicalKind(String delegateKind, out String canonical)
        {
            canonical = null;
            if (String.IsNullOrWhiteSpace(delegateKind))
            {
                return false;
            }

            var trimmed = delegateKind.Trim();
            foreach (var key in _kinds.Keys)
            {
                if (String.Equals(key, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    canonical = key;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        ///   Validates a fragment against a delegate kind.
        /// </summary>
        /// <param name="delegateKind">One of the §6.1 kinds (case-insensitive).</param>
        /// <param name="fragment">The method body returning a lambda; null/empty is valid.</param>
        /// <param name="result">The validation result with fragment-coordinate diagnostics.</param>
        /// <returns>False when the kind is unknown (the caller should 400); true otherwise.</returns>
        public static Boolean TryValidate(String delegateKind, String fragment, out DelegateValidationREST result)
        {
            result = null;

            if (String.IsNullOrWhiteSpace(delegateKind) || !_kinds.TryGetValue(delegateKind.Trim(), out var kind))
            {
                return false;
            }

            result = new DelegateValidationREST { Valid = true };

            // A null/empty fragment means "match everything" / "no custom cost" - valid by definition.
            if (String.IsNullOrWhiteSpace(fragment))
            {
                return true;
            }

            // Same pre-Roslyn size guard as the query endpoints (feature dynamic-code-resource-limits).
            if (fragment.Length > CodeGenerationHelper.MaxFilterFragmentLength)
            {
                result.Valid = false;
                result.Diagnostics.Add(new DelegateDiagnosticREST
                {
                    Line = 1,
                    Column = 1,
                    EndLine = 1,
                    EndColumn = 1,
                    Id = "F8LIMIT",
                    Severity = "error",
                    Message = String.Format("The fragment ({0} chars) exceeds the maximum of {1}.",
                        fragment.Length, CodeGenerationHelper.MaxFilterFragmentLength)
                });
                return true;
            }

            // Normalize line endings once so the built source and the clamping line table agree
            // with Roslyn's line counting for every input ending style.
            var normalizedFragment = fragment.Replace("\r\n", "\n").Replace('\r', '\n');
            var fragmentLines = normalizedFragment.Split('\n');

            var source = BuildValidationSource(kind.ReturnType, kind.SubGraph, normalizedFragment, out var preambleLines);

            var tree = SyntaxFactory.ParseSyntaxTree(source,
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));

            var compilation = CSharpCompilation.Create("f8-delegate-validation")
                .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddReferences(CodeGenerationHelper.GlobalReferences)
                .AddSyntaxTrees(tree);

            var hasError = false;
            foreach (var diagnostic in compilation.GetDiagnostics())
            {
                if (diagnostic.Severity == DiagnosticSeverity.Hidden)
                {
                    continue;
                }

                hasError |= diagnostic.Severity == DiagnosticSeverity.Error;
                result.Diagnostics.Add(MapDiagnostic(diagnostic, preambleLines, fragmentLines));
            }

            result.Valid = !hasError;
            return true;
        }

        /// <summary>
        ///   Builds the wrapper source. The fragment is inserted verbatim (no reindentation)
        ///   starting on line <paramref name="preambleLines"/> + 1, so mapping back is
        ///   <c>fragmentLine = generatedLine - preambleLines</c> with columns unchanged.
        /// </summary>
        private static String BuildValidationSource(String returnType, Boolean subGraph, String normalizedFragment, out Int32 preambleLines)
        {
            var sb = new StringBuilder();
            sb.Append("using System;\n");
            sb.Append("using System.Linq;\n");
            sb.Append("using NoSQL.GraphDB.Core.Model;\n");
            sb.Append("using NoSQL.GraphDB.Core.Index.Vector;\n");
            if (subGraph)
            {
                sb.Append("using NoSQL.GraphDB.Core.Algorithms;\n");
            }
            sb.Append(subGraph
                ? "namespace NoSQL.GraphDB.Core.Algorithms.SubGraph.Generated\n"
                : "namespace NoSQL.GraphDB.Core.Algorithms.Path\n");
            sb.Append("{\n");
            sb.Append("public sealed class ").Append(ValidationClassName).Append('\n');
            sb.Append("{\n");
            sb.Append("public ").Append(returnType).Append(" Validate(TraversalContext context)\n");
            sb.Append("{\n");

            preambleLines = subGraph ? 11 : 10;

            sb.Append(normalizedFragment).Append('\n');
            sb.Append("}\n}\n}\n");

            return sb.ToString();
        }

        private static DelegateDiagnosticREST MapDiagnostic(Diagnostic diagnostic, Int32 preambleLines, String[] fragmentLines)
        {
            var span = diagnostic.Location.GetLineSpan();

            var (line, column) = MapPosition(span.StartLinePosition, preambleLines, fragmentLines);
            var (endLine, endColumn) = MapPosition(span.EndLinePosition, preambleLines, fragmentLines);

            if (endLine < line || (endLine == line && endColumn < column))
            {
                endLine = line;
                endColumn = column;
            }

            return new DelegateDiagnosticREST
            {
                Line = line,
                Column = column,
                EndLine = endLine,
                EndColumn = endColumn,
                Id = diagnostic.Id,
                Message = diagnostic.GetMessage(),
                Severity = diagnostic.Severity switch
                {
                    DiagnosticSeverity.Error => "error",
                    DiagnosticSeverity.Warning => "warning",
                    _ => "info",
                }
            };
        }

        private static (Int32 Line, Int32 Column) MapPosition(LinePosition position, Int32 preambleLines, String[] fragmentLines)
        {
            var line = position.Line + 1 - preambleLines;
            var column = position.Character + 1;

            // Clamp positions that fall on the wrapper's preamble or trailer to the nearest
            // fragment position - the marker must always land inside what the user wrote.
            if (line < 1)
            {
                return (1, 1);
            }

            if (line > fragmentLines.Length)
            {
                var lastLine = fragmentLines.Length;
                return (lastLine, fragmentLines[lastLine - 1].Length + 1);
            }

            return (line, Math.Max(1, Math.Min(column, fragmentLines[line - 1].Length + 1)));
        }
    }
}
