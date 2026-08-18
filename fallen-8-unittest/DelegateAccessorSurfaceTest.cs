// MIT License
//
// DelegateAccessorSurfaceTest.cs
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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core.App.Helper;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Pins the fragment-callable accessor surface against the REAL compile environment, so the
    ///   Studio completion list and the NL-assist prompt cannot drift from what Roslyn accepts.
    ///   <para>The drift this exists to catch shipped once: Studio offered four members that every
    ///   fragment using them failed to compile, and the NL-assist prompt taught a model to reach
    ///   for one of them. The JS half is fallen-8-web-ui/tests/type-model.test.ts; the flag both
    ///   halves agree on is <c>"compilable": false</c> in
    ///   fallen-8-web-ui/src/delegate/type-model.json.</para>
    /// </summary>
    [TestClass]
    public class DelegateAccessorSurfaceTest
    {
        // Comparing the result to null is enough to trigger the failure: it is signature binding,
        // not any use made of the result.
        private static readonly (String Member, String Fragment)[] _uncompilable =
        {
            ("AGraphElementModel.GetAllProperties", "return (v) => v.GetAllProperties() != null;"),
            ("VertexModel.GetAllNeighbors", "return (v) => v.GetAllNeighbors() != null;"),
            ("VertexModel.GetIncomingEdgeIds", "return (v) => v.GetIncomingEdgeIds() != null;"),
            ("VertexModel.GetOutgoingEdgeIds", "return (v) => v.GetOutgoingEdgeIds() != null;"),
        };

        // Every member the completion list offers WITHOUT the flag, plus each substitute the warning
        // text points a user at. If one of these stops compiling, the editor is advertising a lie.
        private static readonly (String Kind, String Member, String Fragment)[] _compilable =
        {
            ("VertexFilter", "Id / Label", "return (v) => v.Id > 0 && v.Label == \"person\";"),
            ("VertexFilter", "timestamps / count", "return (v) => v.GetPropertyCount() > 0 && v.GetCreationDate() <= v.GetModificationDate();"),
            ("VertexFilter", "TryGetProperty", "return (v) => v.TryGetProperty(out int age, \"age\") && age > 30;"),
            ("VertexFilter", "AnyPropertyValueMatches", "return (v) => v.AnyPropertyValueMatches(s => s.Contains(\"Tech\", StringComparison.OrdinalIgnoreCase));"),
            ("VertexFilter", "degrees", "return (v) => v.GetInDegree() + v.GetOutDegree() > 2;"),
            ("VertexFilter", "OutEdges / InEdges", "return (v) => v.OutEdges != null && v.OutEdges.Count > 0 && v.InEdges == null;"),
            ("VertexFilter", "TryGetOutEdge", "return (v) => v.TryGetOutEdge(out var g, \"knows\") && g.Count > 0;"),
            ("VertexFilter", "TryGetInEdge", "return (v) => v.TryGetInEdge(out var g, \"knows\") && g.Count > 0;"),
            ("VertexFilter", "TryGetOutEdgesSpan", "return (v) => v.TryGetOutEdgesSpan(out var es, \"knows\") && es.Length > 0;"),
            ("VertexFilter", "TryGetInEdgesSpan", "return (v) => v.TryGetInEdgesSpan(out var es, \"knows\") && es.Length > 0;"),
            ("VertexFilter", "TryGetEmbedding", "return (v) => v.TryGetEmbedding(out var vec) && vec.Length > 0;"),
            ("VertexFilter", "TryGetEmbedding(name)", "return (v) => v.TryGetEmbedding(out var vec, \"title\") && vec.Length > 0;"),
            ("VertexFilter", "TryGetEmbeddingModelStamp", "return (v) => v.TryGetEmbeddingModelStamp(out var st) && st != null;"),
            // --- the substitutes the CS0012 warning text names: they are the promise the editor makes to
            //     somebody who just hit CS0012, so they are load-bearing, not decoration ---
            ("VertexFilter", "substitute for GetOutgoingEdgeIds", "return (v) => v.OutEdges != null && v.OutEdges.Keys.Any(k => k == \"knows\");"),
            ("VertexFilter", "substitute for GetIncomingEdgeIds", "return (v) => v.InEdges != null && v.InEdges.Keys.Contains(\"knows\");"),
            ("VertexFilter", "substitute for GetAllNeighbors", "return (v) => v.OutEdges != null && v.OutEdges.SelectMany(kv => kv.Value).Any(e => e.TargetVertex.Label == \"person\");"),
            ("VertexFilter", "substitute for GetAllProperties", "return (v) => v.GetPropertyCount() > 0 && v.TryGetProperty(out String n, \"name\") && n != null;"),
            // --- EdgeModel, through an EdgeFilter ---
            ("EdgeFilter", "edge fields + base", "return (e) => e.SourceVertex != null && e.TargetVertex != null && e.EdgePropertyId == \"knows\" && e.Id > 0 && e.Label != null;"),
            // --- the whole string surface in one fragment, through an EdgePropertyFilter: the only kind
            //     whose parameter is not a graph element ---
            ("EdgePropertyFilter", "string surface", "return (p) => p.Length > 3 && p.StartsWith(\"k\") && p.EndsWith(\"s\") && p.Contains(\"now\") && p.Equals(\"knows\") && p.ToLower() != p.ToUpper() && p.Trim() == p && p.IndexOf(\"n\") >= 0;"),
        };

        [TestMethod]
        public void FlaggedAccessors_FailWithCS0012_SoTheCompletionListMustKeepFlaggingThem()
        {
            foreach (var (member, fragment) in _uncompilable)
            {
                Assert.IsTrue(
                    DelegateValidationHelper.TryValidate("VertexFilter", fragment, out var result),
                    "VertexFilter is a known kind.");
                Assert.IsFalse(result.Valid, member + " unexpectedly COMPILES now. If the compile "
                    + "environment gained a reference, clear \"compilable\": false for it in "
                    + "fallen-8-web-ui/src/delegate/type-model.json, drop it from UNCOMPILABLE in "
                    + "fallen-8-web-ui/tests/type-model.test.ts, and update the accessor-surface "
                    + "section of docs/src/content/docs/delegates.mdx.");
                Assert.IsTrue(result.Diagnostics.Exists(d => d.Id == "CS0012"),
                    member + " must fail with CS0012 specifically (the missing-reference shape), "
                    + "not some other error. Got: "
                    + String.Join("; ", result.Diagnostics.ConvertAll(d => d.Id + ": " + d.Message)));
            }
        }

        [TestMethod]
        public void UnflaggedAccessorsAndDocumentedSubstitutes_Compile()
        {
            foreach (var (kind, member, fragment) in _compilable)
            {
                Assert.IsTrue(
                    DelegateValidationHelper.TryValidate(kind, fragment, out var result),
                    kind + " is a known kind.");
                Assert.IsTrue(result.Valid, kind + "/" + member + " no longer compiles, so the Studio completion "
                    + "list and the NL-assist prompt are advertising a member a fragment cannot use. "
                    + "Diagnostics: "
                    + String.Join("; ", result.Diagnostics.ConvertAll(d => d.Id + ": " + d.Message)));
            }
        }
    }
}
